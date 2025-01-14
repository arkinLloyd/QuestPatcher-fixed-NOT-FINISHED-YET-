﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using QuestPatcher.Zip.Data;

namespace QuestPatcher.Zip
{
    /// <summary>
    /// ZIP implementation used for reading APK files.
    /// Not thread safe; an ApkZip should only ever be called upon by one thread.
    /// </summary>
    public class ApkZip : IDisposable
    {
        internal static ZipVersion MaxSupportedVersion = new ZipVersion
        {
            Major = 2,
            Minor = 0
        };

        /// <summary>
        /// The PEM data of the certificate to sign the APK with.
        /// </summary>
        public string CertificatePem
        {
            set
            {
                var (cert, privateKey) = SigningUtility.LoadCertificate(value);
                if (cert == null)
                {
                    throw new ArgumentException("No certificate in given PEM data");
                }

                if (privateKey == null)
                {
                    throw new ArgumentException("No private key in given PEM data");
                }

                _certificate = cert;
                _privateKey = privateKey;
            }
        }

        /// <summary>
        /// The full file names of each entry inside the APK.
        /// </summary>
        public ICollection<string> Entries => _centralDirectoryRecords.Keys;

        private readonly Dictionary<string, CentralDirectoryFileHeader> _centralDirectoryRecords;
        private Dictionary<string, string>? _existingHashes;
        private long _postFilesOffset;
        private readonly Stream _stream;
        private readonly BinaryReader _reader;

        private AsymmetricKeyParameter? _privateKey;
        private X509Certificate? _certificate;

        private bool _disposed = false;

        private ApkZip(Dictionary<string, CentralDirectoryFileHeader> centralDirectoryRecords, long postFilesOffset, Stream stream, BinaryReader reader)
        {
            _centralDirectoryRecords = centralDirectoryRecords;
            _postFilesOffset = postFilesOffset;
            _stream = stream;
            _reader = reader;

            // Use the default certificate until another is assigned
            CertificatePem = Certificates.DefaultCertificatePem;
        }

        /// <summary>
        /// Opens an APK's ZIP file.
        /// </summary>
        /// <param name="stream">The stream to load the APK ZIP from. Must support seeking and reading.
        /// May support writing.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">If the stream does not support seeking or reading</exception>
        /// <exception cref="ZipFormatException">If the ZIP file cannot be loaded by this ZIP implementation.</exception>
        public static ApkZip Open(Stream stream)
        {
            if (!stream.CanSeek || !stream.CanRead)
            {
                throw new ArgumentException("Must have seekable and readable stream.");
            }

            var reader = new BinaryReader(stream);

            // A ZIP file must end with an end of central directory record, which is, at minimum, 22 bytes long.
            // The record starts with 4 header bytes, so we will locate these first.
            stream.Position = stream.Length - 22;

            while (reader.ReadUInt32() != EndOfCentralDirectory.Header)
            {
                // If we have just read from the start of the stream and there is still no EOCD signature, then this isn't a valid ZIP file
                if (stream.Position == 0 + 4)
                {
                    throw new ZipFormatException("File is not a valid ZIP archive: No end of central directory record found");
                }

                // Move 1 byte before the 4 bytes we just read.
                stream.Position -= (4 + 1);
            }

            stream.Position -= 4;
            var eocd = EndOfCentralDirectory.Read(reader);

            stream.Position = eocd.CentralDirectoryOffset;

            // TODO: Possibly add support for duplicate files, and unnamed files, depending on whether android accepts then in APKs
            // Right now they will both trigger an exception.
            var centralDirectoryRecords = new Dictionary<string, CentralDirectoryFileHeader>();
            CentralDirectoryFileHeader? lastRecord = null;
            for (int i = 0; i < eocd.CentralDirectoryRecords; i++)
            {
                var record = CentralDirectoryFileHeader.Read(reader);
                if (record.FileName == null)
                {
                    throw new ZipFormatException("Zero-length file names are not supported");
                }
                if (centralDirectoryRecords.ContainsKey(record.FileName))
                {
                    throw new ZipFormatException("Duplicate file names are not supported");
                }
                centralDirectoryRecords[record.FileName] = record;
                if (record.LocalHeaderOffset >= (lastRecord?.LocalHeaderOffset ?? uint.MinValue))
                {
                    lastRecord = record;
                }
            }

            // Find the position of the first byte after the last local header
            if (lastRecord == null)
            {
                stream.Position = 0;
            }
            else
            {
                SeekToEndOfEntry(stream, lastRecord);
            }
            long postFilesOffset = stream.Position;
            stream.Position = 0;


            // Delete the existing eocd and central directory.
            // This isn't strictly necessary, and we could also just append our new files and then a new central directory
            // BUT we might as well do it since it saves space and doesn't involve pushing too many bytes around
            // It is done NOW since we don't want to overwrite any new files that may be added later.
            if (stream.CanWrite)
            {
                stream.SetLength(postFilesOffset);
            }

            var apkZip = new ApkZip(centralDirectoryRecords, postFilesOffset, stream, reader);
            if (stream.CanWrite)
            {
                // Load the digests of each file from the signature. When we re-sign the APK later, we then only need to hash the changed files.
                apkZip._existingHashes = JarSigner.CollectExistingHashes(apkZip);
            }

            return apkZip;
        }

        /// <summary>
        /// Checks whether or not the ZIP contains a file.
        /// </summary>
        /// <param name="fileName">The file name</param>
        /// <returns>True if the ZIP contains an entry with this name</returns>
        public bool ContainsFile(string fileName)
        {
            ThrowIfDisposed();

            return _centralDirectoryRecords.ContainsKey(NormaliseFileName(fileName));
        }

        /// <summary>
        /// Gets the CRC-32 digest of the file with the given name within the APK.
        /// </summary>
        /// <param name="fileName">The full path to the file</param>
        /// <exception cref="ArgumentException">If no file with the given name exists within the APK</exception>
        /// <returns>The CRC-32 of the file</returns>
        public uint GetCrc32(string fileName)
        {
            ThrowIfDisposed();

            fileName = NormaliseFileName(fileName);
            if (!ContainsFile(fileName))
            {
                throw new ArgumentException($"No file with name {fileName} exists within the ZIP");
            }

            return _centralDirectoryRecords[fileName].Crc32;
        }

        /// <summary>
        /// Removes a file from the ZIP.
        /// </summary>
        /// <param name="fileName">The file name to remove</param>
        /// <returns>true if the file existed in the ZIP and was thus removed, false otherwise</returns>
        /// <exception cref="InvalidOperationException">If this ZIP file is readonly</exception>
        public bool RemoveFile(string fileName)
        {
            ThrowIfDisposed();

            fileName = NormaliseFileName(fileName);
            if (!_stream.CanWrite)
            {
                throw new InvalidOperationException("Attempted to delete a file in a readonly ZIP file");
            }
            _existingHashes?.Remove(fileName);
            return _centralDirectoryRecords.Remove(fileName);
        }

        /// <summary>
        /// Opens a stream to read a file in the APK.
        /// </summary>
        /// <param name="fileName">The name of the file to open.</param>
        /// <returns>A stream that can be used to read from the file. Must only be read from the thread that opened it.</returns>
        /// <exception cref="ArgumentException">If no file with the given name exists within the APK</exception>
        public Stream OpenReader(string fileName)
        {
            ThrowIfDisposed();

            if (_centralDirectoryRecords.TryGetValue(NormaliseFileName(fileName), out var centralDirectoryHeader))
            {
                _stream.Position = centralDirectoryHeader.LocalHeaderOffset;
                var _ = LocalFileHeader.Read(_reader); // LocalFileHeader currently doesn't contain any information we need

                var entryStream = new ZipEntryReadStream(_stream, this, _stream.Position, centralDirectoryHeader.CompressedSize);

                // Currently only DEFLATE and STORE compression methods are supported
                if (centralDirectoryHeader.CompressionMethod == CompressionMethod.Deflate)
                {
                    return new DeflateStream(entryStream, CompressionMode.Decompress);
                }
                else
                {
                    return entryStream;
                }
            }
            else
            {
                throw new ArgumentException($"No file with name {fileName} exists within the ZIP");
            }
        }

        /// <summary>
        /// Copies the data from a stream to an entry on the ZIP.
        /// If the file exists, it will be deleted first and a new entry for the file appended to the ZIP.
        /// </summary>
        /// <param name="fileName">The name/path of the file to write to</param>
        /// <param name="sourceData">The stream containing data to copy to the file. Must support the Length property and reading.</param>
        /// <param name="compressionLevel">The (DEFLATE) compression level to use. If null, the STORE method will be used for the file.</param>
        public void AddFile(string fileName, Stream sourceData, CompressionLevel? compressionLevel)
        {
            ThrowIfDisposed();

            fileName = NormaliseFileName(fileName);

            _centralDirectoryRecords.Remove(fileName);
            _existingHashes?.Remove(fileName);

            // Move to a position after the last ZIP entry
            _stream.Position = _postFilesOffset;
            long localHeaderOffset = _stream.Position;

            var flags = EntryFlags.UsesUtf8;
            byte[] fileNameBytes = flags.GetStringEncoding().GetBytes(fileName);

            // Move past where the local file header for this entry will go.
            _stream.Position += 30 + fileNameBytes.Length;
            long dataOffset = _stream.Position;


            long uncompressedSize = sourceData.Length;
            var compressionMethod = compressionLevel == null ? CompressionMethod.Store : CompressionMethod.Deflate;

            // Copy the data into the entry, calculating the Crc32 at the same time.
            uint crc32;
            if (compressionLevel != null)
            {
                using var compressor = new DeflateStream(_stream, (CompressionLevel) compressionLevel, true);
                crc32 = sourceData.CopyToCrc32(compressor);
            }
            else
            {
                // TODO: Could align files using STORE to 4 bytes like zipalign does
                // However, this is likely unnecessary, as this is only important for large (e.g. media) files to allow them to be read with mmap.
                crc32 = sourceData.CopyToCrc32(_stream);
            }
            long postEntryDataOffset = _stream.Position;
            long compressedSize = postEntryDataOffset - dataOffset;


            var lastModified = new Timestamp
            {
                DateTime = DateTime.Now // ZIP files use the local timestamp
            };
            var localHeader = new LocalFileHeader()
            {
                VersionNeededToExtract = MaxSupportedVersion,
                Flags = flags,
                CompressionMethod = compressionMethod,
                LastModified = lastModified,

                Crc32 = crc32,
                CompressedSize = (uint) compressedSize,
                UncompressedSize = (uint) uncompressedSize,

                FileName = fileName,
                ExtraField = null
            };

            // Write the entry's local header
            _stream.Position = localHeaderOffset;
            var writer = new BinaryWriter(_stream);
            localHeader.Write(writer);

            var centralDirectoryHeader = new CentralDirectoryFileHeader()
            {
                // The existing resources file and some other APK files use this ID without setting any external file attributes, so it should be safe for us
                VersionMadeBy = 0,
                VersionNeededToExtract = MaxSupportedVersion,
                Flags = flags,
                CompressionMethod = compressionMethod,
                LastModified = lastModified,

                Crc32 = crc32,
                CompressedSize = (uint) compressedSize,
                UncompressedSize = (uint) uncompressedSize,

                FileName = fileName,
                ExtraField = null,
                FileComment = null,
                DiskNumberStart = 0,
                InternalFileAttributes = 0,
                ExternalFileAttributes = 0,
                LocalHeaderOffset = (uint) localHeaderOffset
            };
            _centralDirectoryRecords[fileName] = centralDirectoryHeader;

            // Update the position where the next file will be stored.
            _postFilesOffset = postEntryDataOffset;
        }

        internal static ZipVersion CheckVersionSupported(ZipVersion version)
        {
            if (version.Major >= MaxSupportedVersion.Major)
            {
                if (version.Minor > MaxSupportedVersion.Minor)
                {
                    throw new ZipFormatException($"ZIP file uses version: {version}, but the maximum supported version is {MaxSupportedVersion}");
                }
            }

            return version;
        }

        /// <summary>
        /// Seeks the stream to the first byte after the contents of a ZIP entry (including after the data descriptor if present).
        /// </summary>
        /// <param name="stream">The stream to seek.</param>
        /// <param name="header">The file's header.</param>
        private static void SeekToEndOfEntry(Stream stream, CentralDirectoryFileHeader header)
        {
            const uint DataDescriptorSignature = 0x08074b50;

            // Read the local header and skip past the compressed data.
            stream.Position = header.LocalHeaderOffset;
            var reader = new BinaryReader(stream);
            var _ = LocalFileHeader.Read(reader);
            stream.Position += header.CompressedSize;

            if (header.Flags.HasFlag(EntryFlags.UsesDataDescriptor))
            {
                // If the entry has stated that it has a data descriptor, read this too.

                // Some data descriptors contain the signature, some do not.
                // The signature was originally not part of the ZIP specification but has been widely adopted.
                uint _crc = reader.ReadUInt32();
                if (_crc == DataDescriptorSignature)
                {
                    // If the signature was present, and so we just read the signature, read the actual CRC here.
                    // In the situation that the CRC32 happened to equal the data descriptor signature, this will read invalid data.
                    // This case is not accounted for in the ZIP specification, so we will have to live in fear.
                    _crc = reader.ReadUInt32();
                }


                uint _compressedSize = reader.ReadUInt32();
                uint _uncompressedSize = reader.ReadUInt32();
            }
        }

        private string NormaliseFileName(string fileName)
        {
            // Remove leading slash and normalise slashes to forward slashes
            fileName = fileName.Replace('\\', '/');
            if (fileName.StartsWith('/'))
            {
                fileName = fileName.Substring(1);
            }

            return fileName;
        }

        private void Save()
        {
            // _certificate and _privateKey are non-null due to the default certificate assigned in the constructor.
            JarSigner.SignApkFile(this, _certificate!, _privateKey!, _existingHashes);

            _stream.Position = _postFilesOffset;
            V2Signer.SignAndCompleteZipFile(_centralDirectoryRecords.Values, _stream, _certificate!, _privateKey!);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_stream.CanWrite)
                {
                    Save();
                }
            }
            finally
            {
                // Do not dispose until this point, as JarSigner needs to be able to add the signature files to the APK, and for that the ApkZip must not be disposed.
                _disposed = true;
                _stream.Dispose();
                _reader.Dispose();
            }
        }
    }
}

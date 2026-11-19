// SPDX-License-Identifier: MIT
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.Common.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Ryujinx.HLE.Loaders.Processes
{
    // NUP v1, as documented in https://github.com/a55uka/nup/blob/meow/SPEC.md.
    // Expose ExeFS and RomFS as read-only views backed by the NUP file.
    internal sealed class NupArchive : IDisposable
    {
        private const int HeaderSize = 0x80;
        private const int TocHeaderSize = 0x30;
        private const int PackageSize = 0x40;
        private const int DirectorySize = 0x18;
        private const int FileSize = 0x50;
        private const uint NoDirectory = uint.MaxValue;
        private const byte BasePackage = 0;
        private const byte UpdatePackage = 1;
        private const byte StandaloneUpdateFlag = 1;
        private const byte RawCodec = 0;
        private const byte ZstdCodec = 1;
        private const long MaxTocBytes = 64L * 1024 * 1024;
        private const ulong MaxMappedBytes = 1UL << 40;
        private const ulong RomFsAlignment = 16;
        private const int MaxTreeDepth = 64;
        private const int MaxZstdWindowLog = 27; // 128 MiB per decoder.
        private const int MaxNameBytes = 255;
        private const int MaxRelativePath = 4096;

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly record struct Entry(string Path, long Size, long Offset, long StoredSize, int DigestOffset, byte Codec);

        private readonly FileStream _archive;
        private readonly IStorage _storage;
        private readonly byte[] _toc;
        private readonly List<Entry> _entries;
        private readonly Lock _readLock = new();
        private readonly bool _hasRomFs;

        private Entry _decoderEntry;
        private DecompressionStream _decoder;
        private long _decoderPosition;

        public bool IsStandaloneUpdate { get; }

        private NupArchive(FileStream archive, byte[] toc, List<Entry> entries, bool hasRomFs, bool isStandaloneUpdate)
        {
            _archive = archive;
            _storage = archive.AsStorage(true);
            _toc = toc;
            _entries = entries;
            _hasRomFs = hasRomFs;
            IsStandaloneUpdate = isStandaloneUpdate;
        }

        public static NupArchive Open(string path)
        {
            FileStream archive = File.OpenRead(path);
            try
            {
                return Open(archive);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        private static NupArchive Open(FileStream archive)
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            archive.ReadExactly(header);

            ulong tocSize = Read64(header, 0x10);
            ulong blobOffset = Read64(header, 0x38);
            ulong archiveSize = (ulong)archive.Length;
            uint packageCount = Read32(header, 0x48);

            if (!header[..4].SequenceEqual("NUP\u0001"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != HeaderSize ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 1 ||
                Read64(header, 8) != HeaderSize || tocSize is < TocHeaderSize or > MaxTocBytes ||
                blobOffset < HeaderSize + tocSize || blobOffset > archiveSize || (blobOffset & 15) != 0 ||
                Read64(header, 0x40) != archiveSize || packageCount == 0 || Read32(header, 0x4c) != 0)
            {
                throw new InvalidDataException("Invalid NUP v1 header or TOC bounds.");
            }

            // The header check bounds the allocation to MaxTocBytes.
            byte[] toc = new byte[(int)tocSize];
            archive.ReadExactly(toc);
            if (!SHA256.HashData(toc).AsSpan().SequenceEqual(header.Slice(0x18, 32)))
            {
                throw new InvalidDataException("NUP TOC SHA-256 mismatch.");
            }

            List<Entry> entries = ReadProgramTree(toc, packageCount, blobOffset, archiveSize, out bool hasRomFs, out bool isStandaloneUpdate);

            return new NupArchive(archive, toc, entries, hasRomFs, isStandaloneUpdate);
        }

        public IFileSystem OpenExeFs() => new RomFsFileSystem(BuildView("exefs/"));

        public IStorage OpenRomFs() => _hasRomFs ? BuildView("romfs/") : null;

        private IStorage BuildView(string prefix)
        {
            RomFsBuilder builder = new();
            foreach (Entry entry in _entries)
            {
                if (entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    builder.AddFile(entry.Path[(prefix.Length - 1)..], new StorageFile(new EntryStorage(this, entry), OpenMode.Read));
                }
            }

            return builder.Build();
        }

        private Result Read(Entry entry, long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > entry.Size || destination.Length > entry.Size - offset)
            {
                return ResultFs.OutOfRange;
            }

            if (destination.IsEmpty)
            {
                return Result.Success;
            }

            lock (_readLock)
            {
                try
                {
                    if (entry.Codec == RawCodec)
                    {
                        _archive.Position = entry.Offset + offset;
                        _archive.ReadExactly(destination);
                    }
                    else
                    {
                        ReadCompressed(entry, offset, destination);
                    }

                    // A whole-file read can verify its digest without scanning unrelated bytes.
                    if (offset == 0 && destination.Length == entry.Size)
                    {
                        Span<byte> digest = stackalloc byte[32];
                        SHA256.HashData(destination, digest);
                        if (!digest.SequenceEqual(_toc.AsSpan(entry.DigestOffset, digest.Length)))
                        {
                            throw new InvalidDataException("File SHA-256 mismatch.");
                        }
                    }

                    return Result.Success;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ZstdException)
                {
                    ResetDecoder();
                    Logger.Error?.Print(LogClass.Loader, $"Cannot read NUP '{_archive.Name}', entry '{entry.Path}' at {offset}: {ex.Message}");
                    return ResultFs.DataCorrupted;
                }
            }
        }

        private void ReadCompressed(Entry entry, long offset, Span<byte> destination)
        {
            // A NUP v1 file is a single Zstandard frame. Restart it on a backwards seek.
            // Keep one decoder live across the archive, rather than one per ROMFS file.
            if (_decoder == null || _decoderEntry != entry || offset < _decoderPosition)
            {
                ResetDecoder();
                SubStorage extent = new(_storage, entry.Offset, entry.StoredSize);
                _decoder = new DecompressionStream(extent.AsStream());
                _decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxZstdWindowLog);
                _decoderEntry = entry;
            }

            if (offset > _decoderPosition)
            {
                byte[] scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (_decoderPosition < offset)
                    {
                        // The scratch buffer bounds the narrowing conversion.
                        int count = (int)Math.Min(offset - _decoderPosition, scratch.Length);
                        _decoder.ReadExactly(scratch.AsSpan(0, count));
                        _decoderPosition += count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            }

            _decoder.ReadExactly(destination);
            _decoderPosition += destination.Length;
            if (_decoderPosition == entry.Size)
            {
                if (_decoder.ReadByte() != -1)
                {
                    throw new InvalidDataException("File exceeds its declared size.");
                }

                ResetDecoder();
            }
        }

        private void ResetDecoder()
        {
            _decoder?.Dispose();
            _decoder = null;
            _decoderPosition = 0;
        }

        private sealed class EntryStorage(NupArchive archive, Entry entry) : IStorage
        {
            public override Result Read(long offset, Span<byte> destination) => archive.Read(entry, offset, destination);
            public override Result Write(long offset, ReadOnlySpan<byte> source) => ResultFs.UnsupportedWriteForReadOnlyFile;
            public override Result Flush() => Result.Success;
            public override Result SetSize(long size) => ResultFs.UnsupportedWriteForReadOnlyFile;
            public override Result OperateRange(Span<byte> output, OperationId operationId, long offset, long size, ReadOnlySpan<byte> input) =>
                ResultFs.UnsupportedOperateRangeForStorageFile;

            public override Result GetSize(out long size)
            {
                size = entry.Size;
                return Result.Success;
            }
        }

        private static List<Entry> ReadProgramTree(byte[] toc, uint packageCount, ulong blobOffset, ulong archiveSize, out bool hasRomFs, out bool isStandaloneUpdate)
        {
            ulong dirCount = Read64(toc, 8);
            ulong fileCount = Read64(toc, 0x10);
            ulong strings = Read64(toc, 0x18);
            ulong stringSize = Read64(toc, 0x20);
            ulong tocSize = (ulong)toc.Length;

            if (!toc.AsSpan(0, 4).SequenceEqual("TOC\u0001"u8) || Read32(toc, 4) != packageCount ||
                packageCount > tocSize / PackageSize || dirCount > tocSize / DirectorySize ||
                fileCount > tocSize / FileSize || strings > tocSize || stringSize > tocSize - strings)
            {
                throw new InvalidDataException("Invalid NUP TOC counts or string pool.");
            }

            ulong directories = TocHeaderSize + (ulong)packageCount * PackageSize;
            ulong files = directories + dirCount * DirectorySize;
            ulong children = files + fileCount * FileSize;
            if (children > strings || (strings - children) % sizeof(uint) != 0)
            {
                throw new InvalidDataException("Invalid NUP TOC table bounds.");
            }

            int package = SelectProgramPackage(toc, packageCount);
            isStandaloneUpdate = toc[package + 0x18] == UpdatePackage;
            uint exeFs = Read32(toc, package + 0x0c);
            uint romFs = Read32(toc, package + 0x10);
            if (exeFs >= dirCount || (romFs != NoDirectory && romFs >= dirCount))
            {
                throw new InvalidDataException("The selected NUP package has invalid ExeFS or RomFS directory indexes.");
            }

            List<Entry> entries = [];
            HashSet<uint> visited = [];
            ulong mappedBytes = 0;
            ReadDirectory(exeFs, "exefs", 0);
            hasRomFs = romFs != NoDirectory;
            if (hasRomFs)
            {
                ReadDirectory(romFs, "romfs", 0);
            }

            return entries;

            void ReadDirectory(uint index, string parent, int depth)
            {
                if (depth >= MaxTreeDepth || !visited.Add(index))
                {
                    throw new InvalidDataException($"NUP directory '{parent}' is too deep or referenced more than once.");
                }

                // Directory and child indexes are bounded by the validated TOC tables.
                int directory = (int)(directories + index * (ulong)DirectorySize);
                ulong first = Read32(toc, directory + 8);
                ulong count = Read32(toc, directory + 0xc);
                if (first + count > (strings - children) / sizeof(uint))
                {
                    throw new InvalidDataException($"NUP directory '{parent}' has invalid child bounds.");
                }

                HashSet<string> names = new(StringComparer.Ordinal);
                for (ulong i = first; i < first + count; i++)
                {
                    uint node = Read32(toc, (int)(children + i * sizeof(uint)));
                    bool isDirectory = node < dirCount;
                    if (node >= dirCount + fileCount)
                    {
                        throw new InvalidDataException($"NUP directory '{parent}' references invalid node {node}.");
                    }

                    int record = isDirectory
                        ? (int)(directories + node * (ulong)DirectorySize)
                        : (int)(files + (node - dirCount) * FileSize);
                    uint nameOffset = Read32(toc, record);
                    uint nameLength = Read32(toc, record + 4);
                    if (nameLength is 0 or > MaxNameBytes || (ulong)nameOffset + nameLength > stringSize)
                    {
                        throw new InvalidDataException($"Invalid NUP name bounds for node {node} under '{parent}'.");
                    }

                    // The string pool bounds both casts and the UTF-8 decode.
                    string name = StrictUtf8.GetString(toc.AsSpan((int)(strings + nameOffset), (int)nameLength));
                    if (name is "." or ".." || name.AsSpan().IndexOfAny("/\\:\0") >= 0 || !names.Add(name))
                    {
                        throw new InvalidDataException($"Unsafe or duplicate NUP name '{name}' under '{parent}'.");
                    }

                    string path = parent + "/" + name;
                    if (path.Length > MaxRelativePath)
                    {
                        throw new InvalidDataException($"NUP path exceeds {MaxRelativePath} characters under '{parent}'.");
                    }

                    if (isDirectory)
                    {
                        ReadDirectory(node, path, depth + 1);
                    }
                    else
                    {
                        if (!visited.Add(node))
                        {
                            throw new InvalidDataException($"NUP file '{path}' is referenced more than once.");
                        }

                        ulong size = Read64(toc, record + 8);
                        ulong offset = Read64(toc, record + 0x10);
                        ulong storedSize = Read64(toc, record + 0x18);
                        byte codec = toc[record + 0x40];
                        if (size > MaxMappedBytes - RomFsAlignment || size + RomFsAlignment > MaxMappedBytes - mappedBytes || toc[record + 0x41] != 0 ||
                            codec is not (RawCodec or ZstdCodec) || (codec == RawCodec && size != storedSize) ||
                            (size == 0 ? offset != 0 || codec != RawCodec :
                                storedSize == 0 || offset < blobOffset || offset > archiveSize ||
                                storedSize > archiveSize - offset || (offset & 15) != 0))
                        {
                            throw new InvalidDataException($"Invalid or unsupported NUP extent for '{path}'.");
                        }

                        // The extent and size checks bound these casts to the signed storage API.
                        entries.Add(new Entry(path, (long)size, (long)offset, (long)storedSize, record + 0x20, codec));
                        mappedBytes += size + RomFsAlignment;
                    }
                }
            }
        }

        private static int SelectProgramPackage(ReadOnlySpan<byte> toc, uint packageCount)
        {
            int basePackage = -1;
            int standaloneUpdate = -1;
            for (int i = 0; i < packageCount; i++)
            {
                // ReadProgramTree bounds the package table by the size of the TOC.
                int package = TocHeaderSize + i * PackageSize;
                if (toc[package + 0x18] == BasePackage)
                {
                    if (basePackage >= 0)
                    {
                        throw new InvalidDataException("The NUP contains multiple base packages.");
                    }

                    basePackage = package;
                }
                else if (toc[package + 0x18] == UpdatePackage &&
                    toc[package + 0x19] == StandaloneUpdateFlag &&
                    (standaloneUpdate < 0 || Read32(toc, package + 0x14) > Read32(toc, standaloneUpdate + 0x14)))
                {
                    standaloneUpdate = package;
                }
            }

            if (basePackage >= 0)
            {
                return basePackage;
            }

            if (standaloneUpdate < 0)
            {
                throw new InvalidDataException("The NUP contains neither a base package nor a standalone update.");
            }

            return standaloneUpdate;
        }

        private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

        private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

        public void Dispose()
        {
            lock (_readLock)
            {
                ResetDecoder();
                _storage.Dispose();
                _archive.Dispose();
            }
        }
    }
}

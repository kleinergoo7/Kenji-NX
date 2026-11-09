// SPDX-License-Identifier: MIT
using LibHac.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;
using Path = System.IO.Path;

namespace Ryujinx.HLE.Loaders.Processes
{
    // NUP v1, as documented in https://github.com/a55uka/nup/blob/meow/SPEC.md.
    // Materialize the base package for the same ExeFS/RomFS loader as a loose title.
    internal sealed class NupArchive : IDisposable
    {
        private const int HeaderSize = 0x80;
        private const int TocHeaderSize = 0x30;
        private const int PackageSize = 0x40;
        private const int DirectorySize = 0x18;
        private const int FileSize = 0x50;
        private const uint NoDirectory = uint.MaxValue;
        private const byte BasePackage = 0;
        private const byte RawCodec = 0;
        private const byte ZstdCodec = 1;
        private const long MaxTocBytes = 64L * 1024 * 1024;
        private const long MaxExtractedBytes = 64L * 1024 * 1024 * 1024;
        private const int MaxTreeDepth = 64;
        private const int MaxZstdWindowLog = 27; // 128 MiB per decoder.
        private const int MaxNameBytes = 255;
        private const int MaxRelativePath = 4096;

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        private readonly record struct Entry(string Path, long Size, long Offset, long StoredSize, int DigestOffset, byte Codec);

        private readonly string _directory;

        public string ExeFsPath => Path.Combine(_directory, "exefs");
        public string RomFsPath { get; }

        private NupArchive(string directory, bool hasRomFs)
        {
            _directory = directory;
            RomFsPath = hasRomFs ? Path.Combine(directory, "romfs") : null;
        }

        public static NupArchive ExtractBase(string path, string cacheDirectory)
        {
            using FileStream archive = File.OpenRead(path);
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

            List<Entry> entries = ReadBaseTree(toc, packageCount, blobOffset, archiveSize, out long totalSize, out bool hasRomFs);
            Directory.CreateDirectory(cacheDirectory);
            string mount = Path.GetPathRoot(cacheDirectory);
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.Name.Length > mount.Length &&
                    (cacheDirectory == drive.Name || cacheDirectory.StartsWith(
                        Path.TrimEndingDirectorySeparator(drive.Name) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    mount = drive.Name;
                }
            }

            if (new DriveInfo(mount).AvailableFreeSpace < totalSize)
            {
                throw new IOException($"The NUP base package requires {totalSize} bytes of free space in '{cacheDirectory}'.");
            }

            NupArchive result = new(Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N")), hasRomFs);
            bool extracted = false;
            try
            {
                using IStorage storage = archive.AsStorage(true);
                byte[] buffer = new byte[128 * 1024];

                Logger.Info?.Print(LogClass.Loader, $"Extracting NUP base package '{path}' ({totalSize} bytes).");
                foreach (Entry entry in entries)
                {
                    string destination = Path.Combine(result._directory, entry.Path);
                    if (entry.DigestOffset < 0)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write);
                    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    if (entry.Size != 0)
                    {
                        using SubStorage extent = new(storage, entry.Offset, entry.StoredSize);
                        using Stream raw = extent.AsStream(FileAccess.Read, true);
                        using DecompressionStream decoder = entry.Codec == ZstdCodec ? new(raw, leaveOpen: true) : null;
                        decoder?.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxZstdWindowLog);
                        Stream decoded = (Stream)decoder ?? raw;
                        long remaining = entry.Size;
                        while (remaining > 0)
                        {
                            // The buffer bounds the count, even for a multi-gigabyte entry.
                            int count = (int)Math.Min(remaining, buffer.Length);
                            decoded.ReadExactly(buffer.AsSpan(0, count));
                            output.Write(buffer, 0, count);
                            hash.AppendData(buffer, 0, count);
                            remaining -= count;
                        }

                        if (decoded.ReadByte() != -1)
                        {
                            throw new InvalidDataException($"NUP entry '{entry.Path}' exceeds its declared size.");
                        }
                    }

                    if (!hash.GetHashAndReset().AsSpan().SequenceEqual(toc.AsSpan(entry.DigestOffset, 32)))
                    {
                        throw new InvalidDataException($"NUP entry '{entry.Path}' has a SHA-256 mismatch.");
                    }
                }

                extracted = true;
                return result;
            }
            finally
            {
                if (!extracted)
                {
                    result.Dispose();
                }
            }
        }

        private static List<Entry> ReadBaseTree(byte[] toc, uint packageCount, ulong blobOffset, ulong archiveSize, out long totalSize, out bool hasRomFs)
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

            uint exeFs = NoDirectory;
            uint romFs = NoDirectory;
            bool foundBase = false;
            for (int i = 0; i < packageCount; i++)
            {
                int package = TocHeaderSize + i * PackageSize;
                if (toc[package + 0x18] == BasePackage)
                {
                    if (foundBase)
                    {
                        throw new InvalidDataException("The NUP contains multiple base packages.");
                    }

                    foundBase = true;
                    exeFs = Read32(toc, package + 0x0c);
                    romFs = Read32(toc, package + 0x10);
                }
            }

            if (!foundBase || exeFs >= dirCount || (romFs != NoDirectory && romFs >= dirCount))
            {
                throw new InvalidDataException("The NUP needs a base package with valid ExeFS and RomFS directory indexes.");
            }

            List<Entry> entries = [];
            HashSet<uint> visited = [];
            long bytes = 0;
            ReadDirectory(exeFs, "exefs", 0);
            hasRomFs = romFs != NoDirectory;
            if (hasRomFs)
            {
                ReadDirectory(romFs, "romfs", 0);
            }

            totalSize = bytes;
            return entries;

            void ReadDirectory(uint index, string parent, int depth)
            {
                if (depth >= MaxTreeDepth || !visited.Add(index))
                {
                    throw new InvalidDataException($"NUP directory '{parent}' is too deep or referenced more than once.");
                }

                entries.Add(new Entry(parent, 0, 0, 0, -1, RawCodec));

                // Directory and child indexes are bounded by the validated TOC tables.
                int directory = (int)(directories + index * (ulong)DirectorySize);
                ulong first = Read32(toc, directory + 8);
                ulong count = Read32(toc, directory + 0xc);
                if (first + count > (strings - children) / sizeof(uint))
                {
                    throw new InvalidDataException($"NUP directory '{parent}' has invalid child bounds.");
                }

                HashSet<string> names = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
                    if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
                        name.AsSpan().IndexOfAny("/\\:\0") >= 0 ||
                        name.IndexOfAny(InvalidNameChars) >= 0 || IsReservedWindowsName(name) || !names.Add(name))
                    {
                        throw new InvalidDataException($"Unsafe or duplicate NUP name '{name}' under '{parent}'.");
                    }

                    string path = Path.Combine(parent, name);
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
                        if (size > (ulong)(MaxExtractedBytes - bytes) || toc[record + 0x41] != 0 ||
                            codec is not (RawCodec or ZstdCodec) || (codec == RawCodec && size != storedSize) ||
                            (size == 0 ? offset != 0 || codec != RawCodec :
                                storedSize == 0 || offset < blobOffset || offset > archiveSize ||
                                storedSize > archiveSize - offset || (offset & 15) != 0))
                        {
                            throw new InvalidDataException($"Invalid or unsupported NUP extent for '{path}'.");
                        }

                        // The output cap and the archive's signed stream length bound these casts.
                        entries.Add(new Entry(path, (long)size, (long)offset, (long)storedSize, record + 0x20, codec));
                        bytes += (long)size;
                    }
                }
            }
        }

        private static bool IsReservedWindowsName(string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();

            return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
        }

        private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

        private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning?.Print(LogClass.Loader, $"Could not remove extracted NUP files in '{_directory}': {ex.Message}");
            }
        }
    }
}

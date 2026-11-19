// SPDX-License-Identifier: MIT
//
// Extract raw code bytes at a guest VA from a module in a Switch 2 NUP's ExeFS.
// Reuses the repository's NupArchive (NUP TOC + Zstd) and LibHac's NsoReader
// (NSO section decompression) rather than reimplementing either format.
//
// Usage: Nx2DumpGuestCode <nup> <module> <guest-va> <length> <out.bin>
using System;
using System.IO;
using System.Reflection;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Tools.FsSystem;

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: Nx2DumpGuestCode <nup> <module> <guest-va> <length> <out.bin>");
    return 2;
}

static ulong ParseHex(string value) => Convert.ToUInt64(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, 16);

string nupPath = args[0];
string module = args[1].TrimStart('/');
ulong guestVa = ParseHex(args[2]);
int length = checked((int)ParseHex(args[3]));
string outPath = args[4];

// cengine.nss is the "main" NSO. Task 1's stack capture puts it at this base
// (VA 0x863d118 -> cengine.nss:0x11d118); override with NX2_GUEST_MODULE_BASE.
ulong moduleBase = 0x8520000;
if (Environment.GetEnvironmentVariable("NX2_GUEST_MODULE_BASE") is string mb && mb.Length != 0)
{
    moduleBase = ParseHex(mb);
}

Assembly hle = Assembly.Load("Ryujinx.HLE");
Type nupArchive = hle.GetType("Ryujinx.HLE.Loaders.Processes.NupArchive", throwOnError: true);
MethodInfo open = nupArchive.GetMethod("Open", BindingFlags.Public | BindingFlags.Static,
    null, new[] { typeof(string) }, null);
MethodInfo openExeFs = nupArchive.GetMethod("OpenExeFs", BindingFlags.Public | BindingFlags.Instance);

object archive = open.Invoke(null, new object[] { nupPath });
try
{
    using IFileSystem exefs = (IFileSystem)openExeFs.Invoke(archive, null);
    using UniqueRef<IFile> moduleFile = new();
    exefs.OpenFile(ref moduleFile.Ref, ("/" + module).ToU8Span(), OpenMode.Read).ThrowIfFailure();
    moduleFile.Get.GetSize(out long nsoSize).ThrowIfFailure();

    byte[] nsoBytes = new byte[nsoSize];
    moduleFile.Get.Read(out long read, 0, nsoBytes).ThrowIfFailure();
    if (read != nsoSize)
    {
        throw new IOException($"short read of NSO '{module}': {read} of {nsoSize}");
    }

    using MemoryStorage storage = new(nsoBytes);
    NsoReader reader = new();
    reader.Initialize(storage.AsFile(OpenMode.Read)).ThrowIfFailure();
    reader.GetSegmentSize(NsoReader.SegmentType.Text, out uint textSize).ThrowIfFailure();

    byte[] text = new byte[textSize];
    reader.ReadSegment(NsoReader.SegmentType.Text, text).ThrowIfFailure();

    // Same arithmetic as Nx2GuestCodeTests: file offset within the decompressed text section.
    ulong textRva = reader.Header.TextMemoryOffset;
    if (guestVa < moduleBase + textRva)
    {
        throw new ArgumentOutOfRangeException(nameof(guestVa), "Address is before the NSO text section.");
    }

    ulong offset = guestVa - moduleBase - textRva;
    int alignedLength = (length + 15) & ~15;
    if (offset + (ulong)alignedLength > (ulong)text.Length)
    {
        throw new ArgumentOutOfRangeException(nameof(guestVa), "Requested slice is outside the NSO text section.");
    }

    byte[] slice = new byte[alignedLength];
    Array.Copy(text, (int)offset, slice, 0, alignedLength);
    File.WriteAllBytes(outPath, slice);

    Console.WriteLine($"module={module} guestVA=0x{guestVa:X} base=0x{moduleBase:X} textRVA=0x{textRva:X}");
    Console.WriteLine($"text_size=0x{textSize:X} file_offset=0x{offset:X} length=0x{alignedLength:X} out={outPath}");
    return 0;
}
finally
{
    (archive as IDisposable)?.Dispose();
}

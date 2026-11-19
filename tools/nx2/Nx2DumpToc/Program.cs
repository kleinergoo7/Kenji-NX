// SPDX-License-Identifier: MIT
//
// Describe a Switch 2 NUP's package/entry makeup (no ROM bytes are extracted).
// Reuses the repository's NupTocInspector and NupArchive (both internal to
// Ryujinx.HLE, reached by reflection) and LibHac's filesystem views.
//
// Usage: Nx2DumpToc <nup>
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Nx2DumpToc <nup>");
    return 2;
}

string nupPath = args[0];
Assembly hle = Assembly.Load("Ryujinx.HLE");
Type inspector = hle.GetType("Ryujinx.HLE.Loaders.Processes.NupTocInspector", throwOnError: true);
MethodInfo describe = inspector.GetMethod("DescribePackages", BindingFlags.Public | BindingFlags.Static,
    null, new[] { typeof(byte[]) }, null);

// NupArchive does not expose its TOC, so read just the header and TOC bytes here.
using (FileStream stream = File.OpenRead(nupPath))
{
    Span<byte> header = stackalloc byte[0x80];
    stream.ReadExactly(header);
    ulong tocSize = BinaryPrimitives.ReadUInt64LittleEndian(header[0x10..]);
    byte[] toc = new byte[tocSize];
    stream.ReadExactly(toc);
    Console.WriteLine((string)describe.Invoke(null, new object[] { toc }));
}

Type nupArchive = hle.GetType("Ryujinx.HLE.Loaders.Processes.NupArchive", throwOnError: true);
MethodInfo open = nupArchive.GetMethod("Open", BindingFlags.Public | BindingFlags.Static,
    null, new[] { typeof(string) }, null);
MethodInfo openExeFs = nupArchive.GetMethod("OpenExeFs", BindingFlags.Public | BindingFlags.Instance);
MethodInfo openRomFs = nupArchive.GetMethod("OpenRomFs", BindingFlags.Public | BindingFlags.Instance);
PropertyInfo isStandaloneUpdate = nupArchive.GetProperty("IsStandaloneUpdate");

object archive = open.Invoke(null, new object[] { nupPath });
try
{
    Console.WriteLine($"isStandaloneUpdate={(bool)isStandaloneUpdate.GetValue(archive)}");

    using (IFileSystem exefs = (IFileSystem)openExeFs.Invoke(archive, null))
    {
        foreach (var entry in exefs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories)
                                    .Where(e => e.Type == DirectoryEntryType.File))
        {
            Console.WriteLine($"exefs {entry.FullPath} size={entry.Size}");
        }
    }

    IStorage romFsStorage = (IStorage)openRomFs.Invoke(archive, null);
    if (romFsStorage == null)
    {
        Console.WriteLine("romfs_files=0 romfs_bytes=0");
    }
    else
    {
        using IFileSystem romfs = new RomFsFileSystem(romFsStorage);
        int count = 0;
        long bytes = 0;
        foreach (var entry in romfs.EnumerateEntries("/", "*", SearchOptions.RecurseSubdirectories)
                                    .Where(e => e.Type == DirectoryEntryType.File))
        {
            count++;
            bytes += entry.Size;
        }

        Console.WriteLine($"romfs_files={count} romfs_bytes={bytes}");
    }
}
finally
{
    (archive as IDisposable)?.Dispose();
}

return 0;

// SPDX-License-Identifier: MIT
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem.RomFs;
using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;

namespace Ryujinx.Tests.HLE
{
    public class NupArchiveTests
    {
        private const int TocOffset = 0x80;
        private const int TocHashOffset = 0x18;

        private string _root;
        private string _archive;
        private byte[] _fixture;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateTempSubdirectory("NupArchiveTests").FullName;
            _archive = Path.Combine(_root, "test.nup");

            // Generated with a55uka/nup from synthetic base and update directories.
            using Stream resource = typeof(NupArchiveTests).Assembly.GetManifestResourceStream(
                typeof(NupArchiveTests), "Fixtures.NupBaseAndUpdate.nup");
            using MemoryStream buffer = new();
            resource.CopyTo(buffer);
            _fixture = buffer.ToArray();
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        [Test]
        public void ReadsRawAndCompressedBaseContentWithoutCreatingFiles()
        {
            File.WriteAllBytes(_archive, _fixture);
            byte[] expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressed fixture", 4096)));

            using (NupArchive package = NupArchive.Open(_archive))
            {
                using IFileSystem exefs = package.OpenExeFs();
                using IStorage romfsStorage = package.OpenRomFs();
                using IFileSystem romfs = new RomFsFileSystem(romfsStorage);
                using UniqueRef<IFile> main = new();
                using UniqueRef<IFile> data = new();
                exefs.OpenFile(ref main.Ref, "/main".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                romfs.OpenFile(ref data.Ref, "/data.bin".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Assert.That(Encoding.UTF8.GetString(Read(main.Get, 0, 15)), Is.EqualTo("base-executable"));
                Assert.That(Read(data.Get, 4096, 1024), Is.EqualTo(expected[4096..5120]));
                Assert.That(Read(main.Get, 5, 4), Is.EqualTo("exec"u8.ToArray()));
                Assert.That(Read(data.Get, 5120, 1024), Is.EqualTo(expected[5120..6144]));
                Assert.That(Read(data.Get, 4, 31), Is.EqualTo(expected[4..35]));
                Assert.That(Read(data.Get, 0, expected.Length), Is.EqualTo(expected));
                Assert.That(Directory.GetFileSystemEntries(_root), Is.EqualTo(new[] { _archive }));
            }

            Assert.That(File.ReadAllBytes(_archive), Is.EqualTo(_fixture));
        }

        [Test]
        public void RejectsCorruptTableOnOpen()
        {
            _fixture[TocOffset + 4] ^= 1;
            File.WriteAllBytes(_archive, _fixture);

            Assert.That(() => NupArchive.Open(_archive),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("TOC SHA-256"));
        }

        [Test]
        public void RejectsTraversalNameWithValidTableHash()
        {
            int tocSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(_fixture.AsSpan(0x10));
            Span<byte> toc = _fixture.AsSpan(TocOffset, tocSize);
            int stringPool = (int)BinaryPrimitives.ReadUInt64LittleEndian(toc[0x18..]);
            int name = toc[stringPool..].IndexOf("main"u8);
            Assert.That(name, Is.GreaterThanOrEqualTo(0));
            "../x"u8.CopyTo(toc[(stringPool + name)..]);
            SHA256.HashData(toc).CopyTo(_fixture, TocHashOffset);
            File.WriteAllBytes(_archive, _fixture);

            Assert.That(() => NupArchive.Open(_archive),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe or duplicate"));
        }

        [Test]
        public void ReportsCorruptFileWhenRead()
        {
            int payload = _fixture.AsSpan().IndexOf("base-executable"u8);
            Assert.That(payload, Is.GreaterThan(TocOffset));
            _fixture[payload] ^= 1;
            File.WriteAllBytes(_archive, _fixture);

            using NupArchive package = NupArchive.Open(_archive);
            using IFileSystem exefs = package.OpenExeFs();
            using UniqueRef<IFile> file = new();
            exefs.OpenFile(ref file.Ref, "/main".ToU8Span(), OpenMode.Read).ThrowIfFailure();

            Assert.That(ResultFs.DataCorrupted.Includes(file.Get.Read(out _, 0, new byte[15])), Is.True);
            Assert.That(Directory.GetFileSystemEntries(_root), Is.EqualTo(new[] { _archive }));
        }

        private static byte[] Read(IFile file, long offset, int count)
        {
            byte[] bytes = new byte[count];
            file.Read(out long read, offset, bytes).ThrowIfFailure();
            Assert.That(read, Is.EqualTo(count));
            return bytes;
        }
    }
}

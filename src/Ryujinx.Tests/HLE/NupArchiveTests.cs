// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    public class NupArchiveTests
    {
        private const int TocOffset = 0x80;
        private const int TocHashOffset = 0x18;

        private string _root;
        private string _archive;
        private string _cache;
        private byte[] _fixture;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateTempSubdirectory("NupArchiveTests").FullName;
            _archive = Path.Combine(_root, "test.nup");
            _cache = Path.Combine(_root, "cache");

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
        public void ExtractsRawAndCompressedBaseContentAndCleansUpOnDispose()
        {
            File.WriteAllBytes(_archive, _fixture);

            using (NupArchive package = NupArchive.ExtractBase(_archive, _cache))
            {
                Assert.That(File.ReadAllText(Path.Combine(package.ExeFsPath, "main")), Is.EqualTo("base-executable"));
                byte[] data = File.ReadAllBytes(Path.Combine(package.RomFsPath, "data.bin"));
                Assert.That(Encoding.UTF8.GetString(data), Is.EqualTo(string.Concat(Enumerable.Repeat("compressed fixture", 4096))));
            }

            Assert.That(Directory.EnumerateFileSystemEntries(_cache), Is.Empty);
            Assert.That(File.ReadAllBytes(_archive), Is.EqualTo(_fixture));
        }

        [Test]
        public void RejectsCorruptTableBeforeExtracting()
        {
            _fixture[TocOffset + 4] ^= 1;
            File.WriteAllBytes(_archive, _fixture);

            Assert.That(() => NupArchive.ExtractBase(_archive, _cache),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("TOC SHA-256"));
            Assert.That(Directory.Exists(_cache), Is.False);
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

            Assert.That(() => NupArchive.ExtractBase(_archive, _cache),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsafe or duplicate"));
            Assert.That(Directory.Exists(_cache), Is.False);
        }

        [Test]
        public void RejectsCorruptFileAndRemovesPartialExtraction()
        {
            int payload = _fixture.AsSpan().IndexOf("base-executable"u8);
            Assert.That(payload, Is.GreaterThan(TocOffset));
            _fixture[payload] ^= 1;
            File.WriteAllBytes(_archive, _fixture);

            Assert.That(() => NupArchive.ExtractBase(_archive, _cache),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("SHA-256 mismatch"));
            Assert.That(Directory.EnumerateFileSystemEntries(_cache), Is.Empty);
        }
    }
}

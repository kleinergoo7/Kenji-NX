// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.Buffers.Binary;

namespace Ryujinx.Tests.HLE
{
    class Nx2TocTests
    {
        // Build only the part of a NUP TOC the package decision reads: magic, package
        // count, then one 0x40-byte package record. The ExeFS/RomFS dir indexes are
        // present so the base package "contains" main and romfs in the TOC's terms.
        private static byte[] MakeToc(params (byte kind, byte flag, uint version)[] packages)
        {
            byte[] toc = new byte[0x30 + packages.Length * 0x40];
            "TOC\u0001"u8.CopyTo(toc);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(4), (uint)packages.Length);

            for (int i = 0; i < packages.Length; i++)
            {
                int package = 0x30 + i * 0x40;
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(package + 0x0c), 0); // exefs dir
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(package + 0x10), 1); // romfs dir
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(package + 0x14), packages[i].version);
                toc[package + 0x18] = packages[i].kind;
                toc[package + 0x19] = packages[i].flag;
            }

            return toc;
        }

        [Test]
        public void SingleBasePackageIsReportedAsComplete()
        {
            string summary = NupTocInspector.DescribePackages(MakeToc((0, 0, 0)));

            Assert.That(summary, Does.StartWith("packages=1; base=1; standalone=0"));
            Assert.That(summary, Does.Contain("selected=base"));
            Assert.That(summary, Does.Contain("selectedStandalone=False"));
        }

        [Test]
        public void BaseAndStandaloneUpdateAreReportedDistinctly()
        {
            // Base wins even when a higher-version standalone update is present —
            // this mirrors NupArchive.SelectProgramPackage's decision.
            string summary = NupTocInspector.DescribePackages(MakeToc((0, 0, 0), (1, 1, 262144)));

            Assert.That(summary, Does.StartWith("packages=2; base=1; standalone=1"));
            Assert.That(summary, Does.Contain("selected=base"));
            Assert.That(summary, Does.Contain("selectedStandalone=False"));
        }
    }
}

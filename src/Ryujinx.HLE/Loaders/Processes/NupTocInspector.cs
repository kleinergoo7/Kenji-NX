// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;
using System.IO;

namespace Ryujinx.HLE.Loaders.Processes
{
    // Diagnostic view of the NUP package table. Mirrors NupArchive.SelectProgramPackage's
    // decision without changing NupArchive itself, so tools can describe a TOC it was
    // handed and tests can exercise that decision on a synthetic TOC in memory.
    internal static class NupTocInspector
    {
        private const int TocHeaderSize = 0x30;
        private const int PackageSize = 0x40;
        private const byte BasePackage = 0;
        private const byte UpdatePackage = 1;
        private const byte StandaloneUpdateFlag = 1;

        public static string DescribePackages(byte[] toc)
        {
            ArgumentNullException.ThrowIfNull(toc);

            uint packageCount = Read32(toc, 4);
            if (packageCount > (toc.Length < TocHeaderSize ? 0 : (toc.Length - TocHeaderSize) / PackageSize))
            {
                throw new InvalidDataException("Invalid NUP package table bounds.");
            }

            int baseCount = 0;
            int standaloneCount = 0;
            int basePackage = -1;
            int standaloneUpdate = -1;

            for (int i = 0; i < packageCount; i++)
            {
                int package = TocHeaderSize + i * PackageSize;
                byte kind = toc[package + 0x18];
                if (kind == BasePackage)
                {
                    baseCount++;
                    if (basePackage < 0)
                    {
                        basePackage = package;
                    }
                }
                else if (kind == UpdatePackage && toc[package + 0x19] == StandaloneUpdateFlag)
                {
                    standaloneCount++;
                    if (standaloneUpdate < 0 || Read32(toc, package + 0x14) > Read32(toc, standaloneUpdate + 0x14))
                    {
                        standaloneUpdate = package;
                    }
                }
            }

            // Same precedence as NupArchive: a base package wins over a standalone update.
            int selected = basePackage >= 0 ? basePackage : standaloneUpdate;
            bool selectedIsStandalone = selected >= 0 && toc[selected + 0x18] == UpdatePackage;
            string selectedKind = selected < 0 ? "none" : selectedIsStandalone ? "standalone-update" : "base";

            return $"packages={packageCount}; base={baseCount}; standalone={standaloneCount}; " +
                   $"selected={selectedKind}; selectedStandalone={selectedIsStandalone}";
        }

        private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }
}

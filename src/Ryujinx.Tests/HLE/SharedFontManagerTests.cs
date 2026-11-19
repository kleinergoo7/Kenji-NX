// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sdb.Pl;
using Ryujinx.HLE.HOS.Services.Sdb.Pl.Types;
using System.Buffers.Binary;
using System.IO;
using EmulatedHorizon = Ryujinx.HLE.HOS.Horizon;

namespace Ryujinx.Tests.HLE
{
    public class SharedFontManagerTests
    {
        [Test]
        public void BundledFallbackFontIsTrueTypeAndFitsSharedMemory()
        {
            byte[] font = SharedFontManager.ReadBundledFallbackFont();

            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(font), Is.EqualTo(0x00010000u));
            Assert.That((font.Length + 8) * (int)SharedFontType.Count, Is.LessThanOrEqualTo(EmulatedHorizon.FontSize));

            using Stream license = typeof(SharedFontManager).Assembly.GetManifestResourceStream(
                typeof(SharedFontManager), "Resources.DejaVuSans-LICENSE.txt");
            Assert.That(license?.Length, Is.GreaterThan(0));
        }
    }
}

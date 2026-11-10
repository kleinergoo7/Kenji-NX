// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Audio;
using System;
using System.Buffers.Binary;

namespace Ryujinx.Tests.HLE
{
    public class Nx2PcmRingTests
    {
        [TestCase(1u, 4u, 8u, 3u)]
        [TestCase(7u, 2u, 8u, 3u)]
        [TestCase(4u, 4u, 8u, 0u)]
        public void CountsFramesAcrossRingWrap(uint read, uint write, uint capacity, uint expected)
        {
            Assert.That(Nx2PcmRing.TryGetFilled(read, write, capacity, out uint filled), Is.True);
            Assert.That(filled, Is.EqualTo(expected));
        }

        [TestCase(8u, 0u, 8u)]
        [TestCase(0u, 8u, 8u)]
        [TestCase(0u, 0u, 0u)]
        public void RejectsInvalidSharedIndices(uint read, uint write, uint capacity)
        {
            Assert.That(Nx2PcmRing.TryGetFilled(read, write, capacity, out _), Is.False);
        }

        [Test]
        public void RequiresSpaceForTheSentinelFrame()
        {
            Assert.That(Nx2PcmRing.Fits(0x1040 + 16, 0x1040, 3, 2, 2), Is.True);
            Assert.That(Nx2PcmRing.Fits(0x1040 + 12, 0x1040, 3, 2, 2), Is.False);
        }

        [TestCase(0x4000UL, 0x2000UL, true)]
        [TestCase(0x4001UL, 0x2000UL, false)]
        [TestCase(0x4000UL, 0x1000UL, false)]
        [TestCase(0xffffffffffffe000UL, 0x2000UL, false)]
        public void ValidatesPageAlignedGuestRange(ulong address, ulong size, bool expected)
        {
            Assert.That(Nx2PcmRing.IsValidBuffer(address, size), Is.EqualTo(expected));
        }

        [Test]
        public void ConvertsFloatMonoToClippedStereo()
        {
            Span<byte> input = stackalloc byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(input, -2);
            BinaryPrimitives.WriteSingleLittleEndian(input[4..], 2);
            BinaryPrimitives.WriteSingleLittleEndian(input[8..], float.NaN);

            byte[] output = Nx2PcmRing.ToPcm16(input, 1, 4);
            short[] decoded = new short[6];
            for (int i = 0; i < decoded.Length; i++)
            {
                decoded[i] = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * sizeof(short)));
            }

            Assert.That(decoded, Is.EqualTo(new short[] { short.MinValue, short.MinValue, short.MaxValue, short.MaxValue, 0, 0 }));
        }

        [Test]
        public void PreservesPcm16Stereo()
        {
            byte[] input = [0x11, 0x22, 0x33, 0x44];
            Assert.That(Nx2PcmRing.ToPcm16(input, 2, 2), Is.EqualTo(input));
        }
    }
}

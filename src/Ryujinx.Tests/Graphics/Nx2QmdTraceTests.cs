// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2QmdTraceTests
    {
        private static ComputeQmd FromBytes(byte[] bytes)
        {
            return MemoryMarshal.Read<ComputeQmd>(bytes);
        }

        private static void SetBits(byte[] bytes, int lower, int upper, uint value)
        {
            for (int bit = lower; bit <= upper; bit++)
            {
                if (((value >> (bit - lower)) & 1u) != 0)
                {
                    bytes[bit >> 3] |= (byte)(1 << (bit & 7));
                }
            }
        }

        [Test]
        public void DescribeDecodesReleaseAndDataRam()
        {
            byte[] fixture = new byte[256];
            SetBits(fixture, 202, 202, 1);       // SemaphoreReleaseEnable0
            SetBits(fixture, 736, 767, 0x1234);  // Release0AddressLower
            SetBits(fixture, 800, 831, 7);       // Release0Payload

            string summary = Nx2QmdTrace.Describe(FromBytes(fixture), 0x27);

            Assert.That(summary, Does.Contain("rel0=0x1234:7"));
            Assert.That(summary, Does.Contain("dram27=0x27"));
        }

        [Test]
        public void DescribeShowsDisabledReleases()
        {
            byte[] fixture = new byte[256]; // both release enables false

            string summary = Nx2QmdTrace.Describe(FromBytes(fixture), 0);

            Assert.That(summary, Does.Contain("rel0=- rel1=-"));
        }
    }
}

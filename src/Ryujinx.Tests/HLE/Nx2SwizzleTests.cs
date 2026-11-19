using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System;

namespace Ryujinx.Tests.HLE
{
    class Nx2SwizzleTests
    {
        // Swizzle must be the exact inverse of the existing Unswizzle, otherwise a decoded
        // frame written into the presented image would be shifted or sliced.
        [TestCase(64, 16)]
        [TestCase(100, 40)]
        [TestCase(256, 144)]
        public void SwizzleRoundTripsUnswizzle(int width, int height)
        {
            uint[] preference = new uint[16];
            preference[2] = (uint)((width - 1) | ((height - 1) << 16));
            preference[4] = 7;
            preference[5] = (uint)((width * 4 + 63) / 64);

            (uint _, uint _, ulong pitch, uint blockHeightLog2, ulong size) = Nx2NativeObjects.Layout(preference);

            byte[] linear = new byte[width * height * 4];
            for (int i = 0; i < linear.Length; i++)
            {
                linear[i] = (byte)(i * 31 + 7);
            }

            byte[] swizzled = new byte[size];
            Nx2NativeObjects.Swizzle(linear, swizzled, width, height, pitch, (int)blockHeightLog2);

            byte[] result = new byte[linear.Length];
            Nx2NativeObjects.Unswizzle(swizzled, result, width, height, pitch, (int)blockHeightLog2);

            Assert.That(result, Is.EqualTo(linear));
        }
    }
}

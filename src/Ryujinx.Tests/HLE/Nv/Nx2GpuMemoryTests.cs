// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Tests.HLE.Nv
{
    [NonParallelizable]
    public class Nx2GpuMemoryTests
    {
        [Test]
        public void DestroyRemovesAliasesAndPreservesReplacementMappings()
        {
            const ulong original = 0x100000;
            const ulong replacement = 0x200000;
            const ulong alias = 0x300000;
            const ulong page = 0x1000;
            uint originalHandle = Nx2GpuMemory.Register(original, 3 * page);
            uint replacementHandle = Nx2GpuMemory.Register(replacement, page);

            try
            {
                Assert.IsTrue(Nx2GpuMemory.Map(alias, 3 * page, originalHandle, 0));
                Assert.IsTrue(Nx2GpuMemory.Map(alias + page, page, replacementHandle, 0));
                Assert.IsTrue(Nx2GpuMemory.Map(original, page, replacementHandle, 0));
                AssertMapping(alias, original);
                AssertMapping(alias + page, replacement);
                AssertMapping(alias + 2 * page, original + 2 * page);

                Assert.IsTrue(Nx2GpuMemory.Destroy(originalHandle));
                Assert.IsFalse(Nx2GpuMemory.TryResolveMapping(alias, out _));
                Assert.IsFalse(Nx2GpuMemory.TryResolveMapping(alias + 2 * page, out _));
                Assert.IsFalse(Nx2GpuMemory.TryResolveMapping(original + page, out _));
                AssertMapping(alias + page, replacement);
                AssertMapping(original, replacement);
            }
            finally
            {
                Nx2GpuMemory.Destroy(originalHandle);
                Nx2GpuMemory.Destroy(replacementHandle);
            }
        }

        private static void AssertMapping(ulong address, ulong expected)
        {
            Assert.IsTrue(Nx2GpuMemory.TryResolveMapping(address, out ulong actual));
            Assert.AreEqual(expected, actual);
        }
    }
}

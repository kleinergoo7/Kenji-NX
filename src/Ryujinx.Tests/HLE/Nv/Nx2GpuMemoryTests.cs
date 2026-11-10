// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Tests.HLE.Nv
{
    [NonParallelizable]
    public class Nx2GpuMemoryTests
    {
        [Test]
        public void OverlappingReplacementPreservesBothLiveSparseRegions()
        {
            const ulong first = 0x700000;
            const ulong second = 0x702000;
            const ulong page = 0x1000;

            try
            {
                Assert.IsTrue(Nx2GpuMemory.CreateSparse(first, page));
                Assert.IsTrue(Nx2GpuMemory.CreateSparse(second, page));
                Assert.IsFalse(Nx2GpuMemory.CreateSparse(first, 3 * page));
                Assert.IsTrue(Nx2GpuMemory.IsSparse(first));
                Assert.IsFalse(Nx2GpuMemory.IsSparse(first + page));
                Assert.IsTrue(Nx2GpuMemory.DestroySparse(first));
                Assert.IsTrue(Nx2GpuMemory.DestroySparse(second));
            }
            finally
            {
                Nx2GpuMemory.DestroySparse(first);
                Nx2GpuMemory.DestroySparse(second);
            }
        }

        [Test]
        public void SparseReplacementCanReuseItsReservationButCannotExceedBudget()
        {
            const ulong start = 0x1000000000;
            const ulong page = 0x1000;
            ulong end = start + Nx2GpuMemory.SparseBudget;

            try
            {
                Assert.IsTrue(Nx2GpuMemory.CreateSparse(start, Nx2GpuMemory.SparseBudget));
                Assert.IsFalse(Nx2GpuMemory.CreateSparse(end, page));
                Assert.IsFalse(Nx2GpuMemory.CreateSparse(start, Nx2GpuMemory.SparseBudget + page));
                Assert.IsTrue(Nx2GpuMemory.CreateSparse(start, Nx2GpuMemory.SparseBudget - page));
                Assert.IsTrue(Nx2GpuMemory.CreateSparse(end, page));
            }
            finally
            {
                Nx2GpuMemory.DestroySparse(start);
                Nx2GpuMemory.DestroySparse(end);
            }
        }

        [Test]
        public void RejectsOversizedMappingWithoutReplacingExistingAlias()
        {
            const ulong original = 0x400000;
            const ulong replacement = 0x500000;
            const ulong alias = 0x600000;
            const ulong page = 0x1000;
            uint originalHandle = Nx2GpuMemory.Register(original, page);
            uint replacementHandle = Nx2GpuMemory.Register(replacement, 2 * page);

            try
            {
                Assert.IsTrue(Nx2GpuMemory.Map(alias, page, originalHandle, 0));
                Assert.IsFalse(Nx2GpuMemory.Map(alias, 2 * page, replacementHandle, page));
                AssertMapping(alias, original);
                Assert.IsFalse(Nx2GpuMemory.TryResolveMapping(alias + page, out _));
                Assert.IsTrue(Nx2GpuMemory.Map(alias, page, replacementHandle, page));
                AssertMapping(alias, replacement + page);
            }
            finally
            {
                Nx2GpuMemory.Destroy(originalHandle);
                Nx2GpuMemory.Destroy(replacementHandle);
            }
        }

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

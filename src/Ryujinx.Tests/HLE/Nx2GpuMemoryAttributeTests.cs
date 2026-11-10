using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.Horizon.Common;

namespace Ryujinx.Tests.HLE
{
    public class Nx2GpuMemoryAttributeTests
    {
        private const ulong Page = KPageTableBase.PageSize;
        private const uint GpuAccessible = (uint)MemoryAttribute.GpuAccessible;

        private KMemoryBlockManager _blocks;

        [SetUp]
        public void SetUp()
        {
            _blocks = new KMemoryBlockManager();
            Assert.That(_blocks.Initialize(0, 8 * Page, new KMemoryBlockSlabManager(32)), Is.EqualTo(Result.Success));
            _blocks.InsertBlock(Page, 3, MemoryState.Heap, KMemoryPermission.ReadAndWrite);
            _blocks.InsertBlock(4 * Page, 2, MemoryState.Heap, KMemoryPermission.Read, MemoryAttribute.Uncached);
        }

        [Test]
        public void SetAndClearPreservesAdjacentPagesAndOtherAttributes()
        {
            Assert.That(_blocks.SetGpuMemoryAttribute(2 * Page, 3 * Page, GpuAccessible, GpuAccessible), Is.EqualTo(Result.Success));
            Assert.That(_blocks.FindBlock(Page).Attribute, Is.EqualTo(MemoryAttribute.None));
            Assert.That(_blocks.FindBlock(2 * Page).Attribute, Is.EqualTo(MemoryAttribute.GpuAccessible));

            KMemoryBlock uncached = _blocks.FindBlock(4 * Page);
            Assert.That(uncached.Attribute, Is.EqualTo(MemoryAttribute.Uncached | MemoryAttribute.GpuAccessible));
            Assert.That(uncached.Permission, Is.EqualTo(KMemoryPermission.Read));
            Assert.That(_blocks.FindBlock(5 * Page).Attribute, Is.EqualTo(MemoryAttribute.Uncached));

            Assert.That(_blocks.SetGpuMemoryAttribute(2 * Page, 3 * Page, GpuAccessible, 0), Is.EqualTo(Result.Success));
            Assert.That(_blocks.FindBlock(2 * Page).Attribute, Is.EqualTo(MemoryAttribute.None));
            Assert.That(_blocks.FindBlock(4 * Page).Attribute, Is.EqualTo(MemoryAttribute.Uncached));
            Assert.That(_blocks.BlocksCount, Is.EqualTo(4));
        }

        [TestCase(0UL, 1UL)]
        [TestCase(5UL, 2UL)]
        [TestCase(6UL, 1UL)]
        public void RejectsUnmappedPagesWithoutChangingMappedPages(ulong page, ulong count)
        {
            Assert.That(_blocks.SetGpuMemoryAttribute(page * Page, count * Page, GpuAccessible, GpuAccessible),
                Is.EqualTo(KernelResult.InvalidMemState));
            Assert.That(_blocks.FindBlock(5 * Page).Attribute, Is.EqualTo(MemoryAttribute.Uncached));
        }

        [Test]
        public void ValidatesArgumentsBeforeApplyingAttributes()
        {
            Assert.That(_blocks.SetGpuMemoryAttribute(Page + 1, Page, GpuAccessible, GpuAccessible), Is.EqualTo(KernelResult.InvalidAddress));
            Assert.That(_blocks.SetGpuMemoryAttribute(Page, 0, GpuAccessible, GpuAccessible), Is.EqualTo(KernelResult.InvalidSize));
            Assert.That(_blocks.SetGpuMemoryAttribute(Page, Page - 1, GpuAccessible, GpuAccessible), Is.EqualTo(KernelResult.InvalidSize));
            Assert.That(_blocks.SetGpuMemoryAttribute(Page, Page, 0, GpuAccessible), Is.EqualTo(KernelResult.InvalidCombination));
            Assert.That(_blocks.SetGpuMemoryAttribute(Page, Page, (uint)MemoryAttribute.Uncached, 0), Is.EqualTo(KernelResult.InvalidCombination));
            Assert.That(_blocks.SetGpuMemoryAttribute(8 * Page, Page, GpuAccessible, GpuAccessible), Is.EqualTo(KernelResult.InvalidMemState));
            Assert.That(_blocks.SetGpuMemoryAttribute(ulong.MaxValue - Page + 1, Page, GpuAccessible, GpuAccessible),
                Is.EqualTo(KernelResult.InvalidMemState));
            Assert.That(_blocks.FindBlock(Page).Attribute, Is.EqualTo(MemoryAttribute.None));
        }
    }
}

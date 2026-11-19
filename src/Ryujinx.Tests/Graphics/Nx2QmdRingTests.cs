// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2QmdRingTests
    {
        [SetUp]
        public void SetUp() => Nx2QmdRing.Reset();

        [Test]
        public void ScheduleWithBaseRecordsDispatchKeyedByQmdVa()
        {
            Nx2QmdRing ring = new();
            ring.SetBase(0x123400);
            ring.Schedule(0x2100, 3);

            Assert.That(ring.DispatchCount, Is.EqualTo(1));
        }

        [Test]
        public void DecrementUpdatesExistingEntryInsteadOfAddingOne()
        {
            Nx2QmdRing ring = new();
            ring.SetBase(0x123400);
            ring.Schedule(0x2100, 3);
            ring.Schedule(0x2100, 7); // PCAS action 7 (decrement): update, do not add.

            Assert.That(ring.DispatchCount, Is.EqualTo(1));
        }

        [Test]
        public void DescriptorWithRingBaseRendersRing()
        {
            QmdDescriptor descriptor = new() { RingBase = 0x123400 };

            Assert.That(descriptor.Describe(), Does.Contain("ring=0x123400"));
        }
    }
}

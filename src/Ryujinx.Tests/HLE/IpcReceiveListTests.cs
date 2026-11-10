// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Ipc;
using Ryujinx.Horizon.Common;

namespace Ryujinx.Tests.HLE
{
    public class IpcReceiveListTests
    {
        [Test]
        public void InlinePointersFollowIncomingMessageAndAlignSubsequentTransfers()
        {
            KServerSession.Message destination = new(0x1000, 0x100);
            KServerSession.PointerBufferDesc pointer = new(0x24UL << 16);
            uint offset = 0;

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, 1, 13, [], ref offset, out ulong address),
                Is.EqualTo(Result.Success));
            Assert.That(address, Is.EqualTo(0x1040UL));
            Assert.That(offset, Is.EqualTo(0x30u));

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, 1, 13, [], ref offset, out address),
                Is.EqualTo(Result.Success));
            Assert.That(address, Is.EqualTo(0x1070UL));
            Assert.That(offset, Is.EqualTo(0x60u));
        }

        [Test]
        public void InlinePointerRejectsInsufficientSpaceWithoutAdvancingOffset()
        {
            KServerSession.Message destination = new(0x1000, 0x100);
            KServerSession.PointerBufferDesc pointer = new(0x11UL << 16);
            uint offset = 0;

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, 1, 60, [], ref offset, out ulong address),
                Is.EqualTo(KernelResult.OutOfResource));
            Assert.That(address, Is.Zero);
            Assert.That(offset, Is.Zero);
        }

        [TestCase(2u)]
        [TestCase(3u)]
        public void ExternalReceiveBufferDoesNotDependOnIncomingMessageSize(uint mode)
        {
            KServerSession.Message destination = new(0x1000, 0x100);
            KServerSession.PointerBufferDesc pointer = new(0x20UL << 16);
            ulong[] receiveList = [(0x20UL << 48) | 0x2000UL];
            uint offset = 0;

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, mode, 64, receiveList, ref offset, out ulong address),
                Is.EqualTo(Result.Success));
            Assert.That(address, Is.EqualTo(0x2000UL));
        }
    }
}

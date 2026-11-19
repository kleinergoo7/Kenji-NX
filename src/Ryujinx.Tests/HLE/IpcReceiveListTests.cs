// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Ipc;
using Ryujinx.HLE.HOS.Services;
using Ryujinx.Horizon.Common;
using System;
using System.Buffers.Binary;

namespace Ryujinx.Tests.HLE
{
    public class IpcReceiveListTests
    {
        [Test]
        public void ManagedServerRearmsPointerBufferAfterTlsIsOverwritten()
        {
            const ulong PointerBufferAddress = 0x1234567000;
            const uint IncomingWords = 25;
            KServerSession.Message destination = new(0x1000, 0x100);
            KServerSession.PointerBufferDesc pointer = new(8UL << 16);
            uint offset = 0;
            byte[] tls = new byte[0x100];
            Array.Fill(tls, (byte)0xFF);
            tls.AsSpan(8, 8).Clear();

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, 2, IncomingWords,
                [BinaryPrimitives.ReadUInt64LittleEndian(tls.AsSpan(8))], ref offset, out _), Is.EqualTo(KernelResult.OutOfResource));

            ServerBase.WriteReceiveTemplate(tls, PointerBufferAddress);

            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(tls), Is.Zero);
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(tls.AsSpan(4));
            Assert.That(header, Is.EqualTo(2u << 10));
            ulong receiveEntry = BinaryPrimitives.ReadUInt64LittleEndian(tls.AsSpan(8));
            Assert.That(receiveEntry >> 48, Is.EqualTo(0x8000UL));
            Assert.That(tls[ServerBase.ReceiveTemplateSize], Is.EqualTo(0xFF));

            Assert.That(KServerSession.GetReceiveListAddress(pointer, destination, header >> 10, IncomingWords,
                [receiveEntry], ref offset, out ulong address), Is.EqualTo(Result.Success));
            Assert.That(address, Is.EqualTo(PointerBufferAddress));
            Assert.That(offset, Is.EqualTo(8u));
        }

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

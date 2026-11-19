// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    public class BsdClientRegistrationTests
    {
        private const ulong ClientPid = 0x1234;

        [Test]
        public void Switch2RegistrationAllowsDescriptorCreationWithoutTransferMemory()
        {
            IClient client = new(null, false, ConsoleGeneration.Switch2);
            using MemoryStream payload = new(new byte[16]);
            using BinaryReader reader = new(payload);
            using MemoryStream response = new();
            using BinaryWriter writer = new(response);
            IpcMessage request = new() { HandleDesc = new IpcHandleDesc([], [], ClientPid) };
            ServiceCtx context = new(null, null, null, null, request, new IpcMessage(), reader, writer);

            try
            {
                Assert.That(client.CmifCommands[36].Invoke(client, [context]), Is.EqualTo(ResultCode.Success));
                Assert.That(response.ToArray(), Is.EqualTo(new byte[4]));

                response.SetLength(0);
                Assert.That(client.EventFd(context), Is.EqualTo(ResultCode.Success));
                Assert.That(response.ToArray(), Is.EqualTo(new byte[8]));
                Assert.That(BsdContext.GetContext(ClientPid).RetrieveFileDescriptor(0), Is.Not.Null);
            }
            finally
            {
                BsdContext.DeleteContext(ClientPid);
            }
        }

        [TestCase(ConsoleGeneration.Switch2, false, 16, (uint)LinuxError.EINVAL)]
        [TestCase(ConsoleGeneration.Switch2, true, 15, (uint)LinuxError.EINVAL)]
        [TestCase(ConsoleGeneration.Switch1, true, 16, (uint)LinuxError.SUCCESS)]
        public void RegistrationReturnsStatusWithoutCreatingContext(
            ConsoleGeneration generation, bool sendPid, int payloadSize, uint expectedStatus)
        {
            IClient client = new(null, false, generation);
            using MemoryStream payload = new(new byte[payloadSize]);
            using BinaryReader reader = new(payload);
            using MemoryStream response = new();
            using BinaryWriter writer = new(response);
            IpcMessage request = new() { HandleDesc = sendPid ? new IpcHandleDesc([], [], ClientPid) : null };
            ServiceCtx context = new(null, null, null, null, request, new IpcMessage(), reader, writer);

            Assert.That(client.RegisterClientWithoutTransferMemory(context), Is.EqualTo(ResultCode.Success));
            using BinaryReader responseReader = new(response);
            response.Position = 0;
            Assert.That(response.Length, Is.EqualTo(sizeof(uint)));
            Assert.That(responseReader.ReadUInt32(), Is.EqualTo(expectedStatus));
            Assert.That(BsdContext.GetContext(ClientPid), Is.Null);
        }
    }
}

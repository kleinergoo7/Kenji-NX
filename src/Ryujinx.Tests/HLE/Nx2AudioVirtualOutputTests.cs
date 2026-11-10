// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services.Audio;
using System;
using System.Buffers.Binary;
using System.IO;
using AudioResultCode = Ryujinx.Audio.ResultCode;

namespace Ryujinx.Tests.HLE
{
    public class Nx2AudioVirtualOutputTests
    {
        [Test]
        public void RegistersDiscoveryCommandsAndHandlesMissingOutputBuffers()
        {
            Nx2AudioDeviceManager manager = new(null);
            Nx2AudioVirtualOutputSession session = new();
            Assert.That(manager.CmifCommands.Keys, Is.EquivalentTo(new[] { 0 }));
            Assert.That(session.CmifCommands.Keys, Is.EquivalentTo(new[] { 0, 100, 401, 700, 800 }));

            using MemoryStream response = new();
            using BinaryWriter writer = new(response);
            ServiceCtx context = new(null, null, null, null, new IpcMessage(), new IpcMessage(), null, writer);

            Assert.That(session.ListVirtualOutputDevices(context), Is.EqualTo(AudioResultCode.Success));
            Assert.That(response.ToArray(), Is.EqualTo(new byte[sizeof(uint)]));
            Assert.That(session.GetDefaultVirtualOutputDevice(context), Is.EqualTo(AudioResultCode.InvalidAddressInfo));
            Assert.That(session.GetVirtualOutputDeviceProperties(context), Is.EqualTo(AudioResultCode.InvalidAddressInfo));
        }

        [Test]
        public void ReturnsOneStereo48KhzDeviceWithTerminatedNames()
        {
            Span<byte> name = stackalloc byte[Nx2AudioVirtualOutputSession.NameSize];
            Span<byte> properties = stackalloc byte[Nx2AudioVirtualOutputSession.PropertiesSize];
            name.Fill(0xFF);
            properties.Fill(0xFF);

            Nx2AudioVirtualOutputSession.WriteOutputName(name);
            Assert.That(name[..20].ToArray(), Is.EqualTo("AudioVirtualOutput0\0"u8.ToArray()));
            Assert.That(Nx2AudioVirtualOutputSession.WriteOutputProperties(name, properties), Is.EqualTo(AudioResultCode.Success));
            Assert.That(properties[..0x100].ToArray(), Is.EqualTo(name.ToArray()));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(properties[0x108..]), Is.EqualTo(2u));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(properties[0x10C..]), Is.EqualTo(48000u));
            Assert.That(properties.Slice(0x110, 14).ToArray(), Is.EqualTo("AudioTvOutput\0"u8.ToArray()));
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(properties[0x218..]), Is.EqualTo(10_000_000UL));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(properties[0x220..]), Is.EqualTo(1u));
        }

        [Test]
        public void RejectsUnknownDeviceWithoutWritingProperties()
        {
            byte[] properties = new byte[Nx2AudioVirtualOutputSession.PropertiesSize];
            properties.AsSpan().Fill(0xFF);

            Assert.That(Nx2AudioVirtualOutputSession.WriteOutputProperties("unknown\0"u8, properties), Is.EqualTo(AudioResultCode.DeviceNotFound));
            Assert.That(properties, Is.All.EqualTo(0xFF));
        }
    }
}

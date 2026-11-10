// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;
using AudioResultCode = Ryujinx.Audio.ResultCode;

namespace Ryujinx.HLE.HOS.Services.Audio
{
    [Service("auddmg:u")]
    class Nx2AudioDeviceManager : IpcService
    {
        public Nx2AudioDeviceManager(ServiceCtx context) { }

        [CommandCmif(0)]
        public AudioResultCode OpenUserSession(ServiceCtx context)
        {
            if (context.Device.CompatibilityProfile.Generation != ConsoleGeneration.Switch2)
            {
                return AudioResultCode.UnsupportedOperation;
            }

            if (context.Request.HandleDesc is not { HasPId: true } ||
                context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 16)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            MakeObject(context, new Nx2AudioVirtualOutputSession(context.ClientProcessId));

            return AudioResultCode.Success;
        }
    }

    class Nx2AudioVirtualOutputSession : IpcService
    {
        internal const int NameSize = 0x100;
        internal const int PropertiesSize = 0x228;

        private static ReadOnlySpan<byte> OutputName => "AudioVirtualOutput0"u8;
        private readonly ulong _ownerPid;

        public Nx2AudioVirtualOutputSession(ulong ownerPid = 0)
        {
            _ownerPid = ownerPid;
        }

        [CommandCmif(0)]
        public AudioResultCode ListVirtualOutputDevices(ServiceCtx context)
        {
            (ulong address, ulong size) = context.Request.GetBufferType0x22();
            uint count = 0;
            if (size >= NameSize && address != 0)
            {
                Span<byte> name = stackalloc byte[NameSize];
                WriteOutputName(name);
                context.Memory.Write(address, name);
                count = 1;
            }

            context.ResponseData.Write(count);

            return AudioResultCode.Success;
        }

        [CommandCmif(100)]
        public AudioResultCode GetDefaultVirtualOutputDevice(ServiceCtx context)
        {
            (ulong address, ulong size) = context.Request.GetBufferType0x22();
            if (size < NameSize || address == 0)
            {
                return AudioResultCode.InvalidAddressInfo;
            }

            Span<byte> name = stackalloc byte[NameSize];
            WriteOutputName(name);
            context.Memory.Write(address, name);

            return AudioResultCode.Success;
        }

        [CommandCmif(401)]
        public AudioResultCode GetVirtualOutputDeviceProperties(ServiceCtx context)
        {
            (ulong nameAddress, ulong nameSize) = context.Request.GetBufferType0x21();
            (ulong outputAddress, ulong outputSize) = context.Request.GetBufferType0x22();
            if (nameAddress == 0 || nameSize < NameSize || outputAddress == 0 || outputSize < PropertiesSize)
            {
                return AudioResultCode.InvalidAddressInfo;
            }

            Span<byte> properties = stackalloc byte[PropertiesSize];
            AudioResultCode result = WriteOutputProperties(context.Memory.GetSpan(nameAddress, NameSize), properties);
            if (result == AudioResultCode.Success)
            {
                context.Memory.Write(outputAddress, properties);
            }

            return result;
        }

        internal static void WriteOutputName(Span<byte> name)
        {
            name.Clear();
            OutputName.CopyTo(name);
        }

        [CommandCmif(700)]
        public AudioResultCode AttachStream(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 8)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            uint id = context.RequestData.ReadUInt32();
            (ulong address, ulong size) = context.Request.GetBufferType0x21();
            if (address == 0 || size < NameSize || !IsOutputName(context.Memory.GetSpan(address, NameSize)))
            {
                return AudioResultCode.DeviceNotFound;
            }

            return Nx2SoundMixer.GetRegistry(context.Device).SetEngaged(_ownerPid, id, true);
        }

        [CommandCmif(800)]
        public AudioResultCode DetachStream(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < sizeof(uint))
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            return Nx2SoundMixer.GetRegistry(context.Device).SetEngaged(_ownerPid, context.RequestData.ReadUInt32(), false);
        }

        private static bool IsOutputName(ReadOnlySpan<byte> name) =>
            name.Length > OutputName.Length && name[OutputName.Length] == 0 && name.StartsWith(OutputName);

        internal static AudioResultCode WriteOutputProperties(ReadOnlySpan<byte> name, Span<byte> properties)
        {
            if (!IsOutputName(name))
            {
                return AudioResultCode.DeviceNotFound;
            }

            properties.Clear();
            OutputName.CopyTo(properties);
            BinaryPrimitives.WriteUInt32LittleEndian(properties[0x100..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(properties[0x104..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(properties[0x108..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(properties[0x10C..], 48000);
            "AudioTvOutput"u8.CopyTo(properties[0x110..]);
            BinaryPrimitives.WriteUInt64LittleEndian(properties[0x218..], 10_000_000);
            BinaryPrimitives.WriteUInt32LittleEndian(properties[0x220..], 1);

            return AudioResultCode.Success;
        }
    }
}

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

            MakeObject(context, new Nx2AudioVirtualOutputSession());

            return AudioResultCode.Success;
        }
    }

    class Nx2AudioVirtualOutputSession : IpcService
    {
        internal const int NameSize = 0x100;
        internal const int PropertiesSize = 0x228;

        private static ReadOnlySpan<byte> OutputName => "AudioVirtualOutput0"u8;

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

        internal static AudioResultCode WriteOutputProperties(ReadOnlySpan<byte> name, Span<byte> properties)
        {
            int terminator = name.IndexOf((byte)0);
            if (terminator < 0 || !name[..terminator].SequenceEqual(OutputName))
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

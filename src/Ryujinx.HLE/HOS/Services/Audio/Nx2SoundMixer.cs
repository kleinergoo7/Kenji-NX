// SPDX-License-Identifier: MIT
using Ryujinx.HLE.HOS.Kernel.Process;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using AudioResultCode = Ryujinx.Audio.ResultCode;

namespace Ryujinx.HLE.HOS.Services.Audio
{
    [Service("audsmx:u")]
    class Nx2SoundMixer : IpcService
    {
        private static readonly ConditionalWeakTable<Switch, Nx2SoundMixerRegistry> Registries = new();

        public Nx2SoundMixer(ServiceCtx context) { }

        internal static Nx2SoundMixerRegistry GetRegistry(Switch device) => Registries.GetOrCreateValue(device);

        [CommandCmif(0)]
        public AudioResultCode OpenUserSession(ServiceCtx context)
        {
            if (context.Request.HandleDesc is not { ToCopy: [int handle] } descriptor)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            KProcess owner = context.Process.HandleTable.GetKProcess(handle);
            try
            {
                if (context.Device.CompatibilityProfile.Generation != ConsoleGeneration.Switch2)
                {
                    return AudioResultCode.UnsupportedOperation;
                }

                if (!descriptor.HasPId || owner == null || owner.Pid != descriptor.PId ||
                    context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 16 ||
                    context.RequestData.ReadUInt32() != 1)
                {
                    return AudioResultCode.InvalidUpdateInfo;
                }

                Nx2SoundMixerRegistry registry = GetRegistry(context.Device);
                if (!registry.Clients.TryAdd(owner.Pid, 0))
                {
                    return AudioResultCode.InvalidExecutionContextOperation;
                }

                MakeObject(context, new Nx2SoundMixerSession(owner, registry));
                return AudioResultCode.Success;
            }
            finally
            {
                context.Process.HandleTable.CloseHandle(handle);
            }
        }
    }

    sealed class Nx2SoundMixerRegistry
    {
        private int _nextId;

        internal ConcurrentDictionary<ulong, byte> Clients { get; } = new();
        internal ConcurrentDictionary<uint, Nx2SoundMixerStream> Streams { get; } = new();

        internal uint AllocateId() => (uint)Interlocked.Increment(ref _nextId);

        internal AudioResultCode SetEngaged(ulong pid, uint id, bool engaged)
        {
            return Streams.TryGetValue(id, out Nx2SoundMixerStream stream) && stream.OwnerPid == pid
                ? stream.SetEngaged(engaged)
                : AudioResultCode.DeviceNotFound;
        }
    }

    sealed class Nx2SoundMixerSession : IpcService, IDisposable
    {
        private readonly KProcess _owner;
        private readonly Nx2SoundMixerRegistry _registry;
        private readonly HashSet<uint> _streams = [];
        private bool _disposed;

        public Nx2SoundMixerSession(KProcess owner, Nx2SoundMixerRegistry registry)
        {
            _owner = owner;
            _registry = registry;
            _owner.IncrementReferenceCount();
        }

        [CommandCmif(1)]
        public AudioResultCode CreateStream(ServiceCtx context)
        {
            if (_disposed || context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 32)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            context.RequestData.ReadUInt64(); // Stream kind and creation flags.
            context.RequestData.ReadUInt64(); // Reserved process ID field.
            ulong address = context.RequestData.ReadUInt64();
            ulong size = context.RequestData.ReadUInt64();

            if (!Nx2PcmRing.IsValidBuffer(address, size) || !_owner.CpuMemory.IsRangeMapped(address, size))
            {
                return AudioResultCode.InvalidAddressInfo;
            }

            uint id = _registry.AllocateId();
            Nx2SoundMixerStream stream = new(context.Device, context.Process.HandleTable, _owner, _registry, id, address, size);
            _registry.Streams[id] = stream;
            _streams.Add(id);

            MakeObject(context, stream);
            context.ResponseData.Write(id);
            return AudioResultCode.Success;
        }

        [CommandCmif(2)]
        public AudioResultCode EngageStreams(ServiceCtx context) => SetEngaged(context, true);

        [CommandCmif(3)]
        public AudioResultCode DisengageStreams(ServiceCtx context) => SetEngaged(context, false);

        private AudioResultCode SetEngaged(ServiceCtx context, bool engaged)
        {
            (ulong address, ulong size) = context.Request.GetBufferType0x21();
            if (size > (ulong)_streams.Count * sizeof(uint) || size % sizeof(uint) != 0)
            {
                return AudioResultCode.InvalidAddressInfo;
            }

            if (size == 0)
            {
                return AudioResultCode.Success;
            }

            if (address == 0)
            {
                return AudioResultCode.InvalidAddressInfo;
            }

            ReadOnlySpan<byte> ids = context.Memory.GetSpan(address, (int)size);
            for (int offset = 0; offset < ids.Length; offset += sizeof(uint))
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(ids[offset..]);
                AudioResultCode result = _registry.SetEngaged(_owner.Pid, id, engaged);
                if (result != AudioResultCode.Success)
                {
                    return result;
                }
            }

            return AudioResultCode.Success;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (uint id in _streams)
            {
                if (_registry.Streams.TryGetValue(id, out Nx2SoundMixerStream stream))
                {
                    stream.Dispose();
                }
            }

            _registry.Clients.TryRemove(_owner.Pid, out _);
            _owner.DecrementReferenceCount();
        }
    }
}

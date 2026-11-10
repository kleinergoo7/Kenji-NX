// SPDX-License-Identifier: MIT
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using AudioResultCode = Ryujinx.Audio.ResultCode;

namespace Ryujinx.HLE.HOS.Services.Audio
{
    sealed class Nx2SoundMixerStream : IpcService, IDisposable
    {
        private const int PollMilliseconds = 5;
        private const int SampleRate = 48000;
        private const int MaxQueuedBuffers = 8;
        private readonly object _gate = new();
        private readonly Switch _device;
        private readonly KHandleTable _handles;
        private readonly KProcess _owner;
        private readonly Nx2SoundMixerRegistry _registry;
        private readonly uint _id;
        private readonly ulong _address;
        private readonly ulong _size;
        private readonly KEvent _stateEvent;
        private readonly KEvent _dataEvent;
        private readonly Queue<AudioBuffer> _pending = new();

        private Timer _timer;
        private HardwareDeviceSessionOutputBase _output;
        private uint _capacity;
        private uint _channels;
        private int _sampleBytes;
        private ulong _dataAddress;
        private int _dataThreshold;
        private uint _dataMode;
        private bool _engaged;
        private bool _disposed;
        private float _gain = 1;
        private ulong _nextBuffer;
        private int _stateHandle;
        private int _dataHandle;
        private int _timerGeneration;

        public ulong OwnerPid => _owner.Pid;

        public Nx2SoundMixerStream(Switch device, KHandleTable handles, KProcess owner, Nx2SoundMixerRegistry registry,
            uint id, ulong address, ulong size)
        {
            _device = device;
            _handles = handles;
            _owner = owner;
            _registry = registry;
            _id = id;
            _address = address;
            _size = size;
            _stateEvent = new KEvent(device.System.KernelContext);
            _dataEvent = new KEvent(device.System.KernelContext);
            owner.IncrementReferenceCount();
        }

        [CommandCmif(0)]
        public AudioResultCode OpenStream(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 24)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            uint format = context.RequestData.ReadUInt32();
            uint channels = context.RequestData.ReadUInt32();
            uint rate = context.RequestData.ReadUInt32();
            uint frames = context.RequestData.ReadUInt32();
            context.RequestData.ReadUInt32(); // Prefill.
            uint flags = context.RequestData.ReadUInt32();

            if (rate != SampleRate)
            {
                return AudioResultCode.UnsupportedSampleRate;
            }

            if (channels is not (1 or 2 or 6))
            {
                return AudioResultCode.UnsupportedChannelConfiguration;
            }

            int sampleBytes = format switch
            {
                2 => 2,
                5 => 4,
                _ => 0,
            };

            ulong offset = 0x1040UL + ((flags & 1) != 0 ? 0x80UL : 0);
            if (sampleBytes == 0 || !Nx2PcmRing.Fits(_size, offset, frames, channels, sampleBytes))
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            lock (_gate)
            {
                if (_disposed || _output != null)
                {
                    return AudioResultCode.InvalidExecutionContextOperation;
                }

                if (!OpenOutput(channels))
                {
                    return AudioResultCode.UnsupportedOperation;
                }

                _capacity = frames + 1;
                _channels = channels;
                _sampleBytes = sampleBytes;
                _dataAddress = _address + offset;
                _timer = new Timer(Poll, ++_timerGeneration, PollMilliseconds, PollMilliseconds);
                _stateEvent.ReadableEvent.Signal();
            }

            return AudioResultCode.Success;
        }

        private bool OpenOutput(uint channels)
        {
            IHardwareDeviceSession session = _device.AudioDeviceDriver.OpenDeviceSession(
                IHardwareDeviceDriver.Direction.Output, _owner.CpuMemory, SampleFormat.PcmInt16, SampleRate, channels == 1 ? 2 : channels);
            if (session is not HardwareDeviceSessionOutputBase output)
            {
                session?.Dispose();
                return false;
            }

            _output = output;
            _output.SetVolume(_gain);
            return true;
        }

        [CommandCmif(1)]
        public AudioResultCode CloseStream(ServiceCtx context)
        {
            lock (_gate)
            {
                CloseOutput();
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(2)]
        public AudioResultCode GetStreamState(ServiceCtx context)
        {
            lock (_gate)
            {
                context.ResponseData.Write(_output == null ? 0u : _engaged ? 2u : 1u);
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(3)]
        public AudioResultCode GetStreamGain(ServiceCtx context)
        {
            lock (_gate)
            {
                context.ResponseData.Write(_gain);
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(4)]
        public AudioResultCode SetStreamGain(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 16)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            float gain = context.RequestData.ReadSingle();
            lock (_gate)
            {
                _gain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 0f;
                _output?.SetVolume(_gain);
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(5)]
        public AudioResultCode AcquireStateEvent(ServiceCtx context)
        {
            lock (_gate)
            {
                return CopyEvent(context, _stateEvent, ref _stateHandle);
            }
        }

        [CommandCmif(6)]
        public AudioResultCode ReleaseStateEvent(ServiceCtx context)
        {
            lock (_gate)
            {
                ReleaseEvent(ref _stateHandle);
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(7)]
        public AudioResultCode AcquireDataEvent(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < 8)
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            lock (_gate)
            {
                _dataMode = context.RequestData.ReadUInt32();
                _dataThreshold = context.RequestData.ReadInt32();
                return CopyEvent(context, _dataEvent, ref _dataHandle);
            }
        }

        [CommandCmif(8)]
        public AudioResultCode ReleaseDataEvent(ServiceCtx context)
        {
            lock (_gate)
            {
                _dataMode = 0;
                ReleaseEvent(ref _dataHandle);
                _dataEvent.ReadableEvent.Clear();
            }

            return AudioResultCode.Success;
        }

        [CommandCmif(9)]
        [CommandCmif(10)]
        [CommandCmif(12)]
        public AudioResultCode UnsupportedStreamCommand(ServiceCtx context) => AudioResultCode.UnsupportedOperation;

        [CommandCmif(11)]
        public AudioResultCode PurgeStream(ServiceCtx context)
        {
            if (context.RequestData.BaseStream.Length - context.RequestData.BaseStream.Position < sizeof(int))
            {
                return AudioResultCode.InvalidUpdateInfo;
            }

            int requested = context.RequestData.ReadInt32();
            lock (_gate)
            {
                if (_output == null)
                {
                    return AudioResultCode.InvalidExecutionContextOperation;
                }

                try
                {
                    if (!TryReadIndices(out uint read, out uint filled))
                    {
                        return AudioResultCode.InvalidUpdateInfo;
                    }

                    uint purged = Math.Min(filled, (uint)Math.Max(0, requested));
                    _owner.CpuMemory.Write(_address + Nx2PcmRing.ReadOffset, (read + purged) % _capacity);
                    context.ResponseData.Write(purged);
                    return AudioResultCode.Success;
                }
                catch (InvalidMemoryRegionException)
                {
                    return AudioResultCode.InvalidAddressInfo;
                }
            }
        }

        internal AudioResultCode SetEngaged(bool engaged)
        {
            lock (_gate)
            {
                if (_disposed || _output == null)
                {
                    return AudioResultCode.InvalidExecutionContextOperation;
                }

                if (_engaged != engaged)
                {
                    _engaged = engaged;
                    if (!engaged)
                    {
                        _output.Stop();
                    }
                    else if (_pending.Count != 0)
                    {
                        _output.Start();
                    }

                    _stateEvent.ReadableEvent.Signal();
                }

                return AudioResultCode.Success;
            }
        }

        private AudioResultCode CopyEvent(ServiceCtx context, KEvent evt, ref int handle)
        {
            if (_disposed)
            {
                return AudioResultCode.InvalidExecutionContextOperation;
            }

            if (handle == 0 && _handles.GenerateHandle(evt.ReadableEvent, out handle) != Result.Success)
            {
                return AudioResultCode.OperationFailed;
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);
            return AudioResultCode.Success;
        }

        private void ReleaseEvent(ref int handle)
        {
            if (handle != 0)
            {
                _handles.CloseHandle(handle);
                handle = 0;
            }
        }

        private bool TryReadIndices(out uint read, out uint filled)
        {
            IVirtualMemoryManager memory = _owner.CpuMemory;
            read = memory.Read<uint>(_address + Nx2PcmRing.ReadOffset);
            uint write = memory.Read<uint>(_address + Nx2PcmRing.WriteOffset);
            return Nx2PcmRing.TryGetFilled(read, write, _capacity, out filled);
        }

        private void Poll(object generation)
        {
            if (!Monitor.TryEnter(_gate))
            {
                return;
            }

            try
            {
                if (_output == null || (int)generation != _timerGeneration ||
                    _device.AudioDeviceDriver.GetPauseEvent()?.WaitOne(0) == false)
                {
                    return;
                }

                try
                {
                    if (!TryReadIndices(out uint read, out uint filled))
                    {
                        return;
                    }

                    while (_pending.TryPeek(out AudioBuffer played) && _output.WasBufferFullyConsumed(played))
                    {
                        _pending.Dequeue();
                        _output.UnregisterBuffer(played);
                    }

                    if (_engaged && filled != 0 && _pending.Count < MaxQueuedBuffers)
                    {
                        uint take = Math.Min(filled, SampleRate * PollMilliseconds / 1000);
                        if (QueueSamples(read, take))
                        {
                            _owner.CpuMemory.Write(_address + Nx2PcmRing.ReadOffset, (read + take) % _capacity);
                            filled -= take;
                        }
                    }

                    if (_dataMode != 0 && (long)_capacity - 1 - filled >= _dataThreshold)
                    {
                        _dataEvent.ReadableEvent.Signal();
                    }
                }
                catch (InvalidMemoryRegionException)
                {
                    Logger.Warning?.Print(LogClass.ServiceAudio, $"audsmx:u stream {_id} lost its PCM ring at 0x{_address:X}.");
                    CloseOutput();
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private bool QueueSamples(uint read, uint frames)
        {
            int stride = (int)_channels * _sampleBytes;
            byte[] raw = new byte[frames * stride];
            int first = (int)Math.Min(frames, _capacity - read) * stride;
            _owner.CpuMemory.Read(_dataAddress + read * (ulong)stride, raw.AsSpan(0, first));
            if (first < raw.Length)
            {
                _owner.CpuMemory.Read(_dataAddress, raw.AsSpan(first));
            }

            byte[] samples = Nx2PcmRing.ToPcm16(raw, (int)_channels, _sampleBytes);
            ulong tag = ++_nextBuffer;
            AudioBuffer buffer = new() { BufferTag = tag, DataPointer = tag, DataSize = (ulong)samples.Length };
            if (!_output.RegisterBuffer(buffer, samples))
            {
                return false;
            }

            _output.QueueBuffer(buffer);
            _pending.Enqueue(buffer);
            _output.Start();
            return true;
        }

        private void CloseOutput()
        {
            _timer?.Dispose();
            _timer = null;
            _engaged = false;
            if (_output != null)
            {
                _output.PrepareToClose();
                _output.Stop();
                foreach (AudioBuffer buffer in _pending)
                {
                    _output.UnregisterBuffer(buffer);
                }

                _pending.Clear();
                _output.Dispose();
                _output = null;
                _stateEvent.ReadableEvent.Signal();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _registry.Streams.TryRemove(_id, out _);
                CloseOutput();
                ReleaseEvent(ref _stateHandle);
                ReleaseEvent(ref _dataHandle);
                _owner.DecrementReferenceCount();
            }
        }
    }

    static class Nx2PcmRing
    {
        public const ulong ReadOffset = 0x44;
        public const ulong WriteOffset = 0x1004;

        public static bool IsValidBuffer(ulong address, ulong size) =>
            address != 0 && (address & 0xFFF) == 0 && (size & 0xFFF) == 0 &&
            size is >= 0x2000 and <= 0x1000000 && address <= ulong.MaxValue - size;

        public static bool Fits(ulong size, ulong offset, uint frames, uint channels, int sampleBytes) =>
            frames is > 0 and <= 48000 && offset + (frames + 1UL) * channels * (ulong)sampleBytes <= size;

        public static bool TryGetFilled(uint read, uint write, uint capacity, out uint filled)
        {
            filled = 0;
            if (read >= capacity || write >= capacity)
            {
                return false;
            }

            filled = (write + capacity - read) % capacity;
            return true;
        }

        public static byte[] ToPcm16(ReadOnlySpan<byte> input, int channels, int sampleBytes)
        {
            if (sampleBytes == 2 && channels != 1)
            {
                return input.ToArray();
            }

            int samples = input.Length / sampleBytes;
            int copies = channels == 1 ? 2 : 1;
            byte[] output = new byte[samples * copies * sizeof(short)];
            for (int i = 0; i < samples; i++)
            {
                ReadOnlySpan<byte> source = input[(i * sampleBytes)..];
                short value;
                if (sampleBytes == 4)
                {
                    float sample = BinaryPrimitives.ReadSingleLittleEndian(source);
                    value = float.IsFinite(sample) ? (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue) : (short)0;
                }
                else
                {
                    value = BinaryPrimitives.ReadInt16LittleEndian(source);
                }

                for (int copy = 0; copy < copies; copy++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((i * copies + copy) * sizeof(short)), value);
                }
            }

            return output;
        }
    }
}

using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using Ryujinx.Graphics.Gpu.Engine.Dma;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Twod;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO command processor.
    /// </summary>
    class GPFifoProcessor : IDisposable
    {
        private const int MacrosCount = 0x80;
        private const int MacroIndexMask = MacrosCount - 1;

        private const int LoadInlineDataMethodOffset = 0x6d;
        private const int UniformBufferUpdateDataMethodOffset = 0x8e4;

        // The GPFIFO has 8 subchannels; Switch 2 titles use the last ones too, and a bound
        // subchannel outside this range would drop every method sent to it.
        private const int SubChannelCount = 8;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;

        // Engine each subchannel is bound to, for Switch 2 streams. Ampere binds object classes
        // rather than the Maxwell engine IDs; values are indices into the dispatch switch below.
        private const int Engine3D = 0;
        private const int EngineCompute = 1;
        private const int EngineInlineToMemory = 2;
        private const int Engine2D = 3;
        private const int EngineDma = 4;
        private const int EngineFifo = 5;

        private readonly int[] _subChannelEngine = new int[SubChannelCount];

        // Object classes the nx2 binder has not seen before, logged once each.
        private readonly System.Collections.Generic.HashSet<int> _unknownClasses = [];

        private sealed class StallException : System.Exception;

        // A channel suspended on an unmet semaphore acquire (Switch 2 NVN2 gates command
        // buffers this way). The remaining words are re-read fresh from guest memory on resume.
        private ulong _stallAddress;
        private uint _stallValue;
        private int _stallOperation;
        private ulong _stallBaseVa;
        private int _stallWordCount;
        private int _stallIndex;
        private bool _stalled;
        private long _stallSince;
        private bool _stallReported;

        private static readonly System.Collections.Generic.List<GPFifoProcessor> _stalledProcessors = [];
        private static bool _resuming;

        private readonly System.Collections.Generic.Queue<(ulong Address, int Count)> _stalledQueue = new();

        /// <summary>
        /// Suspends this channel on an unmet semaphore acquire, aborting the current buffer.
        /// The remaining words are reprocessed from guest memory once the value is met.
        /// </summary>
        /// <param name="address">Semaphore address</param>
        /// <param name="value">Required value</param>
        /// <param name="operation">Acquire operation id</param>
        public void StallOnAcquire(ulong address, int value, int operation)
        {
            ulong baseVa = _resumeBaseVa;
            int count = _resumeWordCount;

            _stallAddress = address;
            _stallValue = (uint)value;
            _stallOperation = operation;
            _stallBaseVa = baseVa;
            _stallWordCount = count;
            _stallIndex = _resumeIndex;
            _stallSince = System.Environment.TickCount64;
            _stallReported = false;

            lock (_stalledProcessors)
            {
                if (!_stalledProcessors.Contains(this))
                {
                    _stalledProcessors.Add(this);
                }
            }

            _stalled = true;

            throw new StallException();
        }

        /// <summary>True when this channel is suspended on an acquire that is still unmet.</summary>
        public bool HasUnmetStall
        {
            get
            {
                if (!_stalled)
                {
                    return false;
                }

                return !AcquireNowMatches();
            }
        }

        private bool AcquireNowMatches()
        {
            int current = MemoryManager.Read<int>(_stallAddress);

            return _stallOperation switch
            {
                1 => current == (int)_stallValue,
                4 => (uint)current >= _stallValue,
                8 => (current & (int)_stallValue) != 0,
                _ => true,
            };
        }

        private void ReportStall()
        {
            if (_stallReported || System.Environment.TickCount64 - _stallSince < 2000)
            {
                return;
            }

            _stallReported = true;

            int current = MemoryManager.Read<int>(_stallAddress);

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"Switch 2 channel waits on semaphore 0x{_stallAddress:X} op {_stallOperation} value {_stallValue} (now {current})");
        }

        /// <summary>
        /// Reprocesses a suspended buffer whose acquire is now met. Called from release writes
        /// and around dispatch; re-reads the remaining words fresh from guest memory.
        /// </summary>
        public static void TryResumeAll()
        {
            GPFifoProcessor[] stalled;

            lock (_stalledProcessors)
            {
                if (_stalledProcessors.Count == 0 || _resuming)
                {
                    return;
                }

                _resuming = true;
                stalled = [.. _stalledProcessors];
            }

            try
            {
                foreach (GPFifoProcessor processor in stalled)
                {
                    processor.TryResume();
                }
            }
            finally
            {
                lock (_stalledProcessors)
                {
                    _resuming = false;
                }
            }
        }

        private void TryResume()
        {
            if (!_stalled)
            {
                lock (_stalledProcessors)
                {
                    _stalledProcessors.Remove(this);
                }

                return;
            }

            if (!AcquireNowMatches())
            {
                ReportStall();

                return;
            }

            _stalled = false;

            lock (_stalledProcessors)
            {
                _stalledProcessors.Remove(this);
            }

            ProcessFresh(_stallBaseVa, _stallWordCount, _stallIndex);

            // The stall is resolved: queued buffers behind it run in order. A nested stall
            // re-registers this processor and leaves the rest queued.
            while (!_stalled && _stalledQueue.Count != 0)
            {
                (ulong address, int count) = _stalledQueue.Dequeue();

                ProcessFresh(address, count, 0);
            }
        }

        private void ProcessFresh(ulong baseVa, int count, int startIndex)
        {
            int[] words = new int[count];

            for (int i = 0; i < words.Length; i++)
            {
                words[i] = MemoryManager.Read<int>(baseVa + (ulong)i * 4);
            }

            ProcessFrom(baseVa, words, startIndex);
        }

        /// <summary>
        /// Queues a buffer behind a suspended one; it runs once the acquire is met.
        /// </summary>
        /// <param name="entryAddress">Buffer GPU address</param>
        /// <param name="wordCount">Buffer length in words</param>
        public bool QueueStalled(ulong entryAddress, int wordCount)
        {
            if (_stalledQueue.Count >= 512)
            {
                return false;
            }

            _stalledQueue.Enqueue((entryAddress, wordCount));

            return true;
        }

        /// <summary>
        /// Channel memory manager.
        /// </summary>
        public MemoryManager MemoryManager => _channel.MemoryManager;

        /// <summary>
        /// Channel texture manager.
        /// </summary>
        public TextureManager TextureManager => _channel.TextureManager;

        /// <summary>
        /// Per-channel MME data RAM, used by the Switch 2 (Turing+) macro engine.
        /// </summary>
        public int[] MmeDataRam => _fifoClass.MmeDataRam;

        /// <summary>
        /// Counter that changes when macro code is uploaded on this channel.
        /// </summary>
        public long MmeCodeGeneration => _fifoClass.MmeCodeGeneration;

        /// <summary>
        /// Counter that changes when compute macro code is uploaded on this channel.
        /// </summary>
        public long ComputeMmeCodeGeneration => _fifoClass.ComputeMmeCodeGeneration;

        /// <summary>
        /// 3D Engine.
        /// </summary>
        public ThreedClass ThreedClass => _3dClass;

        /// <summary>
        /// Internal GPFIFO state.
        /// </summary>
        private struct DmaState
        {
            public int Method;
            public int SubChannel;
            public int MethodCount;
            public bool NonIncrementing;
            public bool IncrementOnce;
        }

        private DmaState _state;

        private readonly ThreedClass _3dClass;
        private readonly ComputeClass _computeClass;
        private readonly InlineToMemoryClass _i2mClass;
        private readonly TwodClass _2dClass;
        private readonly DmaClass _dmaClass;

        private readonly GPFifoClass _fifoClass;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO command processor.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">Channel that the GPFIFO processor belongs to</param>
        public GPFifoProcessor(GpuContext context, GpuChannel channel)
        {
            _context = context;
            _channel = channel;

            for (int index = 0; index < _subChannelEngine.Length; index++)
            {
                _subChannelEngine[index] = index;
            }

            _fifoClass = new GPFifoClass(context, this);
            _3dClass = new ThreedClass(context, channel, _fifoClass);
            _computeClass = new ComputeClass(context, channel, _3dClass, _fifoClass);
            _i2mClass = new InlineToMemoryClass(context, channel);
            _2dClass = new TwodClass(channel);
            _dmaClass = new DmaClass(context, channel, _3dClass);
        }

        /// <summary>
        /// Processes a command buffer.
        /// </summary>
        /// <param name="baseGpuVa">Base GPU virtual address of the command buffer</param>
        /// <param name="commandBuffer">Command buffer</param>
        public void Process(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer)
        {
            _resumeBaseVa = baseGpuVa;
            _resumeWordCount = commandBuffer.Length;

            ProcessFrom(baseGpuVa, commandBuffer, 0);
        }

        private ulong _resumeBaseVa;
        private int _resumeWordCount;
        private int _resumeIndex;

        private void ProcessFrom(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer, int startIndex)
        {
            try
            {
                ProcessLoop(baseGpuVa, commandBuffer, startIndex);
            }
            catch (StallException)
            {
                // StallOnAcquire recorded the resume point; the remaining words stay queued
                // until a release write (or the next dispatch) resumes them.
            }

            _3dClass.FlushUboDirty();
        }

        private void ProcessLoop(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer, int startIndex)
        {
            for (int index = startIndex; index < commandBuffer.Length; index++)
            {
                _resumeIndex = index;
                int command = commandBuffer[index];

                ulong gpuVa = baseGpuVa + (ulong)index * 4;

                if (_state.MethodCount != 0)
                {
                    if (TryFastI2mBufferUpdate(commandBuffer, ref index))
                    {
                        continue;
                    }

                    Send(gpuVa, _state.Method, command, _state.SubChannel, _state.MethodCount <= 1);

                    if (!_state.NonIncrementing)
                    {
                        _state.Method++;
                    }

                    if (_state.IncrementOnce)
                    {
                        _state.NonIncrementing = true;
                    }

                    _state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    if (TryFastUniformBufferUpdate(meth, commandBuffer, index))
                    {
                        index += meth.MethodCount;
                        continue;
                    }

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            _state.Method = meth.MethodAddress;
                            _state.SubChannel = meth.MethodSubchannel;
                            _state.MethodCount = meth.MethodCount;
                            _state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            _state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            Send(gpuVa, meth.MethodAddress, meth.ImmdData, meth.MethodSubchannel, true);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Tries to perform a fast Inline-to-Memory data update.
        /// If successful, all data will be copied at once, and <see cref="DmaState.MethodCount"/>
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="commandBuffer">Command buffer where the data is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where the data is located, auto-incremented on success</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastI2mBufferUpdate(ReadOnlySpan<int> commandBuffer, ref int offset)
        {
            if (_state.Method == LoadInlineDataMethodOffset && _state.NonIncrementing && _state.SubChannel <= 2)
            {
                int availableCount = commandBuffer.Length - offset;
                int consumeCount = Math.Min(_state.MethodCount, availableCount);

                ReadOnlySpan<int> data = commandBuffer.Slice(offset, consumeCount);

                if (_state.SubChannel == 0)
                {
                    _3dClass.LoadInlineData(data);
                }
                else if (_state.SubChannel == 1)
                {
                    _computeClass.LoadInlineData(data);
                }
                else /* if (_state.SubChannel == 2) */
                {
                    _i2mClass.LoadInlineData(data);
                }

                offset += consumeCount - 1;
                _state.MethodCount -= consumeCount;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to perform a fast constant buffer data update.
        /// If successful, all data will be copied at once, and <see cref="CompressedMethod.MethodCount"/> + 1
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="meth">Compressed method to be checked</param>
        /// <param name="commandBuffer">Command buffer where <paramref name="meth"/> is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where <paramref name="meth"/> is located</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastUniformBufferUpdate(CompressedMethod meth, ReadOnlySpan<int> commandBuffer, int offset)
        {
            int availableCount = commandBuffer.Length - offset;

            if (meth.MethodAddress == UniformBufferUpdateDataMethodOffset &&
                meth.MethodCount < availableCount &&
                meth.SecOp == SecOp.NonIncMethod)
            {
                _3dClass.ConstantBufferUpdate(commandBuffer.Slice(offset + 1, meth.MethodCount));

                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends a uncompressed method for processing by the graphics pipeline.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="meth">Method to be processed</param>
        private void Send(ulong gpuVa, int offset, int argument, int subChannel, bool isLastCall)
        {
            if (offset == 0)
            {
                BindObject(subChannel, argument);
            }

            if (offset < 0x60)
            {
                _fifoClass.Write(offset * 4, argument);
            }
            else if (offset < 0xe00)
            {
                offset *= 4;

                switch (SubChannelEngine(subChannel))
                {
                    case Engine3D:
                        _3dClass.Write(offset, argument);
                        break;
                    case EngineCompute:
                        _computeClass.Write(offset, argument);
                        break;
                    case EngineInlineToMemory:
                        _i2mClass.Write(offset, argument);
                        break;
                    case Engine2D:
                        _2dClass.Write(offset, argument);
                        break;
                    case EngineDma:
                        _dmaClass.Write(offset, argument);
                        break;
                    case EngineFifo:
                        _fifoClass.Write(offset, argument);
                        break;
                }
            }
            else
            {
                int engine = SubChannelEngine(subChannel);

                IDeviceState state = engine switch
                {
                    Engine3D => _3dClass,
                    EngineCompute => _context.IsNx2Ampere ? _computeClass : null,
                    Engine2D => _2dClass,
                    _ => null,
                };

                if (state != null)
                {
                    int macroIndex = (offset >> 1) & MacroIndexMask;
                    bool compute = engine == EngineCompute;

                    if ((offset & 1) != 0)
                    {
                        if (compute)
                        {
                            _fifoClass.ComputeMmePushArgument(macroIndex, gpuVa, argument);
                        }
                        else
                        {
                            _fifoClass.MmePushArgument(macroIndex, gpuVa, argument);
                        }
                    }
                    else
                    {
                        if (compute)
                        {
                            _fifoClass.ComputeMmeStart(macroIndex, argument);
                        }
                        else
                        {
                            _fifoClass.MmeStart(macroIndex, argument);
                        }
                    }

                    if (isLastCall)
                    {
                        if (compute)
                        {
                            _fifoClass.ComputeCallMme(macroIndex, state);
                        }
                        else
                        {
                            _fifoClass.CallMme(macroIndex, state);
                        }

                        _3dClass.PerformDeferredDraws();
                    }
                }
            }
        }

        /// <summary>
        /// Binds a subchannel to the engine selected by an object class.
        /// </summary>
        /// <param name="subChannel">Subchannel being bound</param>
        /// <param name="argument">Class ID (and engine ID in the upper bits)</param>
        private void BindObject(int subChannel, int argument)
        {
            if (!_context.IsNx2Ampere || (uint)subChannel >= SubChannelCount)
            {
                return;
            }

            // Switch 2 binds Ampere object classes. They share the Maxwell method layout, so the
            // existing engines process them; only the class-to-engine mapping differs.
            int classId = argument & 0xffff;

            if (classId == 0)
            {
                // Class 0 unbinds the subchannel. -1 matches no engine, so its calls are dropped.
                _subChannelEngine[subChannel] = -1;
                return;
            }

            int engineField = (argument >> 16) & 0x1f;

            _subChannelEngine[subChannel] = classId switch
            {
                0xc797 => Engine3D,        // AMPERE_B
                0xc7c0 => EngineCompute,   // AMPERE_COMPUTE_B
                0xc7b5 => EngineDma,       // AMPERE_DMA_COPY_B
                0xc56f => EngineFifo,      // AMPERE_CHANNEL_GPFIFO_A
                _ => subChannel,           // unknown class: keep the legacy mapping
            };

            if (classId is not (0xc797 or 0xc7c0 or 0xc7b5 or 0xc56f) && _unknownClasses.Add(classId))
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceNv,
                    $"Switch 2 unknown object class 0x{classId:X4} (engine {engineField}) on subchannel {subChannel}");
            }
        }

        /// <summary>
        /// Gets the engine index a subchannel currently dispatches to, or -1 when unbound.
        /// </summary>
        private int SubChannelEngine(int subChannel)
        {
            if ((uint)subChannel >= SubChannelCount)
            {
                return subChannel;
            }

            int engine = _subChannelEngine[subChannel];

            if (engine == -1)
            {
                return EngineFifo + 1; // No matching case: drop the call.
            }

            return engine;
        }

        /// <summary>
        /// Writes data directly to the state of the specified class.
        /// </summary>
        /// <param name="classId">ID of the class to write the data into</param>
        /// <param name="offset">State offset in bytes</param>
        /// <param name="value">Value to be written</param>
        public void Write(ClassId classId, int offset, int value)
        {
            switch (classId)
            {
                case ClassId.Threed:
                    _3dClass.Write(offset, value);
                    break;
                case ClassId.Compute:
                    _computeClass.Write(offset, value);
                    break;
                case ClassId.InlineToMemory:
                    _i2mClass.Write(offset, value);
                    break;
                case ClassId.Twod:
                    _2dClass.Write(offset, value);
                    break;
                case ClassId.Dma:
                    _dmaClass.Write(offset, value);
                    break;
                case ClassId.GPFifo:
                    _fifoClass.Write(offset, value);
                    break;
            }
        }

        /// <summary>
        /// Sets the shadow ram control value of all sub-channels.
        /// </summary>
        /// <param name="control">New shadow ram control value</param>
        public void SetShadowRamControl(int control)
        {
            _3dClass.SetShadowRamControl(control);
        }

        /// <summary>
        /// Forces a full host state update by marking all state as modified,
        /// and also requests all GPU resources in use to be rebound.
        /// </summary>
        public void ForceAllDirty()
        {
            _3dClass.ForceStateDirty();
            _channel.BufferManager.Rebind();
            _channel.TextureManager.Rebind();
        }

        /// <summary>
        /// Perform any deferred draws.
        /// </summary>
        public void PerformDeferredDraws()
        {
            _3dClass.PerformDeferredDraws();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _3dClass.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}

// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // nn::codec::video::ipc::VideoDecService TIPC surface, reached through the session that
    // gsv 1002 returns. Command 1 opens the actual decoder session: the guest reads the raw
    // reply through the standard handle-descriptor slot (TLS+24) and sends the decode
    // commands on it. Answered without a handle descriptor, the slot keeps the previous
    // reply's words (the 991 pool size 0x348000) and the movie's event loop sends on that
    // bogus handle, which aborts with 2001-0114.
    class INx2VideoDecSession : IpcService
    {
        private static int _openLogs;

        public INx2VideoDecSession() : base(registerTipc: true) { }

        [CommandTipc(1)]
        public ResultCode OpenDecoderSession(ServiceCtx context)
        {
            if (System.Threading.Interlocked.Increment(ref _openLogs) <= 4)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2 vdec cmd1: minting decoder session #{_openLogs}");
            }

            MakeSessionObject(context, new INx2VideoDecoderSession());

            return ResultCode.Success;
        }
    }

    // Decode commands.
    //
    // 101 registers an image pool and 3 follows it on the same handle; both replies are read
    // at raw word 1 as the completion event handle. Left empty, the guest reads its own
    // request pointer (0x0E0C4A9A) from the untouched TLS slot and later waits on it, which
    // aborts with 2001-0114 in MovieEventHandleThread.
    //
    // 213 (TIPC type 0xE5) is the movie module's poll
    // (nn::codec::video::ipc::VideoDecService::GetEvent). Its reply is a 40-byte stream
    // descriptor; the movie compares the two u16 availability counts at raw offset 16/20
    // with the previous poll and signals its client events (input/output available) when
    // they change. Answered with success and no data, every field was stale TLS and the
    // poll never reported anything, so the player never submitted its first frame.
    class INx2VideoDecoderSession : IpcService
    {
        // Streams belong to one decoder session: a second session (after the first movie ends)
        // registers new ids at the same cache-slot indexes, so a shared list makes the poll
        // name the old session's stream and the client retries forever.
        private readonly object _streamsLock = new();
        private readonly System.Collections.Generic.List<uint> _streams = [];
        private static int _eventLogs;
        // Per session: a global count made the next movie's first poll look like its 60th, so
        // it reported end-of-stream before the player had set the stream up and never ended.
        private int _pollCount;
        private static int _pollLogs;
        private static int _streamLogs;
        private static int _releaseLogs;
        private int _eosLogged;
        private static int _pumpStarted;
        private static long _inputsSubmitted;
        private static long _framesProduced;

        // WORKAROUND: no decoder runs behind this service, so the pool completion events the
        // player waits on would never be signalled, MovieEventHandleThread would stay blocked
        // on its first frame and the movie would never finish. Signal them at the movie frame
        // cadence so the player advances; the pool images stay blank. Set
        // NX2_MOVIE_FRAME_SIGNAL=0 to disable.
        private static readonly System.Collections.Concurrent.ConcurrentBag<KEvent> FrameEvents = [];

        public INx2VideoDecoderSession() : base(registerTipc: true) { }

        private static int _closeLogs;
        private static int _releaseLogs2;

        [CommandTipc(6)]
        public ResultCode CloseDecoderSession(ServiceCtx context)
        {
            if (System.Threading.Interlocked.Increment(ref _closeLogs) <= 4)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, "NX2 vdec cmd6: close decoder session");
            }
            return ResultCode.Success;
        }

        [CommandTipc(8)]
        public ResultCode ReleaseDecoderSession(ServiceCtx context)
        {
            if (System.Threading.Interlocked.Increment(ref _releaseLogs2) <= 4)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, "NX2 vdec cmd8: release decoder session");
            }
            return ResultCode.Success;
        }

        [CommandTipc(101)]
        [CommandTipc(3)]
        public ResultCode RegisterDecoderPool(ServiceCtx context)
        {
            int command = (int)context.Request.Type - 0x10;
            int handle = MintClientEvent(context, out KEvent ev);

            if (System.Threading.Interlocked.Increment(ref _eventLogs) <= 8)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2 vdec cmd{command}: completion event handle=0x{handle:X8}");
            }

            StartFramePump(ev);

            // Same rule as gsv 911: the reply carries the handle as plain raw data, so it must
            // be minted in the client's (pid 109) table or WaitSynchronization rejects it.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(handle);

            return ResultCode.Success;
        }

        // TIPC type 0xD9. Pool/transfer-memory registration: first request word is a guest
        // address (0x0496B000 in OoT). Probe its contents to see if it holds AV1 data.
        [CommandTipc(201)]
        public ResultCode RegisterPool201(ServiceCtx context)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_DEC_MEM_DUMP") == "1")
            {
                try
                {
                    long saved = context.RequestData.BaseStream.Position;
                    context.RequestData.BaseStream.Seek(0, System.IO.SeekOrigin.Begin);
                    ulong guestAddr = context.RequestData.ReadUInt32();
                    context.RequestData.BaseStream.Position = saved;

                    ulong pid = context.Device?.Processes?.ActiveApplication?.ProcessId ?? 0;
                    Ryujinx.HLE.HOS.Kernel.Process.KProcess appProc = null;
                    if (pid != 0 && context.Device.System.KernelContext.Processes.TryGetValue(pid, out appProc) == false)
                    {
                        appProc = null;
                    }

                    if (appProc == null)
                    {
                        Logger.Warning?.Print(LogClass.ServiceAm, $"NX2 DEC_MEM 0x{guestAddr:X}: no app process (pid={pid})");
                    }
                    else
                    {
                        byte[] mem = new byte[128];
                        appProc.CpuMemory.Read(guestAddr, mem);
                        string hex = System.Convert.ToHexString(mem);
                        ulong nonzero = 0;
                        foreach (byte b in mem) if (b != 0) nonzero++;
                        Logger.Warning?.Print(LogClass.ServiceAm,
                            $"NX2 DEC_MEM 0x{guestAddr:X} pid={pid} nonzero={nonzero}/128 bytes={hex}");
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Warning?.Print(LogClass.ServiceAm, $"NX2 DEC_MEM failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Mirror cmd101: mint the completion event handle in the client table.
            int handle = MintClientEvent(context, out KEvent ev);
            StartFramePump(ev);
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(handle);

            return ResultCode.Success;
        }

        // TIPC type 0x147 (327). CommandTipc ids are the raw type minus the 0x10 TIPC base
        // (the same way type 0xE5 registers as 213), so the attribute id is 311.
        //
        // nn::codec::video::ipc::VideoDecService stream setup. The guest library mints the
        // stream id itself and sends it as the first raw word; the reply is a 16-byte
        // descriptor the library caches per stream. Remember the id so a diagnostic run can
        // name the streams the guest registered.
        [CommandTipc(311)]
        public ResultCode OpenStream(ServiceCtx context)
        {
            uint streamId = 0;

            if (context.RequestData.BaseStream.Length >= 4)
            {
                streamId = context.RequestData.ReadUInt32();
            }

            bool added = false;

            lock (_streamsLock)
            {
                if (!_streams.Contains(streamId) && _streams.Count < 8)
                {
                    _streams.Add(streamId);
                    added = true;
                }
            }

            if (added && System.Threading.Interlocked.Increment(ref _streamLogs) <= 8)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2 vdec cmd327: registered stream id 0x{streamId:X8} ({_streams.Count} total)");
            }

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0ul);
            context.ResponseData.Write(0ul);

            return ResultCode.Success;
        }

        // TIPC type 0x14A (330, attribute id 314). The guest sends a 32-byte record naming a
        // stream plus four words and expects a 16-byte descriptor back. Measured rate in the
        // Ocarina log is ~21/s, i.e. one call per movie frame, so this is the per-frame
        // decoder entry point (submit input or release the previous output; both are keyed by
        // the stream id and carry no guest pointer, the buffers live in the transfer memory
        // registered at decoder init). Count it so the poll below can report frame progress,
        // and probe the words (capped) to learn whether a presentation timestamp rides along.
        [CommandTipc(314)]
        public ResultCode SubmitFrame(ServiceCtx context)
        {
            uint streamId = 0;
            uint word1 = 0;
            ulong word2 = 0;
            ulong word3 = 0;
            ulong word4 = 0;

            if (context.RequestData.BaseStream.Length >= 32)
            {
                streamId = context.RequestData.ReadUInt32();
                word1 = context.RequestData.ReadUInt32();
                word2 = context.RequestData.ReadUInt64();
                word3 = context.RequestData.ReadUInt64();
                word4 = context.RequestData.ReadUInt64();
            }

            long submitted = System.Threading.Interlocked.Increment(ref _inputsSubmitted);

            if (System.Threading.Interlocked.Increment(ref _releaseLogs) <= 6)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2 vdec cmd0x14A: stream=0x{streamId:X8} w1=0x{word1:X8} w2=0x{word2:X16} w3=0x{word3:X16} w4=0x{word4:X16} (#{submitted})");
            }

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0ul);
            context.ResponseData.Write(0ul);

            return ResultCode.Success;
        }

        [CommandTipc(213)]
        public ResultCode PollDecoderEvent(ServiceCtx context)
        {
            // WORKAROUND: no AV1 decode runs behind this service. Report one input slot and
            // one output slot as ready so the player submits a frame and presents it (blank);
            // without this the movie stalls on its first frame. The short sleep stands in for
            // the real poll blocking in the kernel until the decoder produces an event, which
            // also keeps this guest thread from spinning at millions of calls per second.
            System.Threading.Thread.Sleep(8);

            int poll = System.Threading.Interlocked.Increment(ref _pollCount);
            uint streamId = 0;
            int requestedIndex = -1;

            if (System.Environment.GetEnvironmentVariable("NX2_MOVIE_STREAM_X26") == "1")
            {
                // nn::codec's GetEvent (nnSdk+0x331880) keeps the codec client in x25 and the
                // requested 40-byte cache entry in x26 across the TIPC send. The reply must name
                // the stream registered for that cache slot: an id that maps elsewhere leaves the
                // requested slot invalid and the client re-sends forever (one endless poll call).
                // The handler runs on the HLE server thread, so SendSyncRequest hands the caller's
                // registers over; the caller stays blocked until this handler returns.
                ulong codecClient = Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.MovieCallerCodecClient;
                ulong requestedEntry = Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.MovieCallerRequestedEntry;

                if (codecClient != 0 && requestedEntry >= codecClient + 0x120)
                {
                    ulong delta = requestedEntry - (codecClient + 0x120);

                    if (delta % 40 == 0 && delta / 40 < 8)
                    {
                        requestedIndex = (int)(delta / 40);
                    }
                }

                lock (_streamsLock)
                {
                    if (requestedIndex >= 0 && requestedIndex < _streams.Count)
                    {
                        streamId = _streams[requestedIndex];
                    }
                    else if (_streams.Count != 0)
                    {
                        streamId = _streams[0];
                    }
                }
            }
            else if (System.Environment.GetEnvironmentVariable("NX2_MOVIE_STREAM_ROTATE") == "1")
            {
                lock (_streamsLock)
                {
                    if (_streams.Count != 0)
                    {
                        // Experiment: name one registered stream per poll in turn. Off by
                        // default because the validated reply named stream 0 and the guest's
                        // parser falls back to cache slot 0 when an id does not match.
                        streamId = _streams[(poll - 1) % _streams.Count];
                    }
                }
            }

            uint inputSlots = 1;
            uint outputSlots = 1;
            ulong pts = 0;

            if (System.Environment.GetEnvironmentVariable("NX2_MOVIE_NO_OUTPUT") == "1")
            {
                outputSlots = 0;
            }

            bool counterMode = System.Environment.GetEnvironmentVariable("NX2_MOVIE_FRAME_COUNTERS") == "1";

            if (counterMode)
            {
                // Experiment: the real decoder consumes the frames the guest submits (0x14A)
                // and reports progress in counters 3 and 4, which are the only fields the
                // movie's event manager compares. A constant (1,1) makes it signal exactly one
                // transition and then wait forever. Report one consumed input and one decoded
                // frame per poll until the submitted frames are accounted for, with a 30 fps
                // timestamp so a consumer that paces by pts sees a sane timeline.
                long submitted = System.Threading.Interlocked.Read(ref _inputsSubmitted);
                long produced = System.Threading.Interlocked.Read(ref _framesProduced);

                if (produced < submitted)
                {
                    produced = (submitted - produced) > 1 ? produced + 1 : submitted;
                    System.Threading.Interlocked.Exchange(ref _framesProduced, produced);
                }

                inputSlots = (uint)(submitted & 0xFFFF);
                outputSlots = (uint)(produced & 0xFFFF);
                pts = (ulong)produced * 33333;
            }

            if (System.Environment.GetEnvironmentVariable("NX2_MOVIE_SEQ_EVENTS") == "1")
            {
                // Experiment: make the output count change every poll so the event manager sees
                // a new event each time instead of one transition and then silence.
                outputSlots = (uint)(poll & 0xFFFF);

                if (outputSlots == 0)
                {
                    outputSlots = 1;
                }
            }

            bool eos = false;

            if (uint.TryParse(System.Environment.GetEnvironmentVariable("NX2_MOVIE_EOS_AFTER"), out uint eosAfter) &&
                eosAfter != 0 && (uint)poll >= eosAfter)
            {
                eos = true;

                if (System.Threading.Interlocked.Exchange(ref _eosLogged, 1) == 0)
                {
                    Logger.Warning?.Print(LogClass.ServiceAm,
                        $"NX2 vdec cmd213: reporting end-of-stream from poll #{poll} (NX2_MOVIE_EOS_AFTER={eosAfter})");
                }
            }

        // After eos, an ended stream offers no new inputs or outputs; leaving either at 1
        // makes the player release/consume one fake frame per 314 call forever.
        if (eos)
        {
            inputSlots = 0;
            outputSlots = 0;
        }

            int pollLog = System.Threading.Interlocked.Increment(ref _pollLogs);

            if (pollLog <= 4 || (pollLog & 0x3FF) == 0)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2 vdec cmd{(int)context.Request.Type - 0x10}: stream=0x{streamId:X8} idx={requestedIndex} input={inputSlots} output={outputSlots} eos={eos} (poll #{_pollLogs})");
            }

            var reply = context.ResponseData;

            // 40-byte stream descriptor. The guest's parser reads the stream id at TLS+12, four
            // u32 counters at TLS+16/20/24/28, a u64 timestamp at TLS+36 and the end-of-stream
            // bit at TLS+44. The response raw data starts at TLS+8, so its payload starts at
            // offset 4 in this stream.
            reply.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            reply.Write(streamId);            // offset 4:  stream id
            reply.Write(0u);                  // offset 8:  counter 1
            reply.Write(0u);                  // offset 12: counter 2
            reply.Write(inputSlots);          // offset 16: counter 3 (movie compares this)
            reply.Write(outputSlots);         // offset 20: counter 4 (movie compares this)
            reply.Write(0u);                  // offset 24: unused
            reply.Write((uint)pts);           // offset 28: timestamp low
            reply.Write((uint)(pts >> 32));   // offset 32: timestamp high
            reply.Write(eos ? 1u : 0u);       // offset 36: end-of-stream flag (bit 0)

            return ResultCode.Success;
        }

        private static void StartFramePump(KEvent ev)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_MOVIE_FRAME_SIGNAL") == "0")
            {
                return;
            }

            FrameEvents.Add(ev);

            if (System.Threading.Interlocked.Exchange(ref _pumpStarted, 1) != 0)
            {
                return;
            }

            Logger.Warning?.Print(LogClass.ServiceAm,
                "WORKAROUND NX2 fake vdec: signalling movie frame events at 60 Hz (no AV1 decode)");

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                while (clock.Elapsed < System.TimeSpan.FromHours(2))
                {
                    foreach (KEvent frameEvent in FrameEvents)
                    {
                        frameEvent.ReadableEvent.Signal();
                    }

                    await System.Threading.Tasks.Task.Delay(16);
                }
            });
        }

        private static int MintClientEvent(ServiceCtx context, out KEvent ev)
        {
            ev = new KEvent(context.Device.System.KernelContext);
            var client = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetProcessByPid(109) ?? context.Process;

            client.HandleTable.GenerateHandle(ev.ReadableEvent, out int handle);

            return handle;
        }
    }
}

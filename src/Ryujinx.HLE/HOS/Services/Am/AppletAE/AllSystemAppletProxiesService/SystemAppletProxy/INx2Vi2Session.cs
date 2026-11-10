// SPDX-License-Identifier: MIT
// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Minimal nx2:vi2 session. Presentation is acknowledged but not displayed yet.
    class INx2Vi2Session : IpcService
    {
        // Native vsync slots (Eglantine INx2Vi2Session: 8 indexed slots shared by
        // 31/32 acquire and 261-272 bind/unbind; a slot fires on vblank while bound).
        // Unbound slots fire too (the game waits on a fresh slot before binding it),
        // while bind-change slots fire once and go quiet.
        private const int NativeSlots = 8;
        private readonly KEvent[] _nativeSlots = new KEvent[NativeSlots];
        private readonly HashSet<KEvent> _vblankEvents = [];
        private System.Threading.Timer _tickTimer;

        public INx2Vi2Session() : base(registerTipc: true) { }

        private void EnsureTickTimer()
        {
            lock (_vblankEvents)
            {
                // Oracle drives the frame loop off a 60 Hz vblank; the tick also
                // resumes stalled GPU channels and signalled id events.
                _tickTimer ??= new System.Threading.Timer(_ =>
                {
                    lock (_vblankEvents)
                    {
                        foreach (KEvent tick in _vblankEvents)
                        {
                            tick.WritableEvent.Signal();
                        }
                    }

                    Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.TryResumeAll();
                    Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.NvHostGpuDeviceFile.SignalAllNx2Events();
                    INx2GpuSession.SignalAllNx2GpuEvents();
                    Nx2Diagnostics.WatchQueues(System.Threading.Volatile.Read(ref _device));

                    LogProgress();
                }, null, 16, 16);

            }
        }

        private void SetVsyncSlot(KEvent evt, bool on)
        {
            lock (_vblankEvents)
            {
                if (on)
                {
                    _vblankEvents.Add(evt);
                }
                else
                {
                    _vblankEvents.Remove(evt);
                }
            }
        }

        private KEvent NativeSlot(ServiceCtx context, uint index)
        {
            if (index >= NativeSlots)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 native slot {index} out of range, handing out nothing");
                return null;
            }

            KEvent evt = _nativeSlots[index];

            if (evt == null)
            {
                evt = new KEvent(context.Device.System.KernelContext);
                _nativeSlots[index] = evt;
                Nx2EventNames.Register(evt.ReadableEvent, $"vi2native{index}");
                SetVsyncSlot(evt, true);
                EnsureTickTimer();
            }

            return evt;
        }

        private static int _progressTicks;

        private static Ryujinx.HLE.Switch _device;

        // Host syncpoint values 0..7 for the render-gate question: the game waits on
        // channel fence values, and a fence that never advances parks its render loop.
        private static string SpValues()
        {
            Ryujinx.HLE.Switch device = System.Threading.Volatile.Read(ref _device);

            if (device?.System?.HostSyncpoint == null)
            {
                return "?";
            }

            System.Text.StringBuilder sb = new();

            for (uint id = 0; id < 8; id++)
            {
                if (id != 0)
                {
                    sb.Append(',');
                }

                sb.Append(device.System.HostSyncpoint.ReadSyncpointValue(id));
            }

            return sb.ToString();
        }

        private static void LogProgress()
        {
            int mod = System.Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1" ? 600 : 3600;
            if (System.Threading.Interlocked.Increment(ref _progressTicks) % mod != 0)
            {
                return;
            }

            string calls = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                        Ryujinx.HLE.HOS.Services.IpcService.CallCounts, kv => kv.Value), 24),
                kv => $"{kv.Key}={kv.Value}"));

            string ioctls = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Services.Nv.INvDrvServices.IoctlCounts, kv => kv.Value), 10),
                kv => $"{kv.Key}={kv.Value}"));

            string gpu = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(Ryujinx.HLE.HOS.Services.IpcService.CallCounts, kv => kv.Key.StartsWith("INx2GpuSession")),
                kv => $"{kv.Key.Substring("INx2GpuSession".Length)}= {kv.Value}".Replace("= ", "=")));

            string sites = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.GuestCallSites, kv => kv.Value), 8),
                kv => $"pid{kv.Key.Pid}:0x{kv.Key.Pc:X}={kv.Value}" +
                    (Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.GuestCallSiteInfo.TryGetValue(kv.Key, out string info) ? $"[{info}]" : "")));

            string missing = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Services.IpcService.MissingCalls, kv => kv.Value), 12),
                kv => $"{kv.Key}={kv.Value}"));

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2 progress t={_progressTicks / 60}s submits={Nx2Counters.Submits} presents={Nx2Counters.Presents} sparseCommit={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseCommitted >> 20}MiB dropped={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseDropped >> 20}MiB draws={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Draws} unbound={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.UnboundDrops} mme={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacroMemoHits}+{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacroMemoMisses} 3d={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()} emitted=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DescribeEmitted()}] sp=[{SpValues()}] hot=[{HotPcs()}] gpuid=[{INx2GpuSession.DescribeSignals()}] fs=[{Ryujinx.HLE.HOS.Services.Fs.FsLoaderProgress.Describe()}] gsvmap=[{INx2GpuSession.DescribeMapTiming()}] bulk=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2BulkCounters.Describe()}]");
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2 calls {calls} ioctls=[{ioctls}] gpu=[{gpu}] sites=[{sites}] missing=[{missing}]");
            try
            { Nx2ThreadDump.MaybeLogBuildIdentity(); Nx2ThreadDump.Tick(System.Threading.Volatile.Read(ref _device)?.System, _progressTicks / 60); }
            catch { }
        }

        [CommandTipc(0)]
        public ResultCode Handshake(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            ulong token = context.RequestData.ReadUInt64();

            context.ResponseData.Write(token);

            return ResultCode.Success;
        }

        [CommandTipc(11)]
        public ResultCode ReturnEvent(ServiceCtx context)
        {
            // Fresh event per call (Eglantine ReturnEvent): the oracle never signals
            // it, so sharing one would satisfy a new wait with an old signal.
            KEvent evt = new(context.Device.System.KernelContext);
            Nx2EventNames.Register(evt.ReadableEvent, "vi2event11");

            // The oracle never signals this event; signalling it spuriously kept the
            // guest's main loop spinning in its handler instead of advancing.
            if (System.Threading.Interlocked.Increment(ref _event11Logs) <= 2)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    "NX2 vi2 command 11 event handed out unsignalled");
            }

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(1)]
        [CommandTipc(3)]
        public ResultCode ReturnOne(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(5)]
        public ResultCode GetTimestampRecorderCount(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(7u);

            return ResultCode.Success;
        }

        [CommandTipc(31)]
        [CommandTipc(32)]
        public ResultCode GetVsyncEvent(ServiceCtx context)
        {
            // Indexed native slots (Eglantine 31 acquire / 32 duplicate): the slot
            // index is the first data word (copy handles travel separately).
            uint index = 0;

            try
            {
                index = context.RequestData.ReadUInt32();
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 slot acquire without index: {ex.GetType().Name}");
            }

            KEvent evt = NativeSlot(context, index);

            if (evt == null)
            {
                return ResultCode.Success;
            }

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(51)]
        public ResultCode CreateLayer(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(101)]
        [CommandTipc(201)]
        [CommandTipc(251)]
        [CommandTipc(111)]
        [CommandTipc(261)]
        public ResultCode SucceedQuietly(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        // 103(name): u32 refresh rate in Hz then a bool; the SDK accepts only 60 or 120.
        [CommandTipc(103)]
        public ResultCode QueryDisplayRefreshRate(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(60u);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        // 104(name): u64 output format, u16 peak nits. The SDK takes 1 as SDR and 2 as HDR and
        // fails the query on anything else; an empty reply left it reading the request's leftovers.
        [CommandTipc(104)]
        public ResultCode QueryDisplayHdrStatus(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);   // SDR
            context.ResponseData.Write(0u);
            context.ResponseData.Write(400u); // paper white, nits

            return ResultCode.Success;
        }

        // 105(name): u16 paper white in nits (BT.2408 reference white).
        [CommandTipc(105)]
        public ResultCode QueryDisplayHdrPaperWhite(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(203u);

            return ResultCode.Success;
        }

        // 106(name): u64 highest buffer resolution mode of the display. 1 -> 720p, 2 -> 1080p,
        // 4 and 8 -> 1440p; 0 (an empty reply) falls back to 720p. Docked reports 4, handheld 2.
        [CommandTipc(106)]
        public ResultCode GetBufferMaxResolution(ServiceCtx context)
        {
            ulong mode = context.Device.System.State.DockedMode ? 4UL : 2UL;

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)mode);
            context.ResponseData.Write((uint)(mode >> 32));

            return ResultCode.Success;
        }

        // 212(layer): u64 bits of the layer's buffer slots that are ready. The client polls it
        // once a second until some are set; an empty reply left it on zero forever.
        [CommandTipc(212)]
        public ResultCode GetLayerBufferBits(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0x7FFFu);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        private static int _modeResolutionLogs;

        // 263/265/267/269/271(name[8B, "Default"], u32 slot index): bind that vsync
        // slot to a display-change source and fire it once so the game reads the current
        // state (Eglantine BindChange; none of them change here). The reply is empty
        // success, not data. (261 BindDisplayVsyncEvent stays on SucceedQuietly:
        // slots tick unconditionally here, so binding is a no-op.)
        [CommandTipc(263)]
        [CommandTipc(265)]
        [CommandTipc(267)]
        [CommandTipc(269)]
        [CommandTipc(271)]
        public ResultCode BindDisplayChangeEvent(ServiceCtx context)
        {
            byte[] name = context.RequestData.ReadBytes(8);
            uint index = context.RequestData.ReadUInt32();

            if (index < NativeSlots && _nativeSlots[index] != null)
            {
                KEvent evt = _nativeSlots[index];

                SetVsyncSlot(evt, false);
                evt.ReadableEvent.Signal();
            }

            if (System.Threading.Interlocked.Increment(ref _modeResolutionLogs) <= 8)
            {
                string display = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0');
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 bind change cmd={(int)context.Request.Type - 0x10} name={display} slot={index}");
            }

            return ResultCode.Success;
        }

        // 262/264/266/268/270/272: detach the slot from its source (Eglantine
        // UnbindNativeEvent). The reply is empty success.
        [CommandTipc(262)]
        [CommandTipc(264)]
        [CommandTipc(266)]
        [CommandTipc(268)]
        [CommandTipc(270)]
        [CommandTipc(272)]
        public ResultCode UnbindDisplayChangeEvent(ServiceCtx context)
        {
            _ = context.RequestData.ReadBytes(8);
            uint index = context.RequestData.ReadUInt32();

            if (index < NativeSlots && _nativeSlots[index] != null)
            {
                SetVsyncSlot(_nativeSlots[index], false);
            }

            return ResultCode.Success;
        }

        private static int _unknown351Logs;

        // 351(u64, u64): purpose unknown (captured: (0xD01D728, 5) via NX2_MISSING_LOG).
        // WORKAROUND: named success with a capped per-trigger word log instead of a
        // silent auto-stub, so the reply shape can be inferred from guest behavior.
        [CommandTipc(351)]
        public ResultCode Unknown351(ServiceCtx context)
        {
            ulong first = context.RequestData.ReadUInt64();
            ulong second = context.RequestData.ReadUInt64();

            if (System.Threading.Interlocked.Increment(ref _unknown351Logs) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 351 first=0x{first:X} second=0x{second:X}");
            }

            return ResultCode.Success;
        }

        [CommandTipc(228)]
        public ResultCode CancelClientBuffer(ServiceCtx context) => PresentInternal(context, 228);

        [CommandTipc(229)]
        public ResultCode Present(ServiceCtx context) => PresentInternal(context, 229);

        public ResultCode PresentInternal(ServiceCtx context, uint cmdId)
        {
            // Probe: the NX2 present request carries a GPU fence; the reference
            // emulator reports it as (id=1, value=109+3n). Dump the raw words once
            // to correct the offsets instead of guessing.
            if (System.Environment.GetEnvironmentVariable("NX2_PRESENT_WORDS") == "1" &&
                System.Threading.Interlocked.Increment(ref _presentWordLogs) <= 3)
            {
                try
                {
                    System.IO.Stream ws = context.RequestData.BaseStream;
                    long save = ws.Position;
                    System.Text.StringBuilder wb = new();
                    for (int wi = 0; wi < 32 && ws.Position + 4 <= ws.Length; wi++)
                    {
                        wb.Append($" {wi}=0x{context.RequestData.ReadUInt32():X8}");
                    }
                    ws.Position = save;
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 present words len=0x{ws.Length:X}:{wb}");
                }
                catch { }
            }

            // 229(u64 client token, u32 slot, u32 image, ...fences): word 5 after the
            // TIPC header is the presented image id; RequestData starts at word 2.
            context.RequestData.ReadUInt64();  // token
            uint slot = context.RequestData.ReadUInt32();

            uint imageId = context.RequestData.ReadUInt32();

            // 228 = CancelClientBuffer (per droplet: returns only the slot mask, no present/composite/sp0/vsync).
            // 229 = PresentClientBuffer (present path below). Conflating them incorrectly advanced Presents/sp0/vsync on
            // cancels. 228 is observed as 0 in runtime runs, so this is a low-risk correctness fix (reference-backed).
            if (cmdId == 228)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 228 CancelClientBuffer image={imageId} slot={slot} -> mask only (no present/sp0/vsync)");
                context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
                context.ResponseData.Write(AllSlotsFreeMask);
                context.ResponseData.Write(0u);
                context.ResponseData.Write(0u);
                context.ResponseData.Write(0u);
                context.ResponseData.Write(0u);
                context.ResponseData.Write(0u);
                return ResultCode.Success;
            }

            System.Threading.Volatile.Write(ref _device, context.Device);
            int presentN = System.Threading.Interlocked.Increment(ref Nx2Counters.Presents);
            NvHostChannelDeviceFile.TracePresentUserData(context.Device, presentN);

            if (System.Environment.GetEnvironmentVariable("NX2_PRESENT_TRACE") == "1")
            {
                bool logIt = presentN <= 30 || presentN % 100 == 0;
                if (logIt)
                {
                    // 228 (CancelClientBuffer) and 229 (PresentClientBuffer) share this handler; log cmd to distinguish.
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2PT #{presentN} cmd={cmdId} slot={slot} image={imageId} bufHash=dims=format=nonzero=dump=UNRESOLVED(nx2:vi2 TIPC path, buffer not cheaply wired)");
                }
            }

            if (System.Environment.GetEnvironmentVariable("NX2_METHOD_LOG") == "1" &&
                System.Threading.Interlocked.Increment(ref _methodSamples) % 300 == 0)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"NX2 3dmethods {Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()} macros={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun} emitted={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods} draws={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Draws} tol=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Tolerances}] fifo=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Fifo}] compute=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.ComputeMethods()}]");
            }

            if (System.Environment.GetEnvironmentVariable("NX2_PC_SAMPLE") == "1" &&
                System.Threading.Interlocked.Increment(ref _pcSamples) % 120 == 0)
            {
                SampleGuestPcs(context.Device);
                SampleGuestWaits(context.Device);
            }

            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1")
            {
                // The executing thread here is the HLE server thread (ServerBase.ServerLoop),
                // not the guest caller: attribute presents by client only where the message
                // carries a caller pid (CMIF), never by thread owner.
                var caller = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();

                if (caller != null)
                {
                    string key = $"{caller.Owner.Pid}:{imageId}";

                    if (PresentSeen.TryAdd(key, 0) && PresentSeen.Count <= 12)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present caller={caller.Owner.Pid} tid={caller.ThreadUid} image={imageId}");
                    }
                }
            }

            // Guest stacks of every application thread, once, at a choosable present count:
            // NX2_THREAD_DUMP=<present number>. Prints the call path each parked thread is in.
            if (System.Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") is string dumpAt &&
                int.TryParse(dumpAt, out int dumpPresent) &&
                Nx2Counters.Presents == dumpPresent)
            {
                // Dump every process, not just the title: Switch 2 renders through the
                // "Common" system process, and the GPU submit thread lives there.
                foreach (System.Collections.Generic.KeyValuePair<ulong, Ryujinx.HLE.HOS.Kernel.Process.KProcess> proc in context.Device.System.KernelContext.Processes)
                {
                    foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in proc.Value.DebugThreads)
                    {
                        // Kernel wait state: a thread at pc=0 is parked in an SVC wait, and
                        // the sync objects name exactly what it is blocked on.
                        System.Text.StringBuilder waitInfo = new();

                        for (int wi = 0; wi < thread.WaitSyncObjects.Length; wi++)
                        {
                            Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject obj = thread.WaitSyncObjects[wi];

                            if (obj != null)
                            {
                                string evName = Nx2EventNames.Lookup(obj);
                                waitInfo.Append($" w{wi}=H{thread.WaitSyncHandles[wi]}:{obj.GetType().Name}{(evName != null ? ":" + evName : "")}");
                            }
                        }

                        if (thread.SignaledObj != null)
                        {
                            waitInfo.Append($" signaled={thread.SignaledObj.GetType().Name}");
                        }

                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 td pid={proc.Key} pname={proc.Value.Name} uid={thread.ThreadUid} name={thread.GetThreadName()} pc=0x{thread.Context?.Pc:X} wait={thread.WaitingSync} sched={thread.SchedFlags}{waitInfo}");

                        try
                        {
                            thread.PrintGuestStackTrace();
                            if (thread.GetThreadName() == "MainThread" && thread.Context is { IsAarch32: false } cpu)
                            {
                                KThread owner = thread.MutexOwner;
                                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                                    $"NX2 main mutex addr=0x{thread.MutexAddress:X} ownerTid={owner?.ThreadUid} ownerName={owner?.GetThreadName()} requester=0x{thread.ThreadHandleForUserMutex:X} x0=0x{cpu.GetX(0):X} x1=0x{cpu.GetX(1):X} x2=0x{cpu.GetX(2):X}");
                                owner?.PrintGuestStackTrace();
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 td failed {ex.GetType().Name}");
                        }
                    }
                }
            }

            if (System.Threading.Interlocked.Increment(ref _presentLogs) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present #{Nx2Counters.Presents} image={imageId} draws=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DrawBreakdown}] 3d=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()}]");
            }

            // One-shot GPU method trace at a chosen present. Answers whether the scene
            // produces real draws and, if not, which 3D/compute method stream led there.
            if (System.Environment.GetEnvironmentVariable("NX2_DRAW_DUMP") is string dumpDrawAt &&
                int.TryParse(dumpDrawAt, out int dumpDrawPresent) &&
                Nx2Counters.Presents == dumpDrawPresent)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 draw-dump present={dumpDrawPresent} draws=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DrawBreakdown}] " +
                    $"mme={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods} " +
                    $"3d=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()}]");
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 draw-dump methods=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DescribeRecentMethods(256)}]");
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 draw-dump emitted=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DescribeEmitted()}] " +
                    $"compute=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.ComputeMethods()}]");
            }

            if (System.Threading.Interlocked.Increment(ref _lenLogs) <= 6)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present request length={context.RequestData.BaseStream.Length} pos={context.RequestData.BaseStream.Position}");
            }

            // The presented image's GPU fence travels in words 22/24 (the id is stored as its
            // bitwise NOT). Pace the guest on it, bounded, like the oracle does.
            try
            {
                System.IO.Stream stream = context.RequestData.BaseStream;

                // 24-word request: fence id at word 20, value at word 22 (after the 4 words read above).
                if (stream.Length - stream.Position >= 19 * sizeof(uint))
                {
                    // Words 6/7 after the header: unknown flags + the drawn-area size
                    // ((height - 1) << 16 | (width - 1), Eglantine PresentClientBuffer w[7]).
                    // Read-only probe for the image-size question; the stream is left where
                    // the fence parse below expects it.
                    uint word6 = context.RequestData.ReadUInt32();
                    uint shownSize = context.RequestData.ReadUInt32();

                    if (System.Threading.Interlocked.Increment(ref _shownLogs) <= 12)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present words w6=0x{word6:X} shown=0x{shownSize:X} (image #{Nx2Counters.Presents})");
                    }

                    stream.Seek(14 * sizeof(uint), System.IO.SeekOrigin.Current);

                    uint fenceRawId = context.RequestData.ReadUInt32();
                    context.RequestData.ReadUInt32();
                    uint fenceValue = context.RequestData.ReadUInt32();
                    uint fenceId = ~fenceRawId;

                    // Wait for the previous frame's fence, like the console: the guest prepares
                    // frame N+1 while the GPU still runs frame N.
                    uint waitId = _previousFenceId;
                    uint waitValue = _previousFenceValue;

                    _previousFenceId = fenceId;
                    _previousFenceValue = fenceValue;

                    if (System.Threading.Interlocked.Increment(ref _fenceLogs) <= 12)
                    {
                        uint now = waitId < MaxSyncpoints ? context.Device.Gpu.Synchronization.GetSyncpointValue(waitId) : 0;

                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present fence id={fenceId} value={fenceValue} (previous {waitId},{waitValue}) now={now}");
                    }

                    if (waitId < MaxSyncpoints && waitValue != 0)
                    {
                        // Up to 4 s, like the console path: the CPU runs frames ahead while the
                        // GPU is still compiling shaders, and a 1 s cap made presents fail.
                        bool reached = context.Device.Gpu.Synchronization.WaitOnSyncpoint(
                            waitId, waitValue, TimeSpan.FromSeconds(4));

                        if (!reached && System.Threading.Interlocked.Increment(ref _fenceTimeouts) <= 8)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                                $"NX2 present fence {waitId},{waitValue} not reached in 4 s");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A short request just skips the pacing; anything else is a real bug.
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present fence parse failed: {ex.GetType().Name}: {ex.Message}");
            }

            Composite(context, imageId);

            // The display consumed the frame: advance the display syncpoint the guest waits on
            // after Present. INx2SyncptSession.GetSlot hands out slot 0, and the guest polls
            // QueryCounter(0) in a loop; nvhost channel fences use their own ids (1..n), so
            // without this advance that poll never changes and the render loop never restarts.
            if (DisplaySyncpoint < MaxSyncpoints)
            {
                uint now = context.Device.Gpu.Synchronization.IncrementSyncpoint(DisplaySyncpoint);

                if (System.Environment.GetEnvironmentVariable("NX2_ARB_LOG") == "1" &&
                    System.Threading.Interlocked.Increment(ref _displaySyncLogs) <= 8)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 display syncpoint {DisplaySyncpoint} -> {now} (present #{Nx2Counters.Presents})");
                }
            }

            // Oracle: reply is a u64 mask of buffer slots the client may acquire next
            // (all free = AllSlotsFreeMask) plus four zero words. A zero mask stalls the producer.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(AllSlotsFreeMask);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            // Frame-paced vsync: the game starts frames on the native slot events.
            lock (_vblankEvents)
            {
                foreach (KEvent evt in _vblankEvents)
                {
                    evt.WritableEvent.Signal();
                }
            }

            return ResultCode.Success;
        }

        // The CPU present runs on the guest's own present thread; it must stay cheap.
        // Disabled until the renderer submits real frames; static (not const) so the
        // disabled path does not trip unreachable-code warnings.
        private static readonly bool CompositeEnabled = true;

        private static void Composite(ServiceCtx context, uint imageId)
        {
            if (!CompositeEnabled)
            {
                return;
            }

            Nx2NativeObjects.Image image = null;
            ulong imageOffset = 0;
            ulong address = 0;

            lock (Nx2NativeObjects.Lock)
            {
                uint baseId = 0;

                foreach ((uint id, Nx2NativeObjects.Image candidate) in Nx2NativeObjects.Images)
                {
                    if (imageId >= id && imageId - id < candidate.Count && (image == null || id > baseId))
                    {
                        baseId = id;
                        image = candidate;
                        imageOffset = (imageId - id) * candidate.Size;
                    }
                }

                if (image == null || !Nx2NativeObjects.Memories.TryGetValue(image.Memory, out Nx2NativeObjects.Memory memory))
                {
                    return;
                }

                address = memory.Address + imageOffset;
            }

            (uint width, uint height, ulong pitch, uint blockHeightLog2, ulong size) = Nx2NativeObjects.Layout(image.Preference);

            // Guest-controlled dimensions: validate before any narrowing cast or allocation.
            // RGBA8 needs width * 4 * height bytes; reject anything that overflows an int.
            long pixelBytes;

            try
            {
                pixelBytes = checked((long)width * 4 * height);
            }
            catch (OverflowException)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId} dimensions overflow: {width}x{height}");

                return;
            }

            if (size > int.MaxValue || pixelBytes > int.MaxValue || width == 0 || height == 0)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId} size out of range: {width}x{height} size=0x{size:X}");

                return;
            }

            ulong pid = context.Device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !context.Device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId}: no active application process");

                return;
            }

            IRenderer renderer = context.Device.Gpu.Renderer;
            context.Device.Gpu.Window.TryCaptureHostFrame(() =>
            {
                // The guest can reuse the image after the IPC reply. Snapshot it before returning.
                byte[] source = new byte[(int)size];

                try
                {
                    process.CpuMemory.Read(address, source.AsSpan());
                }
                catch (Exception ex)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 composite image={imageId} read of 0x{size:X} bytes at 0x{address:X} failed: {ex.GetType().Name}: {ex.Message}");

                    return null;
                }

                return swapBuffers =>
                {
                    try
                    {
                        PresentSnapshot(renderer, swapBuffers, source, (int)pixelBytes, width, height,
                            pitch, blockHeightLog2, imageId, address, pid);
                    }
                    catch (Exception ex)
                    {
                        if (System.Threading.Interlocked.Increment(ref _compositeLogs) <= 4)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 present failed: {ex.Message}");
                        }
                    }
                };
            });
        }

        private static void PresentSnapshot(IRenderer renderer, Action swapBuffers, byte[] source,
            int pixelBytes, uint width, uint height, ulong pitch, uint blockHeightLog2, uint imageId, ulong address, ulong pid)
        {
            PresentTextureRing textures = PresentTextures.GetValue(renderer, static _ => new());
            byte[] linear = textures.GetLinearBuffer(pixelBytes);
            Nx2NativeObjects.Unswizzle(source, linear, (int)width, (int)height, pitch, (int)blockHeightLog2);

            RunFrameDumpControlOnce();

            int compositeN = System.Threading.Interlocked.Increment(ref _compositeN);
            double elapsedS = (System.Diagnostics.Stopwatch.GetTimestamp() - _compositeStartTs) / (double)System.Diagnostics.Stopwatch.Frequency;

            // Per-composite stats + dumps (env-gated, observe-only). NX2_FRAME_DUMP enables dumps+stats.
            if (Ryujinx.Common.Nx2ProbeLog.Enabled("NX2_FRAME_DUMP"))
            {
                (ulong hash, double nonzeroFrac) = ComputeFrameStats(linear, pixelBytes);
                bool logStats = compositeN <= 30 || compositeN % 100 == 0;
                if (logStats)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 composite stats #{compositeN} t={elapsedS:F1}s image={imageId} addr=0x{address:X} {width}x{height} pitch={pitch} blockLog2={blockHeightLog2} format=RGBA8(non-swizzled) bytes={pixelBytes} nonzero={nonzeroFrac:F6} hash=0x{hash:X16}");
                }

                // Dumps: first 3 composites (early) + one near 60/300/600s, up to 6 files total.
                int dumpIndex = System.Threading.Interlocked.Increment(ref _frameDumps);
                string tag = null;
                if (dumpIndex <= 3)
                {
                    tag = $"early-{dumpIndex}";
                }
                else if (TryClaimDumpMark(elapsedS, 60.0, ref _dump60))
                {
                    tag = "t60s";
                }
                else if (TryClaimDumpMark(elapsedS, 300.0, ref _dump300))
                {
                    tag = "t300s";
                }
                else if (TryClaimDumpMark(elapsedS, 600.0, ref _dump600))
                {
                    tag = "t600s";
                }
                else
                {
                    System.Threading.Interlocked.Decrement(ref _frameDumps);
                }

                if (tag != null && System.Threading.Volatile.Read(ref _frameDumps) <= 6)
                {
                    string path = Ryujinx.Common.Nx2LogConfinement.ResolveLogPath($"frame-{tag}-c{compositeN}-img{imageId}-{(int)width}x{(int)height}.rgba");
                    System.IO.File.WriteAllBytes(path, linear.AsSpan(0, pixelBytes).ToArray());
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 frame dump {tag} c#{compositeN} -> {path} ({(int)width}x{(int)height} nonzero={nonzeroFrac:F6} hash=0x{hash:X16})");
                }
            }

            // MKW draws into the top-left corner of its present images: 2560x1440 docked in
            // 3840x2160 images, 1920x1080 handheld.
            int cropWidth = (int)Math.Min(width, MkwCropWidth);
            int cropHeight = (int)Math.Min(height, MkwCropHeight);

            ImageCrop crop = new(0, cropWidth, 0, cropHeight, false, false, false, 1.0f, 1.0f);

            TextureCreateInfo info = new(
                (int)width,
                (int)height,
                1,
                1,
                1,
                1,
                1,
                4,
                Format.R8G8B8A8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            ITexture texture = textures.Rent(renderer, info);
            texture.SetData(MemoryOwner<byte>.RentCopy(linear.AsSpan(0, pixelBytes)));
            renderer.Window.Present(texture, crop, swapBuffers);

            if (System.Threading.Interlocked.Increment(ref _compositeLogs) <= 4)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 composite image={imageId} addr=0x{address:X} {width}x{height} blockLog2={blockHeightLog2} pid={pid}");
            }
        }

        private static int _frameDumps;
        private static int _compositeN;
        private static readonly long _compositeStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
        private static int _dump60;
        private static int _dump300;
        private static int _dump600;
        private static int _frameDumpControlDone;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRenderer, PresentTextureRing> PresentTextures = new();

        // A ring of reusable host textures, accessed on the GPU thread.
        internal sealed class PresentTextureRing
        {
            private readonly ITexture[] _textures = new ITexture[3];
            private TextureCreateInfo _info;
            private int _index;
            private byte[] _linearBuffer;

            public byte[] GetLinearBuffer(int size) => RentBuffer(ref _linearBuffer, size);

            public ITexture Rent(IRenderer renderer, TextureCreateInfo info)
            {
                if (!_info.Equals(info))
                {
                    foreach (ITexture old in _textures)
                    {
                        old?.Release();
                    }

                    Array.Clear(_textures);
                    _index = 0;
                    _info = info;
                }

                ITexture texture = _textures[_index] ??= renderer.CreateTexture(info);
                _index = (_index + 1) % _textures.Length;
                return texture;
            }
        }
        private static uint _previousFenceId = uint.MaxValue;
        private static int _event11Logs;
        private static uint _previousFenceValue;

        private static int _pcSamples;

        private static int _methodSamples;

        // Samples every guest thread's PC of the application process. The GDB stub reports one
        // shared trampoline for all paused threads, so this is the only usable per-thread view.
        private static void SampleGuestPcs(Ryujinx.HLE.Switch device)
        {
            ulong pid = device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                return;
            }

            System.Text.StringBuilder sb = new();

            foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in process.DebugThreads)
            {
                if (thread.Context == null)
                {
                    continue;
                }

                sb.Append($"{thread.ThreadUid}:{thread.GetThreadName()}=0x{thread.Context.Pc:X} ");

                // Track how many distinct PCs each thread visits: a parked thread scores 1,
                // a running loop scores many. That separates "sleeping" from "executing".
                string tid = $"{thread.ThreadUid}:{thread.GetThreadName()}";

                ThreadPcs.GetOrAdd(tid, _ => new System.Collections.Concurrent.ConcurrentDictionary<ulong, int>())
                    .AddOrUpdate(thread.Context.Pc, 1, (_, v) => v + 1);
            }

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 pcs {sb}");
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<ulong, int>> ThreadPcs = new();

        /// <summary>Threads ranked by how many distinct PCs they visit, with their hottest PC.</summary>
        public static string HotPcs()
        {
            System.Text.StringBuilder sb = new();

            var ranked = new System.Collections.Generic.List<(string Tid, int Distinct, ulong Hot, int HotCount)>();

            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Concurrent.ConcurrentDictionary<ulong, int>> kv in ThreadPcs)
            {
                ulong hot = 0;
                int hotCount = 0;

                foreach (System.Collections.Generic.KeyValuePair<ulong, int> pc in kv.Value)
                {
                    if (pc.Value > hotCount)
                    {
                        hotCount = pc.Value;
                        hot = pc.Key;
                    }
                }

                ranked.Add((kv.Key, kv.Value.Count, hot, hotCount));
            }

            ranked.Sort((a, b) => b.Distinct.CompareTo(a.Distinct));

            for (int i = 0; i < ranked.Count && i < 14; i++)
            {
                sb.Append($"{ranked[i].Tid}[pc={ranked[i].Distinct},hot=0x{ranked[i].Hot:X}x{ranked[i].HotCount}] ");
            }

            return sb.ToString();
        }

        // Dumps what every application thread waits on (object type + server/waiter info).
        // Names the futex/event/session a parked loader or render thread is stuck on.
        private static void SampleGuestWaits(Ryujinx.HLE.Switch device)
        {
            ulong pid = device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                return;
            }

            foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in process.DebugThreads)
            {
                Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject[] waited = thread.WaitSyncObjects;

                if (waited == null || waited.Length == 0)
                {
                    continue;
                }

                System.Text.StringBuilder sb = new();

                foreach (Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject obj in waited)
                {
                    string name = Nx2EventNames.Lookup(obj);
                    sb.Append(name != null ? $"{obj?.GetType().Name}({name}) " : $"{obj?.GetType().Name ?? "null"} ");
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 wait tid={thread.ThreadUid}({thread.GetThreadName()}) pc=0x{thread.Context?.Pc:X} objs=[{sb}]");
            }
        }

        private static int _waitSamples;

        private static int _fenceTimeouts;

        private static int _shownLogs;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PresentSeen = new();

        private static int _compositeLogs;
        private static int _fenceLogs;
        private static int _presentLogs;
        private static int _lenLogs;
        private static int _presentWordLogs;

        // MKW draws into the top-left corner of its present images; crop to the drawn area.
        private const uint MkwCropWidth = 2560u;
        private const uint MkwCropHeight = 1440u;

        // All buffer slots reported free in the 229 reply.
        private const uint AllSlotsFreeMask = 0x7fffu;

        // Hardware syncpoint count; fence ids at or above it are not real syncpoints.
        private const uint MaxSyncpoints = 192u;

        // Syncpoint the display advances once per presented frame (the slot GetSlot hands out).
        private const uint DisplaySyncpoint = 0u;

        private static int _displaySyncLogs;

        private static byte[] RentBuffer(ref byte[] buffer, int size)
        {
            if (buffer == null || buffer.Length < size)
            {
                buffer = new byte[size];
            }

            return buffer;
        }

        private static (ulong hash, double nonzeroFrac) ComputeFrameStats(byte[] linearRgba, int bytes)
        {
            // FNV-1a 64-bit over sampled bytes (stride) for speed on large frames; full nonzero scan is cheap.
            const ulong Offset = 14695981039346656037UL;
            const ulong Prime = 1099511628211UL;
            ulong hash = Offset;
            int nonzero = 0;
            int stride = bytes > 1_000_000 ? 16 : (bytes > 64_000 ? 4 : 1);
            for (int i = 0; i < bytes; i++)
            {
                byte b = linearRgba[i];
                if (b != 0)
                {
                    nonzero++;
                }
                if ((i % stride) == 0)
                {
                    hash ^= b;
                    hash *= Prime;
                }
            }

            double frac = bytes > 0 ? nonzero / (double)bytes : 0.0;
            return (hash, frac);
        }

        private static bool TryClaimDumpMark(double elapsedS, double markS, ref int claimed)
        {
            if (elapsedS < markS)
            {
                return false;
            }

            return System.Threading.Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
        }

        private static void RunFrameDumpControlOnce()
        {
            if (!Ryujinx.Common.Nx2ProbeLog.Enabled("NX2_FRAME_DUMP_TEST"))
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _frameDumpControlDone, 1, 0) != 0)
            {
                return;
            }

            // Control: known nonzero RGBA pattern through the same hash/nonzero path (+ unswizzle path exercised).
            const int W = 16;
            const int H = 16;
            byte[] linear = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    linear[o + 0] = (byte)(x * 17);
                    linear[o + 1] = (byte)(y * 17);
                    linear[o + 2] = 0x80;
                    linear[o + 3] = 0xFF;
                }
            }

            (ulong hash, double nonzeroFrac) = ComputeFrameStats(linear, linear.Length);
            byte[] src = new byte[1024];
            byte[] dst = new byte[W * H * 4];
            try
            {
                Nx2NativeObjects.Unswizzle(src, dst, W, H, W * 4, 4);
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2_FRAME_DUMP_TEST control unswizzle threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            bool hashNonzero = hash != 14695981039346656037UL;
            bool fracExpected = nonzeroFrac > 0.90; // 993/1024 for this pattern
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2_FRAME_DUMP_TEST control known-nonzero-linear 16x16 RGBA: bytes={linear.Length} nonzero={nonzeroFrac:F6} hash=0x{hash:X16} hashNonzero={hashNonzero} frac>0.90={fracExpected} unswizzle=no-throw PASS={hashNonzero && fracExpected}");
        }
    }
}

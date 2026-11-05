// SPDX-License-Identifier: MIT
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System.Text;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    /// <summary>
    /// NX2 bring-up diagnostics: a one-shot snapshot of every guest thread and the GPU
    /// submission counters, triggered from a service call that runs in the steady loop
    /// (currently <c>IHidServer:214</c>, polled at 60 Hz) rather than from a present.
    /// This catches the render-thread state at the moment a frame should be submitted.
    /// </summary>
    static class Nx2Diagnostics
    {
        private static long _lastStateWatch;
        private static int _watchFirstPass;

        /// <summary>
        /// NX2_STATE_WATCH=1: once a second, locate the game frame-manager object passed
        /// to FUN_71025ec50c (Drive.nss+0x25ec50c) and log its state bytes. The object is
        /// identified by [+0x88]==60 (refresh rate) and [+0x1ec] in {0,1}.
        /// </summary>
        public static void WatchState(ServiceCtx context)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_STATE_WATCH") != "1" || context?.Device == null)
            {
                return;
            }

            long now = System.Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastStateWatch);

            if (now - last < 1000 || System.Threading.Interlocked.CompareExchange(ref _lastStateWatch, now, last) != last)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<ulong, KProcess> proc in context.Device.System.KernelContext.Processes)
            {
              foreach (KThread thread in proc.Value.DebugThreads)
              {
                bool isMain = thread.GetThreadName() == "MainThread";

                if (thread.Context == null || (!isMain && proc.Key != 109))
                {
                    continue;
                }

                Ryujinx.Memory.IVirtualMemoryManager mem = proc.Value.CpuMemory;
                ulong pc = thread.Context.Pc;

                if (isMain && System.Threading.Interlocked.Exchange(ref _watchFirstPass, 1) == 0)
                {
                    StringBuilder regs = new();

                    for (int r = 19; r <= 28; r++)
                    {
                        regs.Append($" x{r}=0x{thread.Context.GetX(r):X}");
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                        $"NX2SW first pid={proc.Key} tname={thread.GetThreadName()} pc=0x{pc:X} sp=0x{thread.Context.GetX(31):X}{regs}");
                }

                for (int r = 19; r <= 28; r++)
                {
                    Probe(context, thread, mem, pc, $"x{r}", thread.Context.GetX(r));
                }

                if (!isMain)
                {
                    continue;
                }

                ulong sp = thread.Context.GetX(31);

                for (ulong off = 0; off < 0x800; off += 8)
                {
                    ulong value;

                    try
                    {
                        value = mem.Read<ulong>(sp + off);
                    }
                    catch
                    {
                        break;
                    }

                    Probe(context, thread, mem, pc, $"sp+0x{off:X}", value);
                }
              }
            }
        }

        private static long _lastCurWatch;
        private static int _probeLogs;
        private static int _vtLogs;
        private static long _lastListenerWalk;
        private static int _listenerLogs;
        private static long _lastQueueWatch;
        private static int _queueLogs;
        private static int _workerVtDump;
        private static ulong _driveBase;
        private static ulong _driveSize;
        private static bool _driveResolved;
        private static int _curFirstPass;

        /// <summary>
        /// Sample the current guest thread from a hot SVC that the frame limiter
        /// FUN_71025ec50c itself calls (GetSystemTick). param_1 is then live in x19..x28
        /// or on the caller's stack, unlike at the Hid SVC.
        /// </summary>
        public static void WatchCurrentThread()
        {
            if (System.Environment.GetEnvironmentVariable("NX2_STATE_WATCH") != "1")
            {
                return;
            }

            KThread thread = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();

            if (thread?.Context == null || thread.Owner == null || thread.GetThreadName() != "MainThread")
            {
                return;
            }

            long now = System.Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastCurWatch);

            if (now - last < 1000 || System.Threading.Interlocked.CompareExchange(ref _lastCurWatch, now, last) != last)
            {
                return;
            }

            Ryujinx.Memory.IVirtualMemoryManager mem = thread.Owner.CpuMemory;
            EnsureDriveImage(thread.Owner);
            ulong pc = thread.Context.Pc;
            ulong lr = thread.Context.GetX(30);
            ulong sp = thread.Context.GetX(31);

            if (System.Threading.Interlocked.Exchange(ref _curFirstPass, 1) == 0)
            {
                StringBuilder regs = new();

                for (int r = 19; r <= 28; r++)
                {
                    regs.Append($" x{r}=0x{thread.Context.GetX(r):X}");
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                    $"NX2SW cur first pc=0x{pc:X} lr=0x{lr:X} sp=0x{sp:X}{regs}");
            }

            for (int r = 19; r <= 28; r++)
            {
                Probe(null, thread, mem, pc, $"cur x{r}", thread.Context.GetX(r));
                ProbeVtable(mem, thread.Context.GetX(r));
            }

            for (ulong off = 0; off < 0x1000; off += 8)
            {
                ulong value;

                try
                {
                    value = mem.Read<ulong>(sp + off);
                }
                catch
                {
                    break;
                }

                Probe(null, thread, mem, pc, $"cur sp+0x{off:X}", value);
                ProbeVtable(mem, value);
            }
        }

        private static void EnsureDriveImage(KProcess proc)
        {
            if (_driveResolved)
            {
                return;
            }

            try
            {
                System.Collections.Generic.List<HleProcessDebugger.Image> images = proc.Debugger.GetLoadedImages();

                foreach (HleProcessDebugger.Image image in images)
                {
                    if (image.Name == "Drive.nss")
                    {
                        _driveBase = image.BaseAddress;
                        _driveSize = image.Size;
                        break;
                    }
                }

                if (_driveBase == 0)
                {
                    foreach (HleProcessDebugger.Image image in images)
                    {
                        if (image.Size > _driveSize)
                        {
                            _driveBase = image.BaseAddress;
                            _driveSize = image.Size;
                        }
                    }
                }

                if (_driveBase != 0)
                {
                    _driveResolved = true;
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                        $"NX2VT image base=0x{_driveBase:X} size=0x{_driveSize:X}");
                }
            }
            catch
            {
            }
        }

        private static void ProbeVtable(Ryujinx.Memory.IVirtualMemoryManager mem, ulong candidate)
        {
            if (_driveBase == 0 || candidate < 0x10000 || candidate > 0x7FFF_FFFF_FFFF)
            {
                return;
            }

            try
            {
                ulong vtable = mem.Read<ulong>(candidate);

                if (vtable < 0x10000 || vtable > 0x7FFF_FFFF_FFFF)
                {
                    return;
                }

                ulong f70 = mem.Read<ulong>(vtable + 0x70);
                ulong f110 = mem.Read<ulong>(vtable + 0x110);
                ulong f118 = mem.Read<ulong>(vtable + 0x118);

                if (!InDrive(f70) || !InDrive(f110) || !InDrive(f118))
                {
                    return;
                }

                if ((f70 & 3) != 0 || (f110 & 3) != 0 || (f118 & 3) != 0)
                {
                    return;
                }

                if (System.Threading.Interlocked.Increment(ref _vtLogs) > 40)
                {
                    return;
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                    $"NX2VT app=0x{candidate:X} vt=0x{vtable:X} f70=+0x{f70 - _driveBase:X} f110=+0x{f110 - _driveBase:X} f118=+0x{f118 - _driveBase:X}");

                if (f110 == _driveBase + 0x25EC50C)
                {
                    WalkListeners(mem, candidate);
                }
            }
            catch
            {
            }
        }

        private static void WalkListeners(Ryujinx.Memory.IVirtualMemoryManager mem, ulong app)
        {
            long now = System.Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastListenerWalk);

            if (now - last < 1000 || System.Threading.Interlocked.CompareExchange(ref _lastListenerWalk, now, last) != last)
            {
                return;
            }

            ulong head = app + 0x20;
            ulong node;

            try
            {
                node = mem.Read<ulong>(app + 0x28);
            }
            catch
            {
                return;
            }

            int count = 0;

            while (node != head && node > 0x10000 && count < 64)
            {
                try
                {
                    ulong obj = mem.Read<ulong>(node + 0x10);
                    ulong callback = obj + 0x40;
                    ulong fn = mem.Read<ulong>(callback);
                    string target = InDrive(fn) ? $"+0x{fn - _driveBase:X}" : $"0x{fn:X}";

                    if (System.Threading.Interlocked.Increment(ref _listenerLogs) <= 200)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                            $"NX2LS [{count}] node=0x{node:X} obj=0x{obj:X} cb=0x{callback:X} fn={target}");
                    }

                    node = mem.Read<ulong>(node + 0x8);
                }
                catch
                {
                    break;
                }

                count++;
            }

            if (System.Threading.Interlocked.Increment(ref _listenerLogs) <= 200)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                    $"NX2LS done count={count} head=0x{head:X}");
            }
        }

        /// <summary>
        /// NX2_STATE_WATCH=1: once a second, find nn::os::LightMessageQueue objects on the
        /// loader/worker stacks and log their count/head/capacity/state. Layout from
        /// nn::os::InitializeLightMessageQueue: +0x00 buf, +0x08 cap, +0x0c count,
        /// +0x10 head, +0x18 lock, +0x20 state (2=ready), +0x28 waiter, +0x14 init=1.
        /// </summary>
        public static void WatchQueues(Switch device)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_STATE_WATCH") != "1" || device == null)
            {
                return;
            }

            long now = System.Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastQueueWatch);

            if (now - last < 1000 || System.Threading.Interlocked.CompareExchange(ref _lastQueueWatch, now, last) != last)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<ulong, KProcess> proc in device.System.KernelContext.Processes)
            {
                EnsureDriveImage(proc.Value);

                foreach (KThread thread in proc.Value.DebugThreads)
                {
                    string name = thread.GetThreadName();

                    if (thread.Context == null || name == null ||
                        !(name.StartsWith("ARes") || name.StartsWith("gear::") ||
                          name.StartsWith("ParallelDecomp") || name.StartsWith("WorkerMgr")))
                    {
                        continue;
                    }

                    Ryujinx.Memory.IVirtualMemoryManager mem = proc.Value.CpuMemory;
                    ulong sp = thread.Context.GetX(31);
                    System.Collections.Generic.HashSet<ulong> seen = new();

                    for (ulong off = 0; off < 0x800; off += 8)
                    {
                        ulong q;

                        try
                        {
                            q = mem.Read<ulong>(sp + off);
                        }
                        catch
                        {
                            break;
                        }

                        if (q < 0x10000 || q > 0x7FFF_FFFF_FFFF || !seen.Add(q))
                        {
                            continue;
                        }

                        ProbeQueue(mem, name, q);
                    }
                }
            }
        }

        private static void ProbeQueue(Ryujinx.Memory.IVirtualMemoryManager mem, string threadName, ulong q)
        {
            try
            {
                if (mem.Read<byte>(q + 0x14) != 1)
                {
                    return;
                }

                int capacity = mem.Read<int>(q + 0x8);
                int count = mem.Read<int>(q + 0xc);
                int head = mem.Read<int>(q + 0x10);
                int state = mem.Read<int>(q + 0x20);
                ulong buffer = mem.Read<ulong>(q);

                if (capacity < 1 || capacity > 0x10000 || count < 0 || count > capacity ||
                    head < 0 || head >= capacity || state < 0 || state > 2 || buffer < 0x10000)
                {
                    return;
                }

                if (System.Threading.Interlocked.Increment(ref _queueLogs) > 4000)
                {
                    return;
                }

                int workerState = -1;
                int stateE4 = -1;

                try
                {
                    // Worker object base for both families is q-0x28.
                    // FUN_710226b068 sets +0xac (1 start, 2 run, 3 done, 4 waited);
                    // FUN_710235135c/1438 ping-pong +0xe4 (2<->3).
                    workerState = mem.Read<int>(q - 0x28 + 0xac);
                    stateE4 = mem.Read<int>(q - 0x28 + 0xe4);
                }
                catch
                {
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                    $"NX2Q [{threadName}] q=0x{q:X} buf=0x{buffer:X} cap={capacity} count={count} head={head} state={state} wac={workerState} we4={stateE4}");

                if (threadName == "gear::PrepareThread" && _driveBase != 0 &&
                    System.Threading.Interlocked.Exchange(ref _workerVtDump, 1) == 0)
                {
                    try
                    {
                        ulong vtable = mem.Read<ulong>(q - 0x28);

                        for (int slot = 0; slot < 0x100; slot += 8)
                        {
                            ulong entry = mem.Read<ulong>(vtable + (ulong)slot);

                            if (InDrive(entry) && (entry & 3) == 0)
                            {
                                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                                    $"NX2VT2 worker=0x{q - 0x28:X} vt=0x{vtable:X} slot=+0x{slot:X} -> +0x{entry - _driveBase:X}");
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool InDrive(ulong address)
        {
            return address >= _driveBase && address < _driveBase + _driveSize;
        }

        private static void Probe(ServiceCtx context, KThread thread, Ryujinx.Memory.IVirtualMemoryManager mem, ulong pc, string where, ulong candidate)
        {
            if (candidate < 0x10000 || candidate > 0x7FFF_FFFF_FFFF)
            {
                return;
            }

            try
            {
                int fps = mem.Read<int>(candidate + 0x88);
                byte state = mem.Read<byte>(candidate + 0x1ec);

                if (fps < 0 || fps > 240 || (state != 0 && state != 1))
                {
                    return;
                }

                byte gate = mem.Read<byte>(candidate + 0x1c9);
                byte post = mem.Read<byte>(candidate + 0xae);
                int mode = mem.Read<int>(candidate + 0x50);

                if (gate > 1 || post > 1 || mode < 0 || mode > 2)
                {
                    return;
                }

                if (System.Threading.Interlocked.Increment(ref _probeLogs) > 60)
                {
                    return;
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                    $"NX2SW where={where} base=0x{candidate:X} pc=0x{pc:X} +88={fps} +1ec={state} +1c9={gate} +ae={post} +50={mode} sched={thread.SchedFlags}");
            }
            catch
            {
                // Candidate is not a readable object; ignore.
            }
        }

        public static void Dump(ServiceCtx context, string reason)
        {
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                $"NX2D ===== dump reason={reason} submits={Nx2Counters.Submits} presents={Nx2Counters.Presents} " +
                $"draws=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DrawBreakdown}] " +
                $"mme={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods} " +
                $"unbound={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.UnboundDrops} " +
                $"gpuid=[{INx2GpuSession.DescribeSignals()}]");

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                $"NX2D 3d=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()}]");

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                $"NX2D recent=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DescribeRecentMethods(64)}]");

            if (context?.Device == null)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<ulong, KProcess> proc in context.Device.System.KernelContext.Processes)
            {
                foreach (KThread thread in proc.Value.DebugThreads)
                {
                    StringBuilder wait = new();

                    for (int i = 0; i < thread.WaitSyncObjects.Length; i++)
                    {
                        Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject obj = thread.WaitSyncObjects[i];

                        if (obj == null)
                        {
                            continue;
                        }

                        string name = Nx2EventNames.Lookup(obj);
                        wait.Append($" w{i}=H{thread.WaitSyncHandles[i]}:{obj.GetType().Name}{(name != null ? ":" + name : "")}");
                    }

                    if (thread.SignaledObj != null)
                    {
                        wait.Append($" signaled={thread.SignaledObj.GetType().Name}");
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                        $"NX2D td pid={proc.Key} name={proc.Value.Name} uid={thread.ThreadUid} tname={thread.GetThreadName()} " +
                        $"pc=0x{thread.Context?.Pc:X} wait={thread.WaitingSync} sched={thread.SchedFlags}{wait}");
                }
            }
        }
    }
}

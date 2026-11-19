using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    /// <summary>NX2_THREAD_DUMP=1 (env-gated, observe-only): dumps guest threads from kernel objects.
    /// KThread fields quoted/used: ThreadUid, GetThreadName(), Owner.Pid/Name, SchedFlags(ThreadSchedState),
    /// WaitingSync(bool), WaitSyncObjects/WaitSyncHandles/MaxWaitSyncObjects, SignaledObj, MutexAddress(ulong),
    /// MutexOwner(KThread), WaitingInArbitration(bool). Condvar address, arbiter address, wait timeout:
    /// UNMAPPED (no obvious public field found on KThread in this tree; searched _condVar/ArbiterAddress/WaitTimeout).</summary>
    static class Nx2ThreadDump
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1" || Environment.GetEnvironmentVariable("NX2_SVC_COUNT") == "1";
        private static bool _loggedBuild;
        private static int _lastDumpSec = -999;
        private static int _lastStuckSec = -999;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> StuckArbitrationLogged = new();
        private static readonly bool StuckArbitrationStacks = Environment.GetEnvironmentVariable("NX2_ARB_STACKS") == "1";
        private static int _arbStackCount;
        private static readonly System.Diagnostics.Stopwatch _procSw = System.Diagnostics.Stopwatch.StartNew();
        private static System.Threading.Timer _timer;
        private static volatile Ryujinx.HLE.HOS.Kernel.KernelContext _kernel;
        internal static Ryujinx.HLE.HOS.Kernel.KernelContext CurrentKernel => _kernel;
        public static void CaptureKernel(Ryujinx.HLE.HOS.Kernel.KernelContext k) { if (k != null) _kernel = k; }
        private static int _timerStarted;
        private static readonly object _dumpLock = new();
        public static void EnsureTimer()
        {
            Nx2PcWatch.EnsureTimer();
            if (!Enabled)
                return;
            if (System.Threading.Interlocked.CompareExchange(ref _timerStarted, 1, 0) != 0)
                return;
            try
            {
                string eff = $"NX2_ENV_EFFECTIVE NX2_THREAD_DUMP={Environment.GetEnvironmentVariable("NX2_THREAD_DUMP")} NX2_IPC_COUNT={Environment.GetEnvironmentVariable("NX2_IPC_COUNT")} NX2_SVC_COUNT={Environment.GetEnvironmentVariable("NX2_SVC_COUNT")} NX2_METHOD_PROBE={Environment.GetEnvironmentVariable("NX2_METHOD_PROBE")} NX2_SUBMIT_LOG={Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG")} NX2_RUN_DIR={Environment.GetEnvironmentVariable("NX2_RUN_DIR")}";
                Logger.Warning?.Print(LogClass.ServiceAm, eff);
                try
                { var dir = Environment.GetEnvironmentVariable("NX2_RUN_DIR") ?? Environment.GetEnvironmentVariable("NX2_LOG_DIR"); if (!string.IsNullOrEmpty(dir)) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "env_effective.txt"), eff + "\n"); }
                catch { }
            }
            catch { }
            _timer = new System.Threading.Timer(_ => { try { TimerTick(); } catch { } }, null, 1000, 1000);
            try
            { AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { lock (_dumpLock) { DumpAt(_kernel ?? ResolveKernel(), "exit"); Nx2IpcCounters.Dump("exit"); Nx2SvcCounters.Dump("exit"); } } catch { } }; }
            catch { }
        }
        private static Ryujinx.HLE.HOS.Kernel.KernelContext ResolveKernel() => _kernel;
        private static void TimerTick()
        {
            if (!Enabled)
                return;
            EnsureTimer();
            int sec = (int)_procSw.Elapsed.TotalSeconds;
            if (sec > 0 && sec % 10 == 0 && sec != _lastDumpSec)
                Nx2IpcCounters.DumpBucket(sec);
            if (sec > 0 && sec % 5 == 0 && sec != _lastStuckSec)
            { _lastStuckSec = sec; DumpStuckArbitration($"t={sec}s", 5.0); DumpStuckRunning($"t={sec}s"); }
            if (PcSample)
                DumpRunningPcs($"t={sec}s");
            int[] marks = { 60, 150, 240, 300, 600, 900 };
            foreach (int m in marks)
                if (sec >= m && _lastDumpSec < m)
                { lock (_dumpLock) { if (_lastDumpSec < m) { _lastDumpSec = m; var k = _kernel ?? ResolveKernel(); if (k == null) Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD t={m}s kernel=null (timer)"); DumpAt(k, $"t={m}s"); Nx2IpcCounters.Dump($"t={m}s"); Nx2SvcCounters.Dump($"t={m}s"); Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.Nx2SyncptTrace.Dump($"t={m}s"); } } }
        }

        public static void MaybeLogBuildIdentity()
        {
            if (_loggedBuild)
                return;
            if (Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") != "1" &&
                Environment.GetEnvironmentVariable("NX2_IPC_COUNT") != "1" &&
                Environment.GetEnvironmentVariable("NX2_SVC_COUNT") != "1" &&
                Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") != "1" &&
                Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") != "1")
                return;
            _loggedBuild = true;
            try
            {
                string asm = typeof(Nx2ThreadDump).Assembly.Location;
                string mtime = System.IO.File.Exists(asm) ? System.IO.File.GetLastWriteTimeUtc(asm).ToString("o") : "n/a";
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_BUILD asm={asm} mtimeUtc={mtime} (git head/dirty: record externally; build may be unknown if not logged)");
            }
            catch (Exception e) { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_BUILD log failed: {e.Message}"); }
        }

        public static void DumpAt(Ryujinx.HLE.HOS.Kernel.KernelContext kernel, string tag)
        {
            if (!Enabled)
                return;
            if (kernel == null)
            { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD {tag} kernel=null (dump skipped; no thread enumeration)"); return; }
            try
            {
                int total = 0;
                bool includeStacks = Environment.GetEnvironmentVariable("NX2_STALL_STACKS") == "1";
                ulong applicationPid = kernel.Device.Processes?.ActiveApplication?.ProcessId ?? 0;
                int stacksRemaining = 12;
                var waitReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                var focusNames = new[] { "gear::gfx", "tera::", "ModuleSystemWorker", "Prepare Thread", "PrepareThread", "MainThread" };
                foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
                {
                    foreach (KThread thread in proc.Value.DebugThreads)
                    {
                        total++;
                        string tname = thread.GetThreadName() ?? "";
                        bool waitingSync = thread.WaitingSync;
                        bool waitingInArbitration = thread.WaitingInArbitration;
                        var waits = new List<string>();
                        for (int i = 0; waitingSync && i < thread.WaitSyncObjects.Length; i++)
                        {
                            KSynchronizationObject obj = thread.WaitSyncObjects[i];
                            if (obj == null)
                                continue;
                            string sig = "";
                            try
                            { sig = obj.IsSignaled() ? " SIGNALED" : " unsignaled"; }
                            catch { sig = " (IsSignaled?)"; }
                            string ev = "";
                            if (obj is KReadableEvent re)
                            {
                                try
                                { ev = $" readableSignaled={re.IsSignaled()}"; }
                                catch { }
                                // Resolve owning HLE object/service where registered (appmsg/gpuid*/spslot*/vi2*/vi2event11).
                                // Nx2EventNames keys by RuntimeHelpers.GetHashCode(KReadableEvent instance).
                                try
                                {
                                    string ownerName = Nx2EventNames.Lookup(re);
                                    if (!string.IsNullOrEmpty(ownerName))
                                        ev += $" owner={ownerName}";
                                    else
                                        ev += " owner=UNRESOLVED";
                                }
                                catch { ev += " owner=UNRESOLVED"; }
                            }
                            waits.Add($"w{i}=H{thread.WaitSyncHandles[i]}:{obj.GetType().Name}{sig}{ev}");
                        }
                        string waitKey = waitingSync ? (waits.Count > 0 ? waits[0].Split(':')[1].Split(' ')[0] : "sync") : (thread.SchedFlags.ToString());
                        waitReasons[waitKey] = waitReasons.TryGetValue(waitKey, out int c) ? c + 1 : 1;
                        KThread mutexOwner = thread.MutexOwner;
                        string mutexInfo;

                        if (waitingInArbitration)
                        {
                            // A word that no longer holds the expected value while the thread
                            // is still queued means the signal modified memory but the wakeup
                            // was lost. An unchanged word means nothing signalled this address.
                            mutexInfo = $" arbiterAddr=0x{thread.MutexAddress:X}{DescribeArbitrationWait(thread)}";
                        }
                        else
                        {
                            mutexInfo = mutexOwner != null ? $" mutexAddr=0x{thread.MutexAddress:X} owner={mutexOwner.ThreadUid}" : "";
                        }
                        string line = $"NX2_THREAD {tag} pid={proc.Key} name={proc.Value.Name} uid={thread.ThreadUid} tname={tname} " +
                                      $"sched={thread.SchedFlags} waitingSync={waitingSync} waitArb={waitingInArbitration}{mutexInfo} " +
                                      $"signaled={thread.SignaledObj?.GetType().Name ?? "-"} waits=[{string.Join("; ", waits)}]";
                        bool focus = focusNames.Any(n => tname.Contains(n, StringComparison.OrdinalIgnoreCase)) ||
                            tname.Length == 0 || IsBaseProcExecutor(tname);
                        if (focus || waitingSync)
                            Logger.Warning?.Print(LogClass.ServiceAm, line);

                        if (includeStacks && proc.Key == applicationPid && stacksRemaining > 0 &&
                            !thread.TerminationRequested && ShouldDumpStallStack(tname, waitingSync || waitingInArbitration))
                        {
                            stacksRemaining--;
                            DumpStallStack(thread, tag);
                        }
                    }
                }
                var topW = waitReasons.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}");
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD {tag} total={total} topWaitReasons=[{string.Join(", ", topW)}]");
            }
            catch (Exception e) { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD dump failed: {e.Message}"); }
        }

        private static bool IsBaseProcExecutor(string name) => name.StartsWith("BaseProcExecutor", StringComparison.Ordinal);

        private static string DescribeArbitrationWait(KThread thread)
        {
            ulong word = 0;
            bool read;

            try
            {
                word = thread.ArbitrationWaitType == ArbitrationType.WaitIfEqual64
                    ? thread.Owner.CpuMemory.Read<ulong>(thread.MutexAddress)
                    : thread.Owner.CpuMemory.Read<uint>(thread.MutexAddress);
                read = true;
            }
            catch (Exception)
            {
                // The guest word may be unmapped; the wait itself already reports that result.
                read = false;
            }

            long start = thread.ArbitrationWaitStartTimestamp;
            double ageSeconds = start == 0
                ? 0
                : (System.Diagnostics.Stopwatch.GetTimestamp() - start) / (double)System.Diagnostics.Stopwatch.Frequency;

            return FormatArbitrationWait(thread.ArbitrationWaitType, thread.ArbitrationWaitValue,
                read ? word : null, ageSeconds);
        }

        internal static string FormatArbitrationWait(ArbitrationType type, ulong want, ulong? word, double ageSeconds)
        {
            string wordText = word.HasValue
                ? $"mem=0x{word.Value:X} word={(word.Value == want ? "unchanged" : "CHANGED")}"
                : "mem=<unmapped>";

            return $" {wordText} want=0x{want:X} type={type} age={ageSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s";
        }

        private static void DumpStuckArbitration(string tag, double minAgeSeconds)
        {
            if (!Enabled)
                return;

            Ryujinx.HLE.HOS.Kernel.KernelContext kernel = _kernel ?? ResolveKernel();

            if (kernel == null)
                return;

            try
            {
                lock (_dumpLock)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    ulong appPid = kernel.Device.Processes?.ActiveApplication?.ProcessId ?? 0;

                    foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
                    {
                        if (appPid != 0 && proc.Key != appPid)
                            continue;

                        foreach (KThread thread in proc.Value.DebugThreads)
                        {
                            if (!thread.WaitingInArbitration || thread.ArbitrationWaitStartTimestamp == 0)
                                continue;

                            double age = (now - thread.ArbitrationWaitStartTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;

                            if (age < minAgeSeconds)
                                continue;

                            string key = $"{thread.ThreadUid}:0x{thread.MutexAddress:X}";

                            if (StuckArbitrationLogged.TryAdd(key, 0))
                            {
                                // The wait itself only names the word and the kernel stub pc.
                                // NX2_ARB_STACKS=1 adds the guest call path (capped) so a probe
                                // can tell which movie/sead primitive parks the thread and who
                                // is supposed to signal the same word.
                                string stack = string.Empty;

                                if (StuckArbitrationStacks &&
                                    System.Threading.Interlocked.Increment(ref _arbStackCount) <= 8)
                                {
                                    try
                                    {
                                        stack = $"\n{thread.GetGuestStackTrace()}";
                                    }
                                    catch (Exception exception)
                                    {
                                        stack = $"\n<stack unavailable: {exception.GetType().Name}: {exception.Message}>";
                                    }
                                }

                                Logger.Warning?.Print(LogClass.ServiceAm,
                                    $"NX2_ARB_STUCK {tag} pid={proc.Key} uid={thread.ThreadUid} tname={thread.GetThreadName()} " +
                                    $"pc=0x{thread.Context.Pc:X} lr=0x{thread.Context.GetX(30):X} addr=0x{thread.MutexAddress:X}{DescribeArbitrationWait(thread)}{stack}");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_ARB_STUCK {tag} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        internal static bool ShouldDumpStallStack(string name, bool waiting)
        {
            // The executor signals the module worker, so sample it even if it is idle or running.
            return IsBaseProcExecutor(name) || (waiting && (
                name.Contains("MainThread", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ModuleSystemWorker", StringComparison.Ordinal) ||
                name.StartsWith("nn.npln.", StringComparison.Ordinal)));
        }

        private static void DumpStallStack(KThread thread, string tag)
        {
            try
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2_STALL_STACK {tag} uid={thread.ThreadUid} pc=0x{thread.Context.Pc:X}{FormatCodeWindow(thread, thread.Context.Pc)}{FormatRegisters(thread)}{FormatRegisterMemory(thread)}\n{thread.GetGuestStackTrace()}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"NX2_STALL_STACK {tag} uid={thread.ThreadUid} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static readonly bool StallCodeWindow = Environment.GetEnvironmentVariable("NX2_STALL_CODE") == "1";
        private static readonly bool StallRegisters = Environment.GetEnvironmentVariable("NX2_STALL_REGS") == "1";
        private static readonly bool StallRegisterMemory = Environment.GetEnvironmentVariable("NX2_STALL_MEM") == "1";
        private static readonly bool PcSample = Environment.GetEnvironmentVariable("NX2_PC_SAMPLE") == "1";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (ulong Pc, int Streak)> RunningPcStreak = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> RunningStuckLogged = new();

        // NX2_STALL_REGS / NX2_STALL_MEM: the guest registers and the words they point at.
        // A thread that keeps the same PC across samples is spinning, and the registers name
        // the object it spins on without guessing the code's operands.
        private static string FormatRegisters(KThread thread)
        {
            if (!StallRegisters)
                return "";

            StringBuilder sb = new(" regs");

            for (int i = 0; i <= 31; i++)
            {
                sb.Append(i == 31 ? $" sp=0x{thread.Context.GetX(i):X}" : $" x{i}=0x{thread.Context.GetX(i):X}");
            }

            return sb.ToString();
        }

        private static string FormatRegisterMemory(KThread thread)
        {
            return FormatRegisterMemory(i => thread.Context.GetX(i), address => thread.Owner.CpuMemory.Read<uint>(address));
        }

        // force=true bypasses NX2_STALL_MEM for the PC watch, which always needs the object dump.
        internal static string FormatRegisterMemory(Func<int, ulong> getX, Func<ulong, uint> read32, bool force = false)
        {
            if (!StallRegisterMemory && !force)
                return "";

            StringBuilder sb = new();
            HashSet<ulong> seen = [];
            int dumped = 0;

            for (int i = 0; i <= 31 && dumped < 16; i++)
            {
                ulong address = getX(i);

                if (address < 0x1000 || address > ulong.MaxValue - 0x40 || !seen.Add(address))
                {
                    continue;
                }

                StringBuilder words = new();
                bool any = false;

                for (int k = 0; k < 16; k++)
                {
                    try
                    {
                        words.Append($" {read32(address + (ulong)k * 4):X8}");
                        any = true;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }

                if (!any)
                {
                    continue;
                }

                sb.Append($" mem x{i}@0x{address:X}=[{words.ToString().Trim()}]");
                dumped++;
            }

            return sb.ToString();
        }

        private static void DumpStuckRunning(string tag)
        {
            if (!Enabled)
                return;

            Ryujinx.HLE.HOS.Kernel.KernelContext kernel = _kernel ?? ResolveKernel();

            if (kernel == null)
                return;

            try
            {
                lock (_dumpLock)
                {
                    ulong appPid = kernel.Device.Processes?.ActiveApplication?.ProcessId ?? 0;

                    foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
                    {
                        if (appPid != 0 && proc.Key != appPid)
                            continue;

                        foreach (KThread thread in proc.Value.DebugThreads)
                        {
                            if (thread.SchedFlags != ThreadSchedState.Running)
                            {
                                RunningPcStreak.TryRemove(thread.ThreadUid, out _);
                                continue;
                            }

                            ulong pc = thread.Context.Pc;
                            int streak = RunningPcStreak.TryGetValue(thread.ThreadUid, out (ulong Pc, int Streak) previous) && previous.Pc == pc
                                ? previous.Streak + 1
                                : 1;

                            RunningPcStreak[thread.ThreadUid] = (pc, streak);

                            // Three 5 s samples on the same PC: the thread is not passing through.
                            if (streak < 3)
                                continue;

                            string key = $"{thread.ThreadUid}:0x{pc:X}";

                            // Report again periodically so a changing operand (a list walk)
                            // is distinguishable from a value that never changes (a livelock).
                            int priorLogs = RunningStuckLogged.AddOrUpdate(key, 1, (_, value) => value);

                            if (priorLogs == 1 || (streak % 6 == 0 && priorLogs <= 6))
                            {
                                Logger.Warning?.Print(LogClass.ServiceAm,
                                    $"NX2_RUN_STUCK {tag} pid={proc.Key} uid={thread.ThreadUid} tname={thread.GetThreadName()} " +
                                    $"pc=0x{pc:X} samples={streak} report={priorLogs}{FormatCodeWindow(thread, pc)}{FormatRegisters(thread)}{FormatRegisterMemory(thread)}");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_RUN_STUCK {tag} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // NX2_PC_SAMPLE=1: log the precise PC of every running app thread once per second.
        // Requires NX2_PRECISE_PC=1 so GetPc() returns the actual instruction, not the
        // translation-entry PC (which stays fixed while a loop spins further in).
        private static void DumpRunningPcs(string tag)
        {
            if (!Enabled)
                return;

            Ryujinx.HLE.HOS.Kernel.KernelContext kernel = _kernel ?? ResolveKernel();

            if (kernel == null)
                return;

            try
            {
                lock (_dumpLock)
                {
                    ulong appPid = kernel.Device.Processes?.ActiveApplication?.ProcessId ?? 0;
                    int logged = 0;

                    foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
                    {
                        if (appPid != 0 && proc.Key != appPid)
                            continue;

                        foreach (KThread thread in proc.Value.DebugThreads)
                        {
                            if (thread.SchedFlags != ThreadSchedState.Running || logged >= 8)
                                continue;

                            logged++;
                            Logger.Warning?.Print(LogClass.ServiceAm,
                                $"NX2_PC_SAMPLE {tag} uid={thread.ThreadUid} tname={thread.GetThreadName()} pc=0x{thread.Context.Pc:X}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_PC_SAMPLE {tag} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // NX2_STALL_CODE=1: dump the instruction words around the stalled PC so a spinning
        // guest loop can be named offline; the guest code lives in the title's own NSO.
        private static string FormatCodeWindow(KThread thread, ulong pc)
        {
            if (!StallCodeWindow || pc < 0x40)
                return "";

            const int before = 0x60;
            const int after = 0x80;
            StringBuilder sb = new($" code@0x{pc - before:X}=");

            for (ulong address = pc - before; address < pc + after; address += 4)
            {
                try
                {
                    sb.Append($"{thread.Owner.CpuMemory.Read<uint>(address):X8}");
                }
                catch (Exception)
                {
                    sb.Append("????????");
                }

                sb.Append(address + 4 < pc + after ? " " : "");
            }

            return sb.ToString();
        }

        public static void Tick(Horizon horizon, int progressSec)
        {
            if (!Enabled)
                return;
            if (horizon?.KernelContext != null)
                _kernel = horizon.KernelContext;
            EnsureTimer();
            // timer is primary; Tick is fallback/capture
            if ((progressSec == 60 || progressSec == 150 || progressSec == 240 || progressSec == 300 || progressSec == 600 || progressSec == 900) && _lastDumpSec < progressSec)
            {
                lock (_dumpLock)
                { if (_lastDumpSec < progressSec) { _lastDumpSec = progressSec; DumpAt(_kernel ?? horizon?.KernelContext ?? ResolveKernel(), $"t={progressSec}s"); Nx2IpcCounters.Dump($"t={progressSec}s"); Nx2SvcCounters.Dump($"t={progressSec}s"); Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.Nx2SyncptTrace.Dump($"t={progressSec}s"); } }
            }
        }
    }

    /// <summary>NX2_PC_WATCH (env-gated, observe-only): a 10 ms sampler that logs registers and
    /// the object memory at a virtual completion-poll PC; no guest execution or GPU dispatch change.</summary>
    internal static class Nx2PcWatch
    {
        private const int MaxLines = 200;
        // The blr at 0x860B878 returns here; w0 holds the bool the poll tests with tbz.
        private const ulong PostCallPc = 0x860B87C;

        private static ulong[] GetWatchPcs()
        {
            string value = Environment.GetEnvironmentVariable("NX2_PC_WATCH");
            if (string.IsNullOrWhiteSpace(value))
                return [];

            List<ulong> pcs = [];
            foreach (string part in value.Split(','))
            {
                string text = part.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[2..];
                }

                if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out ulong pc))
                {
                    pcs.Add(pc);
                }
            }

            return pcs.ToArray();
        }

        public static bool Enabled => GetWatchPcs().Length > 0;

        internal static bool Matches(ulong pc) => Array.IndexOf(GetWatchPcs(), pc) >= 0;

        public static string Format(ulong pc, KThread thread)
        {
            return Format(pc, i => thread.Context.GetX(i), address => thread.Owner.CpuMemory.Read<uint>(address));
        }

        internal static string Format(ulong pc, Func<int, ulong> getX, Func<ulong, uint> read32)
        {
            StringBuilder sb = new("regs=[");
            for (int i = 0; i <= 30; i++)
            {
                sb.Append(i == 0 ? $"x{i}=0x{getX(i):X}" : $" x{i}=0x{getX(i):X}");
            }

            sb.Append($" sp=0x{getX(31):X}]");
            if (pc == PostCallPc)
            {
                sb.Append($" w0=0x{(uint)getX(0):X8}");
            }

            sb.Append(Nx2ThreadDump.FormatRegisterMemory(getX, read32, force: true));
            return sb.ToString();
        }

        private static int _timerStarted;
        private static System.Threading.Timer _timer;
        private static int _lineCount;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LastLoggedMs = new();

        internal static void EnsureTimer()
        {
            if (!Enabled || System.Threading.Interlocked.CompareExchange(ref _timerStarted, 1, 0) != 0)
                return;

            ulong[] pcs = GetWatchPcs();
            Logger.Warning?.Print(LogClass.ServiceAm,
                $"NX2PCW enabled pcs=[{string.Join(",", pcs.Select(p => $"0x{p:X}"))}] cap={MaxLines}");
            _timer = new System.Threading.Timer(_ => { try { Tick(pcs); } catch { } }, null, 10, 10);
        }

        private static void Tick(ulong[] pcs)
        {
            if (System.Threading.Volatile.Read(ref _lineCount) >= MaxLines)
                return;

            Ryujinx.HLE.HOS.Kernel.KernelContext kernel = Nx2ThreadDump.CurrentKernel;

            if (kernel == null)
                return;

            ulong appPid = kernel.Device.Processes?.ActiveApplication?.ProcessId ?? 0;

            foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
            {
                if (appPid != 0 && proc.Key != appPid)
                    continue;

                foreach (KThread thread in proc.Value.DebugThreads)
                {
                    if (thread.SchedFlags != ThreadSchedState.Running)
                        continue;

                    ulong pc = thread.Context.Pc;

                    if (Array.IndexOf(pcs, pc) < 0)
                        continue;

                    string key = $"{thread.ThreadUid}:0x{pc:X}";

                    // One line per thread/PC per second: the 200-line cap then spans the capture,
                    // leaving repeated hits whose object bytes can be compared over time.
                    long now = Environment.TickCount64;

                    if (LastLoggedMs.TryGetValue(key, out long last) && now - last < 1000)
                        continue;

                    LastLoggedMs[key] = now;

                    if (System.Threading.Volatile.Read(ref _lineCount) >= MaxLines)
                        continue;

                    System.Threading.Interlocked.Increment(ref _lineCount);
                    Logger.Warning?.Print(LogClass.ServiceAm,
                        $"NX2PCW uid={thread.ThreadUid} pc=0x{pc:X} {Format(pc, thread)}");
                }
            }
        }
    }
}

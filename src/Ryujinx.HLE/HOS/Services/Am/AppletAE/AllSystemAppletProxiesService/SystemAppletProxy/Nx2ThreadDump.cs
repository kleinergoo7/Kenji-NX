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
        private static readonly System.Diagnostics.Stopwatch _procSw = System.Diagnostics.Stopwatch.StartNew();
        private static System.Threading.Timer _timer;
        private static volatile Ryujinx.HLE.HOS.Kernel.KernelContext _kernel;
        public static void CaptureKernel(Ryujinx.HLE.HOS.Kernel.KernelContext k) { if (k != null) _kernel = k; }
        private static int _timerStarted;
        private static readonly object _dumpLock = new();
        public static void EnsureTimer()
        {
            if (!Enabled) return;
            if (System.Threading.Interlocked.CompareExchange(ref _timerStarted, 1, 0) != 0) return;
            try
            {
                string eff = $"NX2_ENV_EFFECTIVE NX2_THREAD_DUMP={Environment.GetEnvironmentVariable("NX2_THREAD_DUMP")} NX2_IPC_COUNT={Environment.GetEnvironmentVariable("NX2_IPC_COUNT")} NX2_SVC_COUNT={Environment.GetEnvironmentVariable("NX2_SVC_COUNT")} NX2_METHOD_PROBE={Environment.GetEnvironmentVariable("NX2_METHOD_PROBE")} NX2_SUBMIT_LOG={Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG")} NX2_RUN_DIR={Environment.GetEnvironmentVariable("NX2_RUN_DIR")}";
                Logger.Warning?.Print(LogClass.ServiceAm, eff);
                try { var dir = Environment.GetEnvironmentVariable("NX2_RUN_DIR") ?? Environment.GetEnvironmentVariable("NX2_LOG_DIR"); if (!string.IsNullOrEmpty(dir)) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "env_effective.txt"), eff + "\n"); } catch { }
            }
            catch { }
            _timer = new System.Threading.Timer(_ => { try { TimerTick(); } catch { } }, null, 1000, 1000);
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { lock (_dumpLock) { DumpAt(_kernel ?? ResolveKernel(), "exit"); Nx2IpcCounters.Dump("exit"); Nx2SvcCounters.Dump("exit"); } } catch { } }; } catch { }
        }
        private static Ryujinx.HLE.HOS.Kernel.KernelContext ResolveKernel() => _kernel;
        private static void TimerTick()
        {
            if (!Enabled) return;
            EnsureTimer();
            int sec = (int)_procSw.Elapsed.TotalSeconds;
            if (sec > 0 && sec % 10 == 0 && sec != _lastDumpSec) Nx2IpcCounters.DumpBucket(sec);
            int[] marks = {60,150,240,300,600,900};
            foreach(int m in marks) if (sec >= m && _lastDumpSec < m) { lock(_dumpLock){ if (_lastDumpSec < m){ _lastDumpSec=m; var k=_kernel??ResolveKernel(); if(k==null) Logger.Warning?.Print(LogClass.ServiceAm,$"NX2_THREAD t={m}s kernel=null (timer)"); DumpAt(k,$"t={m}s"); Nx2IpcCounters.Dump($"t={m}s"); Nx2SvcCounters.Dump($"t={m}s"); Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.Nx2SyncptTrace.Dump($"t={m}s"); } } }
        }

        public static void MaybeLogBuildIdentity()
        {
            if (_loggedBuild) return;
            if (Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") != "1" &&
                Environment.GetEnvironmentVariable("NX2_IPC_COUNT") != "1" &&
                Environment.GetEnvironmentVariable("NX2_SVC_COUNT") != "1" &&
                Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") != "1" &&
                Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") != "1") return;
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
            if (!Enabled) return; if (kernel == null) { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD {tag} kernel=null (dump skipped; no thread enumeration)"); return; }
            try
            {
                int total = 0;
                var waitReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                var focusNames = new[] { "gear::gfx", "tera::", "ModuleSystemWorker", "Prepare Thread", "PrepareThread", "MainThread" };
                foreach (KeyValuePair<ulong, KProcess> proc in kernel.Processes)
                {
                    foreach (KThread thread in proc.Value.DebugThreads)
                    {
                        total++;
                        string tname = thread.GetThreadName() ?? "";
                        var waits = new List<string>();
                        for (int i = 0; i < thread.WaitSyncObjects.Length; i++)
                        {
                            KSynchronizationObject obj = thread.WaitSyncObjects[i];
                            if (obj == null) continue;
                            string sig = "";
                            try { sig = obj.IsSignaled() ? " SIGNALED" : " unsignaled"; } catch { sig = " (IsSignaled?)"; }
                            string ev = "";
                            if (obj is KReadableEvent re)
                            {
                                try { ev = $" readableSignaled={re.IsSignaled()}"; } catch { }
                                // who signals: KEvent/KReadableEvent signaled by Signal(); owning HLE object not resolved generically (unmapped)
                            }
                            waits.Add($"w{i}=H{thread.WaitSyncHandles[i]}:{obj.GetType().Name}{sig}{ev}");
                        }
                        string waitKey = thread.WaitingSync ? (waits.Count > 0 ? waits[0].Split(':')[1].Split(' ')[0] : "sync") : (thread.SchedFlags.ToString());
                        waitReasons[waitKey] = waitReasons.TryGetValue(waitKey, out int c) ? c + 1 : 1;
                        string mutexInfo = thread.MutexOwner != null ? $" mutexAddr=0x{thread.MutexAddress:X} owner={thread.MutexOwner.ThreadUid}" : (thread.MutexAddress != 0 ? $" mutexAddr=0x{thread.MutexAddress:X}" : "");
                        string line = $"NX2_THREAD {tag} pid={proc.Key} name={proc.Value.Name} uid={thread.ThreadUid} tname={tname} " +
                                      $"sched={thread.SchedFlags} waitingSync={thread.WaitingSync} waitArb={thread.WaitingInArbitration}{mutexInfo} " +
                                      $"signaled={(thread.SignaledObj!=null?thread.SignaledObj.GetType().Name:"-")} waits=[{string.Join("; ", waits)}] " +
                                      $"condvar=UNMAPPED arbiter=UNMAPPED timeout=UNMAPPED";
                        bool focus = focusNames.Any(n => tname.Contains(n, StringComparison.OrdinalIgnoreCase)) || tname.Length == 0;
                        if (focus || thread.WaitingSync) Logger.Warning?.Print(LogClass.ServiceAm, line);
                    }
                }
                var topW = waitReasons.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}");
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD {tag} total={total} topWaitReasons=[{string.Join(", ", topW)}]");
            }
            catch (Exception e) { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD dump failed: {e.Message}"); }
        }

        public static void Tick(Horizon horizon, int progressSec)
        {
            if (!Enabled) return;
            if (horizon?.KernelContext != null) _kernel = horizon.KernelContext;
            EnsureTimer();
            // timer is primary; Tick is fallback/capture
            if ((progressSec == 60 || progressSec == 150 || progressSec == 240 || progressSec == 300 || progressSec == 600 || progressSec == 900) && _lastDumpSec < progressSec)
            {
                lock(_dumpLock){ if (_lastDumpSec < progressSec){ _lastDumpSec=progressSec; DumpAt(_kernel ?? horizon?.KernelContext ?? ResolveKernel(), $"t={progressSec}s"); Nx2IpcCounters.Dump($"t={progressSec}s"); Nx2SvcCounters.Dump($"t={progressSec}s"); Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.Nx2SyncptTrace.Dump($"t={progressSec}s"); } }
            }
        }
    }
}

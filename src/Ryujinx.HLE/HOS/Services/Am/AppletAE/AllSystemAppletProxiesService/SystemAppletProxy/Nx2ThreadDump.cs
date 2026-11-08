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
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1";
        private static bool _loggedBuild;
        private static int _lastDumpSec = -999;

        public static void MaybeLogBuildIdentity()
        {
            if (_loggedBuild) return;
            if (Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") != "1" &&
                Environment.GetEnvironmentVariable("NX2_IPC_COUNT") != "1" &&
                Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") != "1" &&
                Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") != "1") return;
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
            if (!Enabled || kernel == null) { Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_THREAD {tag} skipped (kernel null, Enabled={Enabled})"); return; }
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
            if ((progressSec == 60 || progressSec == 150) && _lastDumpSec != progressSec)
            {
                _lastDumpSec = progressSec;
                var kernel = horizon?.KernelContext ?? Ryujinx.HLE.HOS.Kernel.Threading.KThread.CurrentThread?.Owner?.KernelContext;
                DumpAt(kernel, $"t={progressSec}s");
                Nx2IpcCounters.Dump($"t={progressSec}s");
            }
        }
    }
}

using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    /// <summary>NX2_SVC_COUNT=1 (env-gated, off by default, observe-only): per (guest thread uid, SVC id) counts.
    /// Increment path: SyscallHandler.SvcCall increments on every SVC dispatch with (KThread.ThreadUid, id) before dispatch.
    /// Dumps at 60/150/300s and exit via Nx2ThreadDump. Capped by natural key space (uid x svcId). Inert when unset.</summary>
    static class Nx2SvcCounters
    {
        private static readonly ConcurrentDictionary<long, long> _counts = new(); // key = ((long)uid<<32)|(uint)svcId
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_SVC_COUNT") == "1";
        private static readonly string[] SvcNames = BuildNames();

        private static string[] BuildNames()
        {
            var n = new string[256];
            n[0x0B] = "SleepThread"; n[0x18] = "WaitSynchronization"; n[0x1A] = "ArbitrateLock"; n[0x1B] = "ArbitrateUnlock";
            n[0x1C] = "WaitProcessWideKeyAtomic"; n[0x1D] = "SignalProcessWideKey"; n[0x1E] = "GetSystemTick";
            n[0x21] = "GetInfo"; n[0x22] = "GetResourceLimitLimitValue"; n[0x25] = "GetProcessId"; n[0x2C] = "MapSharedMemory";
            n[0x34] = "WaitForAddress"; n[0x35] = "SignalToAddress"; n[0x3D] = "FlushProcessDataCache"; n[0x40] = "CreateSession";
            n[0x1F] = "ConnectToNamedPort"; n[0x41] = "AcceptSession"; n[0x43] = "ReplyAndReceive"; n[0x45] = "CreateEvent";
            n[0x16] = "CloseHandle"; n[0x21] = "SendSyncRequest"; n[0x18] = "WaitSynchronization"; n[0x07] = "ExitProcess";
            n[0x08] = "CreateThread"; n[0x09] = "StartThread"; n[0x0A] = "ExitThread"; n[0x0C] = "GetThreadPriority";
            n[0x13] = "MapMemory"; n[0x14] = "UnmapMemory"; n[0x01] = "SetHeapSize"; n[0x05] = "UnmapMemory"; n[0x06] = "QueryMemory";
            n[0x27] = "Break"; n[0x28] = "OutputDebugString"; n[0x29] = "ReturnFromException"; n[0x2F] = "GetSystemInfo";
            n[0x4E] = "ReadWriteRegister"; n[0x4F] = "CreateInterruptEvent"; n[0x70] = "CallSecureMonitor";
            // canonical overrides (last wins in mind; set explicitly)
            n[0x01]="SetHeapSize"; n[0x03]="SetMemoryAttribute"; n[0x04]="MapMemory"; n[0x05]="UnmapMemory"; n[0x06]="QueryMemory";
            n[0x07]="ExitProcess"; n[0x16]="CloseHandle"; n[0x17]="ResetSignal"; n[0x18]="WaitSynchronization"; n[0x19]="CancelSynchronization";
            n[0x1A]="ArbitrateLock"; n[0x1B]="ArbitrateUnlock"; n[0x1C]="WaitProcessWideKeyAtomic"; n[0x1D]="SignalProcessWideKey";
            n[0x1F]="ConnectToNamedPort"; n[0x21]="SendSyncRequest"; n[0x22]="SendSyncRequestWithUserBuffer"; n[0x25]="GetThreadId";
            n[0x29]="GetInfo"; n[0x34]="WaitForAddress"; n[0x35]="SignalToAddress"; n[0x40]="CreateSession"; n[0x43]="ReplyAndReceive";
            n[0x45]="CreateEvent"; n[0x55]="GetProcessId"; n[0x65]="FlushProcessDataCache";
            return n;
        }

        public static void Increment(int threadUid, int svcId)
        {
            if (!Enabled) return;
            Nx2ThreadDump.EnsureTimer();
            long key = ((long)threadUid << 32) | (uint)svcId;
            _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
        }

        private static string Name(int id) => (id >= 0 && id < SvcNames.Length && SvcNames[id] != null) ? SvcNames[id] : $"SVC_0x{id:X2}";

        public static void Dump(string tag)
        {
            if (!Enabled) return;
            var byTid = _counts.GroupBy(kv => (int)(kv.Key >> 32))
                .Select(g => (uid: g.Key, total: g.Sum(x => x.Value),
                    top: g.OrderByDescending(x => x.Value).Take(12).Select(x => (svc: (int)(x.Key & 0xFFFFFFFF), n: x.Value)).ToArray()))
                .OrderByDescending(x => x.total).Take(30).ToArray();
            Logger.Warning?.Print(LogClass.KernelSvc, $"NX2_SVC_COUNT {tag} threads={byTid.Length} keys={_counts.Count}");
            foreach (var t in byTid)
            {
                var sb = new StringBuilder();
                sb.Append($"NX2_SVC_THREAD {tag} uid={t.uid} total={t.total} top=[");
                foreach (var e in t.top) sb.Append($" {Name(e.svc)}(0x{e.svc:X2})={e.n}");
                sb.Append(" ]");
                Logger.Warning?.Print(LogClass.KernelSvc, sb.ToString());
            }
        }

        public static long Get(int threadUid, int svcId)
        {
            long key = ((long)threadUid << 32) | (uint)svcId;
            return _counts.TryGetValue(key, out long v) ? v : 0;
        }
    }
}

using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    /// <summary>NX2_IPC_COUNT=1 (env-gated, off by default): per (tid, interface, cmd) counters at IPC dispatch.
    /// Increment path: IpcService.CallCmifMethod/CallTipcMethod increments on each dispatch attempt (before invoke),
    /// keyed by (threadUid, serviceTypeName, commandId). Read-only observe.</summary>
    static class Nx2IpcCounters
    {
        // key: "tid|iface|cmd"
        private static readonly ConcurrentDictionary<string, long> _counts = new();
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_IPC_COUNT") == "1"
            || Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1";

        public static void Increment(int threadUid, string iface, int commandId)
        {
            if (!Enabled) return;
            _counts.AddOrUpdate($"{threadUid}|{iface}|{commandId}", 1, (_, v) => v + 1);
        }

        public static void Dump(string tag)
        {
            if (!Enabled) return;
            var top = _counts.OrderByDescending(kv => kv.Value).Take(30).ToArray();
            var sb = new StringBuilder();
            sb.Append($"NX2_IPC_COUNT {tag} totalKeys={_counts.Count} top30=[");
            foreach (var kv in top)
            {
                var p = kv.Key.Split('|');
                sb.Append($" (tid={p[0]} {p[1]} cmd={p.ElementAtOrDefault(2)} n={kv.Value})");
            }
            sb.Append(" ]");
            Logger.Warning?.Print(LogClass.ServiceAm, sb.ToString());

            // Per-thread Hid rate-ish / post-#8 view helper: group by tid
            var byTid = _counts.GroupBy(kv => kv.Key.Split('|')[0])
                .Select(g => (tid: g.Key, n: g.Sum(x => x.Value), top: g.OrderByDescending(x => x.Value).Take(5).Select(x => { var p=x.Key.Split('|'); return $"{p[1]}:{p.ElementAtOrDefault(2)}={x.Value}"; }).ToArray()))
                .OrderByDescending(x => x.n).Take(12);
            foreach (var t in byTid)
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_IPC_COUNT {tag} tid={t.tid} total={t.n} top=[{string.Join(", ", t.top)}]");
        }

        public static long Get(string keyContains)
        {
            long sum = 0;
            foreach (var kv in _counts) if (kv.Key.Contains(keyContains, StringComparison.Ordinal)) sum += kv.Value;
            return sum;
        }
    }
}

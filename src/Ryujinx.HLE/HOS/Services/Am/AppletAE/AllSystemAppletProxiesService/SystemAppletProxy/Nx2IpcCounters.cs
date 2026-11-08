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
        private static readonly ConcurrentDictionary<string, long> _ioctlCounts = new(); // "tid|dev|var|type/nr"
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_IPC_COUNT") == "1"
            || Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1";

        public static void Increment(int threadUid, string iface, int commandId)
        {
            if (!Enabled) return;
            Nx2ThreadDump.EnsureTimer();
            _counts.AddOrUpdate($"{threadUid}|{iface}|{commandId}", 1, (_, v) => v + 1);
        }
        /// <summary>Increment path (item 4): called from nvdrv ioctl dispatch (INvDrvServices/device handlers) with device name, variant (Ioctl/Ioctl2/Ioctl3), type/nr.</summary>
        public static void IncrementIoctl(int threadUid, string device, string variant, uint type, uint nr)
        {
            if (!Enabled) return;
            _ioctlCounts.AddOrUpdate($"{threadUid}|{device}|{variant}|0x{type:X2}/0x{nr:X2}", 1, (_, v) => v + 1);
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

            var byTid = _counts.GroupBy(kv => kv.Key.Split('|')[0])
                .Select(g => (tid: g.Key, n: g.Sum(x => x.Value), top: g.OrderByDescending(x => x.Value).Take(8).Select(x => { var p=x.Key.Split('|'); return $"{p[1]}:{p.ElementAtOrDefault(2)}={x.Value}"; }).ToArray()))
                .OrderByDescending(x => x.n).Take(15);
            foreach (var t in byTid)
                Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_IPC_COUNT {tag} tid={t.tid} total={t.n} top=[{string.Join(", ", t.top)}]");

            if (_ioctlCounts.Count > 0)
            {
                var ioTop = _ioctlCounts.OrderByDescending(kv => kv.Value).Take(40).ToArray();
                var io = new StringBuilder();
                io.Append($"NX2_IOCTL_COUNT {tag} keys={_ioctlCounts.Count} [");
                foreach (var kv in ioTop) { var p=kv.Key.Split('|'); io.Append($" (tid={p[0]} {p[1]} {p[2]} {p[3]} n={kv.Value})"); }
                io.Append(" ]");
                Logger.Warning?.Print(LogClass.ServiceAm, io.ToString());
            }
            // control: SubmitGpfifo - NvHostChannel SubmitGpfifo ioctl (NvHostCustomMagic/0x48 0x1B historically? we count by observed)
            long submits = _ioctlCounts.Where(kv => kv.Key.Contains("SubmitGpfifo", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("/0x1B", StringComparison.OrdinalIgnoreCase) && kv.Key.Contains("host-gpu", StringComparison.OrdinalIgnoreCase)).Sum(kv=>kv.Value);
            Logger.Warning?.Print(LogClass.ServiceAm, $"NX2_IOCTL_COUNT {tag} submitGpfifo-like(sum heuristic)={submits} (verify by device/nr rows)");
        }

        public static long Get(string keyContains)
        {
            long sum = 0;
            foreach (var kv in _counts) if (kv.Key.Contains(keyContains, StringComparison.Ordinal)) sum += kv.Value;
            return sum;
        }
    }
}

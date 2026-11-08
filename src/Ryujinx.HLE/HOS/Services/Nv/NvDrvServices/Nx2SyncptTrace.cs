using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    /// <summary>Env-gated (NX2_SYNCPT_TRACE=1), observe-only syncpoint/fence trace. Inert when unset.</summary>
    internal static class Nx2SyncptTrace
    {
        private static readonly bool _enabled = System.Environment.GetEnvironmentVariable("NX2_SYNCPT_TRACE") == "1";
        private static int _q103Lines;
        private const int Q103Cap = 50;

        private sealed class SpSummary
        {
            public uint First, Last;
            public int Polls;
            public int DistinctValues;
            private uint _prev;
            private bool _hasPrev;
            public void Record(uint v)
            {
                if (Polls == 0) First = v;
                Last = v;
                if (!_hasPrev || _prev != v) { DistinctValues++; _prev = v; _hasPrev = true; }
                Polls++;
            }
        }

        private static readonly ConcurrentDictionary<uint, SpSummary> _q103 = new();

        private sealed class SlotRec
        {
            public uint Slot, Syncpoint;
            public ulong Target;
            public uint Mode;
            public long RequestMs;
            public long SignalMs = -1;
            public uint HostAtRequest, HostAtSignal;
            public string Note = "";
        }

        private static readonly ConcurrentDictionary<uint, SlotRec> _slots = new();
        private static readonly object _slotLock = new();

        private sealed class SubmitRec
        {
            public int Index;
            public uint Sp, FenceValue, HostAfter;
        }

        private static readonly SubmitRec[] _submits = new SubmitRec[64];
        private static int _submitCount;

        public static bool Enabled => _enabled;

        public static void Query103(uint rawCounter, uint syncpoint, uint hostValue, uint gpuValue, int callerUid, Func<uint, uint> readHost)
        {
            if (!_enabled) return;
            var s = _q103.GetOrAdd(syncpoint, _ => new SpSummary());
            lock (s) { s.Record(hostValue); }
            int n = Interlocked.Increment(ref _q103Lines);
            long ms = System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (n <= Q103Cap)
            {
                Logger.Warning?.Print(LogClass.ServiceNv,
                    $"NX2SPT 103 raw=0x{rawCounter:X} sp={syncpoint} (~raw agrees) host={hostValue} gpu={gpuValue} uid={callerUid} n={n} ms={ms}");
            }
            else if (n == Q103Cap + 1)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT 103 cap {Q103Cap} reached (individual lines); summaries continue");
            }
        }

        public static void Request201(uint slot, uint syncpoint, ulong target, uint mode, uint hostValue, bool signalledNow, bool outOfRange, bool nullHandle)
        {
            if (!_enabled) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
            lock (_slotLock)
            {
                var r = new SlotRec { Slot = slot, Syncpoint = syncpoint, Target = target, Mode = mode, RequestMs = now, HostAtRequest = hostValue };
                if (signalledNow) { r.SignalMs = now; r.HostAtSignal = hostValue; r.Note = outOfRange ? "outOfRange" : (nullHandle ? "nullHandle/already" : "alreadyReached"); }
                _slots[slot] = r;
            }
            Logger.Warning?.Print(LogClass.ServiceNv,
                $"NX2SPT 201 slot={slot} sp={syncpoint} target={target} mode={mode} hostAtReq={hostValue} signalledNow={signalledNow} outOfRange={outOfRange} nullHandle={nullHandle}");
        }

        public static void SlotSignalled(uint slot, uint hostValue)
        {
            if (!_enabled) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
            lock (_slotLock)
            {
                if (_slots.TryGetValue(slot, out var r) && r.SignalMs < 0) { r.SignalMs = now; r.HostAtSignal = hostValue; }
            }
            Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT 201-signal slot={slot} hostAtSig={hostValue}");
        }

        public static void Cancel202(uint slot, uint hostValue)
        {
            if (!_enabled) return;
            Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT 202 slot={slot} hostAtCancel={hostValue}");
        }

        public static void SubmitFence(int index1Based, uint sp, uint fenceValue, uint hostAfter)
        {
            if (!_enabled) return;
            int i = Math.Min(index1Based - 1, _submits.Length - 1);
            if (i >= 0) _submits[i] = new SubmitRec { Index = index1Based, Sp = sp, FenceValue = fenceValue, HostAfter = hostAfter };
            Interlocked.Increment(ref _submitCount);
            Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT submit#{index1Based} sp={sp} fence={fenceValue} hostAfter={hostAfter}");
        }

        public static void Dump(string tag)
        {
            if (!_enabled) return;
            var sb = new StringBuilder();
            sb.Append($"NX2SPT dump tag={tag} q103Syncpoints={_q103.Count} slots={_slots.Count} submits={Math.Min(_submitCount,_submits.Length)}");
            Logger.Warning?.Print(LogClass.ServiceNv, sb.ToString());
            foreach (var kv in _q103)
            {
                SpSummary s; lock (kv.Value) s = new SpSummary { First = kv.Value.First, Last = kv.Value.Last, Polls = kv.Value.Polls, DistinctValues = kv.Value.DistinctValues };
                // did 103 ever see >= any submit fence for this sp?
                string fences = "";
                for (int i = 0; i < Math.Min(_submitCount, _submits.Length); i++)
                {
                    var r = _submits[i]; if (r == null || r.Sp != kv.Key) continue;
                    fences += $" sub#{r.Index}f={r.FenceValue} lastSeen>={r.FenceValue}?{s.Last >= r.FenceValue}";
                }
                Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT 103sum tag={tag} sp={kv.Key} first={s.First} last={s.Last} distinct={s.DistinctValues} polls={s.Polls}{fences}");
            }
            lock (_slotLock)
            {
                foreach (var kv in _slots)
                {
                    var r = kv.Value;
                    Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT 201sum tag={tag} slot={r.Slot} sp={r.Syncpoint} target={r.Target} mode={r.Mode} hostAtReq={r.HostAtRequest} signalled={(r.SignalMs>=0?"yes":"never")} hostAtSig={r.HostAtSignal} note={r.Note}");
                }
            }
            for (int i = 0; i < Math.Min(_submitCount, _submits.Length); i++)
            {
                var r = _submits[i]; if (r == null) continue;
                // check if 103 polled this sp to >= fence
                bool reached = false; uint last = 0;
                if (_q103.TryGetValue(r.Sp, out var s2)) { lock (s2) { last = s2.Last; } reached = last >= r.FenceValue; }
                Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SPT subsum tag={tag} sub#{r.Index} sp={r.Sp} fence={r.FenceValue} hostAfter={r.HostAfter} q103last={last} q103ReachedFence={reached}");
            }
        }
    }
}

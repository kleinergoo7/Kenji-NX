using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Env-gated (NX2_METHOD_PROBE=1), observe-only method-level instrument.
    /// Records full direct/macro histograms, draw-set hits (direct vs emitted),
    /// SET_OBJECT classes per subchannel, and unmapped-word counts. Dumps to file
    /// at intervals and on process exit. Default off / inert when unset.
    /// </summary>
    static class Nx2MethodProbe
    {
        private static int _enabledVal = Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") == "1" ? 1 : 0;
        public static bool Enabled => Volatile.Read(ref _enabledVal) != 0;

        // Maxwell/NVN draw+clear registered word offsets (DeviceState byte/4). Computed via
        // Marshal.OffsetOf<ThreedClassState> (see __reconstruct/notes/tools/OffMapThreedState.cs).
        private static readonly HashSet<int> _drawSet = new()
        {
            0x42B, // DrawTextureSrcY -> DrawTexture
            0x43E, // ClearFlags -> Clear
            0x485, 0x486, // DrawVertexArray BeginEndInstance First/Subsequent
            0x585, 0x586, // DrawEnd, DrawBegin
            0x5F9, 0x5FA, 0x5FB, // DrawIndexBuffer32/16/8 First
            0x5FC, 0x5FD, 0x5FE, // DrawIndexBuffer32/16/8 Subsequent
        };

        private static readonly ConcurrentDictionary<int, int> _direct = new();     // <0xE00 word offset
        private static readonly ConcurrentDictionary<int, int> _macro = new();      // >=0xE00 start/push words
        private static readonly ConcurrentDictionary<int, int> _emitted = new();    // macro-emitted words
        private static readonly ConcurrentDictionary<int, int> _unmappedSamples = new(); // not in ThreedClassState map (sampled)
        private static readonly ConcurrentDictionary<int, int> _classesBySub = new();   // subchannel -> last classId

        private static int _totalDirect;
        private static int _totalMacro;
        private static int _totalEmitted;
        private static int _unmappedCount;
        private static int _drawDirect;
        private static int _drawEmitted;
        private static readonly ConcurrentDictionary<int, int> _drawDirectWords = new();
        private static readonly ConcurrentDictionary<int, int> _drawEmittedWords = new();
        private static int _dumpSeq;
        private static int _exitHooked;
        private static readonly object _dumpLock = new();

        private static string DumpPath()
        {
            string dir = Environment.GetEnvironmentVariable("NX2_METHOD_PROBE_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetEnvironmentVariable("NX2_LOG_DIR");
            if (string.IsNullOrEmpty(dir)) dir = "/tmp";
            return Path.Combine(dir, "nx2-methodprobe.log");
        }

        private static void MaybeHookExit()
        {
            if (Interlocked.CompareExchange(ref _exitHooked, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump("exit");
            }
        }

        public static void RecordClass(int subChannel, int classId)
        {
            if (!Enabled) return;
            MaybeHookExit();
            _classesBySub[subChannel] = classId;
        }

        public static void RecordDirect(int wordOffset, int subChannel, int boundClass)
        {
            if (!Enabled) return;
            MaybeHookExit();
            _direct.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
            int t = Interlocked.Increment(ref _totalDirect);
            if (_drawSet.Contains(wordOffset))
            {
                Interlocked.Increment(ref _drawDirect);
                _drawDirectWords.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
            }
            if ((t & 0xFF) == 0) Dump($"interval-direct-{t}");
        }

        public static void RecordMacro(int wordOffset)
        {
            if (!Enabled) return;
            MaybeHookExit();
            _macro.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
            int t = Interlocked.Increment(ref _totalMacro);
            // >=0xE00 words are macro start/push registers, not ThreedClassState draw methods; do not count as draw-set.
            if ((t & 0x3F) == 0) Dump($"interval-macro-{t}");
        }

        public static void RecordEmitted(int wordOffset)
        {
            if (!Enabled) return;
            MaybeHookExit();
            _emitted.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
            int t = Interlocked.Increment(ref _totalEmitted);
            if (_drawSet.Contains(wordOffset))
            {
                Interlocked.Increment(ref _drawEmitted);
                _drawEmittedWords.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
            }
            if ((t & 0xFF) == 0) Dump($"interval-emitted-{t}");
        }

        public static void RecordUnmapped(int wordOffset, string source)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _unmappedCount);
            _unmappedSamples.AddOrUpdate(wordOffset, 1, (_, v) => v + 1);
        }

        private static readonly bool _fullDump = Environment.GetEnvironmentVariable("NX2_METHOD_PROBE_FULL") == "1";

        public static void Dump(string reason)
        {
            if (!Enabled) return;
            // Default: totals+draw-set+top-N at a few points + exit. NX2_METHOD_PROBE_FULL=1 dumps full histograms.
            bool isExit = reason == "exit";
            bool isMilestone = reason.StartsWith("interval-direct-256") || reason.StartsWith("interval-direct-1024") ||
                               reason == "interval-direct-512" || reason.StartsWith("interval-macro-64") ||
                               reason.StartsWith("interval-macro-192") || reason.StartsWith("interval-emitted-256") ||
                               reason.StartsWith("interval-emitted-768");
            bool always = isExit || _fullDump || (Interlocked.Increment(ref _dumpSeq) <= 3);
            // For non-full non-exit, only dump at sparse milestones to control size.
            if (!_fullDump && !isExit && !always && !isMilestone && (reason.StartsWith("interval-")))
                return;

            lock (_dumpLock)
            {
                int seq = Volatile.Read(ref _dumpSeq);
                try
                {
                    var sb = new StringBuilder(2048);
                    sb.AppendLine($"NX2_METHOD_PROBE dump reason={reason} pid={Environment.ProcessId} utc={DateTime.UtcNow:o}");
                    sb.AppendLine($"totals direct={Volatile.Read(ref _totalDirect)} macro={Volatile.Read(ref _totalMacro)} emitted={Volatile.Read(ref _totalEmitted)} unmapped={Volatile.Read(ref _unmappedCount)} drawDirect={Volatile.Read(ref _drawDirect)} drawEmitted={Volatile.Read(ref _drawEmitted)}");
                    sb.Append("classesBySubchannel:");
                    foreach (var kv in _classesBySub) sb.Append($" sub{kv.Key}=0x{kv.Value:X}"); sb.AppendLine();
                    sb.Append("drawDirectWords:"); foreach (var kv in _drawDirectWords) sb.Append($" 0x{kv.Key:X}={kv.Value}"); sb.AppendLine();
                    sb.Append("drawEmittedWords:"); foreach (var kv in _drawEmittedWords) sb.Append($" 0x{kv.Key:X}={kv.Value}"); sb.AppendLine();
                    if (_fullDump || isExit)
                    {
                        sb.AppendLine("direct(<0xE00,full): " + DumpDict(_direct, 0));
                        sb.AppendLine("macro(>=0xE00,full): " + DumpDict(_macro, 0));
                        sb.AppendLine("emitted(full): " + DumpDict(_emitted, 0));
                    }
                    else
                    {
                        sb.AppendLine("direct(<0xE00,top64): " + DumpDict(_direct, 64));
                        sb.AppendLine("macro(>=0xE00,top64): " + DumpDict(_macro, 64));
                        sb.AppendLine("emitted(top64): " + DumpDict(_emitted, 64));
                    }
                    sb.AppendLine("unmappedSamples: " + DumpDict(_unmappedSamples, 64));
                    sb.AppendLine("---");
                    File.AppendAllText(DumpPath(), sb.ToString());
                }
                catch (Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"NX2_METHOD_PROBE dump failed: {e.Message}");
                }
            }
        }

        private static string DumpDict(ConcurrentDictionary<int, int> d, int topN)
        {
            if (d.IsEmpty) return "(empty)";
            var list = new List<KeyValuePair<int, int>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int n = (topN > 0 && list.Count > topN) ? topN : list.Count;
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append($"0x{list[i].Key:X}={list[i].Value}");
            }
            if (n < list.Count) sb.Append($" (+{list.Count - n} more)");
            return sb.ToString();
        }
    }
}

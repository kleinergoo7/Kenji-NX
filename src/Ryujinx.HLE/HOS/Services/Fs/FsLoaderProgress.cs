// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text;
using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Services.Fs
{
    /// <summary>
    /// Loader-progress probe. Distinguishes "slow but advancing" startup asset
    /// loading from a retry loop: monotonic bytes/unique paths mean slow, while a
    /// high repeated-open count for one path means the title is re-reading it.
    /// </summary>
    static class FsLoaderProgress
    {
        private static long _bytes;
        private static long _reads;
        private static long _opens;
        private static long _hleTicks;
        private static long _lastBytes;
        private static long _lastReads;
        private static long _lastTick;

        private const int MaxTrackedPaths = 8192;

        private static readonly ConcurrentDictionary<string, int> PathCounts = new();

        // Blocks (1 MiB) read more than once. A single block read thousands of times
        // means the title is retrying, not streaming.
        private static readonly ConcurrentDictionary<long, int> BlockCounts = new();
        private static long _maxBlockRepeats;
        private static long _lastLogTick;
        private static readonly bool LogEnabled =
            System.Environment.GetEnvironmentVariable("NX2_FS_LOG") == "1";
        private static long _maxBlockOffset = -1;

        // Application filesystem view (NX2_FS_TRACE). Observe-only: these counters never
        // influence Fs results, they only describe what the application pid could access.
        internal static readonly bool TraceEnabled =
            System.Environment.GetEnvironmentVariable("NX2_FS_TRACE") == "1";

        private const int MaxTraceLines = 200;

        private static long _appPid = -1;
        private static long _filterPid = -1;
        private static long _viewOpens;
        private static long _contentOpens;
        private static long _contentFails;
        private static long _viewReads;
        private static long _viewBytes;
        private static long _traceLines;

        public static void OnRead(long bytes)
        {
            OnRead(bytes, 0);
        }

        public static void OnRead(long bytes, long elapsedTicks)
        {
            OnRead(bytes, elapsedTicks, -1);
        }

        public static void OnRead(long bytes, long elapsedTicks, long offset)
        {
            if (bytes <= 0)
            {
                return;
            }

            System.Threading.Interlocked.Add(ref _bytes, bytes);
            System.Threading.Interlocked.Add(ref _hleTicks, elapsedTicks);
            System.Threading.Interlocked.Increment(ref _reads);

            if (LogEnabled)
            {
                long now = System.Environment.TickCount64;
                long last = System.Threading.Interlocked.Read(ref _lastLogTick);

                if (now - last >= 60000 && System.Threading.Interlocked.CompareExchange(ref _lastLogTick, now, last) == last)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceFs,
                        $"NX2FS {Describe()}");
                }
            }

            if (offset >= 0)
            {
                long block = offset >> 20;

                if (BlockCounts.Count < MaxTrackedPaths || BlockCounts.ContainsKey(block))
                {
                    int n = BlockCounts.AddOrUpdate(block, 1, (_, v) => v + 1);

                    if (n > System.Threading.Interlocked.Read(ref _maxBlockRepeats))
                    {
                        System.Threading.Interlocked.Exchange(ref _maxBlockRepeats, n);
                        System.Threading.Interlocked.Exchange(ref _maxBlockOffset, block << 20);
                    }
                }
            }
        }

        public static void OnOpen(string path)
        {
            System.Threading.Interlocked.Increment(ref _opens);

            if (path == null)
            {
                return;
            }

            if (PathCounts.Count >= MaxTrackedPaths && !PathCounts.ContainsKey(path))
            {
                return;
            }

            PathCounts.AddOrUpdate(path, 1, (_, v) => v + 1);
        }

        internal static void ResetFsView()
        {
            System.Threading.Interlocked.Exchange(ref _appPid, -1);
            System.Threading.Interlocked.Exchange(ref _filterPid, -1);
            System.Threading.Interlocked.Exchange(ref _viewOpens, 0);
            System.Threading.Interlocked.Exchange(ref _contentOpens, 0);
            System.Threading.Interlocked.Exchange(ref _contentFails, 0);
            System.Threading.Interlocked.Exchange(ref _viewReads, 0);
            System.Threading.Interlocked.Exchange(ref _viewBytes, 0);
            System.Threading.Interlocked.Exchange(ref _traceLines, 0);
        }

        internal static void SetFsViewPid(ulong pid)
        {
            System.Threading.Interlocked.Exchange(ref _appPid, (long)pid);
            System.Threading.Interlocked.Exchange(ref _filterPid, (long)pid);
        }

        // Runtime: fsp-srv handles guest Fs calls on its own thread and the IPC header
        // carries no caller pid for these requests, so observe all traffic through the
        // server and label it with the active application pid. Individual trace lines
        // still show the raw caller pid.
        internal static void SetFsViewAppPid(ulong pid)
        {
            System.Threading.Interlocked.Exchange(ref _appPid, (long)pid);
        }

        internal static void OnFsOpen(ulong pid, string path)
        {
            if (!IsViewPid(pid))
            {
                return;
            }

            System.Threading.Interlocked.Increment(ref _viewOpens);

            if (IsContentPath(path))
            {
                System.Threading.Interlocked.Increment(ref _contentOpens);
            }
        }

        internal static void OnFsOpenFail(ulong pid, string path)
        {
            if (!IsViewPid(pid))
            {
                return;
            }

            System.Threading.Interlocked.Increment(ref _viewOpens);

            if (IsContentPath(path))
            {
                System.Threading.Interlocked.Increment(ref _contentFails);
            }
        }

        internal static void OnFsRead(ulong pid, long bytes)
        {
            if (!IsViewPid(pid) || bytes <= 0)
            {
                return;
            }

            System.Threading.Interlocked.Increment(ref _viewReads);
            System.Threading.Interlocked.Add(ref _viewBytes, bytes);
        }

        private static bool IsViewPid(ulong pid)
        {
            long filter = System.Threading.Interlocked.Read(ref _filterPid);

            return filter < 0 || pid == (ulong)filter;
        }

        // Content = anything outside save/system/config-style paths.
        private static bool IsContentPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // "/save.bin" is this title's save file, not game content.
            return path != "/save.bin" && !HasPathPrefix(path, "/save") && !HasPathPrefix(path, "/system") &&
                !HasPathPrefix(path, "/config") && !HasPathPrefix(path, "/register") &&
                !HasPathPrefix(path, "/device") && !HasPathPrefix(path, "/host");
        }

        private static bool HasPathPrefix(string path, string prefix)
        {
            return path == prefix || path.StartsWith(prefix + "/", System.StringComparison.Ordinal);
        }

        internal static string DescribeFsView()
        {
            return $"pid={System.Threading.Interlocked.Read(ref _appPid)} " +
                $"opens={System.Threading.Interlocked.Read(ref _viewOpens)} " +
                $"contentOpens={System.Threading.Interlocked.Read(ref _contentOpens)} " +
                $"contentFails={System.Threading.Interlocked.Read(ref _contentFails)} " +
                $"reads={System.Threading.Interlocked.Read(ref _viewReads)} " +
                $"bytes={System.Threading.Interlocked.Read(ref _viewBytes)}";
        }

        internal static void Trace(string line)
        {
            if (TraceEnabled && System.Threading.Interlocked.Increment(ref _traceLines) <= MaxTraceLines)
            {
                Logger.Warning?.Print(LogClass.ServiceFs, $"NX2FSTRACE {line}");
                Logger.Warning?.Print(LogClass.ServiceFs, $"NX2FSVIEW {DescribeFsView()}");
            }
        }

        public static string Describe()
        {
            long now = System.Environment.TickCount64;
            long bytes = System.Threading.Interlocked.Read(ref _bytes);
            long reads = System.Threading.Interlocked.Read(ref _reads);
            long opens = System.Threading.Interlocked.Read(ref _opens);
            long hleTicks = System.Threading.Interlocked.Read(ref _hleTicks);

            long lastTick = System.Threading.Interlocked.Exchange(ref _lastTick, now);
            long lastBytes = System.Threading.Interlocked.Exchange(ref _lastBytes, bytes);
            long lastReads = System.Threading.Interlocked.Exchange(ref _lastReads, reads);

            double seconds = lastTick == 0 ? 0 : (now - lastTick) / 1000.0;
            double mibPerSec = seconds > 0 ? (bytes - lastBytes) / (1024.0 * 1024.0) / seconds : 0;

            int maxRepeat = 0;
            string maxPath = string.Empty;

            foreach (System.Collections.Generic.KeyValuePair<string, int> kv in PathCounts)
            {
                if (kv.Value > maxRepeat)
                {
                    maxRepeat = kv.Value;
                    maxPath = kv.Key;
                }
            }

            StringBuilder sb = new();

            sb.Append($"bytes={bytes / (1024 * 1024)}MiB reads={reads} readsDelta={reads - lastReads} ");
            double hleMsPerRead = reads > 0 ? hleTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / reads : 0;
            sb.Append($"rate={mibPerSec:F1}MiB/s hle={hleMsPerRead:F2}ms/read opens={opens} uniq={PathCounts.Count}");
            sb.Append($" maxRepeat={maxRepeat}({maxPath})");
            sb.Append($" blocks={BlockCounts.Count} maxBlockRepeat={System.Threading.Interlocked.Read(ref _maxBlockRepeats)}@0x{System.Threading.Interlocked.Read(ref _maxBlockOffset):X}");

            return sb.ToString();
        }
    }
}

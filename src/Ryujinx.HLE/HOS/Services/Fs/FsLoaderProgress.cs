// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text;

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

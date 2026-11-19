// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Env-gated, observe-only trace of compute MME macro code uploads. Counts and hashes
    /// every word written into the compute code bank; no upload or decode behaviour is
    /// changed. Log lines are emitted only when <c>NX2_MME_UPLOAD=1</c>, capped at 200.
    /// </summary>
    public static class Nx2MacroUploadTrace
    {
        private const ulong FnvOffsetBasis = 1469598103934665603UL;
        private const ulong FnvPrime = 1099511628211UL;
        private const int Cap = 200;

        internal const int BankWords = 65536;

        private static readonly bool _enabled = Environment.GetEnvironmentVariable("NX2_MME_UPLOAD") == "1";

        private static int _lines;
        private static int _summaryLogged;
        private static long _nonzero;
        private static long _words;
        private static ulong _hash = FnvOffsetBasis;
        private static int _firstWord;
        private static int _lastWord;

        /// <summary>Records one word written into the compute code bank.</summary>
        public static void RecordWord(int word)
        {
            if (word != 0)
            {
                Interlocked.Increment(ref _nonzero);
            }

            Interlocked.Increment(ref _words);

            if (Volatile.Read(ref _words) == 1)
            {
                Volatile.Write(ref _firstWord, word);
            }

            Volatile.Write(ref _lastWord, word);

            ulong hash = Volatile.Read(ref _hash);
            hash ^= (uint)word;
            hash *= FnvPrime;
            Volatile.Write(ref _hash, hash);
        }

        /// <summary>Non-zero words recorded so far.</summary>
        public static long NonZero => Volatile.Read(ref _nonzero);

        /// <summary>FNV-1a hash of all words recorded so far.</summary>
        public static ulong Hash => Volatile.Read(ref _hash);

        /// <summary>First and last words recorded (for the add log line).</summary>
        public static int FirstWord => Volatile.Read(ref _firstWord);

        public static int LastWord => Volatile.Read(ref _lastWord);

        /// <summary>Logs one upload line when enabled and under cap.</summary>
        public static void Log(string message)
        {
            if (_enabled && Interlocked.Increment(ref _lines) <= Cap)
            {
                Logger.Warning?.Print(LogClass.Gpu, message);
            }
        }

        /// <summary>
        /// Logs the compute bank summary once, at the first compute macro execution.
        /// Bypasses the line cap so the decisive bank result is always emitted.
        /// </summary>
        public static void LogSummaryOnce()
        {
            if (_enabled && Interlocked.Exchange(ref _summaryLogged, 1) == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, Summary);
            }
        }

        /// <summary>One-line summary of the compute code bank.</summary>
        public static string Summary =>
            $"NX2MMEUP bank nonzero={NonZero} hash=0x{Hash:X16} words={BankWords}";

        /// <summary>Test-only state reset so fixtures can assert exact counts.</summary>
        public static void Reset()
        {
            Volatile.Write(ref _nonzero, 0);
            Volatile.Write(ref _words, 0);
            Volatile.Write(ref _hash, FnvOffsetBasis);
            Volatile.Write(ref _firstWord, 0);
            Volatile.Write(ref _lastWord, 0);
            Volatile.Write(ref _lines, 0);
            Volatile.Write(ref _summaryLogged, 0);
        }
    }
}

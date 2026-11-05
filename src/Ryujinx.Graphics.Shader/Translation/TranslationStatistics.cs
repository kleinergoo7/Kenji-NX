using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Graphics.Shader.Translation
{
    /// <summary>
    /// Shader translation statistics (v1.1 addition).
    /// Reconstructed from the v1.1 decompile's counter/字典 shape.
    /// </summary>
    public static class TranslationStatistics
    {
        private const int MaxEntries = 256;

        private static readonly ConcurrentDictionary<string, long> _counts = new();
        private static long _programs;
        private static long _incompletePrograms;

        public static long Programs => Interlocked.Read(in _programs);
        public static long IncompletePrograms => Interlocked.Read(in _incompletePrograms);

        public static void Increment(string key)
        {
            if (_counts.Count < MaxEntries || _counts.ContainsKey(key))
            {
                _counts.AddOrUpdate(key, 1L, (_, value) => value + 1);
            }
        }

        public static void ReportResult(bool complete)
        {
            Interlocked.Increment(ref _programs);

            if (!complete)
            {
                Interlocked.Increment(ref _incompletePrograms);
            }
        }

        public static IReadOnlyList<KeyValuePair<string, long>> GetResults()
        {
            return (from entry in _counts
                    orderby entry.Value descending, entry.Key
                    select entry).ToList();
        }

        public static void Reset()
        {
            _counts.Clear();
            Interlocked.Exchange(ref _programs, 0L);
            Interlocked.Exchange(ref _incompletePrograms, 0L);
        }
    }
}

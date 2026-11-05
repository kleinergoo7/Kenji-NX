using System.Threading;

namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// Tracks host GPU ASTC decoding availability and statistics (v1.1 addition).
    /// Reconstructed from the v1.1 decompile (availability snapshot + decoded counters).
    /// </summary>
    public static class GpuAstcDecode
    {
        private sealed class Registry : System.IDisposable
        {
            private sealed class Snapshot
            {
                public readonly bool Available;
                public readonly string State;
                public readonly string? Detail;
                public readonly bool Disposed;

                public Snapshot(bool available, string state, string? detail, bool disposed = false)
                {
                    Available = available;
                    State = state;
                    Detail = detail;
                    Disposed = disposed;
                }

                public Snapshot With(bool? available = null, bool? disposed = null)
                {
                    return new Snapshot(available ?? Available, State, Detail, disposed ?? Disposed);
                }
            }

            private Snapshot _snapshot = new(false, "Unavailable", null);
            private long _decodedTextures;
            private long _decodedBlocks;

            public bool IsCurrent => Volatile.Read(ref _current) == this;
            public bool Available => IsCurrent && Volatile.Read(ref _snapshot).Available;
            public string State => Volatile.Read(ref _snapshot).State;
            public string? Detail => Volatile.Read(ref _snapshot).Detail;
            public long DecodedTextures => Interlocked.Read(ref _decodedTextures);
            public long DecodedBlocks => Interlocked.Read(ref _decodedBlocks);

            public bool TrySet(string state, bool available, string? detail = null, string? expectedState = null)
            {
                while (IsCurrent)
                {
                    Snapshot current = Volatile.Read(ref _snapshot);

                    if (current.Disposed || (expectedState != null && current.State != expectedState))
                    {
                        return false;
                    }

                    if (Interlocked.CompareExchange(ref _snapshot, new Snapshot(available, state, detail), current) == current)
                    {
                        return IsCurrent;
                    }
                }

                return false;
            }

            public void Report(long blocks)
            {
                if (Available)
                {
                    Interlocked.Increment(ref _decodedTextures);
                    Interlocked.Add(ref _decodedBlocks, blocks);
                }
            }

            public void Dispose()
            {
                Snapshot current;

                do
                {
                    current = Volatile.Read(ref _snapshot);

                    if (current.Disposed)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref _snapshot, current.With(disposed: true), current) != current);
            }
        }

        private static Registry? _current;

        public static bool Available => Volatile.Read(ref _current)?.Available ?? false;
        public static string State => Volatile.Read(ref _current)?.State ?? "Unavailable";
        public static string? Detail => Volatile.Read(ref _current)?.Detail;
    }
}

using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Kind of compute dispatch described by a <see cref="QmdDescriptor"/>.
    /// </summary>
    enum QmdDispatchKind
    {
        Compute,
        Mme64,
    }

    /// <summary>
    /// Dispatch action for a <see cref="QmdDescriptor"/>.
    /// </summary>
    enum QmdDispatchAction
    {
        Launch,
        Resume,
    }

    /// <summary>
    /// Versioned compute dispatch descriptor.
    /// v1.1 (Switch 2 enablement): MME64/QMD 3.0 dispatch plumbing. Records the
    /// bound engine class, QMD mapping state and the major/minor/current/oldest
    /// versions negotiated through the Set/CheckQmdVersion registers.
    /// </summary>
    struct QmdDescriptor
    {
        public QmdDispatchKind Kind;
        public int BoundClassId;
        public QmdDispatchAction Action;
        public int Select;
        public bool QmdAddressIsZero;
        public bool QmdAddressMapped;
        public int MajorVersion;
        public int MinorVersion;
        public int CurrentVersion;
        public int OldestVersion;
        public ulong RingBase;

        /// <summary>One-line description including the QMD ring base.</summary>
        public string Describe() => $"kind={Kind} action={Action} ring=0x{RingBase:X}";
    }

    /// <summary>
    /// The three Kepler compute PCAS/QMD signal methods and their dispatch
    /// eligibility (Eglantine kepler_compute.cpp:364-365). All three read the
    /// QMD address from register 0xAD (SendPcasA), not from their own argument.
    /// </summary>
    static class PcasMethodAction
    {
        public const int SendPcasB = 0xAE;
        public const int SendSignalingPcasB = 0xAF;
        public const int SendPcasC = 0xB0;

        /// <summary>0xAE always skips; 0xAF skips unless bit 1 is set; 0xB0 skips on low nibble 0 or 1.</summary>
        public static bool Eligible(int method, int argument) => method switch
        {
            SendSignalingPcasB => (argument & 2) != 0,
            SendPcasC => (argument & 15) >= 2,
            _ => false,
        };
    }

    /// <summary>
    /// Tracks compute dispatches for the MME64/QMD scheduler.
    /// v1.1 (Switch 2 enablement).
    /// </summary>
    class QmdScheduler
    {
        private QmdDescriptor _last;
        private long _dispatchCount;

        public void Track(ref QmdDescriptor descriptor)
        {
            _last = descriptor;
            _dispatchCount++;
        }

        public QmdDescriptor Last => _last;

        public long DispatchCount => _dispatchCount;
    }

    /// <summary>
    /// NX2 QMD ring state. Observe-only: tracks QMD ring actions but never changes dispatch
    /// behaviour. Eglantine native_qmd.h:163-166 keeps one table for every channel because a
    /// QMD's dependency counter is state of the QMD itself — title 1 schedules a QMD on one
    /// channel while another decrements it (NX2_NOTES.md row 84).
    /// </summary>
    class Nx2QmdRing
    {
        private sealed class Entry
        {
            public ulong Base;
            public int Action;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<ulong, Entry> _entries = new();

        private ulong _base;

        /// <summary>Sets the ring base fed from MME data RAM word 0x27.</summary>
        public void SetBase(ulong baseVa) => _base = baseVa;

        /// <summary>Number of distinct QMD VAs tracked in the process-global table.</summary>
        public int DispatchCount
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        /// <summary>
        /// Tracks a QMD action: 3/2 load (schedule) a QMD, 7/12 adjust an existing entry,
        /// 0/1 are no-ops (Eglantine native_qmd.h:80-113). Decrements/increments never add
        /// a new entry — only load actions create one.
        /// </summary>
        public void Schedule(ulong qmdVa, int action)
        {
            lock (_lock)
            {
                switch (action)
                {
                    case 3:
                    case 2:
                        if (_entries.TryGetValue(qmdVa, out Entry existing))
                        {
                            existing.Base = _base;
                            existing.Action = action;
                        }
                        else
                        {
                            _entries[qmdVa] = new Entry { Base = _base, Action = action };
                        }
                        break;
                    case 7:
                    case 12:
                        if (_entries.TryGetValue(qmdVa, out Entry target))
                        {
                            target.Action = action;
                        }
                        break;
                }
            }

            Nx2QmdTrace.LogRing($"NX2QMRING base=0x{_base:X} qmd=0x{qmdVa:X} action={action}");
        }

        /// <summary>Clears the process-global table (test isolation).</summary>
        internal static void Reset()
        {
            lock (_lock) { _entries.Clear(); }
        }
    }
}

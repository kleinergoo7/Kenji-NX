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
}

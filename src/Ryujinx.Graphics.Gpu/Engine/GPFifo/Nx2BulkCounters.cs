using System;
using System.Threading;
namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>NX2_METHOD_PROBE-gated counters for fast-path bulk consumers (item 5). Off by default.
    /// Increment paths (named): TryFastUniformBufferUpdate (GPFifoProcessor.cs, 0x8E4 NonInc) increments
    /// UboCalls and UboBulkWords by meth.MethodCount (data words consumed; header consumed by caller loop advance = +1).
    /// TryFastI2mBufferUpdate (GPFifoProcessor.cs, 0x6D NonInc subch<=2) increments I2mCalls and I2mBulkWords by consumeCount.
    /// First/last method addr recorded per path (best-effort, volatile).</summary>
    public static class Nx2BulkCounters
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") == "1";
        public static long UboCalls, UboBulkWords, I2mCalls, I2mBulkWords;
        public static int UboFirst = -1, UboLast, I2mFirst = -1, I2mLast;
        public static void Ubo(int methodAddr, int bulkWords) { if (!Enabled) return; Interlocked.Increment(ref UboCalls); Interlocked.Add(ref UboBulkWords, bulkWords);
            if (UboFirst < 0) Interlocked.CompareExchange(ref UboFirst, methodAddr, -1); Volatile.Write(ref UboLast, methodAddr); }
        public static void I2m(int methodAddr, int bulkWords) { if (!Enabled) return; Interlocked.Increment(ref I2mCalls); Interlocked.Add(ref I2mBulkWords, bulkWords);
            if (I2mFirst < 0) Interlocked.CompareExchange(ref I2mFirst, methodAddr, -1); Volatile.Write(ref I2mLast, methodAddr); }
        public static string Describe() => !Enabled ? "disabled" : $"ubo(calls={Interlocked.Read(ref UboCalls)} bulkWords={Interlocked.Read(ref UboBulkWords)} first=0x{UboFirst:X} last=0x{Volatile.Read(ref UboLast):X}) i2m(calls={Interlocked.Read(ref I2mCalls)} bulkWords={Interlocked.Read(ref I2mBulkWords)} first=0x{I2mFirst:X} last=0x{Volatile.Read(ref I2mLast):X})";
    }
}

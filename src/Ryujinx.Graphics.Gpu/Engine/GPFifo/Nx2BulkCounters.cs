using Ryujinx.Common.Logging;
using System;
using System.Threading;
namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>NX2_METHOD_PROBE-gated bulk/word counters (env-gated). Increment paths (named):
    /// TryFastUniformBufferUpdate (0x8E4, NonInc, MethodCount==0 header path) -> UboCalls++, UboBulkWords+=meth.MethodCount.
    /// TryFastI2mBufferUpdate (0x6D, state.Method, NonInc, subch<=2, MethodCount!=0 path) -> I2mCalls++, I2mBulkWords+=consumeCount.
    /// Reject debug: DebugReject at same sites when 0x8E4/0x6D seen but clause fails (capped 20/path).
    /// Word accounting (ProcessLoop, per consumed commandBuffer word): SendDataWords++ on MethodCount!=0 non-fast path;
    /// FastI2mWords+=consumeCount; FastUboWords+=(1+MethodCount) on fast-ubo; SetupHeaderWords++ on MethodCount==0 non-fast header/setup.
    /// Per-pushbuffer totals logged (capped) as: total=cmdBufLen send+fastI2m+fastUbo+setup (+ state carry noted if any).</summary>
    public static class Nx2BulkCounters
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("NX2_METHOD_PROBE") == "1";
        public static bool DebugEnabled => Enabled; // capped debug
        public static long UboCalls, UboBulkWords, I2mCalls, I2mBulkWords;
        public static int UboFirst = -1, UboLast, I2mFirst = -1, I2mLast;
        private static int _uboReject, _i2mReject;
        // word accounting (global, GPU thread dominant; Interlocked for safety)
        public static long SendDataWords, FastI2mWords, FastUboWords, SetupHeaderWords, PushBuffers, PushBufferWords;
        public static void Ubo(int methodAddr, int bulkWords) { if (!Enabled) return; Interlocked.Increment(ref UboCalls); Interlocked.Add(ref UboBulkWords, bulkWords);
            if (UboFirst < 0) Interlocked.CompareExchange(ref UboFirst, methodAddr, -1); Volatile.Write(ref UboLast, methodAddr); }
        public static void I2m(int methodAddr, int bulkWords) { if (!Enabled) return; Interlocked.Increment(ref I2mCalls); Interlocked.Add(ref I2mBulkWords, bulkWords);
            if (I2mFirst < 0) Interlocked.CompareExchange(ref I2mFirst, methodAddr, -1); Volatile.Write(ref I2mLast, methodAddr); }
        public static void DebugReject(string path, int method, int methodCount, int available, string detail)
        {
            if (!Enabled) return;
            ref int c = ref path == "UBO" ? ref _uboReject : ref _i2mReject;
            if (Interlocked.Increment(ref c) <= 20)
                Logger.Warning?.Print(LogClass.Gpu, $"NX2_BULK_DEBUG reject path={path} method=0x{method:X} MethodCount={methodCount} available={available} {detail}");
        }
        public static void PbStart(int words) { if (!Enabled) return; Interlocked.Increment(ref PushBuffers); Interlocked.Add(ref PushBufferWords, words); }
        public static void AddSend(int n=1){ if(Enabled) Interlocked.Add(ref SendDataWords,n); }
        public static void AddI2m(int n){ if(Enabled) Interlocked.Add(ref FastI2mWords,n); }
        public static void AddUbo(int n){ if(Enabled) Interlocked.Add(ref FastUboWords,n); }
        public static void AddSetup(int n=1){ if(Enabled) Interlocked.Add(ref SetupHeaderWords,n); }
        public static void PbEnd(int total, int send, int i2m, int ubo, int setup, int carryMethodCount)
        {
            if (!Enabled) return;
            long n = Interlocked.Read(ref PushBuffers);
            if (n <= 40)
                Logger.Warning?.Print(LogClass.Gpu, $"NX2_BULK_PB n={n} total={total} send={send} fastI2m={i2m} fastUbo={ubo} setup={setup} sum={send+i2m+ubo+setup} carryMethodCount={carryMethodCount} {(send+i2m+ubo+setup==total?"OK":"DIFF="+(total-(send+i2m+ubo+setup)))}");
        }
        public static string Describe() => !Enabled ? "disabled" : $"ubo(calls={Interlocked.Read(ref UboCalls)} bulkWords={Interlocked.Read(ref UboBulkWords)} first=0x{UboFirst:X} last=0x{Volatile.Read(ref UboLast):X}) i2m(calls={Interlocked.Read(ref I2mCalls)} bulkWords={Interlocked.Read(ref I2mBulkWords)} first=0x{I2mFirst:X} last=0x{Volatile.Read(ref I2mLast):X}) words[pb={Interlocked.Read(ref PushBuffers)} tot={Interlocked.Read(ref PushBufferWords)} send={Interlocked.Read(ref SendDataWords)} fi2m={Interlocked.Read(ref FastI2mWords)} fubo={Interlocked.Read(ref FastUboWords)} setup={Interlocked.Read(ref SetupHeaderWords)}]";
    }
}

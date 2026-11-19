// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Env-gated, observe-only trace of compute-class PCAS entries. Counts every
    /// <c>SendSignalingPcasB</c> call (including qmd=0 Resume paths); no dispatch
    /// behaviour is changed. Log lines are emitted only when <c>NX2_PCAS_TRACE=1</c>.
    /// </summary>
    public static class Nx2PcasTrace
    {
        private static readonly bool _enabled = Environment.GetEnvironmentVariable("NX2_PCAS_TRACE") == "1";

        private const int Cap = 200;
        private static int _lines;
        private static long _calls;
        private static long _zero;
        private static long _nonzero;
        private static long _mme64;

        /// <summary>Records a PCAS entry. Counts unconditionally; logs when enabled and under cap.</summary>
        public static void Enter(uint qmdAddress, bool usesMme64)
        {
            Interlocked.Increment(ref _calls);

            if (qmdAddress == 0)
            {
                Interlocked.Increment(ref _zero);
            }
            else
            {
                Interlocked.Increment(ref _nonzero);
            }

            if (usesMme64)
            {
                Interlocked.Increment(ref _mme64);
            }

            if (_enabled && Interlocked.Increment(ref _lines) <= Cap)
            {
                string action = qmdAddress == 0 ? "Resume" : "Launch";
                Logger.Warning?.Print(LogClass.ServiceNv,
                    $"NX2PCAS qmd=0x{qmdAddress:X8} action={action} mme64={usesMme64}");
            }
        }

        /// <summary>One-line cumulative summary of PCAS entries.</summary>
        public static string Summary =>
            $"calls={Volatile.Read(ref _calls)} zero={Volatile.Read(ref _zero)} nonzero={Volatile.Read(ref _nonzero)} mme64={Volatile.Read(ref _mme64)}";
    }
}

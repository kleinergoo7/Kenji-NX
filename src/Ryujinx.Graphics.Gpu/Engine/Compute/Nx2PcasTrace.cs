// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Env-gated, observe-only trace of compute-class PCAS entries. Counts every
    /// PCAS/QMD signal write (0xAE/0xAF/0xB0), including qmd=0 Resume paths and
    /// writes that skip dispatch; no dispatch behaviour is changed. Log lines are
    /// emitted only when <c>NX2_PCAS_TRACE=1</c>.
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
        private static long _mAE;
        private static long _mAF;
        private static long _mB0;
        private static long _act;

        /// <summary>Records a PCAS entry. Counts unconditionally; logs when enabled and under cap.</summary>
        public static void Enter(uint qmdAddress, bool usesMme64, int method = 0, int argument = 0)
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

            switch (method)
            {
                case PcasMethodAction.SendPcasB:
                    Interlocked.Increment(ref _mAE);
                    break;
                case PcasMethodAction.SendSignalingPcasB:
                    Interlocked.Increment(ref _mAF);
                    break;
                case PcasMethodAction.SendPcasC:
                    Interlocked.Increment(ref _mB0);
                    break;
            }

            bool dispatch = PcasMethodAction.Eligible(method, argument);
            if (dispatch)
            {
                Interlocked.Increment(ref _act);
            }

            if (_enabled && Interlocked.Increment(ref _lines) <= Cap)
            {
                string action = qmdAddress == 0 ? "Resume" : "Launch";
                Logger.Warning?.Print(LogClass.ServiceNv,
                    $"NX2PCAS qmd=0x{qmdAddress:X8} action={action} mme64={usesMme64} m=0x{method:X2} arg=0x{argument:X8} dispatch={dispatch}");
            }
        }

        /// <summary>One-line cumulative summary of PCAS entries.</summary>
        public static string Summary =>
            $"calls={Volatile.Read(ref _calls)} zero={Volatile.Read(ref _zero)} nonzero={Volatile.Read(ref _nonzero)} mme64={Volatile.Read(ref _mme64)} mAE={Volatile.Read(ref _mAE)} mAF={Volatile.Read(ref _mAF)} mB0={Volatile.Read(ref _mB0)} act={Volatile.Read(ref _act)}";

        /// <summary>Test-only counter reset so fixtures can assert exact counts.</summary>
        public static void Reset()
        {
            Volatile.Write(ref _calls, 0);
            Volatile.Write(ref _zero, 0);
            Volatile.Write(ref _nonzero, 0);
            Volatile.Write(ref _mme64, 0);
            Volatile.Write(ref _mAE, 0);
            Volatile.Write(ref _mAF, 0);
            Volatile.Write(ref _mB0, 0);
            Volatile.Write(ref _act, 0);
            Volatile.Write(ref _lines, 0);
        }
    }
}

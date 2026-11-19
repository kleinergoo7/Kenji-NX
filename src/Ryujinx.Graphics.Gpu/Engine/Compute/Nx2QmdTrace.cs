// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Env-gated, observe-only decode of QMD release slots and 4-byte semaphore words.
    /// Inert unless <c>NX2_QMD_TRACE=1</c> / <c>NX2_SEM_WATCH=1</c>. No dispatch or release
    /// behaviour is changed.
    /// </summary>
    internal static class Nx2QmdTrace
    {
        private static readonly bool _qmdEnabled = Environment.GetEnvironmentVariable("NX2_QMD_TRACE") == "1";
        private static readonly bool _semEnabled = Environment.GetEnvironmentVariable("NX2_SEM_WATCH") == "1";

        private const int Cap = 200;
        private static int _semLines;

        public static bool QmdEnabled => _qmdEnabled;

        /// <summary>Builds a one-line summary of the QMD release slots and ring context.</summary>
        public static string Describe(ComputeQmd qmd, int dataRam27)
        {
            string rel0 = qmd.SemaphoreReleaseEnable0 ? Slot(qmd.Release0AddressUpper, qmd.Release0AddressLower, qmd.Release0Payload) : "-";
            string rel1 = qmd.SemaphoreReleaseEnable1 ? Slot(qmd.Release1AddressUpper, qmd.Release1AddressLower, qmd.Release1Payload) : "-";

            // Raw word 52 (bits 1664..1695) is the third QMD release slot on NX2; ComputeQmd
            // exposes those bits as QmdSpareE. Log it verbatim when decoding the layout.
            int word52 = qmd.QmdSpareE;

            return $"rel0={rel0} rel1={rel1} w52=0x{word52:X} qver={qmd.QmdMajorVersion}.{qmd.QmdVersion} dram27=0x{dataRam27:X}";
        }

        /// <summary>Logs a 4-byte semaphore write ("w") or acquire read ("r"), capped.</summary>
        public static void WatchSemaphore(ulong address, uint value, string op)
        {
            if (!_semEnabled || Interlocked.Increment(ref _semLines) > Cap)
            {
                return;
            }

            Logger.Warning?.Print(LogClass.ServiceNv, $"NX2SEM {op} addr=0x{address:X} value=0x{value:X}");
        }

        private static string Slot(int upper, int lower, int payload)
        {
            ulong address = ((ulong)(uint)upper << 32) | (uint)lower;
            return $"0x{address:X}:{payload}";
        }
    }
}

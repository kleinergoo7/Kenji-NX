// SPDX-License-Identifier: MIT
using ARMeilleure.Translation;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    /// <summary>
    /// NX2 producer trace (env-gated <c>NX2_BP_PRODUCER=1</c>, off by default).
    /// Observe-only: arms dispatcher-entry probes at the PrepareThread producer
    /// (<c>Drive+0xA80F24</c> = FUN_7100a80f24) and the job-manager submit
    /// (<c>Drive+0x272A908</c> = FUN_710272a908), plus a positive control at the
    /// per-frame driver (<c>Drive+0x3A9E28</c> = FUN_71003a9e28, first 3 hits only).
    /// Logs thread, PC, LR (resolved to a Drive offset when possible), x0, x1,
    /// worker+0xac and queue count before/after, then disarms. Never forces state,
    /// never writes guest memory or registers. When the variable is unset, the
    /// only cost is one null check per JIT dispatcher iteration.
    /// Caveat: this catches dispatcher-mediated entries only; direct JIT-to-JIT
    /// tail calls after FunctionTable registration bypass it. Zero hits therefore
    /// means "never entered via dispatcher", which is weaker than a BRK
    /// breakpoint's "never executed" (that option was rejected: it needs the GDB
    /// stub, globally deoptimizes JIT and blocks the guest thread up to 5 s/hit).
    /// </summary>
    static class Nx2ProducerTrace
    {
        private const ulong ProducerOff = 0xA80F24;
        private const ulong SubmitOff = 0x272A908;
        private const ulong ControlOff = 0x3A9E28;

        private const int ProducerCap = 100; // ~50 enter + ~50 exit lines
        private const int ControlCap = 3;

        private static int _armed;
        private static int _lines;
        private static int _controlLines;
        private static ulong _driveBase;
        private static ulong _driveSize;

        internal static ulong LastPrepareQueue;

        public static void EnsureArmed(Switch device)
        {
            if (Environment.GetEnvironmentVariable("NX2_BP_PRODUCER") != "1" || device == null)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _armed, 1, 0) != 0)
            {
                return;
            }

            if (!ResolveDrive(device))
            {
                System.Threading.Interlocked.Exchange(ref _armed, 0);

                return;
            }

            // The probe lives in Translator.ExecuteSingle (managed dispatch loop).
            // Ryujinx runs the native DispatchLoop by default, which bypasses it
            // (that is why an earlier armed run logged zero hits, not even the
            // per-frame control). Force the managed loop while tracing; correctness
            // is identical, dispatch is just slower. Logged so the perturbation
            // check can account for it.
            ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = false;

            Translator.ProbeAddrA = _driveBase + ProducerOff;
            Translator.ProbeAddrB = _driveBase + SubmitOff;
            Translator.ProbeAddrCtl = _driveBase + ControlOff;
            Translator.ProbeEnter = OnEnter;
            Translator.ProbeExit = OnExit;
            Translator.ProbeMissLog = true;

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                $"NX2BP armed base=0x{_driveBase:X} size=0x{_driveSize:X} A=+0x{ProducerOff:X} B=+0x{SubmitOff:X} ctl=+0x{ControlOff:X}");
        }

        private static bool ResolveDrive(Switch device)
        {
            try
            {
                foreach (KeyValuePair<ulong, KProcess> proc in device.System.KernelContext.Processes)
                {
                    foreach (HleProcessDebugger.Image image in proc.Value.Debugger.GetLoadedImages())
                    {
                        if (image.Name == "Drive.nss" ||
                            (_driveBase == 0 && image.Size > _driveSize))
                        {
                            _driveBase = image.BaseAddress;
                            _driveSize = image.Size;

                            if (image.Name == "Drive.nss")
                            {
                                return true;
                            }
                        }
                    }

                    if (_driveBase != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return _driveBase != 0;
        }

        private static void OnEnter(ulong address, ulong x0, ulong x1, ulong lr, ulong threadUid)
        {
            bool ctl = address == Translator.ProbeAddrCtl;
            int n;

            if (ctl)
            {
                n = System.Threading.Interlocked.Increment(ref _controlLines);

                if (n > ControlCap)
                {
                    return;
                }
            }
            else
            {
                n = System.Threading.Interlocked.Increment(ref _lines);

                if (n > ProducerCap)
                {
                    Disarm();

                    return;
                }
            }

            Log("enter", address, threadUid, x0, x1, lr, 0, ReadQueue());
        }

        private static void OnExit(ulong address, ulong threadUid, ulong nextAddr)
        {
            if (address == Translator.ProbeAddrCtl)
            {
                return;
            }

            int n = System.Threading.Interlocked.Increment(ref _lines);

            if (n > ProducerCap)
            {
                Disarm();

                return;
            }

            Log("exit ", address, threadUid, 0, 0, 0, nextAddr, ReadQueue());
        }

        private static (int wac, int count, int head) ReadQueue()
        {
            ulong q = System.Threading.Volatile.Read(ref LastPrepareQueue);

            if (q < 0x10000)
            {
                return (-9, -9, -9);
            }

            try
            {
                KThread current = Kernel.KernelStatic.GetCurrentThread();
                var mem = current?.Owner?.CpuMemory;

                if (mem == null)
                {
                    return (-8, -8, -8);
                }

                return (mem.Read<int>(q - 0x28 + 0xac), mem.Read<int>(q + 0xc), mem.Read<int>(q + 0x10));
            }
            catch
            {
                return (-7, -7, -7);
            }
        }

        private static void Log(string phase, ulong address, ulong threadUid, ulong x0, ulong x1, ulong lr, ulong nextAddr, (int wac, int count, int head) q)
        {
            string name = "A";
            ulong off = address - _driveBase;

            if (address == Translator.ProbeAddrB)
            {
                name = "B";
            }
            else if (address == Translator.ProbeAddrCtl)
            {
                name = "ctl";
            }

            string thread = $"uid={threadUid}";

            try
            {
                KThread current = Kernel.KernelStatic.GetCurrentThread();

                if (current != null)
                {
                    thread = $"uid={current.ThreadUid} tname={current.GetThreadName()}";
                }
            }
            catch
            {
            }

            string lrText = _driveBase != 0 && lr >= _driveBase && lr < _driveBase + _driveSize
                ? $"+0x{lr - _driveBase:X}"
                : $"0x{lr:X}";

            string extra = phase == "enter"
                ? $" x0=0x{x0:X} x1=0x{x1:X} lr={lrText}"
                : $" next=0x{nextAddr:X}";

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                $"NX2BP {phase} tgt={name} pc=0x{address:X}(+0x{off:X}) {thread}{extra} wac={q.wac} qc={q.count} qh={q.head} t={Environment.TickCount64}");
        }

        private static void Disarm()
        {
            Translator.ProbeEnter = null;
            Translator.ProbeExit = null;
            Translator.ProbeMissLog = false;

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceAm,
                "NX2BP disarmed (cap reached)");
        }
    }
}

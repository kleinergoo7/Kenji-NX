// SPDX-License-Identifier: MIT
// Best-effort Switch 2 TSG device (UNVERIFIED): reference-backed semantics unavailable.
// Handles the two observed ioctls (0x5408, 0x540B) with plausible outputs and keeps
// the input-echoing stub for anything else. Marked UNVERIFIED; empirical validation required.
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Linq;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    class Nx2TsgDeviceFile : NvDeviceFile
    {
        private int _nextSubcontext = 1;

        public Nx2TsgDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner) { }

        public override void Close() { }

        private NvInternalResult Stub(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            string words = string.Join(' ', Enumerable.Select(
                MemoryMarshal.Cast<byte, uint>(arguments[..(arguments.Length & ~3)]).ToArray(),
                w => w.ToString("X8")));

            Logger.Warning?.Print(LogClass.ServiceNv, $"(STUBBED/UNVERIFIED) {Path} ioctl=0x{command.RawValue:X8} type={command.Type:X2} nr=0x{command.Number:X2} size=0x{arguments.Length:X} in=[{words}]");

            arguments.CopyTo(inlineOutBuffer.IsEmpty ? arguments : inlineOutBuffer);
            if (!inlineOutBuffer.IsEmpty) inlineOutBuffer.Clear();

            return NvInternalResult.Success;
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            if (command.Type == 0x54 && command.Number == 0x0B && arguments.Length >= 4)
            {
                // Best-effort: treat as subcontext creation; return a fresh id in word 0.
                int id = System.Threading.Interlocked.Increment(ref _nextSubcontext);
                Logger.Warning?.Print(LogClass.ServiceNv, $"(UNVERIFIED) {Path} ioctl 0x540B -> subcontext id={id}");
                // echo input then overwrite first word with id
                byte[] tmp = arguments.ToArray();
                if (tmp.Length >= 4) BitConverter.TryWriteBytes(tmp.AsSpan(0, 4), (uint)id);
                tmp.CopyTo(arguments);
                return NvInternalResult.Success;
            }
            return Stub(command, arguments, Span<byte>.Empty);
        }

        public override NvInternalResult Ioctl2(NvIoctl command, Span<byte> arguments, Span<byte> inlineInBuffer)
        {
            return Stub(command, arguments, Span<byte>.Empty);
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            if (command.Type == 0x54 && command.Number == 0x0B && inlineOutBuffer.Length >= 4)
            {
                int id = System.Threading.Interlocked.Increment(ref _nextSubcontext);
                Logger.Warning?.Print(LogClass.ServiceNv, $"(UNVERIFIED) {Path} ioctl3 0x540B -> subcontext id={id}");
                inlineOutBuffer.Clear();
                BitConverter.TryWriteBytes(inlineOutBuffer[..4], (uint)id);
                return NvInternalResult.Success;
            }
            return Stub(command, arguments, inlineOutBuffer);
        }
    }
}

// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.HLE
{
    class Nx2PcWatchTests
    {
        // Poll sequence from the Task 1d disassembly:
        // 0x860B87C ldr x0,[x22] | 0x860B884 ldr x8,[x8,#272] | 0x860B888 blr x8 | 0x860B88C tbz w0,#0.
        private const ulong PollEntryPc = 0x860B87C;
        private const ulong ObjectDumpPc = 0x860B884;
        private const ulong BlrPc = 0x860B888;
        private const ulong PostCallPc = 0x860B88C;
        private const ulong ObjectAddress = 0x6782FF0010;
        private const uint CompletionBool = 0x00000001;

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("NX2_PC_WATCH", null);
        }

        [Test]
        public void EnabledWhenWatchPcConfigured()
        {
            Environment.SetEnvironmentVariable("NX2_PC_WATCH", "0x860B87C");
            Assert.That(Nx2PcWatch.Enabled, Is.True);

            Environment.SetEnvironmentVariable("NX2_PC_WATCH", "");
            Assert.That(Nx2PcWatch.Enabled, Is.False);
        }

        [Test]
        public void OnlyConfiguredPcMatches()
        {
            Environment.SetEnvironmentVariable("NX2_PC_WATCH", "0x860B87C,0x860B884,0x860B88C");

            Assert.That(Nx2PcWatch.Matches(PollEntryPc), Is.True);
            Assert.That(Nx2PcWatch.Matches(ObjectDumpPc), Is.True);
            Assert.That(Nx2PcWatch.Matches(PostCallPc), Is.True);
            Assert.That(Nx2PcWatch.Matches(0x860B75CUL), Is.False);
        }

        [TestCase(ObjectDumpPc)]
        [TestCase(BlrPc)]
        public void FormatRendersRegistersAndObjectMemory(ulong pc)
        {
            ulong[] regs = new ulong[32];
            regs[0] = ObjectAddress; // x0 holds the object here, after ldr x0,[x22] retired
            ulong vtable = 0x85012340;

            Dictionary<ulong, uint> memory = new()
            {
                [ObjectAddress] = (uint)vtable,
                [ObjectAddress + 4] = 0x00000001, // completion flag byte set
            };

            string line = Nx2PcWatch.Format(pc, i => regs[i],
                address => memory.TryGetValue(address, out uint word) ? word : 0u);

            Assert.That(line, Does.Contain("regs=["));
            Assert.That(line, Does.Contain($"x0=0x{ObjectAddress:X}"));
            Assert.That(line, Does.Contain($"mem x0@0x{ObjectAddress:X}=["));
            Assert.That(line, Does.Contain($"{vtable:X8}"));
            Assert.That(line, Does.Not.Contain("w0="), "w0 belongs to the post-call PC");
        }

        [Test]
        public void NoObjectDumpAtPollEntryPc()
        {
            ulong[] regs = new ulong[32];
            regs[0] = 0x1234; // x0 is not the object at ldr x0,[x22]

            string line = Nx2PcWatch.Format(PollEntryPc, i => regs[i], _ => 0u);

            Assert.That(line, Does.Not.Contain("mem x0@"));
        }

        [Test]
        public void FormatAppendsActualW0AtPostCallPc()
        {
            ulong[] regs = new ulong[32];
            regs[0] = CompletionBool; // w0 holds the bool at tbz w0,#0

            string line = Nx2PcWatch.Format(PostCallPc, i => regs[i], _ => 0u);

            Assert.That(line, Does.Contain($"w0=0x{CompletionBool:X8}"));
            Assert.That(line, Does.Not.Contain("mem x0@"), "x0 may be clobbered by the blr");
        }
    }
}

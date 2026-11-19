// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.HLE
{
    class Nx2PcWatchTests
    {
        private const ulong PollPc = 0x860B87C;
        private const ulong OtherPc = 0x860B884;
        private const ulong ObjectAddress = 0x6782FF0010;

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
            Environment.SetEnvironmentVariable("NX2_PC_WATCH", "0x860B87C,0x860B884");

            Assert.That(Nx2PcWatch.Matches(PollPc), Is.True);
            Assert.That(Nx2PcWatch.Matches(OtherPc), Is.True);
            Assert.That(Nx2PcWatch.Matches(0x860B888UL), Is.False);
        }

        [Test]
        public void FormatRendersRegistersAndObjectMemory()
        {
            ulong[] regs = new ulong[32];
            regs[0] = ObjectAddress; // x0 holds the object at the completion-poll call site
            ulong vtable = 0x85012340;

            Dictionary<ulong, uint> memory = new()
            {
                [ObjectAddress] = (uint)vtable,
                [ObjectAddress + 4] = 0x00000001, // completion flag byte set
            };

            string line = Nx2PcWatch.Format(PollPc, i => regs[i],
                address => memory.TryGetValue(address, out uint word) ? word : 0u);

            Assert.That(line, Does.Contain("regs=["));
            Assert.That(line, Does.Contain($"x0=0x{ObjectAddress:X}"));
            Assert.That(line, Does.Contain($"mem x0@0x{ObjectAddress:X}=["));
            Assert.That(line, Does.Contain($"{vtable:X8}"));
            Assert.That(line, Does.Contain("w0="), "the post-call PC must also log w0");
        }
    }
}

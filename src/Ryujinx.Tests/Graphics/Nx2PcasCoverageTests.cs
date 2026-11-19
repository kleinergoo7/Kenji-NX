// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2PcasCoverageTests
    {
        [SetUp]
        public void SetUp() => Nx2PcasTrace.Reset();

        [Test]
        public void AllThreeMethodsCountSeparately()
        {
            Nx2PcasTrace.Enter(0x1234, false, 0xAE, 0);
            Nx2PcasTrace.Enter(0x1234, false, 0xAF, 0);
            Nx2PcasTrace.Enter(0x1234, false, 0xB0, 0);

            string summary = Nx2PcasTrace.Summary;
            Assert.That(summary, Does.Contain("mAE=1 mAF=1 mB0=1"));
            Assert.That(summary, Does.Contain("calls=3"));
        }

        [Test]
        public void OnlyDispatchEligibleArgumentsCountAsActions()
        {
            // Eglantine kepler_compute.cpp:364-365 skip cases.
            Nx2PcasTrace.Enter(0x1234, false, 0xAE, 2); // 0xAE always returns.
            Nx2PcasTrace.Enter(0x1234, false, 0xAF, 0); // 0xAF returns when !(argument & 2).
            Nx2PcasTrace.Enter(0x1234, false, 0xB0, 0); // 0xB0 low nibble 0.
            Nx2PcasTrace.Enter(0x1234, false, 0xB0, 1); // 0xB0 low nibble 1.
            Assert.That(Nx2PcasTrace.Summary, Does.Contain("act=0"));

            // Dispatch-eligible arguments.
            Nx2PcasTrace.Enter(0x1234, false, 0xAF, 2); // bit 1 set.
            Nx2PcasTrace.Enter(0x1234, false, 0xB0, 2); // low nibble 2.
            Assert.That(Nx2PcasTrace.Summary, Does.Contain("act=2"));
        }

        [Test]
        public void QmdAddressRegisterIsSharedAcrossAllThreeMethods()
        {
            // Method index = byte offset / 4. The QMD address comes from method 0xAD
            // (SendPcasA) for every trigger method, never from the trigger's own argument.
            Assert.That((int)Marshal.OffsetOf<ComputeClassState>(nameof(ComputeClassState.SendPcasA)), Is.EqualTo(0x2B4)); // 0xAD
            Assert.That((int)Marshal.OffsetOf<ComputeClassState>(nameof(ComputeClassState.SendPcasB)), Is.EqualTo(0x2B8)); // 0xAE
            Assert.That((int)Marshal.OffsetOf<ComputeClassState>(nameof(ComputeClassState.SendSignalingPcasB)), Is.EqualTo(0x2BC)); // 0xAF
            Assert.That((int)Marshal.OffsetOf<ComputeClassState>(nameof(ComputeClassState.SendPcasC)), Is.EqualTo(0x2C0)); // 0xB0

            // Eligibility depends only on method + argument, so no trigger changes the address source.
            Assert.That(PcasMethodAction.Eligible(0xAE, 2), Is.False);
            Assert.That(PcasMethodAction.Eligible(0xAF, 2), Is.True);
            Assert.That(PcasMethodAction.Eligible(0xB0, 2), Is.True);
        }
    }
}

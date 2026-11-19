// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2PcasTraceTests
    {
        [Test]
        public void EnterCountsCallsAndMme64()
        {
            Nx2PcasTrace.Enter(0, false);
            Nx2PcasTrace.Enter(0, false);
            Nx2PcasTrace.Enter(0x1234, false);

            string summary = Nx2PcasTrace.Summary;
            Assert.That(summary, Does.Contain("calls=3"));
            Assert.That(summary, Does.Contain("zero=2"));
            Assert.That(summary, Does.Contain("nonzero=1"));

            Nx2PcasTrace.Enter(0x1234, true);

            Assert.That(Nx2PcasTrace.Summary, Does.Contain("mme64=1"));
        }
    }
}

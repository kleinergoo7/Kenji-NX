// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2MacroUploadTraceTests
    {
        [SetUp]
        public void SetUp() => Nx2MacroUploadTrace.Reset();

        [Test]
        public void RecordNonZeroWordIncrementsCountAndChangesHash()
        {
            ulong hashBefore = Nx2MacroUploadTrace.Hash;
            Assert.That(Nx2MacroUploadTrace.Summary, Does.Contain("nonzero=0"));

            Nx2MacroUploadTrace.RecordWord(0x1234);

            Assert.That(Nx2MacroUploadTrace.Summary, Does.Contain("nonzero=1"));
            Assert.That(Nx2MacroUploadTrace.Hash, Is.Not.EqualTo(hashBefore));
        }

        [Test]
        public void RecordZeroWordLeavesNonZeroCountUnchanged()
        {
            Nx2MacroUploadTrace.RecordWord(0x0);

            Assert.That(Nx2MacroUploadTrace.NonZero, Is.EqualTo(0));
            Assert.That(Nx2MacroUploadTrace.Summary, Does.Contain("nonzero=0"));
        }
    }
}

// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.MME;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2DrawMacroSummaryTests
    {
        [SetUp]
        public void SetUp() => MacroTuring.ResetMacroEngineCounters();

        private sealed class FakeState : IDeviceState
        {
            public int Read(int offset) => 0;
            public void Write(int offset, int data) { }
        }

        private static MacroTuring NewEngine() => new(null, new int[0xc00], () => 0);

        [Test]
        public void DrawClassEmitIncrementsDrawEmitHitsOnlyForDrawRegisters()
        {
            MacroTuring.RecordDrawEmit(0x585);
            MacroTuring.RecordDrawEmit(0x586);

            Assert.That(MacroTuring.DrawEmitHits, Is.EqualTo(2));

            MacroTuring.RecordDrawEmit(0x42);

            Assert.That(MacroTuring.DrawEmitHits, Is.EqualTo(2));
        }

        [Test]
        public void DrawMacroSummaryRendersCounters()
        {
            for (int i = 0; i < 3; i++)
            {
                NewEngine().Execute(System.ReadOnlySpan<int>.Empty, new FakeState(), 0, compute: false);
            }

            MacroTuring.RecordDrawEmit(0x585);

            Assert.That(Nx2GpuStall.DescribeDrawMacro(), Is.EqualTo("macros=3 drawEmitHits=1 drawHashes=0"));
        }

        [Test]
        public void ResetClearsDrawCounters()
        {
            MacroTuring.RecordDrawEmit(0x585);

            Assert.That(MacroTuring.DrawEmitHits, Is.EqualTo(1));

            MacroTuring.ResetMacroEngineCounters();

            Assert.That(MacroTuring.DrawEmitHits, Is.EqualTo(0));
            Assert.That(MacroTuring.DrawHashes, Is.EqualTo(0));
        }
    }
}

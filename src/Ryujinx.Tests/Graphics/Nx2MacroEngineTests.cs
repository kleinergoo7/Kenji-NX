// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.MME;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2MacroEngineTests
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
        public void ComputeExecuteIncrementsMacrosComputeOnly()
        {
            NewEngine().Execute(System.ReadOnlySpan<int>.Empty, new FakeState(), 0, compute: true);

            Assert.That(MacroTuring.MacrosCompute, Is.EqualTo(1));
            Assert.That(MacroTuring.Macros3D, Is.EqualTo(0));
        }

        [Test]
        public void NonComputeExecuteIncrementsMacros3DOnly()
        {
            NewEngine().Execute(System.ReadOnlySpan<int>.Empty, new FakeState(), 0, compute: false);

            Assert.That(MacroTuring.Macros3D, Is.EqualTo(1));
            Assert.That(MacroTuring.MacrosCompute, Is.EqualTo(0));
        }

        [Test]
        public void ComputeEmitRecordsRegisterAddressInHistogram()
        {
            MacroTuring.RecordComputeEmitted(0x42);
            MacroTuring.RecordComputeEmitted(0x42);

            Assert.That(MacroTuring.ComputeEmittedHistogram[0x42], Is.EqualTo(2));
        }
    }
}

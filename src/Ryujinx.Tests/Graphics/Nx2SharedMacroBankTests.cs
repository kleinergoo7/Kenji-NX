// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.MME;
using System;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2SharedMacroBankTests
    {
        [SetUp]
        public void SetUp() => MacroTuring.ResetMacroEngineCounters();

        [Test]
        public void ComputeExecutionResolvesToSameBankAs3DExecution()
        {
            // The constructor only stores context/parent and allocates the banks.
            GPFifoClass fifo = new(null, null);

            (Macro[] Slots, int[] Code) threeD = fifo.Resolve3DExecutionBank();
            (Macro[] Slots, int[] Code) compute = fifo.ResolveComputeExecutionBank();

            Assert.That(compute.Slots, Is.SameAs(threeD.Slots));
            Assert.That(compute.Code, Is.SameAs(threeD.Code));
        }

        [Test]
        public void ComputeMemoGenerationKeyUsesSharedBank()
        {
            Func<long> key = Macro.GetMemoGeneration(true, () => 11, () => 22);

            Assert.That(key(), Is.EqualTo(11));
        }
    }
}

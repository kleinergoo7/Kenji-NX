// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.MME;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2ComputeCodeBankTests
    {
        private const uint EmitMethod = 0x585;

        private sealed class FakeState : IDeviceState
        {
            public int Read(int offset) => 0;
            public void Write(int offset, int data) { }
        }

        private static FieldInfo Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetField(object target, string name, object value) => Field(target, name).SetValue(target, value);

        // Build one Turing instruction that emits (method = Imm[0], value = Imm[1]).
        // The code array stores the 96-bit instruction low word last: logical bit 0..31
        // lives in code[2], 32..63 in code[1], 64..95 in code[0] (see Decode/Field).
        private static void WriteEmitInstruction(int[] code, int wordOffset, uint method, uint value)
        {
            void Field(int low, int count, uint bits)
            {
                for (int i = 0; i < count; i++)
                {
                    if (((bits >> i) & 1) != 0)
                    {
                        int bit = low + i;
                        code[wordOffset + 2 - bit / 32] |= 1 << (bit % 32);
                    }
                }
            }

            Field(0, 1, 1);          // End
            Field(15, 5, 24);        // Dst[0] = special (no register write)
            Field(51, 5, 24);        // Dst[1] = special
            Field(30, 16, method);   // Imm[0] -> OutMethod[0] selector 5
            Field(66, 16, value);    // Imm[1] -> OutEmit[0] selector 6
            Field(82, 3, 5);         // OutMethod[0] = Imm[0]
            Field(85, 4, 6);         // OutEmit[0] = Imm[1]
        }

        private GPFifoClass _fifo;
        private int[] _macroCode;
        private int[] _computeMacroCode;
        private Macro[] _macros;
        private Macro[] _computeMacros;

        [SetUp]
        public void SetUp()
        {
            MacroTuring.ResetMacroEngineCounters();
            MacroTuring.MacroMemoHits = 0;
            MacroTuring.MacroMemoMisses = 0;

            var context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            context.IsAmpere = true;

            var channel = (GpuChannel)RuntimeHelpers.GetUninitializedObject(typeof(GpuChannel));

            var processor = (GPFifoProcessor)RuntimeHelpers.GetUninitializedObject(typeof(GPFifoProcessor));
            SetField(processor, "_channel", channel);

            _fifo = new GPFifoClass(context, processor);
            SetField(processor, "_fifoClass", _fifo);

            _macroCode = (int[])Field(_fifo, "_macroCode").GetValue(_fifo);
            _computeMacroCode = (int[])Field(_fifo, "_computeMacroCode").GetValue(_fifo);
            _macros = (Macro[])Field(_fifo, "_macros").GetValue(_fifo);
            _computeMacros = (Macro[])Field(_fifo, "_computeMacros").GetValue(_fifo);
        }

        private void RunCompute(int slot)
        {
            _fifo.ComputeMmeStart(slot, 0);
            _fifo.ComputeCallMme(slot, new FakeState());
        }

        [Test]
        public void ComputeExecutionReadsSharedThreeDCodeBank()
        {
            // Code is uploaded only to the 3D bank; the compute bank stays zero.
            WriteEmitInstruction(_macroCode, 0, EmitMethod, 0x1234);
            Assert.That(_computeMacroCode[0], Is.EqualTo(0));

            _fifo.BindComputeMmeStart(0);
            RunCompute(0);

            Assert.That(MacroTuring.ComputeEmittedHistogram.ContainsKey(EmitMethod), Is.True,
                "compute must execute code from the shared _macroCode bank");
        }

        [Test]
        public void ComputeExecutionUsesSeparateMacroStateArray()
        {
            Assert.That(_computeMacros, Is.Not.SameAs(_macros));

            // Shared code bank: position 0 emits a valid method; position 1 emits an
            // unsupported method (0xE00) and records nothing. If compute reused the 3D
            // slot state, it would start at position 1 and never emit EmitMethod.
            WriteEmitInstruction(_macroCode, 0, EmitMethod, 0x1234);
            WriteEmitInstruction(_macroCode, 3, 0xE00, 0x5678);

            _computeMacros[0] = new Macro(0); // compute state: position 0
            _macros[0] = new Macro(1);        // 3D state: position 1 (must be ignored)

            RunCompute(0);

            Assert.That(MacroTuring.ComputeEmittedHistogram.ContainsKey(EmitMethod), Is.True,
                "compute must use its own _computeMacros slot, not the 3D _macros slot");
        }

        [Test]
        public void ComputeMemoGenerationKeyUsesSharedCodeGeneration()
        {
            WriteEmitInstruction(_macroCode, 0, EmitMethod, 0x1234);
            _fifo.BindComputeMmeStart(0);

            RunCompute(0); // first run: miss, memo stored
            Assert.That(MacroTuring.MacroMemoMisses, Is.EqualTo(1));

            // 3D code generation changes (code upload to the shared bank).
            Field(_fifo, "_mmeCodeGeneration").SetValue(_fifo, 1L);
            RunCompute(0);
            Assert.That(MacroTuring.MacroMemoMisses, Is.EqualTo(2),
                "a 3D code upload must invalidate the compute memo");

            // Compute-only generation changes without any code change: must hit the memo.
            Field(_fifo, "_computeCodeGeneration").SetValue(_fifo, 1L);
            RunCompute(0);
            Assert.That(MacroTuring.MacroMemoHits, Is.EqualTo(1),
                "compute memo must be keyed on the shared 3D code generation");
        }
    }
}

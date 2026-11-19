// SPDX-License-Identifier: MIT
using ARMeilleure.State;
using NUnit.Framework;

namespace Ryujinx.Tests.Cpu
{
    // Regression coverage for the 8/16-bit CAS forms. The guest uses CASH in a
    // refcount/claim loop that sign-extends the previously observed value with
    // SXTH and feeds it back as the expected operand. CASB/CASH must compare
    // only the low 8/16 bits and must write a zero-extended old value back to
    // Ws, otherwise the guest's compare never matches and it livelocks.
    [Category("Cas")]
    public sealed class CpuTestCas : CpuTest
    {
        // cash w11, w9, [x10]
        private const uint CashW11W9X10 = 0x48AB7D49;

        // casb w11, w9, [x10]
        private const uint CasbW11W9X10 = 0x08AB7D49;

        [Test]
        public void Cash16_Success_WritesZeroExtendedOldValue()
        {
            Opcode(CashW11W9X10);
            Opcode(0xD65F03C0); // ret

            ExecutionContext context = GetContext();
            context.SetX(10, DataBaseAddress);
            context.SetX(9, 0xEA02);  // desired
            context.SetX(11, 0xEA82); // expected

            SetWorkingMemory(0, new byte[] { 0x82, 0xEA, 0xBB, 0xBB });

            ExecuteOpcodes(runUnicorn: false);

            Assert.That(context.GetX(11), Is.EqualTo(0xEA82ul));
        }

        [Test]
        public void Cash16_SignExtendedExpected_StillMatchesAndReturnsZeroExtendedOld()
        {
            Opcode(CashW11W9X10);
            Opcode(0xD65F03C0); // ret

            ExecutionContext context = GetContext();
            context.SetX(10, DataBaseAddress);
            context.SetX(9, 0xFFFFEA02ul); // desired after SXTH
            context.SetX(11, 0xFFFFEA82ul); // expected after SXTH

            SetWorkingMemory(0, new byte[] { 0x82, 0xEA, 0xBB, 0xBB });

            ExecuteOpcodes(runUnicorn: false);

            Assert.That(context.GetX(11), Is.EqualTo(0xEA82ul),
                "the sign-extended expected value must compare by low halfword only");
        }

        [Test]
        public void Cash16_Failure_WritesZeroExtendedActualValue()
        {
            Opcode(CashW11W9X10);
            Opcode(0xD65F03C0); // ret

            ExecutionContext context = GetContext();
            context.SetX(10, DataBaseAddress);
            context.SetX(9, 0xEA02);  // desired
            context.SetX(11, 0x1234); // expected (does not match memory)

            SetWorkingMemory(0, new byte[] { 0x82, 0xEA, 0xBB, 0xBB });

            ExecuteOpcodes(runUnicorn: false);

            Assert.That(context.GetX(11), Is.EqualTo(0xEA82ul));
        }

        [Test]
        public void Casb8_SignExtendedExpected_StillMatchesAndReturnsZeroExtendedOld()
        {
            Opcode(CasbW11W9X10);
            Opcode(0xD65F03C0); // ret

            ExecutionContext context = GetContext();
            context.SetX(10, DataBaseAddress);
            context.SetX(9, 0xFFFFFF82ul); // desired after SXTB
            context.SetX(11, 0xFFFFFF82ul); // expected after SXTB

            SetWorkingMemory(0, new byte[] { 0x82, 0xEA, 0xBB, 0xBB });

            ExecuteOpcodes(runUnicorn: false);

            Assert.That(context.GetX(11), Is.EqualTo(0x82ul));
        }
    }
}

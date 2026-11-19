// SPDX-License-Identifier: MIT
using ARMeilleure.Decoders;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using static ARMeilleure.Instructions.InstEmitHelper;
using static ARMeilleure.Instructions.InstEmitMemoryHelper;

namespace ARMeilleure.Instructions
{
    static partial class InstEmit
    {
        // ARMv8.1 CAS/CASA/CASL/CASAL. The old value goes to Rs.
        public static void Cas(ArmEmitterContext context)
        {
            OpCodeCas op = (OpCodeCas)context.CurrOp;

            bool is64 = op.Size == 3;
            Operand address = context.Copy(GetIntOrSP(context, op.Rn));
            Operand physAddr = EmitPtPointerLoad(context, address, default, write: true, op.Size);

            Operand expected = GetIntOrZR(context, op.Rs);
            Operand desired = GetIntOrZR(context, op.Rt);

            if (!is64)
            {
                expected = context.ConvertI64ToI32(expected);
                desired = context.ConvertI64ToI32(desired);

                // CASB/CASH compare only the low 8/16 bits of Rs with memory, and the
                // old value written back to Rs is zero-extended. Mask the operands so a
                // caller that sign-extended the expected value (sxth after a failed
                // CAS, as the guest refcount loop does) still matches, and so no
                // backend can leak the expected value's high bits into the result.
                if (op.Size == 0)
                {
                    expected = context.ZeroExtend8(OperandType.I32, expected);
                    desired = context.ZeroExtend8(OperandType.I32, desired);
                }
                else if (op.Size == 1)
                {
                    expected = context.ZeroExtend16(OperandType.I32, expected);
                    desired = context.ZeroExtend16(OperandType.I32, desired);
                }
            }

            Operand old = op.Size switch
            {
                0 => context.CompareAndSwap8(physAddr, expected, desired),
                1 => context.CompareAndSwap16(physAddr, expected, desired),
                _ => context.CompareAndSwap(physAddr, expected, desired),
            };

            if (is64)
            {
                SetIntOrZR(context, op.Rs, old);
            }
            else
            {
                old = op.Size switch
                {
                    0 => context.ZeroExtend8(OperandType.I32, old),
                    1 => context.ZeroExtend16(OperandType.I32, old),
                    _ => old,
                };

                SetIntOrZR(context, op.Rs, context.ZeroExtend32(OperandType.I64, old));
            }
        }
    }
}

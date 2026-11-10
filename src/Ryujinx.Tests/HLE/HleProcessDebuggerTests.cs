// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Process;

namespace Ryujinx.Tests.HLE
{
    public class HleProcessDebuggerTests
    {
        [TestCase(0xD604E10UL, 0x141BE52Cu, 0x14000000u, 0xDCFE2C0UL)]
        [TestCase(0x1000UL, 0x97FFFFFFu, 0x94000000u, 0xFFCUL)]
        public void DecodesImmediateBranchTarget(ulong pc, uint instruction, uint opcode, ulong expected)
        {
            Assert.IsTrue(HleProcessDebugger.TryDecodeImmediateBranch(pc, instruction, opcode, out ulong target));
            Assert.AreEqual(expected, target);
        }

        [TestCase(0x94000001u)]
        [TestCase(0xD61F0220u)]
        public void RejectsInstructionsThatAreNotDirectJumps(uint instruction)
        {
            Assert.IsFalse(HleProcessDebugger.TryDecodeImmediateBranch(0x1000, instruction, 0x14000000, out _));
        }

        [TestCase(0xB0000010u, 0x9008UL)]
        [TestCase(0xF0FFFFF0u, 0x7008UL)]
        public void DecodesForwardAndBackwardPagesForMutexFallback(uint adrp, ulong expected)
        {
            Assert.IsTrue(HleProcessDebugger.TryDecodeMutexFallbackSlot(
                0x8120, adrp, 0xF9400611, 0x91002210, 0xD61F0220, out ulong slot));
            Assert.AreEqual(expected, slot);
        }

        [Test]
        public void RejectsFallbackThatBranchesThroughAnotherRegister()
        {
            Assert.IsFalse(HleProcessDebugger.TryDecodeMutexFallbackSlot(
                0x8120, 0xB0000010, 0xF9400611, 0x91002210, 0xD61F0200, out _));
        }
    }
}

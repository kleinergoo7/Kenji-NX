// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Process;

namespace Ryujinx.Tests.HLE
{
    public class HleProcessDebuggerTests
    {
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

// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.Tests.HLE
{
    public class KAddressArbiterTests
    {
        [TestCase(0, 0, 1)]
        [TestCase(0, 1, 1)]
        [TestCase(2, -1, -1)]
        [TestCase(2, 0, -1)]
        [TestCase(2, 1, 0)]
        [TestCase(2, 2, -1)]
        [TestCase(2, 3, -1)]
        public void SignalAndModifyDecrementsOnceWhenAllWaitersWillWake(int waitingCount, int count, int expected)
        {
            Assert.AreEqual(expected, KAddressArbiter.GetSignalAndModifyAddend(waitingCount, count));
        }

        [TestCase(3, 4, 2)]
        [TestCase(4, 4, 4)]
        [TestCase(5, 4, 5)]
        [TestCase(int.MinValue, 0, int.MaxValue)]
        public void DecrementingWaitOnlyModifiesWordsBelowThreshold(int initial, int threshold, int expected)
        {
            int word = initial;
            int observed = KAddressArbiter.ReadAndDecrementIfLessThan(ref word, threshold);

            Assert.AreEqual(initial, observed);
            Assert.AreEqual(expected, word);
        }
    }
}

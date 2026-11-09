// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.Tests.HLE
{
    public class KAddressArbiterTests
    {
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

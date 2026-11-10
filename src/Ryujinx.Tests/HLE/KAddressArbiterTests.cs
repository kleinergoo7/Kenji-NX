// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using System.Collections.Generic;

namespace Ryujinx.Tests.HLE
{
    public class KAddressArbiterTests
    {
        [TestCase(0, 1, 0, true)]
        [TestCase(2, 1, 1, false)]
        [TestCase(2, 2, 2, true)]
        [TestCase(2, 3, 2, true)]
        [TestCase(2, 0, 2, true)]
        [TestCase(2, -1, 2, true)]
        public void WakeReportsWhetherTheWaiterQueueWasDrained(
            int queued, int count, int expectedWoken, bool expectedDrained)
        {
            KThread[] threads = new KThread[queued];
            for (int index = 0; index < queued; index++)
            {
                threads[index] = new KThread(null);
            }

            List<KThread> waiting = [.. threads];
            List<KThread> woken = [];

            Assert.That(KAddressArbiter.WakeThreads(waiting, count, woken.Add), Is.EqualTo(expectedDrained));
            Assert.That(woken, Is.EqualTo(threads[..expectedWoken]));
            Assert.That(waiting, Is.EqualTo(threads[expectedWoken..]));
        }

        [TestCase(0UL, 0UL, ConsoleGeneration.Switch2, false)]
        [TestCase(0x1000UL, 0x1004UL, ConsoleGeneration.Switch2, false)]
        [TestCase(0x1000UL, 0x1000UL, ConsoleGeneration.Switch1, false)]
        [TestCase(0x1000UL, 0x1000UL, ConsoleGeneration.Switch2, true)]
        public void ArbiterTraceMatchesOnlyTheSelectedSwitch2Address(
            ulong target, ulong address, ConsoleGeneration generation, bool expected)
        {
            Assert.That(Syscall.MatchesArbiterTrace(target, address, generation), Is.EqualTo(expected));
        }

        [Test]
        public void WaitIfEqual64RequiresSwitch2Aarch64AndEightByteAlignment()
        {
            Assert.That((int)ArbitrationType.WaitIfEqual64, Is.EqualTo(3));
            Assert.That(Syscall.ValidateWaitIfEqual64(0x1008, ConsoleGeneration.Switch2, false), Is.EqualTo(Result.Success));
            Assert.That(Syscall.ValidateWaitIfEqual64(0x1004, ConsoleGeneration.Switch2, false), Is.EqualTo(KernelResult.InvalidAddress));
            Assert.That(Syscall.ValidateWaitIfEqual64(0x1008, ConsoleGeneration.Switch2, true), Is.EqualTo(KernelResult.InvalidEnumValue));
            Assert.That(Syscall.ValidateWaitIfEqual64(0x1008, ConsoleGeneration.Switch1, false), Is.EqualTo(KernelResult.InvalidEnumValue));
        }

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

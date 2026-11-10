using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Horizon.Common;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KAddressArbiter
    {
        private const int HasListenersMask = 0x40000000;
        private static readonly ulong TracedMutexAddress =
            ulong.TryParse(Environment.GetEnvironmentVariable("NX2_MUTEX_TRACE_ADDR"),
                System.Globalization.NumberStyles.HexNumber, null, out ulong traceAddress) ? traceAddress : 0;
        private static int _mutexTraceCount;

        private readonly KernelContext _context;

        private readonly Dictionary<ulong, List<KThread>> _condVarThreads;
        private readonly Dictionary<ulong, List<KThread>> _arbiterThreads;
        private readonly ByDynamicPriority _byDynamicPriority;

        public KAddressArbiter(KernelContext context)
        {
            _context = context;

            _condVarThreads = [];
            _arbiterThreads = [];
            _byDynamicPriority = new ByDynamicPriority();
        }

        public Result ArbitrateLock(int ownerHandle, ulong mutexAddress, int requesterHandle)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();

            _context.CriticalSection.Enter();

            if (currentThread.TerminationRequested)
            {
                _context.CriticalSection.Leave();

                return KernelResult.ThreadTerminating;
            }

            currentThread.SignaledObj = null;
            currentThread.ObjSyncResult = Result.Success;

            KProcess currentProcess = KernelStatic.GetCurrentProcess();

            if (!KernelTransfer.UserToKernel(out int mutexValue, mutexAddress))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            if (mutexValue != (ownerHandle | HasListenersMask))
            {
                _context.CriticalSection.Leave();

                return Result.Success;
            }

            KThread mutexOwner = currentProcess.HandleTable.GetObject<KThread>(ownerHandle);

            if (mutexOwner == null)
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidHandle;
            }

            currentThread.MutexAddress = mutexAddress;
            currentThread.ThreadHandleForUserMutex = requesterHandle;

            mutexOwner.AddMutexWaiter(currentThread);
            if (ShouldTraceMutex(mutexAddress))
            {
                TraceMutex(mutexAddress, $"wait ownerTid={mutexOwner.ThreadUid} ownerHandle=0x{ownerHandle:X} requester=0x{requesterHandle:X}");
            }

            currentThread.Reschedule(ThreadSchedState.Paused);

            _context.CriticalSection.Leave();
            _context.CriticalSection.Enter();

            currentThread.MutexOwner?.RemoveMutexWaiter(currentThread);
            if (ShouldTraceMutex(mutexAddress))
            {
                TraceMutex(mutexAddress, $"resume result=0x{currentThread.ObjSyncResult.ErrorCode:X}");
            }

            _context.CriticalSection.Leave();

            return currentThread.ObjSyncResult;
        }

        public Result ArbitrateUnlock(ulong mutexAddress)
        {
            _context.CriticalSection.Enter();

            KThread currentThread = KernelStatic.GetCurrentThread();

            (int mutexValue, KThread newOwnerThread) = MutexUnlock(currentThread, mutexAddress);

            Result result = Result.Success;

            if (!KernelTransfer.KernelToUser(mutexAddress, mutexValue))
            {
                result = KernelResult.InvalidMemState;
            }

            if (result != Result.Success && newOwnerThread != null)
            {
                newOwnerThread.SignaledObj = null;
                newOwnerThread.ObjSyncResult = result;
            }

            if (ShouldTraceMutex(mutexAddress))
            {
                TraceMutex(mutexAddress, $"unlock nextTid={newOwnerThread?.ThreadUid} word=0x{mutexValue:X8} result=0x{result.ErrorCode:X}");
            }
            _context.CriticalSection.Leave();

            return result;
        }

        private static bool ShouldTraceMutex(ulong address) =>
            TracedMutexAddress != 0 && address == TracedMutexAddress && Interlocked.Increment(ref _mutexTraceCount) <= 64;

        private static void TraceMutex(ulong address, string detail) =>
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.KernelSvc,
                $"NX2 mutex handoff addr=0x{address:X} tid={KernelStatic.GetCurrentThread().ThreadUid} {detail}");

        public Result WaitProcessWideKeyAtomic(ulong mutexAddress, ulong condVarAddress, int threadHandle, long timeout)
        {
            _context.CriticalSection.Enter();

            KThread currentThread = KernelStatic.GetCurrentThread();

            currentThread.SignaledObj = null;
            currentThread.ObjSyncResult = KernelResult.TimedOut;

            if (currentThread.TerminationRequested)
            {
                _context.CriticalSection.Leave();

                return KernelResult.ThreadTerminating;
            }

            (int mutexValue, KThread newOwnerThread) = MutexUnlock(currentThread, mutexAddress);

            KernelTransfer.KernelToUser(condVarAddress, 1);

            if (!KernelTransfer.KernelToUser(mutexAddress, mutexValue))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            if (ShouldTraceMutex(mutexAddress))
            {
                TraceMutex(mutexAddress, $"condvar-release nextTid={newOwnerThread?.ThreadUid} word=0x{mutexValue:X8}");
            }

            currentThread.MutexAddress = mutexAddress;
            currentThread.ThreadHandleForUserMutex = threadHandle;

            if (_condVarThreads.TryGetValue(condVarAddress, out List<KThread> threads))
            {
                int i = 0;

                if (threads.Count > 0)
                {
                    i = threads.BinarySearch(currentThread, _byDynamicPriority);
                    if (i < 0)
                        i = ~i;
                }

                threads.Insert(i, currentThread);
            }
            else
            {
                _condVarThreads.Add(condVarAddress, [currentThread]);
            }

            if (timeout != 0)
            {
                currentThread.Reschedule(ThreadSchedState.Paused);

                if (timeout > 0)
                {
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }
            }

            _context.CriticalSection.Leave();

            if (timeout > 0)
            {
                _context.TimeManager.UnscheduleFutureInvocation(currentThread);
            }

            _context.CriticalSection.Enter();

            currentThread.MutexOwner?.RemoveMutexWaiter(currentThread);

            _condVarThreads[condVarAddress].Remove(currentThread);

            _context.CriticalSection.Leave();

            return currentThread.ObjSyncResult;
        }

        private static (int, KThread) MutexUnlock(KThread currentThread, ulong mutexAddress)
        {
            KThread newOwnerThread = currentThread.RelinquishMutex(mutexAddress, out int count);

            int mutexValue = 0;

            if (newOwnerThread != null)
            {
                mutexValue = newOwnerThread.ThreadHandleForUserMutex;

                if (count >= 2)
                {
                    mutexValue |= HasListenersMask;
                }

                newOwnerThread.SignaledObj = null;
                newOwnerThread.ObjSyncResult = Result.Success;

                newOwnerThread.ReleaseAndResume();
            }

            return (mutexValue, newOwnerThread);
        }

        public void SignalProcessWideKey(ulong address, int count)
        {
            _context.CriticalSection.Enter();

            int validThreads = 0;
            _condVarThreads.TryGetValue(address, out List<KThread> threads);

            if (threads is not null && threads.Count > 0)
            {
                validThreads = WakeThreads(threads, count, TryAcquireMutex);
            }

            if (validThreads == 0)
            {
                KernelTransfer.KernelToUser(address, 0);
            }

            _context.CriticalSection.Leave();
        }

        private static void TryAcquireMutex(KThread requester)
        {
            ulong address = requester.MutexAddress;

            KProcess currentProcess = KernelStatic.GetCurrentProcess();

            if (!currentProcess.CpuMemory.IsMapped(address))
            {
                // Invalid address.
                requester.SignaledObj = null;
                requester.ObjSyncResult = KernelResult.InvalidMemState;

                return;
            }

            ref int mutexRef = ref currentProcess.CpuMemory.GetRef<int>(address);

            int mutexValue, newMutexValue;

            do
            {
                mutexValue = mutexRef;

                if (mutexValue != 0)
                {
                    // Update value to indicate there is a mutex waiter now.
                    newMutexValue = mutexValue | HasListenersMask;
                }
                else
                {
                    // No thread owning the mutex, assign to requesting thread.
                    newMutexValue = requester.ThreadHandleForUserMutex;
                }
            }
            while (Interlocked.CompareExchange(ref mutexRef, newMutexValue, mutexValue) != mutexValue);

            if (mutexValue == 0)
            {
                // We now own the mutex.
                requester.SignaledObj = null;
                requester.ObjSyncResult = Result.Success;

                requester.ReleaseAndResume();

                return;
            }

            mutexValue &= ~HasListenersMask;

            KThread mutexOwner = currentProcess.HandleTable.GetObject<KThread>(mutexValue);

            if (mutexOwner != null)
            {
                // Mutex already belongs to another thread, wait for it.
                mutexOwner.AddMutexWaiter(requester);
            }
            else
            {
                // Invalid mutex owner.
                requester.SignaledObj = null;
                requester.ObjSyncResult = KernelResult.InvalidHandle;

                requester.ReleaseAndResume();
            }
        }

        public Result WaitForAddressIfEqual<T>(ulong address, T value, long timeout) where T : unmanaged, IEquatable<T>
        {
            KThread currentThread = KernelStatic.GetCurrentThread();

            _context.CriticalSection.Enter();

            if (currentThread.TerminationRequested)
            {
                _context.CriticalSection.Leave();

                return KernelResult.ThreadTerminating;
            }

            currentThread.SignaledObj = null;
            currentThread.ObjSyncResult = KernelResult.TimedOut;

            if (!KernelTransfer.UserToKernel(out T currentValue, address))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            if (currentValue.Equals(value))
            {
                if (timeout == 0)
                {
                    _context.CriticalSection.Leave();

                    return KernelResult.TimedOut;
                }

                currentThread.MutexAddress = address;
                currentThread.WaitingInArbitration = true;

                if (_arbiterThreads.TryGetValue(address, out List<KThread> threads))
                {
                    int i = 0;

                    if (threads.Count > 0)
                    {
                        i = threads.BinarySearch(currentThread, _byDynamicPriority);
                        if (i < 0)
                            i = ~i;
                    }

                    threads.Insert(i, currentThread);
                }
                else
                {
                    _arbiterThreads.Add(address, [currentThread]);
                }

                currentThread.Reschedule(ThreadSchedState.Paused);

                if (timeout > 0)
                {
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }

                _context.CriticalSection.Leave();

                if (timeout > 0)
                {
                    _context.TimeManager.UnscheduleFutureInvocation(currentThread);
                }

                _context.CriticalSection.Enter();

                if (currentThread.WaitingInArbitration)
                {
                    _arbiterThreads[address].Remove(currentThread);

                    currentThread.WaitingInArbitration = false;
                }

                _context.CriticalSection.Leave();

                return currentThread.ObjSyncResult;
            }

            _context.CriticalSection.Leave();

            return KernelResult.InvalidState;
        }

        internal static int ReadAndDecrementIfLessThan(ref int addressValue, int threshold)
        {
            int current = Volatile.Read(ref addressValue);

            while (current < threshold)
            {
                int observed = Interlocked.CompareExchange(ref addressValue, unchecked(current - 1), current);
                if (observed == current)
                {
                    break;
                }

                current = observed;
            }

            return current;
        }

        public Result WaitForAddressIfLessThan(ulong address, int value, bool shouldDecrement, long timeout)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();

            _context.CriticalSection.Enter();

            if (currentThread.TerminationRequested)
            {
                _context.CriticalSection.Leave();

                return KernelResult.ThreadTerminating;
            }

            currentThread.SignaledObj = null;
            currentThread.ObjSyncResult = KernelResult.TimedOut;

            KProcess currentProcess = KernelStatic.GetCurrentProcess();

            if (!KernelTransfer.UserToKernel(out int currentValue, address))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            if (shouldDecrement)
            {
                currentValue = ReadAndDecrementIfLessThan(ref currentProcess.CpuMemory.GetRef<int>(address), value);
            }

            if (currentValue < value)
            {
                if (timeout == 0)
                {
                    _context.CriticalSection.Leave();

                    return KernelResult.TimedOut;
                }

                currentThread.MutexAddress = address;
                currentThread.WaitingInArbitration = true;

                if (_arbiterThreads.TryGetValue(address, out List<KThread> threads))
                {
                    int i = 0;

                    if (threads.Count > 0)
                    {
                        i = threads.BinarySearch(currentThread, _byDynamicPriority);
                        if (i < 0)
                            i = ~i;
                    }

                    threads.Insert(i, currentThread);
                }
                else
                {
                    _arbiterThreads.Add(address, [currentThread]);
                }

                currentThread.Reschedule(ThreadSchedState.Paused);

                if (timeout > 0)
                {
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }

                _context.CriticalSection.Leave();

                if (timeout > 0)
                {
                    _context.TimeManager.UnscheduleFutureInvocation(currentThread);
                }

                _context.CriticalSection.Enter();

                if (currentThread.WaitingInArbitration)
                {
                    _arbiterThreads[address].Remove(currentThread);

                    currentThread.WaitingInArbitration = false;
                }

                _context.CriticalSection.Leave();

                return currentThread.ObjSyncResult;
            }

            _context.CriticalSection.Leave();

            return KernelResult.InvalidState;
        }

        public Result Signal(ulong address, int count)
        {
            _context.CriticalSection.Enter();

            WakeArbiterThreads(address, count);

            _context.CriticalSection.Leave();

            return Result.Success;
        }

        public Result SignalAndIncrementIfEqual(ulong address, int value, int count)
        {
            _context.CriticalSection.Enter();

            KProcess currentProcess = KernelStatic.GetCurrentProcess();

            if (!currentProcess.CpuMemory.IsMapped(address))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            ref int valueRef = ref currentProcess.CpuMemory.GetRef<int>(address);

            int currentValue;

            do
            {
                currentValue = valueRef;

                if (currentValue != value)
                {
                    _context.CriticalSection.Leave();

                    return KernelResult.InvalidState;
                }
            }
            while (Interlocked.CompareExchange(ref valueRef, currentValue + 1, currentValue) != currentValue);

            WakeArbiterThreads(address, count);

            _context.CriticalSection.Leave();

            return Result.Success;
        }

        internal static int GetSignalAndModifyAddend(int waitingCount, int count)
        {
            if (waitingCount == 0)
            {
                return 1;
            }

            // A nonpositive count wakes everyone. For a positive count, equality
            // also empties the queue and must clear the guest's waiter indication.
            return count <= 0 || waitingCount <= count ? -1 : 0;
        }

        public Result SignalAndModifyIfEqual(ulong address, int value, int count)
        {
            _context.CriticalSection.Enter();

            _arbiterThreads.TryGetValue(address, out List<KThread> threads);
            int addend = GetSignalAndModifyAddend(threads?.Count ?? 0, count);

            KProcess currentProcess = KernelStatic.GetCurrentProcess();

            if (!currentProcess.CpuMemory.IsMapped(address))
            {
                _context.CriticalSection.Leave();

                return KernelResult.InvalidMemState;
            }

            ref int valueRef = ref currentProcess.CpuMemory.GetRef<int>(address);

            int currentValue;

            do
            {
                currentValue = valueRef;

                if (currentValue != value)
                {
                    _context.CriticalSection.Leave();

                    return KernelResult.InvalidState;
                }
            }
            while (Interlocked.CompareExchange(ref valueRef, currentValue + addend, currentValue) != currentValue);

            WakeArbiterThreads(address, count);

            _context.CriticalSection.Leave();

            return Result.Success;
        }

        private void WakeArbiterThreads(ulong address, int count)
        {
            static void RemoveArbiterThread(KThread thread)
            {
                thread.SignaledObj = null;
                thread.ObjSyncResult = Result.Success;

                thread.ReleaseAndResume();

                thread.WaitingInArbitration = false;
            }

            _arbiterThreads.TryGetValue(address, out List<KThread> threads);

            if (threads is not null && threads.Count > 0)
            {
                WakeThreads(threads, count, RemoveArbiterThread);
            }
        }

        private static int WakeThreads(
            List<KThread> threads,
            int count,
            Action<KThread> removeCallback)
        {
            int validCount = count > 0 ? Math.Min(count, threads.Count) : threads.Count;

            for (int i = 0; i < validCount; i++)
            {
                KThread thread = threads[i];
                removeCallback(thread);
            }

            threads.RemoveRange(0, validCount);

            return validCount;
        }

        private class ByDynamicPriority : IComparer<KThread>
        {
            public int Compare(KThread x, KThread y)
            {
                return x!.DynamicPriority.CompareTo(y!.DynamicPriority);
            }
        }
    }
}

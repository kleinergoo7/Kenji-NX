using ARMeilleure.CodeGen;
using ARMeilleure.Common;
using ARMeilleure.Decoders;
using ARMeilleure.Diagnostics;
using ARMeilleure.Instructions;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation.Cache;
using ARMeilleure.Translation.PTC;
using Ryujinx.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Translation
{
    public class Translator
    {
        /// <summary>
        /// NX2 producer trace (observe-only, env-gated from HLE via
        /// <c>NX2_BP_PRODUCER=1</c>). When set, dispatcher entries at
        /// <see cref="ProbeAddrA"/>, <see cref="ProbeAddrB"/> or
        /// <see cref="ProbeAddrCtl"/> invoke the probes with guest registers.
        /// Null by default: a single null check per dispatcher iteration.
        /// Never modifies guest state; HLE only logs and disarms.
        /// </summary>
        public static Action<ulong, ulong, ulong, ulong, ulong> ProbeEnter;
        public static Action<ulong, ulong, ulong> ProbeExit;
        public static ulong ProbeAddrA;
        public static ulong ProbeAddrB;
        public static ulong ProbeAddrCtl;

        /// <summary>
        /// Translation-demand log (same env gate as the entry probes). A miss in the
        /// <c>Functions</c> cache means genuine on-demand translation: PTC load, PTC
        /// background translation and rejit all bypass <c>GetOrTranslate</c>, so with
        /// PTC disabled a miss proves the function executed (every first execution of
        /// an unregistered function funnels through the slow-dispatch stub into here).
        /// </summary>
        public static bool ProbeMissLog;

        /// <summary>
        /// Log-all mode for the translation-demand trace. Set before any guest code
        /// runs (process creation): every <c>GetOrTranslate</c> miss is logged once,
        /// with no address filtering and no need for the guest base address.
        /// Auto-disables at <see cref="ProbeMissLogAllCap"/> lines.
        /// </summary>
        public static bool ProbeMissLogAll;
        public static int ProbeMissLogAllCap = 150000;
        private static int _probeMissLines;

        private readonly IJitMemoryAllocator _allocator;
        private readonly ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>> _oldFuncs;

        public readonly JitCache JitCache;
        private readonly Ptc _ptc;

        internal TranslatorCache<TranslatedFunction> Functions { get; }
        public IAddressTable<ulong> FunctionTable { get; }
        internal EntryTable<uint> CountTable { get; }
        internal TranslatorStubs Stubs { get; }
        internal TranslatorQueue Queue { get; }
        internal IMemoryManager Memory { get; }

        private Thread[] _backgroundTranslationThreads;
        private volatile int _threadCount;

        public Translator(IJitMemoryAllocator allocator, IMemoryManager memory, IAddressTable<ulong> functionTable)
        {
            _allocator = allocator;
            Memory = memory;

            _oldFuncs = new ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>>();

            JitCache = new JitCache(allocator);

            _ptc = new Ptc(JitCache);

            Queue = new TranslatorQueue();


            CountTable = new EntryTable<uint>();
            Functions = new TranslatorCache<TranslatedFunction>();

            FunctionTable = functionTable;

            Stubs = new TranslatorStubs(JitCache, FunctionTable);

            FunctionTable.Fill = (ulong)Stubs.SlowDispatchStub;
        }

        public IPtcLoadState LoadDiskCache(PtcCacheInfo cacheInfo, bool enabled)
        {
            _ptc.Initialize(cacheInfo, enabled, Memory.Type);
            return _ptc;
        }

        public void PrepareCodeRange(ulong address, ulong size)
        {
            if (_ptc.Profiler.StaticCodeSize == 0)
            {
                _ptc.Profiler.StaticCodeStart = address;
                _ptc.Profiler.StaticCodeSize = size;
            }
        }

        public void Execute(State.ExecutionContext context, ulong address)
        {
            if (Interlocked.Increment(ref _threadCount) == 1)
            {
                if (_ptc.State == PtcState.Enabled)
                {
                    Debug.Assert(Functions.Count == 0);
                    _ptc.LoadTranslations(this);
                    _ptc.MakeAndSaveTranslations(this);
                }

                _ptc.Profiler.Start();

                _ptc.Disable();

                // Simple heuristic, should be user configurable in future. (1 for 4 core/ht or less, 2 for 6 core + ht
                // etc). All threads are normal priority except from the last, which just fills as much of the last core
                // as the os lets it with a low priority. If we only have one rejit thread, it should be normal priority
                // as highCq code is performance critical.
                //
                // TODO: Use physical cores rather than logical. This only really makes sense for processors with
                // hyperthreading. Requires OS specific code.
                int unboundedThreadCount = Math.Max(1, (Environment.ProcessorCount - 6) / 3);
                int threadCount = Math.Min(4, unboundedThreadCount);

                Thread[] backgroundTranslationThreads = new Thread[threadCount];

                for (int i = 0; i < threadCount; i++)
                {
                    bool last = i != 0 && i == unboundedThreadCount - 1;

                    backgroundTranslationThreads[i] = new(BackgroundTranslate)
                    {
                        Name = "CPU.BackgroundTranslatorThread." + i,
                        Priority = last ? ThreadPriority.Lowest : ThreadPriority.Normal,
                    };

                    backgroundTranslationThreads[i].Start();
                }

                Interlocked.Exchange(ref _backgroundTranslationThreads, backgroundTranslationThreads);
            }

            Statistics.InitializeTimer();

            NativeInterface.RegisterThread(context, Memory, this);

            if (Optimizations.EnableDebugging)
            {
                context.DebugPc = address;
                do
                {
                    if (Interlocked.CompareExchange(ref context.ShouldStep, 0, 1) == 1)
                    {
                        context.DebugPc = Step(context, context.DebugPc);
                        context.StepHandler();
                    }
                    else
                    {
                        context.DebugPc = ExecuteSingle(context, context.DebugPc);
                    }
                    context.CheckInterrupt();
                }
                while (context.Running && context.DebugPc != 0);
            }
            else if (Optimizations.UseUnmanagedDispatchLoop)
            {
                Stubs.DispatchLoop(context.NativeContextPtr, address);
            }
            else
            {
                do
                {
                    ulong prevAddress = address;
                    address = ExecuteSingle(context, address);

                    if (address == prevAddress && address != 0)
                    {
                        // Guard against an infinite re-execution loop: if a translated
                        // function returns the same guest address it was entered at,
                        // the dispatch loop would spin forever. Break out instead.
                        System.Console.Error.WriteLine($"[NX2_LOOP_GUARD] stuck at 0x{address:X} (mode={context.ExecutionMode})");
                        address = 0;
                    }
                }
                while (context.Running && address != 0);
            }

            NativeInterface.UnregisterThread();

            if (Interlocked.Decrement(ref _threadCount) == 0)
            {
                Queue.Dispose();

                Thread[] backgroundTranslationThreads = Interlocked.Exchange(ref _backgroundTranslationThreads, null);

                if (backgroundTranslationThreads != null)
                {
                    foreach (Thread thread in backgroundTranslationThreads)
                    {
                        thread.Join();
                    }
                }

                ClearJitCache();
                JitCache.Dispose();

                Stubs.Dispose();
                FunctionTable.Dispose();
                CountTable.Dispose();

                _ptc.Close();
                _ptc.Profiler.Stop();

                _ptc.Dispose();
                _ptc.Profiler.Dispose();
            }
        }

        private ulong ExecuteSingle(State.ExecutionContext context, ulong address)
        {
            Action<ulong, ulong, ulong, ulong, ulong> probeEnter = ProbeEnter;
            bool probed = probeEnter != null &&
                (address == ProbeAddrA || address == ProbeAddrB || address == ProbeAddrCtl);

            if (probed)
            {
                probeEnter(address, context.GetX(0), context.GetX(1), context.GetX(30), context.ThreadUid);
            }

            TranslatedFunction func = GetOrTranslate(address, context.ExecutionMode);

            Statistics.StartTimer();

            context.ResetCallDepth();
            ulong nextAddr = func.Execute(Stubs.ContextWrapper, context);

            Statistics.StopTimer(address);

            if (probed)
            {
                ProbeExit?.Invoke(address, context.ThreadUid, nextAddr);
            }

            return nextAddr;
        }

        private ulong Step(State.ExecutionContext context, ulong address)
        {
            TranslatedFunction func = Translate(address, context.ExecutionMode, highCq: false, singleStep: true);

            address = func.Execute(Stubs.ContextWrapper, context);

            EnqueueForDeletion(address, func);

            return address;
        }



        internal TranslatedFunction GetOrTranslate(ulong address, ExecutionMode mode)
        {
            if (!Functions.TryGetValue(address, out TranslatedFunction func))
            {
                if (ProbeMissLogAll && System.Threading.Interlocked.Increment(ref _probeMissLines) <= ProbeMissLogAllCap)
                {
                    ulong allUid = 0;
                    ulong allX0 = 0, allX1 = 0, allX30 = 0;
                    int plusAc = int.MinValue, plus88 = int.MinValue;

                    try
                    {
                        State.ExecutionContext ctx = Instructions.NativeInterface.GetContext();

                        if (ctx != null)
                        {
                            allUid = ctx.ThreadUid;
                            allX0 = ctx.GetX(0);
                            allX1 = ctx.GetX(1);
                            allX30 = ctx.GetX(30);

                            try
                            {
                                plusAc = Memory.Read<int>(allX0 + 0xac);
                                plus88 = Memory.Read<int>(allX0 + 0x88);
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Cpu,
                        $"NX2TXA miss addr=0x{address:X} mode={mode} threadUid={allUid} x0=0x{allX0:X} x1=0x{allX1:X} lr=0x{allX30:X} x0_ac={plusAc} x0_88={plus88} t={System.Environment.TickCount64}");
                }

                if (ProbeMissLog && (address == ProbeAddrA || address == ProbeAddrB || address == ProbeAddrCtl))
                {
                    ulong threadUid = 0;

                    try
                    {
                        threadUid = Instructions.NativeInterface.GetContext()?.ThreadUid ?? 0;
                    }
                    catch
                    {
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Cpu,
                        $"NX2TX miss addr=0x{address:X} mode={mode} threadUid={threadUid} t={System.Environment.TickCount64}");
                }

                func = Translate(address, mode, highCq: false);

                TranslatedFunction oldFunc = Functions.GetOrAdd(address, func.GuestSize, func);

                if (oldFunc != func)
                {
                    JitCache.Unmap(func.FuncPointer);
                    func = oldFunc;
                }

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.AddEntry(address, mode, highCq: false);
                }

                RegisterFunction(address, func);
            }

            return func;
        }

        internal void RegisterFunction(ulong guestAddress, TranslatedFunction func)
        {
            if (FunctionTable.IsValid(guestAddress) && (Optimizations.AllowLcqInFunctionTable || func.HighCq))
            {
                Volatile.Write(ref FunctionTable.GetValue(guestAddress), (ulong)func.FuncPointer);
            }
        }

        internal TranslatedFunction Translate(ulong address, ExecutionMode mode, bool highCq, bool singleStep = false, bool pptcTranslation = false)
        {
            ArmEmitterContext context = new(
                Memory,
                CountTable,
                FunctionTable,
                Stubs,
                address,
                highCq,
                _ptc.State != PtcState.Disabled,
                mode: Aarch32Mode.User,
                isSingleStep: singleStep);

            Logger.StartPass(PassName.Decoding);

            Block[] blocks = Decoder.Decode(Memory, address, mode, highCq, singleStep ? DecoderMode.SingleInstruction : DecoderMode.MultipleBlocks);

            Logger.EndPass(PassName.Decoding);

            Logger.StartPass(PassName.Translation);

            InstEmitFlowHelper.EmitCallDepthCheckAndIncrement(context, Const(address));
            EmitSynchronization(context);

            if (blocks[0].Address != address)
            {
                context.Branch(context.GetLabel(address));
            }

            ControlFlowGraph cfg = EmitAndGetCFG(context, blocks, out Range funcRange, out Counter<uint> counter, pptcTranslation);

            if (cfg == null)
            {
                return null;
            }

            ulong funcSize = funcRange.End - funcRange.Start;

            Logger.EndPass(PassName.Translation, cfg);

            Logger.StartPass(PassName.RegisterUsage);

            RegisterUsage.RunPass(cfg, mode);

            Logger.EndPass(PassName.RegisterUsage);

            OperandType retType = OperandType.I64;
            OperandType[] argTypes = [OperandType.I64];

            CompilerOptions options = highCq ? CompilerOptions.HighCq : CompilerOptions.None;

            if (context.HasPtc && !singleStep)
            {
                options |= CompilerOptions.Relocatable;
            }

            CompiledFunction compiledFunc = Compiler.Compile(cfg, argTypes, retType, options, RuntimeInformation.ProcessArchitecture);

            if (context.HasPtc && !singleStep)
            {
                Hash128 hash = Ptc.ComputeHash(Memory, address, funcSize);

                _ptc.WriteCompiledFunction(address, funcSize, hash, highCq, compiledFunc);
            }

            GuestFunction func = compiledFunc.MapWithPointer<GuestFunction>(JitCache, out nint funcPointer);

            Allocators.ResetAll();

            return new TranslatedFunction(func, funcPointer, counter, funcSize, highCq);
        }

        private void BackgroundTranslate()
        {
            while (_threadCount != 0 && Queue.TryDequeue(out RejitRequest request))
            {
                TranslatedFunction func = Translate(request.Address, request.Mode, highCq: true);

                Functions.AddOrUpdate(request.Address, func.GuestSize, func, (key, oldFunc) =>
                {
                    EnqueueForDeletion(key, oldFunc);
                    return func;
                });

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.UpdateEntry(request.Address, request.Mode, highCq: true);
                }

                RegisterFunction(request.Address, func);
            }
        }

        private readonly struct Range
        {
            public ulong Start { get; }
            public ulong End { get; }

            public Range(ulong start, ulong end)
            {
                Start = start;
                End = end;
            }
        }

        private static ControlFlowGraph EmitAndGetCFG(
            ArmEmitterContext context,
            Block[] blocks,
            out Range range,
            out Counter<uint> counter,
            bool pptcTranslation)
        {
            counter = null;

            ulong rangeStart = ulong.MaxValue;
            ulong rangeEnd = 0;

            for (int blkIndex = 0; blkIndex < blocks.Length; blkIndex++)
            {
                Block block = blocks[blkIndex];

                if (!block.Exit)
                {
                    if (rangeStart > block.Address)
                    {
                        rangeStart = block.Address;
                    }

                    if (rangeEnd < block.EndAddress)
                    {
                        rangeEnd = block.EndAddress;
                    }
                }

                if (block.Address == context.EntryAddress)
                {
                    if (!context.HighCq)
                    {
                        EmitRejitCheck(context, out counter);
                    }

                    context.ClearQcFlag();
                }

                context.CurrBlock = block;

                context.MarkLabel(context.GetLabel(block.Address));

                if (block.Exit)
                {
                    // Return to managed rather than tail call.
                    bool useReturns = Optimizations.EnableDebugging;

                    if (Optimizations.EnableDebugging)
                    {
                        EmitDebugPrecisePcUpdate(context, block.Address);
                    }

                    InstEmitFlowHelper.EmitVirtualJump(context, Const(block.Address), isReturn: useReturns);
                }
                else
                {
                    for (int opcIndex = 0; opcIndex < block.OpCodes.Count; opcIndex++)
                    {
                        OpCode opCode = block.OpCodes[opcIndex];

                        context.CurrOp = opCode;

                        bool isLastOp = opcIndex == block.OpCodes.Count - 1;

                        if (isLastOp)
                        {
                            context.SyncQcFlag();

                            if (block.Branch is { Exit: false } && block.Branch.Address <= block.Address)
                            {
                                EmitSynchronization(context);
                            }
                        }

                        if (Optimizations.EnableDebugging)
                        {
                            EmitDebugPrecisePcUpdate(context, opCode.Address);
                        }

                        Operand lblPredicateSkip = default;

                        if (context.IsInIfThenBlock && context.CurrentIfThenBlockCond != Condition.Al)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, context.CurrentIfThenBlockCond.Inverse);
                        }

                        if (opCode is OpCode32 { Cond: < Condition.Al } op)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, op.Cond.Inverse);
                        }

                        if (opCode.Instruction.Emitter != null)
                        {
                            opCode.Instruction.Emitter(context);
                            // if we're pre-compiling PPTC functions, and we hit an Undefined instruction as the first
                            // instruction in the block, mark the function as blacklisted
                            // this way, we don't pre-compile Exlaunch hooks, which allows ExeFS mods to run with PPTC 
                            if (pptcTranslation && opCode.Instruction.Name == InstName.Und && blkIndex == 0)
                            {
                                range = new Range(rangeStart, rangeEnd);
                                return null;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Invalid instruction \"{opCode.Instruction.Name}\".");
                        }

                        if (lblPredicateSkip != default)
                        {
                            context.MarkLabel(lblPredicateSkip);
                        }

                        if (context.IsInIfThenBlock && opCode.Instruction.Name != InstName.It)
                        {
                            context.AdvanceIfThenBlockState();
                        }
                    }
                }
            }

            range = new Range(rangeStart, rangeEnd);

            return context.GetControlFlowGraph();
        }

        internal static void EmitRejitCheck(ArmEmitterContext context, out Counter<uint> counter)
        {
            const int MinsCallForRejit = 100;

            counter = new Counter<uint>(context.CountTable);

            Operand lblEnd = Label();

            Operand address = !context.HasPtc ?
                Const(ref counter.Value) :
                Const(ref counter.Value, Ptc.CountTableSymbol);

            Operand curCount = context.Load(OperandType.I32, address);
            Operand count = context.Add(curCount, Const(1));
            context.Store(address, count);
            context.BranchIf(lblEnd, curCount, Const(MinsCallForRejit), Comparison.NotEqual, BasicBlockFrequency.Cold);

            context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.EnqueueForRejit)), Const(context.EntryAddress));

            context.MarkLabel(lblEnd);
        }

        internal static void EmitSynchronization(EmitterContext context)
        {
            long countOffs = NativeContext.GetCounterOffset();

            Operand lblNonZero = Label();
            Operand lblExit = Label();

            Operand countAddr = context.Add(context.LoadArgument(OperandType.I64, 0), Const(countOffs));
            Operand count = context.Load(OperandType.I32, countAddr);
            context.BranchIfTrue(lblNonZero, count, BasicBlockFrequency.Cold);

            Operand running = context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.CheckSynchronization)));
            context.BranchIfTrue(lblExit, running, BasicBlockFrequency.Cold);

            context.Return(Const(0L));

            context.MarkLabel(lblNonZero);
            count = context.Subtract(count, Const(1));
            context.Store(countAddr, count);

            context.MarkLabel(lblExit);
        }

        internal static void EmitDebugPrecisePcUpdate(EmitterContext context, ulong address)
        {
            long debugPrecisePcOffs = NativeContext.GetDebugPrecisePcOffset();

            Operand debugPrecisePcAddr = context.Add(context.LoadArgument(OperandType.I64, 0), Const(debugPrecisePcOffs));
            context.Store(debugPrecisePcAddr, Const(address));
        }

        public void InvalidateJitCacheRegion(ulong address, ulong size)
        {
            ulong[] overlapAddresses = [];

            int overlapsCount = Functions.GetOverlaps(address, size, ref overlapAddresses);

            if (overlapsCount != 0)
            {
                // If rejit is running, stop it as it may be trying to rejit a function on the invalidated region.
                ClearRejitQueue(allowRequeue: true);
            }

            for (int index = 0; index < overlapsCount; index++)
            {
                ulong overlapAddress = overlapAddresses[index];

                if (Functions.TryGetValue(overlapAddress, out TranslatedFunction overlap))
                {
                    Functions.Remove(overlapAddress);
                    Volatile.Write(ref FunctionTable.GetValue(overlapAddress), FunctionTable.Fill);
                    EnqueueForDeletion(overlapAddress, overlap);
                }
            }

            // TODO: Remove overlapping functions from the JitCache aswell.
            // This should be done safely, with a mechanism to ensure the function is not being executed.
        }

        internal void EnqueueForRejit(ulong guestAddress, ExecutionMode mode)
        {
            Queue.Enqueue(guestAddress, mode);
        }

        private void EnqueueForDeletion(ulong guestAddress, TranslatedFunction func)
        {
            _oldFuncs.Enqueue(new(guestAddress, func));
        }

        private void ClearJitCache()
        {
            // Ensure no attempt will be made to compile new functions due to rejit.
            ClearRejitQueue(allowRequeue: false);

            List<TranslatedFunction> functions = Functions.AsList();

            foreach (TranslatedFunction func in functions)
            {
                JitCache.Unmap(func.FuncPointer);

                func.CallCounter?.Dispose();
            }

            Functions.Clear();

            while (_oldFuncs.TryDequeue(out KeyValuePair<ulong, TranslatedFunction> kv))
            {
                JitCache.Unmap(kv.Value.FuncPointer);

                kv.Value.CallCounter?.Dispose();
            }
        }

        private void ClearRejitQueue(bool allowRequeue)
        {
            if (!allowRequeue)
            {
                Queue.Clear();

                return;
            }

            lock (Queue.Sync)
            {
                while (Queue.Count > 0 && Queue.TryDequeue(out RejitRequest request))
                {
                    if (Functions.TryGetValue(request.Address, out TranslatedFunction func) && func.CallCounter != null)
                    {
                        Volatile.Write(ref func.CallCounter.Value, 0);
                    }
                }
            }
        }
    }
}

# C# LXTrace Replayer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replay a known-good Lichii GPU trace through our C# GPU layer, first on the synthetic 4-frame sample, then on the real Fast Fusion trace, to isolate GPU correctness from guest boot.

**Architecture:** Stream `.lxtrace` records with a C# reader; bring up a headless GpuContext; feed Memory/Submit/Present records to existing GPU APIs; add snapshot restore in stages. Each stage has a verifiable milestone.

**Tech Stack:** C#/.NET 11, ZstdSharp, XXHash128, xUnit, Lichii oracle at `/tmp/opencode/lichii/build/bin/eden-cli`.

**Spec:** `docs/superpowers/specs/2026-11-20-lxtrace-replayer-design.md`

## Global Constraints

- Author every commit `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to `origin` = github.com/kleinergoo7/Kenji-NX. Never push to `kenji-nx` (Forgejo).
- Keep exactly two local branches; archive read-only.
- Build `dotnet build Ryujinx.slnx -c Debug`; smoke `Nx2SwizzleTests` 3 passed.
- Never commit `.lxtrace`, frames, logs, or scratch (all traces stay in `/tmp/opencode/`).
- Agent model only `opencode-go/hy4-flash-free`.
- Never load the 704 MB trace into memory — stream it.

## Review Focus

1. Wrong Submit header size/array order → corrupt commands — pinned in Task 1 by parsing the real sample and matching the Python miner's known values.
2. Memory writes arriving after a submit's prefetch → stale/wrong commands — pinned in Task 2 by ordering Memory records before the next Submit.
3. Present before GPU drain → stale frames — pinned by draining before composite in Task 2.
4. Snapshot restore corrupting Maxwell — pinned by gating all NX2 restore on trace Config `switch2`.

---

### Task 0: Generate sample trace and expected outputs

**Files:**
- Modify: `docs/nx2/ORACLE.md` (record trace commands and expected frames)
- (artifacts only in `/tmp/opencode/`)

**Interfaces:**
- Consumes: Lichii oracle CLI `--gpu-trace-sample`, `--replay`.
- Produces: sample trace + 4 expected PNG frames + hashes; real trace (already recorded) + expected frames.json.

- [ ] **Step 1: Generate and replay the synthetic sample**

Run:
```
mkdir -p /tmp/opencode/sample-trace /tmp/opencode/sample-replay
/tmp/opencode/lichii/build/bin/eden-cli --gpu-trace-sample /tmp/opencode/sample-trace/sample.lxtrace
/tmp/opencode/lichii/build/bin/eden-cli --replay /tmp/opencode/sample-trace/sample.lxtrace --out /tmp/opencode/sample-replay --frames 4
```
Expected: 4 PNG frames, `frames.json` with hashes; frames non-black (`identify -format '%[fx:mean]'`).

- [ ] **Step 2: Record the real trace's expected frames**

The real trace `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace` and its replay output
`/tmp/opencode/oracle-replay/frames.json` (5 frames, mean ~0.826) already exist — verify they are
present and record their hashes in the doc as the target.

- [ ] **Step 3: Document and commit**

Write the commands, trace paths, and expected hashes into `docs/nx2/ORACLE.md`. Commit as kleinergoo7, do not push.

```bash
git add docs/nx2/ORACLE.md
git commit -m "nx2: record replayer sample traces and expected frames"
```

### Task 1: C# trace reader

**Files:**
- Create: `src/Ryujinx.Graphics.Gpu/GpuTrace/LxTraceReader.cs`
- Create: `src/Ryujinx.Tests/Graphics/LxTraceReaderTests.cs`
- Modify: `src/Ryujinx.Graphics.Gpu/Ryujinx.Graphics.Gpu.csproj` (if a new test project reference is needed — prefer existing `Ryujinx.Tests`)

**Interfaces:**
- Consumes: `.lxtrace` file path.
- Produces: `internal sealed class LxTraceReader : IDisposable` with `LxConfig Config { get; }` and `IEnumerable<LxRecord> ReadRecords()` streaming; record types as typed structs including `LxSubmit { int Channel; ulong[] Entries; uint[] Prefetch; uint[] Offsets; uint[] Words; uint[] Deferred; }` and `LxPresent { ... }`.

- [ ] **Step 1: Write the failing test**

In `LxTraceReaderTests.cs`, build a synthetic in-memory trace (64-byte header + zstd stream with Config/Submit/Present/End) and assert: magic/version parsed; a Submit with `entry_count=1, word_count=2` yields `Entries.Length==1`, `Words.Length==2` with exact values; a Present yields its layer/fence. Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~LxTraceReaderTests" -v q`. Expected: FAIL (type doesn't exist).

- [ ] **Step 2: Implement the streaming reader**

Parse the 64-byte header, open `ZstdSharp.DecompressStream` from byte 64, loop reading 16-byte record headers (`type`, `reserved`, `size`) and typed payloads. Use the recon's exact layouts; Submit uses the 32-byte `<iiIIQII>` header then arrays in order entries/prefetch/offsets/words/deferred. Stream — do not buffer the whole stream. Throw on unknown record types with the type id.

- [ ] **Step 3: Verify against the real sample**

Add a test that reads `/tmp/opencode/sample-trace/sample.lxtrace` (skip if file absent) and asserts Config parses with `switch2` true and at least one Submit. Run the focused tests → PASS.

- [ ] **Step 4: Verify and commit**

Run focused tests + `Nx2SwizzleTests` smoke + `dotnet build Ryujinx.slnx -c Debug`. Commit as kleinergoo7, do not push.

```bash
git add src/Ryujinx.Graphics.Gpu/GpuTrace src/Ryujinx.Tests/Graphics/LxTraceReaderTests.cs
git commit -m "nx2: add streaming lxtrace reader"
```

### Task 2: Headless bring-up and stream feed (sample milestone)

**Files:**
- Create: `src/Ryujinx.Graphics.Gpu/GpuTrace/LxTraceReplayer.cs`
- Create: `src/Ryujinx.Tests/Graphics/LxTraceReplayerTests.cs`
- Modify: `docs/nx2/ORACLE.md` (milestone result)

**Interfaces:**
- Consumes: `LxTraceReader` (Task 1); C# APIs `GpuContext.CreateChannel`, `GpuChannel.BindMemory`, `PushEntries`, `PushHostCommandBuffer`, `GPFifoDevice.SignalNewEntries`, `MemoryManager.Write`, `Window.EnqueueFrameThreadSafe`, `IRenderer.Screenshot`.
- Produces: `internal sealed class LxTraceReplayer { public static ReplayResult Replay(string path, int maxFrames); }` where `ReplayResult` exposes frame hashes and a screenshot path.

- [ ] **Step 1: Write the failing test**

In `LxTraceReplayerTests.cs`, assert `Replay(samplePath, 4)` returns 4 frames and each hash is non-zero / each frame non-black (skip if sample absent). Run focused tests → FAIL (type doesn't exist).

- [ ] **Step 2: Implement headless bring-up**

Use the existing `src/Ryujinx/Headless/HeadlessRyujinx.Init.cs` path to create a headless GpuContext/renderer on `VK_KHR_headless_surface` (or the existing headless window). Register a process page table, create a memory manager and channel (40-bit, 4 KiB pages — the sample's geometry). Do not boot a full guest/Switch.

- [ ] **Step 3: Implement the stream loop for the sample**

For each record: `Memory` → `MemoryManager.Write(physical, data)` (apply BEFORE the next submit's prefetch); `Submit` → translate `Entries`/`Prefetch`/`Words` to our GPFIFO push (`PushEntries` for u64 entries, `PushHostCommandBuffer` for prefetch words), then `SignalNewEntries`; `Present` → drain GPU work (closest: `RunAfterGpu` / `SignalNewEntries` + `DispatchCalls`), then `Window.EnqueueFrameThreadSafe` + `SignalFrameReady` + `IRenderer.Screenshot`; `End` → stop. Handle `Resume` as no-op.

- [ ] **Step 4: Run the sample milestone**

Run the focused test. Expected: 4 non-black frames from our pipeline. If frames are black, record the actual result and stop — do NOT proceed to snapshot restore until the no-snapshot sample works.

- [ ] **Step 5: Verify and commit**

Focused tests + smoke + build. Commit as kleinergoo7, do not push.

```bash
git add src/Ryujinx.Graphics.Gpu/GpuTrace src/Ryujinx.Tests/Graphics/LxTraceReplayerTests.cs docs/nx2/ORACLE.md
git commit -m "nx2: replay synthetic lxtrace sample headlessly"
```

### Task 3: Restore global snapshot state

**Files:** `src/Ryujinx.Graphics.Gpu/GpuTrace/LxStateRestore.cs` (new), tests, doc.
**Interfaces:** State records kinds 8/9/10 (QmdStates, Syncpoints, SemaphoreRegions).

- [ ] **Step 1:** Tests for restoring syncpoint values via `SynchronizationManager.IncrementSyncpoint`, QMD state into `QmdScheduler`, semaphore regions via the NX2 semaphore registry; gate on Config.switch2.
- [ ] **Step 2:** Implement the three global restorers with narrow internal methods on the engine classes if needed (no broad refactor).
- [ ] **Step 3:** Replay the real trace's snapshot prefix + first 2 presents; record whether frames move toward non-black.
- [ ] **Step 4:** Verify, commit (kleinergoo7, no push).

### Task 4: Restore per-channel and macro/shadow state

**Files:** `LxStateRestore.cs` (extend), tests.
**Interfaces:** State kinds 1 Pusher, 2 Maxwell3D, 3 KeplerCompute, 7 MacroContext (263 KB code), other engines.

- [ ] **Step 1:** Tests for macro RAM code load into our `GPFifoClass` macro banks, pusher method state, Maxwell3D shadow/root tables (add narrow internal restore methods to those classes).
- [ ] **Step 2:** Implement, gated by switch2.
- [ ] **Step 3:** Replay real trace first 2 presents; compare frame hashes to oracle.
- [ ] **Step 4:** Verify, commit (kleinergoo7, no push).

### Task 5: Sparse map parity and final validation

**Files:** `LxTraceReplayer.cs` (extend), doc.
**Interfaces:** 1171 sparse GMMU maps; `MemoryManager.Nx2IdentityMapping`, `Nx2GpuMemory`.

- [ ] **Step 1:** Apply sparse GMMU/SMMU maps with parity; document any gaps.
- [ ] **Step 2:** Replay the real trace, 5 frames; compare to oracle frames.json (mean ~0.826).
- [ ] **Step 3:** Final A/B table; update ORACLE.md and CONTINUE_HERE.md; commit and push to origin.

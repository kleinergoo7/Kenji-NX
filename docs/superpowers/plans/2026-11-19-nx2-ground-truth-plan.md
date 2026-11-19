# NX2 Ground-Truth Bring-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer two decisive questions about why Fast Fusion renders nothing, fix only the concrete blocker they reveal, or stop and pivot to OoT.

**Architecture:** Ground-truth-first and bounded: revert the unvalidated fix, add observe-only traces that produce binary verdicts (`FS_VIEW=`, `DRAW_MACRO=`), then make at most one targeted fix. The spec is the authority; this plan argues from it.

**Tech Stack:** C# / .NET 11, Ryujinx HLE + GPU layers, xUnit tests under `src/Ryujinx.Tests`, Eglantine `nx2` source as read-only reference.

**Spec:** `docs/superpowers/specs/2026-11-19-nx2-ground-truth-design.md`

## Global Constraints

- Author every commit `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to `origin` = github.com/kleinergoo7/Kenji-NX. Never push to the `kenji-nx` (Forgejo) remote.
- Keep exactly two local branches: `kenji-nx2-oot-bringup` and `archive/nx2-oot-pre-kenji-rebase`. Never modify the archive branch.
- Build: `dotnet build Ryujinx.slnx -c Debug` (SDK pinned 11.0.100). Smoke: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SwizzleTests"` → 3 passed.
- Fast Fusion runs require `NX2_PATCH=""` and `--dram-size MemoryConfiguration12GiB --memory-manager-mode SoftwarePageTable --ignore-missing-services`.
- ROM `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` (not in repo). Logs/scratch in git-ignored `.nx2-logs/`; never commit `.nup`/`.log`/`.rgba`/`.png`/`.bin`.
- Small focused commits, 50/72 messages.
- **Stop rule:** after Task 2, if no concrete blocker is identified, stop the plan and pivot to OoT. No third diagnostic without explicit human approval.

## Review Focus

1. **Application filesystem calls with wrong pid attribution** could make `FS_VIEW=` meaningless — pinned in Task 1 by asserting the trace filters on the application pid and records result codes.
2. **A draw macro whose hash is absent from `MacroHLETable`** could be misclassified as "no draw macro" — pinned in Task 2 by recording raw emitted draw registers independently of HLE hash recognition.
3. **The reverted fix leaving stale instrumentation** that skews counts — pinned in Task 0 by verifying the revert restores the pre-1l state and the smoke test/build pass.
4. **Unvalidated changes becoming a foundation** — pinned by Task 0 reverting before any new work.
5. **Scope creep into a third diagnostic** — pinned by the stop rule and the gate after Task 2.

---

### Task 0: Revert the unvalidated shared-bank fix

**Files:**
- Revert: commit `545f53d57` (`nx2: route compute macros to the shared MME code bank`)

**Interfaces:**
- Consumes: current HEAD with the unvalidated Task 1l fix.
- Produces: HEAD with that fix reverted; all diagnostic commits (through `702c97531`) retained.

- [ ] **Step 1: Revert the fix commit**

Run: `git revert --no-edit 545f53d57`
Expected: a new revert commit created, authored kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com> (set `git config` first if needed).

- [ ] **Step 2: Verify build and smoke tests**

Run: `dotnet build Ryujinx.slnx -c Debug -v q && dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SharedMacroBankTests|FullyQualifiedName~Nx2SwizzleTests" -v q`
Expected: build 0 errors; the shared-bank regression tests are gone with the revert; swizzle 3 passed. (The reverted test file should no longer exist — confirm with `ls src/Ryujinx.Tests/Graphics/Nx2SharedMacroBankTests.cs`.)

- [ ] **Step 3: Record and commit the revert rationale**

Append a short note to `docs/nx2/CONTINUE_HERE.md`: "Task 1l shared-bank fix reverted: wired correctly but `NO_COMPUTE_EMIT`, and risks 3D/compute Macro slot cross-talk; not a valid foundation." Commit with the revert (fold into the note commit if needed), 50/72 message, do not push yet.

```bash
git add docs/nx2/CONTINUE_HERE.md
git commit -m "docs: record revert of unvalidated bank fix"
```

### Task 1: Decide whether the guest can see its content (`FS_VIEW=`)

**Files:**
- Modify: `src/Ryujinx.HLE/HOS/Services/Fs/FileSystemProxy/IFileSystem.cs` (fs trace extension)
- Modify: `src/Ryujinx.HLE/HOS/Services/Fs/FsLoaderProgress.cs` (decisive counters + summary)
- Create: `src/Ryujinx.Tests/HLE/Nx2FsViewTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `FS_VIEW=`)

**Interfaces:**
- Consumes: existing `NX2_FS_LOG` path decoding (fixed in `7fbe1f39f`) and `FsLoaderProgress.OnOpen/OnRead` counters.
- Produces: `NX2_FS_TRACE=1` env-gated, capped logging of every Fs operation from the application pid with path and **result code** (open, directory open, entry type, read count/bytes), plus a one-line summary `NX2FSVIEW pid=<app> opens=<n> contentOpens=<n> contentFails=<n> reads=<n> bytes=<n>`; `FsLoaderProgress.DescribeFsView()` returning the same counters for tests.

- [ ] **Step 1: Write the failing test**

In `Nx2FsViewTests.cs`, assert that (a) recording an open of a content path (`/romfs/...`) increments `contentOpens`, a failed result increments `contentFails`, and a read of `N` bytes increments `reads`/`bytes`; (b) `DescribeFsView()` renders `contentOpens=1 contentFails=1 reads=1 bytes=4096`; (c) opens from a non-application pid are excluded when the pid filter is set. Reset counters in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2FsViewTests" -v q`
Expected: FAIL — new API does not exist.

- [ ] **Step 3: Add the observe-only trace**

Extend `FsLoaderProgress` with content-path counters and a `DescribeFsView()` summary; in `IFileSystem` (and, if needed, `IFileSystemProxy`/directory services), when `NX2_FS_TRACE=1` and the caller pid is the active application, log path + result code for open/directory/entry-type/read operations (capped 200 lines). Content path = anything not under `/save`, `/system`, or the standard config paths. Do not change FS behaviour or results.

- [ ] **Step 4: Run the decisive capture**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_FS_TRACE=1 NX2_FS_LOG=1`. In `docs/nx2/CONTINUE_HERE.md`, record `FS_VIEW=` as exactly one of:
  - `CONTENT_OPEN_FAILED` — contentOpens > 0 and contentFails > 0 (name the first failed path + result code).
  - `CONTENT_OK` — contentOpens > 0, contentFails == 0, but reads/bytes are 0.
  - `NO_CONTENT_ATTEMPT` — contentOpens == 0 (name the only paths opened).
Cite the `NX2FSVIEW` summary and the first few traced lines.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2FsViewTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.HLE/HOS/Services/Fs src/Ryujinx.Tests/HLE/Nx2FsViewTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: trace application filesystem view"
```

**Gate:** no product-code fix in this task. The `FS_VIEW=` value is input to Task 3.

### Task 2: Decide whether a draw macro runs (`DRAW_MACRO=`)

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/MME/MacroTuring.cs` (per-macro draw-emit summary)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoProcessor.cs` (expose summary via `Nx2GpuStall`)
- Modify: `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs` (one-shot `NX2_DRAW_MACRO_SUMMARY` log / progress token)
- Create: `src/Ryujinx.Tests/Graphics/Nx2DrawMacroSummaryTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `DRAW_MACRO=`)

**Interfaces:**
- Consumes: existing `NX2_MME_ENGINE=1` per-macro log (`NX2MMEC` pos/hash/emits), `MacroTuring.Macros3D`/`MacrosCompute`/`ComputeEmittedHistogram`, `Nx2GpuStall.DescribeEmitted()`, and `MacroHLETable` draw hashes.
- Produces: `drawEmitHits` counter incremented when a macro emits a draw-class register (independent of HLE hash recognition), and a one-shot summary `NX2DRAWMACRO macros=<n> drawEmitHits=<n> drawHashes=<n>` at present #1 or via `NX2_DRAW_MACRO_SUMMARY=1`.

- [ ] **Step 1: Write the failing test**

In `Nx2DrawMacroSummaryTests.cs`, assert that (a) recording an emitted register equal to a draw-class method (e.g. `0x585`/`0x586` DrawArrays/DrawEnd) increments `drawEmitHits`, while a non-draw register does not; (b) the summary renders `macros=3 drawEmitHits=1 drawHashes=0`; (c) counters reset in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2DrawMacroSummaryTests" -v q`
Expected: FAIL — counters/API do not exist.

- [ ] **Step 3: Add the observe-only draw-macro summary**

In `MacroTuring`'s emit path (where `RecordComputeEmitted` lives), increment a shared `drawEmitHits` counter when the emitted register is a draw-class method or the macro's hash matches a `MacroHLETable` draw function; expose via `Nx2GpuStall` and add a one-shot gated log. No change to emit behaviour or dispatch.

- [ ] **Step 4: Run the decisive capture**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_DRAW_MACRO_SUMMARY=1 NX2_MME_ENGINE=1 NX2_MACRO_LOG=1`. In `docs/nx2/CONTINUE_HERE.md`, record `DRAW_MACRO=` as exactly one of:
  - `DRAW_MACRO_PRESENT_BUT_NO_DRAW` — `drawEmitHits > 0` but `draws == 0` (name the macro position/hash and emitted register).
  - `NO_DRAW_MACRO` — `drawEmitHits == 0` across all 3D+compute macros (report per-engine macro counts and top emitted registers).

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2DrawMacroSummaryTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/MME src/Ryujinx.Graphics.Gpu/Engine/GPFifo src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs src/Ryujinx.Tests/Graphics/Nx2DrawMacroSummaryTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: summarize draw macro emission"
```

**Gate:** stop after this task. If `FS_VIEW=NO_CONTENT_ATTEMPT` and `DRAW_MACRO=NO_DRAW_MACRO`, there is no concrete emulator-side blocker — stop the plan and pivot to OoT. Otherwise proceed to Task 3.

### Task 3: Fix the single concrete blocker

**Files:** determined by the Task 1/2 verdicts — to be written only after those results are known and approved.

**Interfaces:** consumes `FS_VIEW=` and `DRAW_MACRO=` from Tasks 1-2.

- [ ] **Step 1: State the blocker and the candidate fix**

Write a short note naming the one concrete blocker (from the decisive verdicts) and the minimal fix, cited to the relevant file:line. Do not start from a list of "differences from Eglantine" — only the blocker the evidence identified.

- [ ] **Step 2: Write the failing test** that pins the blocker's expected behaviour.

- [ ] **Step 3: Implement the single fix** (no bundled cleanup).

- [ ] **Step 4: Verify** `draws > 0` in `NX2 progress` and a non-black frame dump/screenshot.

- [ ] **Step 5: Commit and push** with a short focused message as kleinergoo7.

**Gate:** if the fix does not move `draws`, stop — do not attempt a second fix without human approval.

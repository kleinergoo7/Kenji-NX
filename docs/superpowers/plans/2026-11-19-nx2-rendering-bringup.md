# NX2 Rendering Bring-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Get Fast Fusion (Switch 2 NUP) from zero-draw, all-zero presents to a drawn non-black frame, by closing the documented NX2 GPU-pipeline gaps in verified increments.

**Architecture:** One shared pipeline, generation-flagged rather than forked: the existing GPFIFO/MME/QMD/shader-cache classes gain NX2 behaviour behind `IsAmpere`/`UsesNx2` flags; the only new shader code is an SM86 opcode table plus IR-to-IR rewrites feeding the existing translator and backend. Work proceeds in small, individually testable commits, each verified with the diagnostics already in the tree.

**Tech Stack:** C# / .NET 11, Ryujinx GPU and HLE layers, xUnit tests under `src/Ryujinx.Tests`, Eglantine `nx2` branch as read-only algorithm reference (`/tmp/opencode/eglantine`).

**Spec:** `docs/superpowers/specs/2026-11-19-nx2-rendering-design.md`

## Global Constraints

- Author every commit `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to `origin` = github.com/kleinergoo7/Kenji-NX. Never push to the `kenji-nx` (Forgejo) remote.
- Keep exactly two local branches: `kenji-nx2-oot-bringup` and `archive/nx2-oot-pre-kenji-rebase`. Never modify the archive branch.
- Build: `dotnet build Ryujinx.slnx -c Debug` (SDK pinned 11.0.100). Smoke test: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SwizzleTests"` → 3 passed.
- Fast Fusion runs require `NX2_PATCH=""` and the standard profile: `--dram-size MemoryConfiguration12GiB --memory-manager-mode SoftwarePageTable --ignore-missing-services`.
- ROM: `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` (not in repo). Logs and dumps land in git-ignored scratch `.nx2-logs/`; never commit them.
- Small focused commits, 50/72 messages; keep `.nup`, `.rgba`, `.png` out of git.
- Stop rule: if three consecutive fixes in one phase fail to move its chosen verification signal, stop and revisit the design with the human partner.

## Review Focus

1. **A Maxwell shader decoded with the Ampere flag (or vice versa)** must keep producing the same program — pinned in Task 4 by a Maxwell fixture passed through the generation-flagged `Decoder.Decode`.
2. **QMD release writes with the wrong width or missing slots** silently deadlock the 3D queue — pinned in Task 2 by tests asserting 4-byte writes at slots 24, 28 and 52 and bit-19 max semantics.
3. **Parked split methods leaking across submissions** swallow unrelated later entries — pinned in Task 3 by a test asserting `Clear()` at submission end and the 16-entry eviction cap.
4. **NX2 characteristics tail (`+0xb4 = 64`, versions `0x70..0x80 = 0x40`) regressions** break TSG subcontexts, reintroducing "no command memory" — pinned in Task 2 by a guard test on the existing tail writer.
5. **No draws even after the GPU path is fixed** means the blocker is in the guest, not the emulator — pinned in Task 1 by a mandatory evidence gate before Task 2 begins, and by the stop rule above.

---

### Task 0: Record the Phase 0 triage result and route

**Files:**
- Modify: `docs/nx2/CONTINUE_HERE.md` (status + last-verified block)
- Reference log: `.nx2-logs/run-20261119-160446/ocarina.log`

**Interfaces:**
- Consumes: existing `NX2_FIFO_TRACE`, `NX2_SUBMIT_LOG`, `NX2_SYNCPT_TRACE`, `NX2_METHOD_LOG` output.
- Produces: one of two routes — `ROUTE=GUEST_GATE` or `ROUTE=GPU_PIPELINE` — recorded in the doc.

- [ ] **Step 1: Write the measured result into `docs/nx2/CONTINUE_HERE.md`**

Record these facts verbatim from the referenced log: 4 submits on syncpoints 1 and 2 with `inc=1`/`inc=2`; `NX2 spincr id=1 now=1`, `id=2 now=2`; `NX2SPT 201-slot=0 hostAtSig=2`; `NX2FIFO` entries captured fully (`captured == words`) with `pendingMethod=0x0` at entry; `NX2 progress` still `draws=0`; characteristics tail written (0xE0/0xD0 NX2 block) and subcontext ids 2 and 3 created. Conclude: the emulator's GPFIFO, fence and characteristics paths are healthy; **no render command buffers are produced by the guest**.

- [ ] **Step 2: Apply the decision rule**

If the log shows an unmet acquire, a parked method, an unreached fence or a TSG/characteristics error, set `ROUTE=GPU_PIPELINE`. Otherwise (the measured case) set `ROUTE=GUEST_GATE`: the guest-side render gate must be localised before GPU work can be justified.

- [ ] **Step 3: Verify and commit**

Run: `grep -n "ROUTE=\|draws=0\|subcontext" docs/nx2/CONTINUE_HERE.md`
Expected: the route line and the evidence lines are present.

```bash
git add docs/nx2/CONTINUE_HERE.md
git commit -m "docs: record Fast Fusion Phase 0 triage and route"
```

### Task 1: Localise the guest-side render gate (ROUTE=GUEST_GATE)

**Files:**
- Modify: `docs/nx2/CONTINUE_HERE.md` (evidence and finding)
- Modify (only if Step 3 finds an emulator-side gate): the owning `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2*Session.cs`, plus a focused test under `src/Ryujinx.Tests/HLE/`.

**Interfaces:**
- Consumes: direct `dotnet … Ryujinx.dll` invocation (the run script unsets `NX2_THREAD_DUMP_AT_PRESENT`).
- Produces: a named gate (service reply, shared-memory flag or event) with log evidence, or an explicit "not found" with the next diagnostic to add.

- [ ] **Step 1: Capture stacks at two steady-state presents**

Run two 90 s invocations with `NX2_PATCH=""`, `NX2_THREAD_DUMP_AT_PRESENT=100` and `=5000`, adding `NX2_PRESENT_TRACE=1 NX2_MISSING_LOG=1 NX2_SVC_COUNT=1`. Use the direct command form; write each log to its own `.nx2-logs/run-*` directory.

- [ ] **Step 2: Compare the MainThread and TaskQueue stacks**

Run: `grep -a "NX2 td pid=109\|NX2 main mutex" <log>`
Expected: identify whether the main thread leaves `nn::wsi::ounce::native::NativeWindow::Present` between presents and whether any `TaskQueue_Core*` leaves `nn::os::WaitEvent`. Read the `NX2_SVC_THREAD` histograms to confirm whether submit-related syscalls ever resume.

- [ ] **Step 3: Record the finding, fix emulator-side gates, and set the next action**

Write the named gate, its evidence (log line + guest stack frame) and the smallest candidate fix into the doc. If the gate is an emulator-side reply or unsignalled event, implement that single fix in the owning `INx2*Session`/service file in this task with a focused unit test, then re-run the same capture to confirm the guest moves past it. If the gate is outside the emulator (e.g. missing controller input or an asset load that never completes), stop and report to the human partner before Task 2 — do not start GPU changes against an unconfirmed blocker.

- [ ] **Step 4: Verify and commit**

Run: `grep -n "GATE=\|evidence:" docs/nx2/CONTINUE_HERE.md`
Expected: a single named gate with evidence.

```bash
git add docs/nx2/CONTINUE_HERE.md
git commit -m "docs: localise Fast Fusion render gate"
```

### Task 1b: Decode QMD releases and semaphore words for confirmation

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs` (`SendSignalingPcasB`, near :134-162)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs` (4-byte release path near :235-243)
- Create: `src/Ryujinx.Graphics.Gpu/Engine/Compute/Nx2QmdTrace.cs`
- Test: `src/Ryujinx.Tests/Graphics/Nx2QmdTraceTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record the confirmation result)

**Interfaces:**
- Consumes: `ComputeQmd` bitfield (`ComputeQmd.cs:126-249`), `QmdDescriptor`/`QmdScheduler` (`QmdDescriptor.cs`), existing `NX2_SUBMIT_LOG`/`NX2_SYNCPT_TRACE`/`NX2_FIFO_TRACE` env conventions.
- Produces: `internal static class Nx2QmdTrace { public static string Describe(ComputeQmd qmd, int dataRam27); }` returning a one-line summary; `public static void WatchSemaphore(ulong address, uint value, string op)` logging 4-byte release writes and acquire reads. No behaviour change — purely env-gated observation.

- [ ] **Step 1: Write the failing decode test**

In `Nx2QmdTraceTests.cs`, build a 256-byte QMD fixture with `SemaphoreReleaseEnable0 = true`, `Release0AddressLower = 0x1234`, `Release0Payload = 7`, and assert `Describe(qmd, 0x27)` contains `rel0=0x1234:7` and `dram27=0x27`; assert a second fixture with both enables false contains `rel0=- rel1=-`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdTraceTests" -v q`
Expected: FAIL — `Nx2QmdTrace` does not exist.

- [ ] **Step 3: Add the env-gated trace**

In `ComputeClass.SendSignalingPcasB`, when `NX2_QMD_TRACE=1`, read the raw 256-byte QMD once, call `Nx2QmdTrace.Describe(qmd, dataRam27)` and log one `NX2QMD qmd=0x{address:X} {summary}` line per dispatch (capped at 200 lines). Include release enables, addresses, payloads, the third slot at raw word 52, QMD version and `UsesMme64`. In the `GPFifoClass` 4-byte release path, when `NX2_SEM_WATCH=1`, log `NX2SEM w addr=0x{address:X} value=0x{value:X}` / `r` for acquire reads (capped at 200 lines). No functional change to dispatch or release writes.

- [ ] **Step 4: Re-run the confirmation capture**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_QMD_TRACE=1 NX2_SEM_WATCH=1 NX2_SYNCPT_TRACE=1 NX2_FIFO_TRACE=1`. Write the result into `docs/nx2/CONTINUE_HERE.md` as `CONFIRM=` with one of three values: `QMD_RELEASE_MISSING` (QMD dispatches with release enables set, but matching semaphore writes never appear), `UPSTREAM_OF_QMD` (no QMD dispatch at all), or `NO_BLOCKER_FOUND`. Name the exact QMD address, release address/payload and semaphore word observed.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdTraceTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Graphics.Gpu/Engine/GPFifo/Nx2QmdTrace.cs src/Ryujinx.Tests/Graphics/Nx2QmdTraceTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: trace QMD releases and semaphore words for confirmation"
```

**Gate:** Task 2 may start only if `CONFIRM=QMD_RELEASE_MISSING`. If `UPSTREAM_OF_QMD` or `NO_BLOCKER_FOUND`, stop and report to the human partner rather than writing GPU code against an unconfirmed blocker.

### Task 1c: Trace the macro → PCAS → QMD link

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs` (`SendSignalingPcasB`, :139-162)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/QmdDescriptor.cs` (counter only)
- Create: `src/Ryujinx.Graphics.Gpu/Engine/Compute/Nx2PcasTrace.cs`
- Test: `src/Ryujinx.Tests/Graphics/Nx2PcasTraceTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `MACRO_QMD=`)

**Interfaces:**
- Consumes: existing `NX2_MACRO_LOG=1` output (`Macro.cs:119` emits `NX2 mme exec pos=... dram27=...`; `MacroTuring.cs:740` emits `NX2 emit addr=... value=...`), `NX2_QMD_TRACE=1` from Task 1b, `QmdScheduler`/`QmdDescriptor`.
- Produces: `internal static class Nx2PcasTrace { public static void Enter(uint qmdAddress, bool usesMme64); public static string Summary { get; } }` — env-gated (`NX2_PCAS_TRACE=1`), capped at 200 lines, observe-only.

- [ ] **Step 1: Write the failing test**

In `Nx2PcasTraceTests.cs`, assert that three calls to `Nx2PcasTrace.Enter` with qmdAddress `0`, `0`, `0x1234` produce `Summary` containing `calls=3 zero=2 nonzero=1`, and that a call with `usesMme64: true` adds `mme64=1`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcasTraceTests" -v q`
Expected: FAIL — `Nx2PcasTrace` does not exist.

- [ ] **Step 3: Add the observe-only PCAS trace**

Add the counter/log at the *entry* of `SendSignalingPcasB`, before any early return or QMD read, so all invocations are counted — including `qmdAddress == 0` (Resume) and any path that currently returns without dispatch. Log `NX2PCAS qmd=0x{address:X8} action={Launch|Resume} mme64={UsesMme64}` capped at 200 lines. Add `pcas=` from `Nx2PcasTrace.Summary` to the existing `NX2 progress` line. Do not change dispatch behaviour.

- [ ] **Step 4: Re-run the macro-path capture**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_PCAS_TRACE=1 NX2_MACRO_LOG=1 NX2_QMD_TRACE=1 NX2_METHOD_LOG=1`. In `docs/nx2/CONTINUE_HERE.md`, record `MACRO_QMD=` as exactly one of:
  - `MACROS_RUN_NO_PCAS` — `mme` macros run but `pcas calls=0`: the macros never emit the PCAS/QMD-launch method.
  - `PCAS_ONLY_ZERO_QMD` — PCAS calls exist but all with `qmd=0` (Resume), no launch address.
  - `PCAS_LAUNCH_NO_QMD_TRACE` — a nonzero QMD launch address is written but no `NX2QMD` line follows (dispatch not reached).
Also record the top macro positions from `NX2 mme exec` and whether any `NX2 emit` targets the compute-class PCAS register.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcasTraceTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Tests/Graphics/Nx2PcasTraceTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: trace macro to PCAS to QMD dispatch link"
```

**Gate:** the `MACRO_QMD=` value selects the next task — `MACROS_RUN_NO_PCAS` means fix macro emission; `PCAS_ONLY_ZERO_QMD` means find where the guest programs the PCAS QMD address; `PCAS_LAUNCH_NO_QMD_TRACE` means fix the dispatch path. Do not begin any of them without a human decision on the recorded result.

### Task 1d: Disassemble the stalled guest loop

**Files:**
- Create: `tools/nx2/dump-guest-code.sh` (helper: NUP ExeFS → NSO section → raw binary at a guest VA)
- Create: `src/Ryujinx.Tests/HLE/Nx2GuestCodeTests.cs` (address→file-offset mapping test, no disassembly dependency)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `GUEST_LOOP=`)

**Interfaces:**
- Consumes: the NUP file `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup`; guest return addresses from the Task 1 stack capture (`0x863d118`, `0x863b54c`, `0x860b87c` in `cengine.nss`; module base `0x85CC000`); `aarch64-linux-gnu-objdump` (verified installed).
- Produces: `tools/nx2/dump-guest-code.sh <nup> <module> <guest-va> <length> <out.bin>` extracting the 16-byte-aligned code bytes at that VA; a written analysis in `CONTINUE_HERE.md` naming the function/loop at each return address.

- [ ] **Step 1: Write the failing address-mapping test**

In `Nx2GuestCodeTests.cs`, assert that for a module loaded at VA `0x85CC000` with an NSO text section at file offset `0x1000`, guest VA `0x863d118` maps to file offset `0x11118` (and that an address outside the section is rejected). No disassembly in the test — just the arithmetic the shell helper must use.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2GuestCodeTests" -v q`
Expected: FAIL — test file/type does not exist.

- [ ] **Step 3: Implement the extraction helper and address mapping**

Write `tools/nx2/dump-guest-code.sh` using the existing `NupArchive`/NSO layout knowledge (or, if the NUP parser cannot be reused from shell, a small `dotnet run` helper under `tools/nx2/`) to locate the module's text section and copy `length` bytes starting at the translated file offset. Verify with `sha256sum` that two runs produce identical output.

- [ ] **Step 4: Disassemble the three return addresses**

Run `aarch64-linux-gnu-objdump -D -b binary -m aarch64 <out.bin>` on each extracted slice. Identify the call sites that produced the Task 1 MainThread stack frames; read the surrounding control flow to name the engine loop (e.g. a resource/job wait, a file load, a service call). Record one `GUEST_LOOP=` line in `docs/nx2/CONTINUE_HERE.md` with the named function, its VA and the concrete condition it waits for.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2GuestCodeTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add tools/nx2 src/Ryujinx.Tests/HLE/Nx2GuestCodeTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: extract and disassemble the stalled guest loop"
```

**Gate:** record `GUEST_LOOP=` and stop for a human decision before acting on what it names — disassembly findings can be misattributed without symbols.

### Task 1e: Watch the guest completion flag

**Files:**
- Modify: `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/Nx2ThreadDump.cs` (targeted PC watch timer + register/memory dump)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `FLAG_WATCH=`)
- Test: `src/Ryujinx.Tests/HLE/Nx2PcWatchTests.cs` (new)

**Interfaces:**
- Consumes: `GUEST_LOOP=` from Task 1d (`cengine.nss:0xeb87c`, VA `0x860B87C`, call site of `blr [vtable+0x110]`, loop target `0x860B75C`); existing `FormatRegisterMemory`/`KThread.Context` accessors in `Nx2ThreadDump.cs`.
- Produces: `internal static class Nx2PcWatch { public static bool Enabled { get; } public static string Format(ulong pc, KThread thread); }` — env `NX2_PC_WATCH=0x860B87C,0x860B884` (comma-separated hex PCs), capped 200 lines; and a 10 ms timer that checks running app-thread PCs while enabled.

- [ ] **Step 1: Write the failing test**

In `Nx2PcWatchTests.cs`, assert that `NX2_PC_WATCH=0x860B87C` makes `Enabled` true, that a different PC does not match, and that `Format` for a synthetic thread context renders `regs=` plus `mem x0@0x...=` from a pointer register. Use reflection to set env state if needed; reset it in `[TearDown]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcWatchTests" -v q`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Add the targeted watch**

When `NX2_PC_WATCH` is set, start a 10 ms timer that iterates running threads of the active application and logs at most one line per thread per watch PC: `NX2PCW uid=... pc=0x... regs=[...] mem=[...]`. Reuse `FormatRegisterMemory` to dump 64 bytes at pointer-like registers (`x0` holds the object in the disassembled call). At the instruction after the `blr`, also log `w0`. Cap at 200 lines total. Do not change guest execution.

- [ ] **Step 4: Capture and interpret**

Run Fast Fusion 60 s with `NX2_PATCH="" NX2_PC_WATCH=0x860B87C,0x860B884 NX2_PC_SAMPLE=1 NX2_THREAD_DUMP=1`. In `docs/nx2/CONTINUE_HERE.md`, record `FLAG_WATCH=` with: the object pointer in `x0`, the vtable pointer `mem[x0]`, the 64-byte object dump, the value of `w0` on each hit, and whether any byte in the object changes across hits. Conclude one of: `FLAG_NEVER_SET` (no byte ever toggles to 1), `FLAG_SET_LATE` (it toggles but the game still stalls), or `FLAG_SET_BUT_IGNORED`.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcWatchTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/Nx2ThreadDump.cs src/Ryujinx.Tests/HLE/Nx2PcWatchTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: watch the guest completion flag at its poll site"
```

**Gate:** record `FLAG_WATCH=` and stop for a human decision before writing any fix based on it.

### Task 1f: Compare our NX2 graphics path to Eglantine's working title 4

**Files:**
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `EG_COMPARE=` with a ranked, evidence-backed divergence list)

**Interfaces:**
- Consumes: Eglantine reference at `/tmp/opencode/eglantine` (`NX2_NOTES.md` rows 45-57, 81-84, 115; source `src/video_core/dma_pusher.cpp`, `engines/maxwell_3d.cpp`, `engines/kepler_compute.cpp`, `macro.cpp`, `macro_turing.h`, `control/channel_state.cpp`); this repo's `ComputeClass.cs`, `Macro.cs`, `MacroTuring.cs`, `GPFifoClass.cs`/`GPFifoProcessor.cs`, `INx2GraphicsSystemSession.cs`, `INx2Vi2Session.cs`.
- Produces: a ranked list of **concrete divergences** (file:line here vs file:line there), each labelled `FIXABLE_HERE` or `NEEDS_REFERENCE_PORT`, with the first candidate fix named.

- [ ] **Step 1: Compare the four areas**

For each area, read both implementations and record what ours does differently:
  1. **Macro upload/binding** — how macro code is uploaded (method offsets `0x45`-`0x48` in `ComputeClass.cs`), where positions/entry points are stored, how a method in `[0xE00,0xE00+0x100)` triggers a macro, and whether the compute class uses the same macro context as 3D.
  2. **MME data RAM** — per-channel storage, word `0x27` (QMD ring base), and whether any code schedules QMDs from it.
  3. **GPFIFO acquire/resume and PB stability** — unmet-acquire suspension, split methods, and pushbuffer word consistency (`EDEN_NX2_PB_CHECK` equivalent).
  4. **Window/native-image init** — gsv 1510/111/429/423, `Nx2NativeObjects.Layout`, and the chain into `NativeWindow::Present` / image acquire.

- [ ] **Step 2: Write the ranked divergence list**

In `docs/nx2/CONTINUE_HERE.md`, record `EG_COMPARE=` followed by a numbered list. Each item: our file:line, the Eglantine file:line it diverges from, the behavioural consequence, and `FIXABLE_HERE` / `NEEDS_REFERENCE_PORT`. Rank by how directly each divergence could explain "no macro emits PCAS / no dispatch / no draws". Do not write fix code.

- [ ] **Step 3: Verify and commit**

Run: `grep -n "EG_COMPARE=" docs/nx2/CONTINUE_HERE.md`
Expected: the line and the ranked list are present.

```bash
git add docs/nx2/CONTINUE_HERE.md
git commit -m "docs: compare NX2 graphics path with Eglantine title 4"
```

**Gate:** stop for a human decision on which divergence to fix first.

### Task 1g: Cover all PCAS/QMD compute methods

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs:62-70` (callback table), `:139-278` (`SendSignalingPcasB`)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/Nx2PcasTrace.cs` (per-method counters)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/QmdDescriptor.cs` (per-method action)
- Test: `src/Ryujinx.Tests/Graphics/Nx2PcasCoverageTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `PCAS_COVERAGE=`)

**Interfaces:**
- Consumes: Eglantine `kepler_compute.cpp:355-365` (methods 0xAE/0xAF/0xB0 all read `native_registers[0xad] << 8` as the QMD address; dispatch is skipped for `0xAE`, for `0xAF` when `!(argument & 2)`, and for `0xB0` when `(argument & 15)` is 0 or 1); our `ComputeClass.cs:67` registers only `SendSignalingPcasB`, and `Nx2PcasTrace.cs` counts only its invocations.
- Produces: `Nx2PcasTrace.Enter(uint qmdAddress, bool usesMme64, int method, int argument)` with counters/format including `mAE=`, `mAF=`, `mB0=`, `act=`; existing `Summary` gains per-method counts. Each of 0xAE/0xAF/0xB0 writes is observed (counted + logged) regardless of whether it dispatches.

- [ ] **Step 1: Write the failing test**

In `Nx2PcasCoverageTests.cs`, assert: (a) `Enter` with method 0xAE, 0xAF and 0xB0 increments distinct counters rendered as `mAE=1 mAF=1 mB0=1`; (b) a dispatch-eligible argument (0xAF with `argument & 2` set, 0xB0 with low nibble 2) contributes to `act=`, while the skip cases do not; (c) the QMD address from `ComputeClassState` is read identically for all three methods.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcasCoverageTests" -v q`
Expected: FAIL — new signature/counters do not exist.

- [ ] **Step 3: Wire the three methods**

Register callbacks for the compute methods 0xAE, 0xAF and 0xB0 in `ComputeClass.cs` (using the same field/offset mechanism as the existing `SendSignalingPcasB`). All three call `Nx2PcasTrace.Enter` with the method id and argument before any early return. Keep the existing direct dispatch behaviour for the currently wired method unchanged; do not add QMD ring/release logic in this task. Gate any NX2-specific dispatch with the existing generation flag so Maxwell titles are unaffected.

- [ ] **Step 4: Re-run the PCAS capture**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_PCAS_TRACE=1 NX2_QMD_TRACE=1 NX2_METHOD_LOG=1`. In `docs/nx2/CONTINUE_HERE.md`, record `PCAS_COVERAGE=` with per-method counts, `act=` total, and one of: `AE_AF_B0_ACTIVE` (all three methods written), `ONLY_PREVIOUSLY_WIRED` (only the existing method appears), or `NONE` (no PCAS writes at all). Also record whether `NX2QMD` lines now appear and whether `draws` changed.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PcasCoverageTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Tests/Graphics/Nx2PcasCoverageTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: observe all PCAS QMD compute methods"
```

**Gate:** stop for a human decision on the recorded `PCAS_COVERAGE=` result before further QMD work.

### Task 1h: Verify NUP package completeness

**Files:**
- Create: `tools/nx2/Nx2DumpToc/Nx2DumpToc.csproj` + `Program.cs` (helper printing package/entry summary)
- Create: `src/Ryujinx.Tests/HLE/Nx2TocTests.cs` (new; validates package selection helpers with a synthetic TOC)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `NUP_COMPLETE=`)

**Interfaces:**
- Consumes: NUP at `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup`; `NupArchive.cs` (`ReadProgramTree`, `SelectProgramPackage`, `IsStandaloneUpdate`, `OpenExeFs`, `OpenRomFs`).
- Produces: a report of package type (base / standalone update / update with base), ExeFS and RomFS file counts and total bytes, key ExeFS module names, and whether a base package is present; plus `NUP_COMPLETE=YES|NO|UNKNOWN` in the doc.

- [ ] **Step 1: Write the failing test**

In `Nx2TocTests.cs`, build a synthetic NUP TOC in memory with one base package containing `main` and `romfs`, and assert a helper `DescribePackages(byte[] toc)` returns `packages=1; base=1; standalone=0` (or equivalent), and that a two-package TOC (base + update, update marked standalone) is reported distinctly. Test the decision logic only, not the real ROM bytes.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2TocTests" -v q`
Expected: FAIL — helper type does not exist.

- [ ] **Step 3: Implement the TOC helper**

Reuse `NupArchive`'s parsing logic (reflection or a small shared internal extractor) in `tools/nx2/Nx2DumpToc` to print, for the real NUP: package count and types, selected package, `IsStandaloneUpdate`, ExeFS file list with sizes, RomFS file count and total bytes, and whether any base package exists alongside the update. Do not change `NupArchive` behaviour.

- [ ] **Step 4: Run the helper and record the verdict**

Run the helper on `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup`. In `docs/nx2/CONTINUE_HERE.md`, record `NUP_COMPLETE=` with: package count, selected package type, ExeFS modules, RomFS file count/bytes, and one conclusion — `YES` (base + assets present), `NO` (update-only / missing base or romfs), or `UNKNOWN`. Cross-check against the run log's `Mounted NUP base package` line.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2TocTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add tools/nx2/Nx2DumpToc src/Ryujinx.Tests/HLE/Nx2TocTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: verify NUP package completeness"
```

**Gate:** stop for a human decision; if `NUP_COMPLETE=NO`, the next step is obtaining the base NUP rather than any emulator change.

### Task 1i: QMD ring scheduling from MME data-RAM word 0x27

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/QmdDescriptor.cs` (ring state)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs` (use ring base when dispatching QMDs)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/MME/MacroTuring.cs` (observe DWRITE to word 0x27)
- Create: `src/Ryujinx.Tests/Graphics/Nx2QmdRingTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `QMD_RING=`)

**Interfaces:**
- Consumes: Eglantine `native_qmd.h:59-160` (`NativeQmdScheduler::Signal`, PCAS actions 3/2 load, 7 decrement, 12 increment; dependencies from `qmd[4]>>14`; QMD version `0x30` at `qmd[18]&0xff`; release slots 24/28/52; process-global QMD table), `NX2_NOTES.md` row 84 (each channel writes its ring base to data RAM `0x27`, macro 0xda reads it); our `MacroTuring.cs:570-576` (DWRITE case 31) and `:139` (`DataRamWrites`).
- Produces: `internal class Nx2QmdRing { public void SetBase(ulong baseVa); public int DispatchCount { get; } public void Schedule(ulong qmdVa, int action); }` and a `QmdDescriptor.RingBase` field. Observe-only logging when `NX2_QMD_RING=1` for any DWRITE to word `0x27`.

- [ ] **Step 1: Write the failing tests**

In `Nx2QmdRingTests.cs`, assert: (a) `SetBase(0x123400)` then `Schedule` records a dispatch keyed by the QMD VA; (b) a second `Schedule` for the same VA with action `7` (decrement) updates the existing entry rather than adding a new one; (c) a descriptor built with `RingBase` renders `ring=0x123400` in its description.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdRingTests" -v q`
Expected: FAIL — `Nx2QmdRing` does not exist.

- [ ] **Step 3: Implement the ring scheduler and DWRITE observation**

Add `Nx2QmdRing` with a process-global table (per Eglantine row 84), feed `RingBase` from data RAM word `0x27` at QMD dispatch in `ComputeClass`, and log `NX2QMRING base=0x... qmd=0x... action=...` when `NX2_QMD_RING=1`. In `MacroTuring.cs` DWRITE case 31, when the destination is `0x27` and the env gate is on, log the written value (capped 200 lines). No change to dispatch correctness for Maxwell titles; NX2-only.

- [ ] **Step 4: Run and record the premise check FIRST**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_QMD_RING=1 NX2_PCAS_TRACE=1 NX2_QMD_TRACE=1 NX2_MACRO_LOG=1`. Before treating the port as useful, check `NX2QMRING base=` lines: prior captures show `dram27=0x0` on all 73 macros. Record `QMD_RING=` as one of: `BASE_WRITTEN` (guest writes a nonzero word 0x27), `BASE_NEVER_WRITTEN` (no DWRITE to 0x27 ever observed), or `INCONCLUSIVE`. If `BASE_NEVER_WRITTEN`, stop — the guest does not use this path in Fast Fusion and no further ring work is justified.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdRingTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Graphics.Gpu/Engine/MME/MacroTuring.cs src/Ryujinx.Tests/Graphics/Nx2QmdRingTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: add QMD ring scheduling from MME data RAM"
```

**Gate:** stop for a human decision on `QMD_RING=` before any further work.

### Task 1j: Distinguish 3D vs compute macros and their emitted registers

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/MME/MacroTuring.cs` (per-engine macro counters, emitted-register histogram per engine)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoProcessor.cs` (expose counters via `Nx2GpuStall`)
- Modify: `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs` (progress token `mme3d=`/`mmec=`)
- Create: `src/Ryujinx.Tests/Graphics/Nx2MacroEngineTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `MACRO_ENGINE=`)

**Interfaces:**
- Consumes: `Macro.StartExecution(..., bool compute)` (`Macro.cs`) distinguishes compute vs 3D macros; `MacroTuring.Execute` at `:163` increments `MacrosRun`; `Nx2GpuStall.MacrosRun`/`EmittedMethods` and `DescribeEmitted()` are the existing progress tokens.
- Produces: `MacroTuring.MacrosCompute`/`Macros3D` counters and `ComputeEmittedHistogram`; exposed as `mme3d=`/`mmec=` in `NX2 progress` plus a `NX2_MME_ENGINE=1` log of each compute macro's emitted register addresses.

- [ ] **Step 1: Write the failing test**

In `Nx2MacroEngineTests.cs`, assert that a synthetic execute recorded as `compute: true` increments `MacrosCompute` and not `Macros3D`, that `compute: false` does the reverse, and that `ComputeEmittedHistogram` records a register address passed to the emit path. Reset counters in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2MacroEngineTests" -v q`
Expected: FAIL — counters do not exist.

- [ ] **Step 3: Add per-engine counters and emit histogram**

In `Macro.Execute`/`MacroTuring.Execute`, use the existing `compute` flag to increment the matching counter; in the emit path, record compute macro register addresses into `ComputeEmittedHistogram` (capped storage). Expose through `Nx2GpuStall` and add `mme3d=`/`mmec=` to the progress line. Add `NX2_MME_ENGINE=1` gated one-line-per-compute-macro log of emitted addresses (capped 200 lines). Observe-only; no dispatch change.

- [ ] **Step 4: Run and record**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_MME_ENGINE=1 NX2_MACRO_LOG=1 NX2_METHOD_LOG=1`. Record `MACRO_ENGINE=` in `docs/nx2/CONTINUE_HERE.md` with: `mme3d=`/`mmec=` counts, the top compute-emitted registers, and one conclusion — `COMPUTE_MACROS_RUN` (mmec > 0), `ONLY_3D_MACROS` (mmec == 0), or `INCONCLUSIVE`. If `ONLY_3D_MACROS`, stop: the guest never invokes compute macros, so all QMD/PCAS paths are unreachable.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2MacroEngineTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/MME src/Ryujinx.Graphics.Gpu/Engine/GPFifo src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs src/Ryujinx.Tests/Graphics/Nx2MacroEngineTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: separate 3D and compute macro counters"
```

**Gate:** stop for a human decision on `MACRO_ENGINE=` before any fix based on it.

### Task 1k: Trace compute macro code upload

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs` (log 0x45-0x48 writes)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs` (log `KeepComputeMmeCode`/`AddComputeMmeCode`/`SetComputeMmeBindPointer`/`BindComputeMmeStart`)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/Nx2PcasTrace.cs` or new `Nx2MacroUploadTrace.cs` (shared env gate/cap)
- Create: `src/Ryujinx.Tests/Graphics/Nx2MacroUploadTraceTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `COMPUTE_UPLOAD=`)

**Interfaces:**
- Consumes: `ComputeClass.cs:100-112` (methods 0x45-0x48 delegate to `_fifoClass.KeepComputeMmeCode`/`AddComputeMmeCode`/`SetComputeMmeBindPointer`/`BindComputeMmeStart`); `GPFifoClass.cs:488-550` (compute code bank writes into `_computeMacroCode`); Eglantine `kepler_compute.cpp:344-354,165-172` and `maxwell_3d.cpp:858,1650,1842` (how macro code/positions are uploaded and bound).
- Produces: `NX2_MME_UPLOAD=1` gated, capped logging of each upload method call: offset/index, word count, first/last code words, and a running non-zero-word count for the compute code bank; plus a summary at first compute macro execution stating the bank's non-zero word count and hash.

- [ ] **Step 1: Write the failing test**

In `Nx2MacroUploadTraceTests.cs`, assert that recording a code word with value `0x1234` increments the non-zero count and changes the bank hash, while recording `0x0` leaves the non-zero count unchanged; and that the summary renders `nonzero=1` / `nonzero=0` correctly. Reset state in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2MacroUploadTraceTests" -v q`
Expected: FAIL — trace helper does not exist.

- [ ] **Step 3: Add the observe-only upload trace**

Gate with `NX2_MME_UPLOAD=1` (reuse the existing `Nx2QmdTrace`/`Nx2PcasTrace` env/cap pattern). Log each of the four upload methods with its argument; in `AddComputeMmeCode`, track a running non-zero-word count and hash over `_computeMacroCode` writes. At the first compute macro execution (where `mmec` increments), log a one-line summary `NX2MMEUP bank nonzero=<n> hash=<h> words=65536`. No upload/decode behaviour change.

- [ ] **Step 4: Run and record**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_MME_UPLOAD=1 NX2_MME_ENGINE=1 NX2_MACRO_LOG=1`. In `docs/nx2/CONTINUE_HERE.md`, record `COMPUTE_UPLOAD=` as one of: `GUEST_SENT_CODE` (non-zero code words were written to the bank but execution still saw zeros — misrouting), `NO_CODE_SENT` (no non-zero words ever written — guest did not upload compute macro code), or `INCONCLUSIVE`. Cite the per-method counts and the bank summary.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2MacroUploadTraceTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Graphics.Gpu/Engine/GPFifo src/Ryujinx.Tests/Graphics/Nx2MacroUploadTraceTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: trace compute macro code upload"
```

**Gate:** stop for a human decision on `COMPUTE_UPLOAD=` — `GUEST_SENT_CODE` means fix routing; `NO_CODE_SENT` means the guest never uploads compute macro code (look upstream in guest/cengine).

### Task 1l: Route compute macros to the shared macro code bank

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs:540,548,557` (compute execution reads shared bank)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/MME/Macro.cs:69` (memo key uses shared generation for compute)
- Create: `src/Ryujinx.Tests/Graphics/Nx2SharedMacroBankTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `MME_BANK_FIX=`)

**Interfaces:**
- Consumes: confirmed recon — every method 0x45-0x48 write lands in `GPFifoClass._macroCode`/`_macros` (`GPFifoProcessor.cs:745-748`, `GPFifoClassState.cs:226-229`), while compute execution reads the never-written `_computeMacroCode`/`_computeMacros` (`GPFifoClass.cs:540,548,557`); Eglantine points compute at the shared 3D bank by default (`channel_state.cpp:84-88`, `kepler_compute.h:81-89`, `kepler_compute.cpp:155-160`).
- Produces: compute macro execution using the same `_macros`/`_macroCode` bank as 3D, and the memo key for compute macros keyed on `processor.MmeCodeGeneration` (the bank that actually changes). This is a real behaviour change (not observe-only) — the first root-cause fix.

- [ ] **Step 1: Write the failing regression test**

In `Nx2SharedMacroBankTests.cs`, add a test that drives the compute execution bank-selection path and asserts it resolves to the SAME code bank reference and macro-slot array as 3D execution (e.g. an internal `ResolveComputeExecutionBank()` seam returning `(Macro[] slots, int[] code)` compared by reference to the 3D bank), plus a test that a compute macro's memo generation key equals `MmeCodeGeneration`, not `ComputeMmeCodeGeneration`. Reset any counters in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SharedMacroBankTests" -v q`
Expected: FAIL — compute bank resolves to a different reference / wrong generation key.

- [ ] **Step 3: Implement the fix**

In `GPFifoClass.cs`, make `ComputeMmePushArgument`/`ComputeMmeStart`/`ComputeCallMme` operate on `_macros[index]` and `_macroCode` instead of `_computeMacros`/`_computeMacroCode`. In `Macro.cs:69`, use `processor.MmeCodeGeneration` for compute macros too. Keep the now-unused `_computeMacros`/`_computeMacroCode` and their upload delegates harmlessly in place (no deletion — minimal diff). NX2/Ampere-only path; confirm Maxwell 3D macros still use `_macros`/`_macroCode` via `MmeStart`/`CallMme`.

- [ ] **Step 4: Verify with the existing diagnostics**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_MME_ENGINE=1 NX2_MME_UPLOAD=1 NX2_MACRO_LOG=1 NX2_METHOD_LOG=1 NX2_PCAS_TRACE=1`. In `docs/nx2/CONTINUE_HERE.md`, record `MME_BANK_FIX=` with: `mme3d=`/`mmec=` counts, the compute macros' `cen...`/`emits` (must now be non-zero if the fix worked), whether `NX2PCAS`/`pcas=[mAE/mAF/mB0]` still zero, and `draws` from `NX2 progress`. Expected success signal: compute macros now emit registers (`cemitted` non-empty); stronger: PCAS methods appear and `draws > 0`. If compute code is still all-zero, record the actual result honestly and stop.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SharedMacroBankTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs src/Ryujinx.Graphics.Gpu/Engine/MME/Macro.cs src/Ryujinx.Tests/Graphics/Nx2SharedMacroBankTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: route compute macros to the shared MME code bank"
```

**Gate:** stop for a human decision on `MME_BANK_FIX=` before further work.

### Task 2: Fire QMD release payloads and set `UsesMme64`

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs:134-267` (`SendSignalingPcasB`, `UsesMme64` at :43)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/Compute/QmdDescriptor.cs:6-59`
- Create: `src/Ryujinx.Graphics.Gpu/Engine/Compute/Nx2QmdRelease.cs`
- Test: `src/Ryujinx.Tests/Graphics/Nx2QmdReleaseTests.cs`
- Guard test: `src/Ryujinx.Tests/HLE/Nv/Nx2CharacteristicsTailTests.cs` (new)

**Interfaces:**
- Consumes: `ComputeQmd` bitfield at `ComputeQmd.cs:126-249` (release addresses/payloads and enable bits at 202/203); `QmdScheduler` in `QmdDescriptor.cs`.
- Produces: `internal static class Nx2QmdRelease { public static void Fire(ComputeQmd qmd, Action<ulong, uint> write32); }` called from `SendSignalingPcasB` immediately after `DispatchCompute`.

- [ ] **Step 1: Write the failing release tests**

Tests in `Nx2QmdReleaseTests.cs`: (a) with release 0 enabled at address `0x1234` payload `7`, `Fire` calls `write32` exactly once with `(0x1234, 7)`; (b) with bit 19 set and existing value `9`, it writes `max(9, 7) = 9`; (c) slots 24, 28 and 52 all fire when their enables are set; (d) a rejected QMD still fires its releases. Use a `ComputeQmd` fixture constructed from a 256-byte `byte[]` matching the field layout in `ComputeQmd.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdReleaseTests" -v q`
Expected: FAIL — `Nx2QmdRelease` does not exist.

- [ ] **Step 3: Implement `Nx2QmdRelease.Fire` and wire it into `SendSignalingPcasB`**

Read enabled releases from `ComputeQmd`, including the third slot at descriptor word 52; write 4 bytes per release; apply bit-19 as `max`. Set `UsesMme64` from the NX2 compute class/version at dispatch time, not globally. Extend `QmdScheduler` to track the per-process ring keyed by data-RAM word `0x27` (the RAM itself is already per channel in `GPFifoClass`).

- [ ] **Step 4: Add the characteristics guard test**

In `Nx2CharacteristicsTailTests.cs`, assert that the NX2 tail writer emits `0x40` at `0x70..0x80` (s16 each) and `64` at `0xb4` for the 0xD0 block. This pins the existing behaviour in `NvHostCtrlGpuDeviceFile.GetCharacteristicsVariableSize` against accidental removal.

- [ ] **Step 5: Run tests, build and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2QmdReleaseTests|FullyQualifiedName~Nx2CharacteristicsTailTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all tests PASS, 0 build errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/Compute src/Ryujinx.Tests/Graphics/Nx2QmdReleaseTests.cs src/Ryujinx.Tests/HLE/Nv/Nx2CharacteristicsTailTests.cs
git commit -m "nx2: fire QMD releases and enable MME64 on NX2 compute"
```

- [ ] **Step 6: Runtime check**

Run Fast Fusion 90 s with `NX2_PATCH="" NX2_METHOD_LOG=1 NX2_SYNCPT_TRACE=1`. Expected: no new 4 s fence stalls, `compute=[...]` histogram grows if the guest submits compute. If `draws` stays 0 and no compute appears, return to Task 1 — do not continue to Task 3 without new evidence.

### Task 3: Park split methods by resume address

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoProcessor.cs:233,362-415,455-588`
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/Nx2BulkCounters.cs:38-44`
- Create: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/Nx2SplitMethodTable.cs`
- Test: `src/Ryujinx.Tests/Graphics/Nx2SplitMethodTests.cs` (new)

**Interfaces:**
- Consumes: `DmaState` (private state in `GPFifoProcessor.cs`), GPFIFO entry address/size from `ProcessWithStallHandling`.
- Produces: `internal sealed class Nx2SplitMethodTable { public void Park(ulong resumeVa, DmaState state); public bool TryTake(ulong address, out DmaState state); public void Clear(); public int Count { get; } }` with a 16-entry cap and oldest-first eviction.

- [ ] **Step 1: Write the failing table tests**

Assertions: (a) `Park(0x1000, s)` then `TryTake(0x1000, out t)` returns the same state; (b) a `TryTake` at a different address returns false; (c) parking a 17th entry evicts the oldest and `Count == 16`; (d) `Clear()` empties the table.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SplitMethodTests" -v q`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the table and wire it into `GPFifoProcessor`**

At the start of a new entry, `TryTake(header.addr, out state)`; when an entry ends with an unfinished method and the next entry is not at `split_method_end`, `Park(splitMethodEnd, state)`. Clear every parked entry at the end of a submission, and call `Nx2BulkCounters.PbEnd` there too. Add `stalled`/`resumed` counters to the `fifo=[...]` diagnostic that already prints `spwait`/`spincr`.

- [ ] **Step 4: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SplitMethodTests|FullyQualifiedName~Nx2FifoTraceTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/GPFifo src/Ryujinx.Tests/Graphics/Nx2SplitMethodTests.cs
git commit -m "nx2: park split GPFIFO methods by resume address"
```

### Task 4: Generation-flagged shader decode and IR rewrites

**Files:**
- Modify: `src/Ryujinx.Graphics.Shader/Decoders/Decoder.cs:48`
- Modify: `src/Ryujinx.Graphics.Shader/Translation/Translator.cs:24-49`
- Modify: `src/Ryujinx.Graphics.Gpu/Shader/ShaderCache.cs:691-718`
- Create: `src/Ryujinx.Graphics.Shader/Decoders/AmpereOpcodes.cs`
- Create: `src/Ryujinx.Graphics.Shader/Translation/AmpereLowering.cs`
- Create: `src/Ryujinx.Graphics.Gpu/Shader/Nx2ShaderRejectCache.cs`
- Test: `src/Ryujinx.Tests/Graphics/Nx2AmpereLoweringTests.cs` (new)

**Interfaces:**
- Consumes: `TranslationOptions` (`Translator.cs`), `DecodedProgram`/`Context` from `Decoder.cs`, `IsAmpere` from `GpuChannel.cs:67`/`ComputeClass.cs:202,248`.
- Produces: `Decoder.Decode(definitions, gpuAccessor, startAddress, bool isAmpere)`; `AmpereLowering.Rewrite(DecodedProgram program)` returning the same type; `Nx2ShaderRejectCache.TryAdd(ulong address, string reason)` / `Contains(ulong address)`.

- [ ] **Step 1: Write failing rewrite and regression tests**

In `Nx2AmpereLoweringTests.cs`: (a) an SM86 power-of-two IMAD fixture rewrites to the IR SHL form; (b) an R2UR fixture preserves the predicate save/restore; (c) a Maxwell fixture passed with `isAmpere: false` is returned byte-for-byte unchanged; (d) `Nx2ShaderRejectCache.TryAdd` returns true once and false on a second call for the same address.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2AmpereLoweringTests" -v q`
Expected: FAIL — new types/member missing.

- [ ] **Step 3: Add the generation flag without forking the pipeline**

Add `bool IsAmpere` to `TranslationOptions`, set from `ShaderCache.CreateTranslationOptions`, pass through `Translator.DecodeShader` to `Decoder.Decode`. Keep one decoder implementation; put the SM86 opcode table in `AmpereOpcodes.cs` and the IR→IR rules in `AmpereLowering.cs`. No second translator, SPIR-V backend or shader cache.

- [ ] **Step 4: Add the negative cache, dump switch and bring-up fallback**

`Nx2ShaderRejectCache` keys by shader address and logs a rejection once. `NX2_SHADER_DUMP=1` writes raw binaries to `.nx2-logs/`. The trivial constant/blit fallback must be gated by an explicit env var and logged whenever used — never a silent default.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2AmpereLoweringTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS (including 3 swizzle tests), 0 errors.

```bash
git add src/Ryujinx.Graphics.Shader src/Ryujinx.Graphics.Gpu/Shader src/Ryujinx.Tests/Graphics/Nx2AmpereLoweringTests.cs
git commit -m "nx2: lower SM86 shaders through the shared decoder pipeline"
```

### Task 5: Prove a non-black frame and hand off

**Files:**
- Modify: `docs/nx2/CONTINUE_HERE.md`
- Scratch outputs only: `.nx2-logs/frame-*.rgba`, screenshots

**Interfaces:**
- Consumes: all previous tasks; `NX2_FRAME_DUMP=1`, `NX2_FRAME_DUMP_TEST=1`, `NX2_DRAW_DUMP=1`.
- Produces: a documented, reproducible invocation plus screenshot evidence.

- [ ] **Step 1: Run the final verification**

Run Fast Fusion 150 s with `NX2_PATCH="" NX2_FRAME_DUMP=1 NX2_FRAME_DUMP_TEST=1 NX2_DRAW_DUMP=200 NX2_METHOD_LOG=1`. Expected: `NX2 progress` shows `draws > 0`; `NX2 composite stats` shows `nonzero > 0` with at least two different hashes; `NX2_FRAME_DUMP_TEST` prints `PASS=True`.

- [ ] **Step 2: Convert and inspect a frame**

Run: `convert -size 3840x2160 -depth 8 rgba:.nx2-logs/frame-early-1-c1-img1-3840x2160.rgba .nx2-logs/ff-final.png`
Then: `xdotool search --name Ryujinx` → use the id exactly as printed (decimal, or hex **with `0x`**), and `import -window <id> .nx2-logs/ff-screen.png`. Expected: visibly non-black game content in at least one image.

- [ ] **Step 3: Update the continuation guide and commit**

Write the exact invocation, the PASS numbers (`submits`, `draws`, `nonzero`, hashes) and the screenshot path into `docs/nx2/CONTINUE_HERE.md`. If the frame is still black, record the failure and return to Task 1 with the new evidence — do not claim completion.

```bash
git add docs/nx2/CONTINUE_HERE.md
git commit -m "docs: record Fast Fusion rendering verification"
git push origin kenji-nx2-oot-bringup
```

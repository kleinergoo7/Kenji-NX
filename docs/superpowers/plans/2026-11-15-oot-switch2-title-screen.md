# OoT (Switch 2, .nup) Title Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Get past the startup movie sequence so the title renders in Kenji-NX (draws > 0, movie threads exit).

**Architecture:** Empirical bring-up of a closed guest binary. Keep all fixes behind env vars or startup patches, prove each step with one logged run, and change one hypothesis at a time.

**Tech Stack:** C# / .NET 10, Ryujinx-HLE, ARMeilleure JIT + PPTC disk cache, AArch64 guest (movie module `subsdk0` @ `0xD3C0000`, nnSdk @ `0xD4A0000`).

**Spec:** none — this is debugging, not a feature spec. Rulings here are provisional.

## Global Constraints

- Work only in `/home/reng/projects/11-09-switch2/Kenji-NX`, branch `codex/nx2-submission-progress`, HEAD `18233d27b`. Do not commit.
- Build: `dotnet build src/Ryujinx/Ryujinx.csproj -c Debug` (must print `BUILD=0`).
- Run dirs and logs only under `.nx2-logs/`; every probe is env-gated and capped.
- Keep these validated envs: `NX2_MOVIE_STREAM_X26=1 NX2_MOVIE_TRACE=1 NX2_THREAD_DUMP=1 NX2_PATCH="0xD3CF86C:0x2A1F03E0,0xD3CF8A4:0x2A1F03E0,0xD3CF8DC:0x2A1F03E0"`.
- Runtime: `--dram-size MemoryConfiguration12GiB --memory-manager-mode SoftwarePageTable --ignore-missing-services`; unset `NX2_ARB_LOG NX2_THREAD_DUMP_AT_PRESENT`.
- Success is measured only from the log: `draws=` from `NX2 progress`, `minting decoder session`, `reporting end-of-stream`, `Guest stack trace`.

## Review Focus

1. A second/third decoder session must receive its own streams, not session 1's ids (`_streams` is per instance now).
2. PPTC can serve stale translations: any guest patch must be applied before `process.Start` (`ApplyStartupPatches`) and show `Ptc LoadTranslations: Invalidated translated function` for the patched function.
3. A poll reply naming the wrong stream id makes `GetEvent` retry forever — check the E5 rate in `moviesend tid=` lines (healthy is < 5/s per thread, broken is ~60/s).
4. Returning success from patched `ReadBuffer` leaves the frame buffer unfilled — the movie advances on stale bytes; never assume pixel data exists.
5. Movie completion must be distinguished from an app stall: `MovieOperateThread` must send the close command (type `0x16`/`0x18`) and then no new session is an app-side wait, not a decoder bug.

---

### Task 1: Prove the startup movie sequence completes

**Files:**
- Read: `.nx2-logs/ocarina-play-*/ocarina.log`
- Modify (only if evidence fails): `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2VideoDecSession.cs`

**Interfaces:**
- Consumes: per-instance `_streams` (already changed), `NX2_PATCH` startup patches (`ProcessLoaderHelper.ApplyStartupPatches`).
- Produces: the evidence list below, quoted in the ledger.

- [ ] **Step 1: Run the natural-playback command** (`.nx2-logs/run-play.sh`, 480 s timeout, no `NX2_MOVIE_EOS_AFTER`).

- [ ] **Step 2: Verify each decoder session opens, plays, and closes**

Run: `grep -a "minting decoder session" "$D/ocarina.log"` and count `moviesend tid=.*w0=0x0000001[68]`.
Expected: session count increases past 2 without any session's streams re-registering session 1's ids, and each session ends with a `0x16`/`0x18` send.

- [ ] **Step 3: Check for a decoder stall**

Run: `grep -ac "moviesend tid=.*moviesend" "$D/ocarina.log"` over the last 10 s of the log.
Expected: < 50 E5 sends per 10 s for any movie thread; ~600 means the endless-retry bug is back.

- [ ] **Step 4: Record the result in the plan** (one line under this task: sessions seen, close commands seen, E5 rate).

### Task 2: Diagnose the missing GPU submissions

**Files:**
- Read: `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs` (progress line), `src/Ryujinx.HLE/HOS/Services/Nv/INvDrvServices.cs` (ioctl counts)
- Modify (probe only): whichever file owns the suspected wait, env-gated.

**Interfaces:**
- Consumes: `Nx2Counters.Submits`, `Nx2GpuStall.Draws`, `INx2GpuSession:211/101` call counts, `NX2_THREAD` dump (`uid`, `tname`, `waitingSync`, `waits`).
- Produces: the exact thread + wait object that blocks the render path, printed in the log.

- [ ] **Step 1: Capture the t=150 s thread dump of the play run** (`grep -a "NX2_THREAD t=150s pid=109"`).

- [ ] **Step 2: Identify the app threads alive after the last movie closes** (expected movie threads are gone; list `tname=` values and any `waits=` that is unsignaled).

- [ ] **Step 3: Verify GPU call counts are frozen**

Run: `grep -a "NX2 calls" "$D/ocarina.log" | tail -3`.
Expected: `INx2GpuSession:211` and `:101` unchanged across the last three progress lines while `IHidServer:214` grows.

- [ ] **Step 4: Write one line naming the blocking thread/wait** (or `no blocked thread: render loop idle`).

### Task 3: Fix the identified stall, then verify title render

**Files:**
- Modify: the file named by Task 2 Step 4 (HLE handler or kernel wait path only).
- Read: `.nx2-logs/ocarina-fix-*/ocarina.log`

**Interfaces:**
- Consumes: Task 2 verdict.
- Produces: one env-gated fix, one validated run.

- [ ] **Step 1: Implement the smallest fix** for the Task 2 verdict (signal a missing event, or answer the missed service command); gate it behind an env var defaulting to the validated behavior.

- [ ] **Step 2: Build** — `dotnet build src/Ryujinx/Ryujinx.csproj -c Debug`; expected `BUILD=0`.

- [ ] **Step 3: Run with the fix** (same command as Task 1).

- [ ] **Step 4: Verify the goal from the log**

Run: `grep -a "NX2 progress" "$D/ocarina.log" | tail -1`.
Expected: `draws=` > 0 (first time in this branch), `presents` increasing, no `SetTerminateResult`, no `GuestBroke`.

- [ ] **Step 5: Ask the human what is on screen** (black / logo / menu / title) — the log cannot prove pixels.

- [ ] **Step 6: Commit nothing.** Report the run dir and the one-line log evidence.

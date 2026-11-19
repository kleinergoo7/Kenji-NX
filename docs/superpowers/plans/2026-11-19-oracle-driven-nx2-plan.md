# Oracle-Driven NX2 Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make our fork render Fast Fusion's title screen (non-black frame), verified side-by-side against a working Lichii oracle.

**Architecture:** Fix the three measured divergences from a running reference — compute macro code delivery, Ampere SM86 lowering (re-introduced from the archive branch), and present fence/pacing — with an A/B harness as the verifier. No new guesses from stale notes.

**Tech Stack:** C#/.NET 11 (our fork), C++ Lichii oracle at `/tmp/opencode/lichii/build/bin/eden-cli`, xUnit tests, ImageMagick for frame inspection.

**Spec:** `docs/superpowers/specs/2026-11-19-oracle-driven-nx2-design.md`

## Global Constraints

- Author every commit `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to `origin` = github.com/kleinergoo7/Kenji-NX. Never push to `kenji-nx` (Forgejo).
- Keep exactly two local branches: `kenji-nx2-oot-bringup` and `archive/nx2-oot-pre-kenji-rebase`. Never modify the archive branch (read-only source of the Ampere files).
- Build: `dotnet build Ryujinx.slnx -c Debug` (SDK 11.0.100). Smoke: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SwizzleTests"` → 3 passed.
- Fast Fusion: `NX2_PATCH=""` + `--dram-size MemoryConfiguration12GiB --memory-manager-mode SoftwarePageTable --ignore-missing-services`.
- ROM `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` (not in repo). Logs/frames in git-ignored `.nx2-logs/` and `/tmp/opencode/`. Never commit ROM/logs/frames.
- Agent model: only `opencode-go/hy4-flash-free`.

## Review Focus

1. **Sharing `Macro[]` slot state again** would reintroduce the 14→12 collision — pinned in Task 1 by asserting compute execution uses `_computeMacros[index]` (state) with `_macroCode` (shared code), never `_macros[index]`.
2. **A present wait that deadlocks the HLE thread** — pinned in Task 3 by keeping the 4 s cap, `id >= 192` and `value == 0` skips, and no infinite wait.
3. **Stale Ampere code that does not compile on HEAD** — pinned in Task 2 by building immediately after re-introduction and running `AmpereLoweringSelfTest`.
4. **A false PASS from the harness** (e.g. measuring the splash) — pinned in Task 0 by requiring both mean brightness > 0.1 AND shader-lowered count > 0.
5. **Losing Maxwell compatibility** — pinned in Tasks 1/2 by keeping all changes behind `IsAmpere`/`UsesMme64`.

---

### Task 0: Build the A/B oracle harness

**Files:**
- Create: `tools/nx2/oracle-ab.sh`
- Create: `docs/nx2/ORACLE.md`
- Test: none (shell harness; verified by running it)

**Interfaces:**
- Consumes: Lichii oracle `/tmp/opencode/lichii/build/bin/eden-cli` (built, confirmed working); our `tools/nx2/run-oot.sh`; ImageMagick.
- Produces: `tools/nx2/oracle-ab.sh <seconds>` that runs both emulators on the same NUP, converts captured frames to PNG, and prints a table with mean brightness and (for Lichii) shader/pipeline counts.

- [ ] **Step 1: Write the harness**

`oracle-ab.sh <seconds>` (default 60):
  - Oracle: `DISPLAY=:0.0 XDG_*=/tmp/opencode/oracle-run/* EDEN_NX2_AUTO_STUB=1 EDEN_NX2_FRAME_CAPTURE=<dir> eden-cli -g <nup>` under `timeout -s INT $seconds`.
  - Ours: `NX2_PATCH="" NX2_FRAME_DUMP=1 tools/nx2/run-oot.sh <nup> <seconds>` (or direct dotnet invocation).
  - For each side, convert the captured frame with `convert -size 1280x720 file.tga out.png` (oracle) / `-size 3840x2160 -depth 8 rgba:file.rgba out.png` (ours), then print `mean` via `identify -format '%[fx:mean]'`.
  - Extract oracle shader/pipeline totals from its log (`Shader cache v2 programs:`, `Runtime pipelines this session:`).

- [ ] **Step 2: Run it to establish the baseline**

Run: `bash tools/nx2/oracle-ab.sh 60`
Expected: a table showing oracle mean ≈ 0.82 with shaders/pipelines > 0, ours mean ≈ 0.0 with 0 shaders.

- [ ] **Step 3: Document and commit**

Write `docs/nx2/ORACLE.md` with the oracle build path, exact run command, known caveats (Xvfb has no present queue; use `DISPLAY=:0.0`; the trace flags), and the baseline table. Commit as kleinergoo7, do not push.

```bash
git add tools/nx2/oracle-ab.sh docs/nx2/ORACLE.md
git commit -m "nx2: add oracle A/B comparison harness"
```

### Task 1: Deliver compute macro code from the shared bank

**Files:**
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs:540,548,557` (compute execution reads shared code, keeps own state)
- Modify: `src/Ryujinx.Graphics.Gpu/Engine/MME/Macro.cs:69` (memo key uses `MmeCodeGeneration` for compute)
- Create: `src/Ryujinx.Tests/Graphics/Nx2ComputeCodeBankTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `CODE_BANK_FIX=`)

**Interfaces:**
- Consumes: confirmed recon — all `0x45`–`0x48` writes land in `GPFifoClass._macroCode` (`GPFifoProcessor.cs:745-748`); compute execution reads empty `_computeMacroCode`; Lichii shares the code bank but keeps separate macro execution state.
- Produces: compute execution reads `_macroCode` (shared code) using `_computeMacros[index]` (separate state); memo keyed on `MmeCodeGeneration` for compute.

- [ ] **Step 1: Write the failing regression test**

In `Nx2ComputeCodeBankTests.cs`, assert: (a) compute execution resolves code bank by reference to the same array as 3D (`_macroCode`); (b) compute execution resolves its `Macro` slot to a DIFFERENT array from 3D (`_computeMacros` vs `_macros`) — this pins the collision fix; (c) the memo generation key for compute equals the shared code generation. Reset in `[SetUp]`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2ComputeCodeBankTests" -v q`
Expected: FAIL — seams do not exist / banks are different.

- [ ] **Step 3: Implement the split**

In `GPFifoClass.cs`: `ComputeMmePushArgument`/`ComputeMmeStart`/`ComputeCallMme` use `_computeMacros[index]` for state but pass `_macroCode` as the code array. In `Macro.cs:69`, use `processor.MmeCodeGeneration` for compute macros too. Leave `_computeMacroCode` and its upload delegates in place harmlessly. NX2/Ampere-only.

- [ ] **Step 4: Verify with the oracle harness**

Run: `bash tools/nx2/oracle-ab.sh 60`. Record `CODE_BANK_FIX=` in `CONTINUE_HERE.md` with: compute macro count, whether `cemitted` is now non-empty, and whether the oracle/ours brightness gap narrowed. Expected success signal: compute macros emit registers. If still zero, record honestly and stop for a human decision — do NOT try a second bank variant.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2ComputeCodeBankTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs src/Ryujinx.Graphics.Gpu/Engine/MME/Macro.cs src/Ryujinx.Tests/Graphics/Nx2ComputeCodeBankTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: deliver compute macro code from shared bank"
```

### Task 2: Re-introduce Ampere SM86 lowering

**Files:**
- Create (from archive branch, unmodified first): `src/Ryujinx.Graphics.Shader/Decoders/Ampere/*.cs` (9 files)
- Modify: `src/Ryujinx.Graphics.Gpu/Shader/ShaderCache.cs:697-720` (one integration point, mirror archive)
- Test: `Ryujinx.ShaderTools --ampere-self-test` (existing self-test), plus `Nx2AmpereLoweringTests.cs`

**Interfaces:**
- Consumes: the 9 files at `archive/nx2-oot-pre-kenji-rebase:src/Ryujinx.Graphics.Shader/Decoders/Ampere/`, self-test `AmpereLoweringSelfTest.Run()`, integration at archive `ShaderCache.cs:703`.
- Produces: `AmpereGpuAccessor.Create(accessor, gpuVa, hasHeader, out error)` called from our shader decode when `IsAmpere`; SM86 instructions lowered to Maxwell IR before the existing translator.

- [ ] **Step 1: Re-introduce the files verbatim**

Run: `git checkout archive/nx2-oot-pre-kenji-rebase -- src/Ryujinx.Graphics.Shader/Decoders/Ampere/`
Expected: 9 `.cs` files restored to working tree.

- [ ] **Step 2: Add the failing integration test**

In `Nx2AmpereLoweringTests.cs`, assert a synthetic SM86 instruction sequence lowers successfully via `AmpereLowering.LowerToMaxwell` and that the self-test `Run()` does not throw. Reset any state in `[SetUp]`.

- [ ] **Step 3: Wire the integration point and run the self-test**

Mirror archive `ShaderCache.cs:703` in ours at the equivalent decode path, gated by `IsAmpere`. Run: `dotnet run --project src/Ryujinx.ShaderTools -- --ampere-self-test` (or the project's test runner) and `dotnet build Ryujinx.slnx -c Debug -v q`. Expected: self-test passes, build 0 errors.

- [ ] **Step 4: Verify with the oracle harness**

Run: `bash tools/nx2/oracle-ab.sh 60`. Record `AMPERE_LOWER=` in `CONTINUE_HERE.md` with shader/pipeline counts and brightness. Expected: our shaders-lowered count becomes > 0. If still zero, the shader bind still is not reaching the cache — record honestly and stop.

- [ ] **Step 5: Commit**

```bash
git add src/Ryujinx.Graphics.Shader/Decoders/Ampere src/Ryujinx.Graphics.Gpu/Shader/ShaderCache.cs src/Ryujinx.Tests/Graphics/Nx2AmpereLoweringTests.cs
git commit -m "nx2: re-introduce Ampere SM86 lowering"
```

### Task 3: Match Lichii's present fence wait and pacing

**Files:**
- Modify: `src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs` (fence parse/wait, pacing, composite)
- Create: `src/Ryujinx.Tests/HLE/Nx2PresentFenceTests.cs` (new)
- Modify: `docs/nx2/CONTINUE_HERE.md` (record `FENCE_FIX=`)

**Interfaces:**
- Consumes: Lichii `nx2_gsv.h:2123-2154,2507-2543,2631-2713,2720-2855`; ours `PresentInternal` at `:702-728`, `PaceOcarinaPresent` at `:464-489`.
- Produces: timing read from raw w[12] (RequestData 10); wait on the **current** fence `(id<192, value!=0)` with a 4 s cap; pace all titles using the corrected interval and a steady-clock deadline advance; pass the fence to `Composite`.

- [ ] **Step 1: Write the failing test**

In `Nx2PresentFenceTests.cs`, assert: (a) a synthetic request with raw words 22/24 holding `~1`/`3` yields fence `(id=1, value=3)`; (b) `id >= 192` and `value == 0` are both classified as "no wait"; (c) timing word `0x1003c` yields interval 16.67 ms at 60 Hz from raw w[12] (not w[10]).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PresentFenceTests" -v q`
Expected: FAIL — helpers do not exist / timing parsed from w[10].

- [ ] **Step 3: Implement the fence/pacing fix**

Read the 24 request words once into `uint[] w` from position 0 (Lichii-equivalent indexing) instead of relative seeks; derive token/slot/image/timing/fence from `w[2],w[4],w[5],w[10],w[20],w[22]`. Wait on the current fence when `id < 192 && value != 0`, keeping the 4 s cap and timeout log; reserve the previous-fence policy to OoT. Replace `PaceOcarinaPresent` with a pacer called for all titles unless `VSyncMode.Unbounded`, using `lastDeadline + interval` with re-anchoring. Pass the fence to `Composite`. Never use an infinite wait.

- [ ] **Step 4: Verify with the oracle harness**

Run: `bash tools/nx2/oracle-ab.sh 60`. Record `FENCE_FIX=` in `CONTINUE_HERE.md` with observed fence id/values, present rate, and brightness. Expected: our present rate approaches 60 Hz and fence values become `(1,N)`.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2PresentFenceTests|FullyQualifiedName~Nx2SwizzleTests" -v q && dotnet build Ryujinx.slnx -c Debug -v q`
Expected: all PASS, 0 errors.

```bash
git add src/Ryujinx.HLE/HOS/Services/Am/AppletAE/AllSystemAppletProxiesService/SystemAppletProxy/INx2Vi2Session.cs src/Ryujinx.Tests/HLE/Nx2PresentFenceTests.cs docs/nx2/CONTINUE_HERE.md
git commit -m "nx2: match Lichii present fence wait and pacing"
```

### Task 4: Final verification and handoff

**Files:**
- Modify: `docs/nx2/CONTINUE_HERE.md` (final status)
- Modify: `docs/nx2/ORACLE.md` (final A/B table)

**Interfaces:** consumes all prior tasks; `tools/nx2/oracle-ab.sh`.

- [ ] **Step 1: Run the final A/B**

Run: `bash tools/nx2/oracle-ab.sh 90`. Expected: our mean brightness > 0.1 and shaders lowered > 0; capture a PNG of our frame for the doc.

- [ ] **Step 2: Update docs and push**

Write the final table into `docs/nx2/ORACLE.md` and the status into `CONTINUE_HERE.md`. If brightness is still ~0, record the honest result and stop — do not claim success. Commit and push to `origin`.

```bash
git add docs/nx2/ORACLE.md docs/nx2/CONTINUE_HERE.md
git commit -m "docs: record oracle-verified NX2 rendering result"
git push origin kenji-nx2-oot-bringup
```

# Oracle-driven NX2 rendering design

Date: 2026-11-19
Supersedes: `2026-11-19-nx2-rendering-design.md` and `2026-11-19-nx2-ground-truth-design.md`.

## Why this design exists

A working reference now exists locally: Lichii (`/tmp/opencode/lichii`, current `nx2` branch),
built to `/tmp/opencode/lichii/build/bin/eden-cli`. Running the **same** Fast Fusion NUP under it
produces a correct title screen (captured frames at `/tmp/opencode/oracle-run/frames/*.tga`,
mean brightness 0.82) while our fork renders pure black (0.0). This replaces inference with
observation.

The old plan was built on a stale historical blocker table and on a false metric:
**both emulators log zero `DrawArrays` lines**, because NX2 renders via MME64 macros and QMD
dispatches. Chasing "zero draws" was chasing a measurement artifact.

## Oracle comparison (measured)

| Signal | Lichii (works) | Ours (black) |
|---|---|---|
| Frame brightness | 0.82 (logo, ships, flames visible) | 0.0 |
| Shaders lowered | 30 | 0 |
| Pipelines built | 15 | 0 |
| Present rate | 60 Hz, paced | ~300/s, unbounded |
| Present fence | `(id=1, value=3/5/7/...)` | `(id=0, value=0)` |
| `DrawArrays` log lines | 0 | 0 (not a useful metric) |

## Root cause (evidence-backed)

1. **Our MME/QMD chain never emits a shader-bind or draw method.** Recon of both trees: Lichii's
   `MacroEngine::Execute` → `maxwell3d.CallMethod` emits draw methods → `DrawManager::DrawEnd` →
   pipeline cache → shader lowering. Ours emits only setup registers (`0x6D=3328`, `0x8E4=572`,
   `0x44/0xD00-0xD02/0x8C4=28`) — no shader bind, no draw. Therefore `GetGraphicsShader()` and
   `GetComputeShader()` are never called with a valid shader, and zero shaders are lowered.
2. **Compute macro code lives in the wrong bank.** Every method `0x45`–`0x48` write routes to
   `GPFifoClass._macroCode` (3D bank) regardless of subchannel (`GPFifoProcessor.cs:745-748`).
   Compute execution reads `_computeMacroCode`, which is never written — hence 14 compute macros
   execute an all-zero bank. Lichii shares the code bank but keeps **separate per-macro execution
   state**. A prior attempt that shared both the bank *and* the `Macro[]` slot state failed
   (`NO_COMPUTE_EMIT`, 14→12 macros) because of that collision. The correct fix is: share only
   `_macroCode`, keep `_computeMacros[index]` for state, and key the memo on `MmeCodeGeneration`.
3. **Ampere/SM86 lowering is absent from HEAD.** It exists intact on
   `archive/nx2-oot-pre-kenji-rebase` (9 files under `Decoders/Ampere/`, one integration point in
   `ShaderCache.cs:703`, plus `AmpereLoweringSelfTest`). Re-introducing it is mechanical.
4. **Present fence/pacing is a separate correctness bug, not the cause of black frames.** Ours
   reads the correct raw words 22/24 but waits on the *previous* fence (initial `(uint.MaxValue,0)`)
   and paces only OoT. Lichii waits on the current fence and paces all titles at 60 Hz. Timing is
   read 2 words early (ours raw w[10], Lichii w[12]).

## Goals

1. Build a repeatable A/B harness that runs both emulators on the same NUP and reports frame
   brightness + key counters (the oracle as verifier).
2. Fix compute macro code delivery (shared code bank, separate execution state).
3. Re-introduce Ampere SM86 lowering from the archived branch.
4. Fix present fence wait + pacing to match Lichii.
5. Verify: a non-black frame from our fork, compared side by side with the oracle.

## Non-goals

- Full feature parity, playability, audio, or performance.
- Re-porting the entire Eglantine fix chain.
- Repeating the old diagnostic series (`DRAW_MACRO`, `FS_VIEW`, etc.) — those questions are closed.

## Constraints

- Commits authored `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`, pushed to
  `origin` (github.com/kleinergoo7/Kenji-NX). Never the Forgejo `kenji-nx` remote.
- Exactly two local branches; archive untouched (except reading Ampere files from it).
- Build `dotnet build Ryujinx.slnx -c Debug`; smoke `Nx2SwizzleTests` 3 passed.
- Fast Fusion: `NX2_PATCH=""` + 12 GB / SoftwarePageTable / ignore-missing-services.
- No ROM, frame dumps, logs, or scratch in git.
- Agent model: only `opencode-go/hy4-flash-free`.

## Verification

- **A/B harness:** `tools/nx2/oracle-ab.sh` runs both emulators for N seconds with matched flags,
  converts `.tga`/`.rgba` captures to PNG, and prints mean brightness + pipeline/shader counts.
  PASS for our fork = mean > 0.1 and shader-lowered count > 0.
- Oracle path: `/tmp/opencode/lichii/build/bin/eden-cli` with `EDEN_NX2_AUTO_STUB=1`,
  `EDEN_NX2_FRAME_CAPTURE=<dir>`, running on `DISPLAY=:0.0` (Xvfb has no present queue).
- Our path: `tools/nx2/run-oot.sh` with `NX2_FRAME_DUMP=1`, `NX2_PATCH=""`.

## Order

Task 0 (harness) → Task 1 (compute code bank) → Task 2 (Ampere lowering) → Task 3 (present fence/pacing) → Task 4 (verify).

Rationale: Task 1 is the earliest divergence and has a precise, small fix; Task 2 is mechanical
once shaders can be delivered; Task 3 is a correctness win measurable against the oracle but does
not cause black frames on its own.

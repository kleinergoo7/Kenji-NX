# NX2 rendering bring-up design

Date: 2026-11-19
Target: a drawn, non-black frame from a Switch 2 title in this Ryujinx fork, starting with Fast Fusion.

## Intent and why

The fork boots Switch 2 titles (NPDM, services, memory layout, VI2 presents) but shows black frames.
Fast Fusion (`Fast_Fusion_Update_1.4.nup`) boots, presents at ~300/s, and issues **no GPU work** after
initialisation: 4 GPFIFO submits, 168 3D methods, 0 draws, ~35 compute methods, zero-valued frame dumps.
Guest stacks show the main thread in `nn::wsi::ounce::native::NativeWindow::Present` and all task-queue
threads idle in `nn::os::WaitEvent`. OoT shows the same `draws=0` symptom. So the work splits into two
questions: why the guest never dispatches render commands, and whether our GPU path would execute them
if it did.

The reference is Eglantine's `nx2` branch (local clone `/tmp/opencode/eglantine`, `NX2_NOTES.md`), which
documents 100+ blockers in the order its authors hit them and shipped renders on several titles.

## Goals

1. Find the earliest blocker that prevents Fast Fusion from issuing render commands, using existing
   diagnostics rather than new guesswork.
2. Close the missing GPU-pipeline pieces in reference order, verifying after each one.
3. Produce a non-black presented frame with a screenshot/frame dump as proof.
4. Leave the repo in a state a fresh clone can continue from (docs updated, commits on `origin`).

## Non-goals

- Full parity with Eglantine, all titles, audio, or acceptable framerate.
- OoT movie/AV1 decode (separate, already-planned work).
- Changing the proven run profile (12 GB DRAM, software page tables, ignore-missing-services).
- Committing run artifacts or the ROM.

## Anti-goals

- Rewriting GPFIFO/MME/MemoryManager wholesale without evidence that a specific gap is on the path.
- Adding fixes faster than they can be verified (each step must have a pass/fail signal).
- Treating a synthetic test pattern as "rendering" (it is only a verification tool).

## Constraints

- Commits authored `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to
  `origin` = github.com/kleinergoo7/Kenji-NX; exactly two local branches
  (`kenji-nx2-oot-bringup`, `archive/nx2-oot-pre-kenji-rebase`).
- Build `dotnet build src/Ryujinx/Ryujinx.csproj -c Debug`; `Nx2SwizzleTests` 3/3 as a smoke test.
- Run config: `NX2_PATCH=""` for Fast Fusion (the OoT movie patch is harmful here).
- Eglantine is C++, a local read-only reference, not a dependency; we port algorithms, not code.
- The `.nup` lives in `/home/reng/Downloads`; `.nx2-logs/` is untracked scratch.

## Approaches considered

1. **Triage the guest stall only.** Use stack/IPC/SVC diagnostics to find the one condition withholding
   render tasks. Cheapest; risk of open-ended `cengine.nss` reverse engineering with no payoff.
2. **Port the GPU pipeline in reference order** (chosen). Short time-boxed triage, then close the
   documented gaps one at a time, verifying after each. Large but proven: Eglantine's blocker table is
   the closest thing to a specification we have, and several earlier rows are already ported here.
3. **Synthetic frame via the existing `Nx2NativeObjects.Swizzle` helper.** Useful to prove
   composite/screenshot end-to-end, but not the goal; keep as a verification milestone only.

Approach 2 was chosen with a short Phase 0 triage, because our tree already contains many of
Eglantine's early fixes and writing shaders/GPFIFO code blindly would not address the observed
"no submissions at all" symptom.

## What recon already found (as of this design)

| Reference row | State in this tree |
|---|---|
| 30 GMMU identity map, 32 fence increments, 52 4-byte releases | Implemented (`Nx2IdentityMapping`, `NvHostChannelDeviceFile`, `GPFifoClassState`). |
| 50 unmet-acquire stall/resume | Implemented (`GPFifoClass.StallOnAcquire`, `GPFifoProcessor.TryResume`), but no counter/test; current `spwait=0`. |
| 84 per-channel MME data RAM | Implemented (`_mmeDataRam` per `GPFifoClass`); word `0x27` only logged, no QMD ring. |
| 51/56 split methods by resume address | **Absent**: one `_state` carried across buffers, no map keyed by resume VA, no nested called buffers; `PbEnd` never called. |
| 53/60/62 QMD3 releases | **Absent**: release payloads defined in `ComputeQmd` but never fired; no slots 24/28/52, no bit-19 max-release, `UsesMme64` never set. |
| 54 SM86 shader lowering | **Absent**: only the Maxwell decoder; `IsAmpere` affects specialization only. SM86 SASS is rejected. |
| Earlier boot blockers (NPDM, services, characteristics bit 34, gsv registry) | Ported. |

Important caveat from recon: `draws=0` may be caused by an upstream condition (TSG/subcontext/USERD or
the vi2 event counter, reference rows 45/46/82) rather than by QMD or shaders. Phase 0 exists to decide
that before the larger ports.

## Reference algorithms (from Eglantine source recon)

- **Unmet acquire (already ported here):** read the GPU-VA semaphore word, compare with
  equal/geq/mask (`puller.cpp:220-231,518-530`), park the channel without advancing its word index,
  release fences while waiting, re-read the *same* word on wake and resume (`puller.cpp:531-544,316-329`).
- **Split methods:** `ParkedMethod` keyed by `resume_address = entry.addr + entry.size*4`; on a new
  entry, pop a matching record, or park the unfinished method (max 16, evict oldest) when the entry is
  an interleaved call; clear all parked methods at submission end (`dma_pusher.cpp:592-630,349-368`).
  Eglantine itself gates this with `EDEN_NX2_PARK_SPLIT_METHODS`, so for a first frame the existing
  natural continuation across entries may be enough — add parking only if traces show interleaving.
- **QMD ring:** each channel writes its ring base to MME data RAM word `0x27` (already per-channel here);
  macro `0xda` reads it back. The QMD *table* is deliberately process-global because one channel can
  schedule a QMD another decrements (`native_qmd.h:163-166`). PCAS actions 3/2 load, 7 decrement,
  12 increment; dependencies from `qmd[4]>>14`; QMD version must be `0x30`; releases at words/slots
  24, 28 and 52 (`native_qmd.h:59-149`). On dispatch, read the 256-byte QMD, dispatch compute
  (grid/block from qmd 12-19, code from 48-49), then fire every enabled release payload, with bit 19
  writing `max(old, payload)` (`kepler_compute.cpp:364-420,585-619`).
- **Fence increments (already ported here):** raise the kernel-side syncpoint max first, report the
  returned value as the submit fence, then append WaitForIdle + payload + N increment actions, or
  signal directly once the commands are known complete (`nvhost_gpu.cpp:381-400,491-523`).
- **Identity GMMU (already ported here):** `Register` maps `address -> address`; per-address-space
  listener replays mappings; SMMU device ranges shared by CPU range; 35-bit physical range for 12 GB
  (`nx2_stub_device.h:42-200`, `nvhost_as_gpu.cpp:138-227`, `device_memory_manager.h:173-175`).
- **MME64:** 96-bit macro words, two ALUs, registers 0-23, immediates 24-27, FIFO 28/29, DREAD/DWRITE
  ops 30/31; emit `CallMethod` into the 3D/compute class, refuse nested macro addresses >= 0xE00
  (`macro_turing.h:43-257`, `kepler_compute.cpp:207`). Shader lowering is a separate, independent
  prerequisite for visible pixels.

## The work, as decided

**Phase 0 — time-boxed triage (one or two runs, no new code where diagnostics suffice).**
- Read `docs/nx2/CONTINUE_HERE.md` around its TSG/subcontext/USERD notes and Eglantine rows 45/46/50/82/84.
- Run Fast Fusion with `NX2_FIFO_TRACE=1 NX2_SUBMIT_LOG=1 NX2_SYNCPT_TRACE=1 NX2_METHOD_LOG=1` plus guest
  stacks at a steady-state present. Note `tools/nx2/run-oot.sh` unsets `NX2_THREAD_DUMP_AT_PRESENT`, so
  guest-stack capture uses the `dotnet … .nup --dram-size …` invocation directly.
- Decide: does the guest reach a point where it *would* submit render commands, and is any channel
  parked on a semaphore/acquire or an unsignalled vi2 event? Write the finding into the doc before
  any code change.
- Gate: if the stall is upstream of GPFIFO, fix that first (Phase 1). If render submissions exist and
  are being mis-parsed, skip to Phase 2.

**Phase 1 — upstream enablement (only if Phase 0 says so).**
- Compare our `NvHostCtrlGpu` characteristics (`+0xb4` subcontext table, flag bit 26 shim) and
  `INx2Vi2Session` 31/32 event signalling against rows 45/46/82.
- Land the smallest reference-backed change; re-run the Phase 0 diagnostics.

**Phase 2 — GPFIFO/QMD correctness.**
- Wire `PbEnd` so pushbuffer word accounting becomes real; add stalled/resumed counters to the
  `fifo=[...]` diagnostic (row 50 is implemented but has no counter or test).
- Add parked methods keyed by resume address only if `NX2_FIFO_TRACE` shows interleaved command buffers;
  clear them at submission end. Keep the existing natural continuation as the default.
- Set `UsesMme64`, implement the QMD ring read from data-RAM word `0x27`, route PCAS actions to a
  process-global scheduler, and fire release payloads at slots 24/28/52 (bit 19 = max release) after
  dispatch, including for rejected QMDs.
- Verify `draws > 0` in `NX2 progress`, no 4 s fence stalls, and `NX2_SYNCPT_TRACE` fences reached.

**Phase 3 — shaders, only if draws exist but are rejected.**
- **One shared pipeline, generation-flagged rather than forked.** Make `Decoders/Decoder.cs`
  generation-aware via the `IsAmpere` flag that `GpuChannel`/`ComputeClass` already track, and pass it
  from `ShaderCache.Decode*`. Operand decode, control flow and register/predicate tracking stay shared
  with Maxwell; only the SM86 opcode table (`Decoders/AmpereOpcodes.cs`) and IR rewrite rules
  (`Translation/AmpereLowering.cs`) are new. No second translator, SPIR-V backend or shader cache.
- Lowering is IR→IR with ordered, individually unit-tested rewrite rules producing IR the current
  translator already handles: power-of-two IMAD → SHL, PLOP3 tables, integer carry/third-source,
  IPA modes, formatted surface load/store, R2UR predicate save/restore, LDS/RED/STG variants,
  task/mesh pseudo-ops.
- Add a negative reject cache keyed by code address/version (log once, never retry per draw) and an
  opt-in trivial constant/blit shader fallback for rejected programs during bring-up — explicitly a
  diagnostic, never a silent permanent behaviour.
- Add `NX2_SHADER_DUMP=1` raw-binary dumps and a small offline self-test that lowers a dump and reports
  failing opcodes, so iteration does not require booting the game.
- Verify: dumps lower with zero rejects, `draws > 0`, and a non-zero colour target. This is a separate
  prerequisite for visible pixels — Eglantine needed it even after MME/QMD worked.

**Phase 4 — prove and hand off.**
- `NX2_FRAME_DUMP=1`, inspect RGBA for non-zero pixels and distinct hashes, capture a window screenshot,
  record the exact invocation, and update `docs/nx2/CONTINUE_HERE.md`.

Each phase ends with a build + `Nx2SwizzleTests` smoke run, one committed change set authored as
kleinergoo7, and a push to `origin`. If three consecutive fixes in a phase fail to move the chosen
verification signal, stop and revisit the design with the human partner (systematic debugging rule 4.5).

## Verification surface

| Signal | How | PASS |
|---|---|---|
| `NX2 progress` | unconditional line in `ocarina.log` | `draws > 0` **and** a non-black image; `mme` climbing. Today frozen at `submits=4..12 draws=0 spwait=0`. |
| `NX2_METHOD_LOG=1` | `NX2 3dmethods … fifo=[bufs=… words=… spwait=… spincr=…]` | `bufs`/`words` climbing with submits; note `fifo=` is **not** a progress-line token. |
| `NX2_FIFO_TRACE=1` | parsed words + pending method (`Nx2FifoTrace.cs`) | continuity across split GPFIFO entries, no stale replay. |
| `NX2_SYNCPT_TRACE=1` | `NX2SPT subsum … q103ReachedFence=` | waited fences reached; no 4 s stalls. |
| `NX2_SUBMIT_LOG=1` | submit fences, spincr, 211 map args | new submits appear after Phase 1/2. |
| `NX2_FRAME_DUMP=1` | `.nx2-logs/frame-*.rgba` + `NX2 composite stats … nonzero=… hash=…` | `nonzero > 0` and hashes differ. Today 33 MB dumps are 100% zero (FAIL). |
| Screenshot | `xdotool search --name Ryujinx` → id exactly as printed (decimal, or hex **with `0x`**), then `import -window <id> out.png` | visibly non-black game area. |
| RGBA view | `convert -size 3840x2160 -depth 8 rgba:frame-early-1-c1-img1-3840x2160.rgba out.png` (or `ffplay -f rawvideo -pixel_format rgba -video_size 3840x2160 …`) | visibly non-black. No command was documented in-repo before this design. |
| `NX2_FRAME_DUMP_TEST=1` | `PASS={hashNonzero && fracExpected}`, `frac>0.90` | `PASS=True`; code fixed in `7fbe1f39f`, runtime re-run still outstanding — cheapest synthetic-frame milestone. |
| `NX2_SVC_COUNT=1` / `NX2_FS_LOG=1` | per-thread syscall histograms / real decoded FS paths (both now working) | guest-side confirmation; not draw success. |
| `NX2_PRESENT_TRACE=1` | present words | **Known broken**: `bufHash/dims/format/nonzero` print `UNRESOLVED`; do not use it as pixel proof. |

Build/test: `dotnet build Ryujinx.slnx -c Debug` (SDK pinned 11.0.100) and
`dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SwizzleTests"` (3 passed).
Relevant existing tests also cover `Nx2FifoTraceTests`, `SubmitGpfifoTests`, `Nx2GpuMemoryTests`,
`Nx2GpuMemoryAttributeTests`; use them as regression cover for Phase 2.

## What is left to the builder

- Exact code changes per phase, derived from the reference-algorithm recon.
- Unit tests for the split-method map and QMD release semantics (small, assert-based), using the
  existing `Nx2FifoTraceTests`/`SubmitGpfifoTests` as the pattern.
- Whether SM86 lowering is needed at all — decided by Phase 2's `draws` result, not before.
- Re-verify `NX2_FRAME_DUMP_TEST=1` at runtime and update the stale `docs/nx2/CONTINUE_HERE.md`
  (it still records `c56672f52` and `submits=12`) at the Phase 4 milestone.

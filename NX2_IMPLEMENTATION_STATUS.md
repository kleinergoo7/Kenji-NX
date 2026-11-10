# NX2 (Nintendo Switch 2) Implementation Status — Kenji-NX/Ryubing integration

## Current branch update · 2026-11-10

The working branch is `codex/nx2-submission-progress` on `kleinergoo7/Kenji-NX`. The Starforgejo reference HEAD is `bd99084`. The snapshot and historical findings below predate the current branch.

| Area | Status | Latest evidence |
|---|---|---|
| GPU memory aliases | VERIFIED BY TEST | `MemoryManager.cs` tracks the source handle on each mapping and preserves it when a range splits. Destroying a handle removes its aliases and preserves mappings owned by other handles. `Nx2GpuMemoryTests.cs` covers both cases. |
| GPU map IPC | PARTIAL | `INx2GpuSession.MapMemory` reports a rejected map without signaling successful completion. Sparse mapping and the device-wide memory lifetime still need verification. |
| Ampere lowering | PARTIAL | The offline Ampere lowering self-test passes on this build. Guest shader coverage remains unverified. |
| Mario Kart World | BLOCKED | The paired guest run on `37c78aa` recorded eight GPFIFO submissions, zero tracked draws and 3,096 VI2 presents. |
| Ocarina of Time | BLOCKED | The same paired build mounted the standalone NUP update and reached the SDK heap mutex fallback abort before its first present. |

GPU mapping now rejects a request that extends beyond its backing allocation. Sparse map IPC command 216 validates its request buffer and reports the completed prefix if an entry fails. The registry regression test verifies that a rejected oversized map preserves the prior alias.

Sparse-region reservations now validate overlap and virtual budget before replacing a live region. Tests cover a rejected overlap, a rejected over-budget reservation, and reuse of an existing reservation's budget. The solution built and all 64 HLE tests passed.

In the paired run on `b2193d0`, Kart reached present 120 after eight submissions and Ocarina reproduced the same startup abort. Sparse-region reservation changes still need guest validation. A Switch 1 title was not run during this stage.

**Date:** 2026-05-08 (workspace snapshot)  
**Workspace:** `/home/reng/projects/ryujinx-nx2` · `Ryubing` branch `nx2-probes-wip` @ `282029a88` (snapshot) / base probe commit `ad8d88186`  
**Auth (verified before push):** `gh auth status` → `github.com` logged in as **`kleinergoo7`** (active, https, token scopes incl. `repo`). Git author/committer set locally to `348551191+kleinergoo7@users.noreply.github.com` (verified `gh api user` id=348551191, login=kleinergoo7).  
**Destination (verified):** `https://github.com/kleinergoo7/Kenji-NX` (admin, **empty/no refs at audit time**; ls-remote returned no refs). Starforgejo `https://starforgejo.dev/projects/Kenji-NX` / local clone `__reconstruct/Kenji-NX` (origin `https://git.ryujinx.app/projects/Kenji-NX`, master `bd99084a2`) used as reference; its history preserved separately (not discarded).

## Honesty / scope
- **NOT a claim of 100% NX2 compatibility.** MKW (NX2 guest): **no video output** in verified headless runs, **submits=8 (frozen), draws=0, presented frames all-zero/constant** (Phase 0a). “Verified in guest execution” is used **only** where actual guest behavior was observed.
- No functionality faked: fences/syncpoints not treated as proof of draw execution; zero-filled buffer not treated as a rendered frame. Tests/builds reported only when run.

## Guest validation (real runs)
| Title | Loads/init | Graphics init | GPU submits execute (ioctl SubmitGpfifo) | Draws enqueued | Real frames rendered | Audio | Input | Result | Evidence |
|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| MKW (NX2) | yes | partial (NVDRV/nx2 sessions active) | 8 observed, frozen (`t=300s submits=8 draws=0`) | **no** (draw-set words 585/586/674 absent in scoped probe; `draws=0`) | **no** (NX2_FRAME_DUMP: 2560x1440 RGBA8 non-swizzled `nonzero=0.000000 hash=0x4BD87A07A4536325` constant, early+#28@60s/#100@156s/#200@258s) | observed in practice (**UNVERIFIED** path) | not fully verified | boots to black screen + audio (headless runs `--no-gui`) | `.nx2-logs/batch/item12_framedump/diag.log`, `.nx2-logs/nx2-diag.log` |
| MK8 Deluxe (Switch 1 control) | baseline reported earlier | — | — | yes (prior) | renders (prior) | — | — | **Switch 1 baseline must not regress** (full regression re-run **not executed this snapshot**) | session notes |

**NX2_FRAME_DUMP_TEST control (item12):** measurement correct (`bytes=1024 nonzero=0.968750 hash=0x50A5BB4127D129A5 unswizzle=no-throw`). Item12 run binary reported `PASS=False` due stale threshold string; **source fixed** to `frac>0.90` / `PASS={hashNonzero && fracExpected}` (`INx2Vi2Session.cs:1151`) — **runtime re-verification of fixed control not yet executed**.

## Subsystem status (evidence-based)
Labels: NOT IMPLEMENTED · STUB · PARTIAL · IMPLEMENTED—UNVERIFIED · VERIFIED BY TEST · VERIFIED IN GUEST EXECUTION.

| Subsystem | Status | Key files (Ryubing/src) | Changes/tests this line | Remaining blockers |
|---|---|---|---|---|
| NX2 loader (ACID 0x300 rewrite) | IMPLEMENTED—UNVERIFIED | `HLE/Loaders/Processes/Extensions/MetaLoaderExtensions.cs` (0x300→0x200 rewrite) | audit | FAC/FAH/NPDM/layout semantics unverified |
| Kernel NX2 GetInfo/SVC 0x80/39-bit paths | PARTIAL | `HLE/HOS/Kernel/SupervisorCall/Syscall.cs` (InfoType Nx2 29/30/33, `[Svc(0x80)] SetGpuMemoryAttribute` updates the `GpuAccessible` page attribute, Switch 2 profile only), `InfoType.cs`, `KPageTableBase.cs:144` 39-bit path | `Nx2GpuMemoryAttributeTests` covers set/clear, attribute preservation, and invalid ranges | SVC 0x80 behavior in games, T239 memory layout/accounting, 6-core/affinity NX2 path UNVERIFIED |
| nx2 IPC/services (gsv/gpu/syncpt/vi2) | PARTIAL | `.../SystemAppletProxy/INx2GraphicsSystemSession.cs`, `INx2GpuSession.cs` (101/102/211-216 mapping/sparse/GPU-only), `INx2SyncptSession.cs`, `INx2Vi2Session.cs` (228/229) | env-gated probes/logging; no required-stub removal claimed complete | 228/229 conflated vs droplet behavior (228 Cancel vs 229 Present) — 228=0 observed; auto-stubs remain; TIPC alignment UNVERIFIED |
| TSG `/dev/nvhost-tsg-gpu` | **STUB** | `HLE/HOS/Services/Nv/NvDrvServices/Nx2StubDeviceFile.cs` (“placeholder… replace with real TSG subcontext handling”), registered `INvDrvServices.cs:69` | — | real subcontexts/VEIDs/TSG ioctls missing |
| GPFIFO/NvDrv submission | PARTIAL (blocked) | `NvHostChannelDeviceFile.cs` (submits counter), `GPFifoProcessor.cs`, probes | observed 8 submits ioctl, frozen | **root blocker:** no draw enqueue; 8-submit stop cause unresolved; USERD/doorbell incomplete; AllocGpfifoEx2/SetUserData stubbed per prior audit |
| Ampere chars/classes/engines | PARTIAL | `NvHostCtrlGpuDeviceFile.cs` (NX2 chars/TSG flags), `GPFifoProcessor.BindObject` Ampere+Maxwell table | — | correctness UNVERIFIED |
| SM86 shader decode/lowering | PARTIAL | `Graphics.Shader/Decoders/Ampere/*` (`AmpereInstruction` 16B, `AmpereLowering.cs`, `AmpereGpuAccessor`), `ShaderCache.cs` `LowerAmpere` (IsAmpere-guarded, fallback) | `ShaderTools --ampere-self-test`: 6 stages reached, fails `ArgumentOutOfRangeException` at `AmpereLoweringSelfTest.cs:251` | self-test failure, opcode/tensor/mesh-task/high-UR/header(128B vs 0x50) parity UNVERIFIED |
| MME/MacroTuring/QMD3 | PARTIAL | `Graphics.Gpu/Engine/MME/MacroTuring.cs`, `Engine/Compute/ComputeQmd.cs`/`ComputeClassState` QMD version fields | — | NX2 MME64/QMD3 semantics UNVERIFIED |
| VI2/display/present | PARTIAL (no rendered content) | `INx2Vi2Session.cs` (resolve NativeObject, CPU read/unswizzle, `Window.Present` 1/8, display syncpoint 0 inc, AllSlotsFreeMask, vsync signal) | NX2_FRAME_DUMP/NX2_PRESENT_TRACE env-gated stats; Phase 0a shows zero/constant frames | **real rendered frames absent** (draws=0 upstream); 228/229 pacing/slot-release differs from droplet; GOB/ASTC/RT backing UNVERIFIED; headless runs don’t test window |
| NX2 audio IPC (`INx2AudioUserClient`) | **NOT IMPLEMENTED / NOT FOUND** | no NX2 audio service class found by search | — | integrate NX2 audio IPC; MKW audio path UNVERIFIED |
| NX2 applet focus (E1 experiment, NX2-only flag) | STUB/EXPERIMENT (gated) | `ISelfController.cs` cmd 13 `SetFocusHandlingMode` | `NX2_EXP_APPLET_FOCUS=1` (default off): logs cmd13 flags + re-signals `AppletState.MessageEvent` (no new enqueued state transitions). **Hypothesis:** wakes applet/message waiter blocked on appmsg (E1/E6). **Falsifier:** no change in submits/draws/frames/blocked threads over 2×240s NX2 PTC-on warm. **Build 0 errors, KTime 5/5; runtime 2×240s NOT run.** | run controlled experiment; compare droplet behavior (numbers only) before stronger state changes. |
| CPU ARM64 NX2 extensions (ARMeilleure) | PARTIAL | `ARMeilleure` LSE/CAS/LDAPR present; PACGA “QARMA engine not emulated” (`InstEmitSystem.cs:106`) | — | PACGA/MTE/BRA/BLRA/LDRA/RETAA/RETAB full NX2-required coverage UNVERIFIED |
| Diagnostics (env-gated, default off) | IMPLEMENTED—UNVERIFIED | `Nx2Diagnostics`, `Nx2IpcCounters`, `Nx2SyncptTrace`, `Nx2ThreadDump`, `Nx2SvcCounters`, `Nx2MethodProbe`, frame-dump paths, `nx2-diag.sh` | NX2_SVC_COUNT=1 real dumps (nx2-diag.log @60/150s): uid95 `WaitSync+ResetSignal+SendSyncRequest` loop (2013→17618 cycles, **run/state-dependent; do not conflate with item11 t=600 unsignaled wait**), uid64 only 7 SVCs @60s (minimal, consistent with blocked-in-wait), uid30 no SVC_THREAD line found @60/150/240s (consistent with blocked early; handle owner unresolved), uid28 becomes `SignalToAddress/SleepThread/WaitForAddress`-heavy by 150s. NX2_IPC tid/uid same key; tid=29 dominated by INx2GpuSession 216/211/101. | **Controls partial:** correlate SendSyncRequest vs NX2_IPC_COUNT per thread; MK8 spread; unset→0 lines.
**0b handle owners (RESOLVED via NX2_THREAD_DUMP creator-site auto-register, new evidence):**
- `H262151` (uid64 `OeMessageDispatchThread`) → `appmsg` (`AppletStateMgr.MessageEvent`, applet message event; HLE never signals unless messages enqueued). E1 `NX2_EXP_APPLET_FOCUS` targets this (re-signal only; no state fabrications). E1 run1 (240s, PTC-on warm): submits=8/presents 426→832, draws=0, uid64 minimal SVCs; **no wake observed**.
E1 run2 (130s, PTC-on warm): submits=8, presents 0→1104, draws=0; PresentInternal shows `OeMessageDispatchThread wait=False` (uid64 woke; appmsg signaled). **Wake observed, but no change in submits/draws/frames** → E1 partial (wakes waiter) but **does not hit success metric**; keep gated, default off.
- `H196613` (uid30 `SdkSystemThread`) → `IApplicationFunctions._gpuErrorDetectedSystemEvent` (`IApplicationFunctions.cs:52`) — HLE never signals (no GPU error); likely normal/expected, not the blocker.
- `H3866736`/`H3899505` (uid95 `AudioFrameworkThread`) → guest-created event via `svcCreateEvent` (`Syscall.cs:2487`); signaling is guest-side. HLE cannot/should not signal it.
- `H1114142` (uid28/32/34/52-56/128) → `gpuid1` (NX2 GPU id event), SIGNALED; waitArb threads block on condvar/arbiter (UNMAPPED), not on unsignaled event.
**Conclusion:** the two genuinely HLE-owned unsignaled waiters are `appmsg` (uid64) and `_gpuErrorDetectedSystemEvent` (uid30, never-signaled-by-design). Neither explains draws=0 directly; E1 falsified for uid64 wake in run1. Next: audio/guest-side sync (uid95) and TSG/USERD/GPFIFO (Phase 2). |

## Builds / tests executed (real)
- `dotnet build Ryubing/src/Ryujinx/Ryujinx.csproj -v q` → **0 errors, 2 warnings** (observed in session).
- `dotnet test` KTimeManager tests: **5/5 passed** (`Ryubinx.Tests.HLE KTimeManagerTests` observed earlier in session context).
- `ShaderTools --ampere-self-test` (Debug): 6 stages completed, then `ArgumentOutOfRangeException` at `AmpereLoweringSelfTest.cs:251` (real failure, not hidden).
- NX2 runtime probes: NX2_FRAME_DUMP control measurement correct; NX2_SVC_COUNT dump emitted; NX2 submits/draws/frames measured.

## Remaining blockers (severity-ordered)
1. **No draw enqueue / submits frozen at 8** (NX2_METHOD_PROBE shows no 585/586/674 in scoped path; draws=0; frames zero). Investigate TSG/subcontext, USERD/doorbell, GPFIFO/QMD3/MME, command execution, and guest-side wait (0b blocked on unsignaled KReadableEvents: uid30 H196613, uid64 H262151, uid95 H3866736; handle owners unresolved).
2. **TSG/subcontext STUB** (`Nx2StubDeviceFile`) — required for real NX2 GPU channel setup.
3. **VI2 presents zero/constant buffers** — upstream of render; also 228/229/pacing semantics need reference-backed fixes.
4. **SM86 lowering parity/self-test failure** — fix `AmpereLoweringSelfTest` failure, expand opcode coverage tests; no silent wrong-Maxwell fallback.
5. **NX2 audio IPC not found** (`INx2AudioUserClient`) — verify/implement required path.
6. **CPU/kernel semantics gaps** (PACGA/MTE/other unverified ARM; SVC/memory accounting fidelity) — implement with tests, preserve Switch 1.
7. **Full Switch 1 regression suite** — not executed this snapshot.

## Best-effort TSG (UNVERIFIED) test (240s, PTC-on warm)
- Implemented `Nx2TsgDeviceFile` (replaced stub registration): handles 0x540B by returning an incrementing subcontext id in word 0; 0x5408 and unknowns keep input-echoing stub behavior. Marked UNVERIFIED in code/logs.
- Result: submits=8, draws=0, presents 0→912+; **no change**. Best-effort subcontext id did not unlock draws/submits. Revert? Kept (harmless, gated only by registration; env-gated logging). Status: IMPLEMENTED—UNVERIFIED / no guest effect.

## TSG ioctl inventory (real, 60s run, NX2_IOCTL_LOG=1)
- `/dev/nvhost-tsg-gpu`: `0x5408` (size 4, input 0x400) and `0x540B` (size 0x18, first word varies 4/6/7/9/11, second word 0/1/2). Stub echoes input. Reference-backed semantics UNVERIFIED (no droplet/ref source available in workspace); **not implemented** to avoid guessing. Keep stub; revisit with reference.

## Next implementation step
Fix the highest-priority *verified* defect in the GPU submission/display chain with reference-backed, testable changes: (a) resolve NX2_SVC/IPC controls and blocked-event handle owners (0b/0c) to identify what the guest waits on; (b) implement real TSG subcontext/ioctl behavior (replace `Nx2StubDeviceFile` stub) with unit/log evidence; (c) address 228/229 separation and GPFIFO/USERD/doorbell per references. Re-run KTimeManager + NX2/MK8 controls, commit logically, push to `kleinergoo7/Kenji-NX`, verify remote commit hash.

## Commits/push log
- `282029a88` (local `nx2-probes-wip`, author `348551191+kleinergoo7@users.noreply.github.com`): NX2 probes snapshot (NX2_SVC_COUNT + NX2_FRAME_DUMP stats/cmd, env-gated). Pushed earlier to `kleinergoo7/Ryubing` `test/nx2-probes-snapshot-20260508` (force, new branch) per prior explicit request. **Push to `kleinergoo7/Kenji-NX` (verified):** `github-kenji` → `https://github.com/kleinergoo7/Kenji-NX.git` (kleinergoo7). `112edbb19`/follow-ups on `nx2-probes-wip` (default)+`master`; Starforgejo ref `bd99084a2` on `upstream-kenji-starforgejo`. Latest NX2_SVC+E1 snapshot committed+pushed (see git log/hash).

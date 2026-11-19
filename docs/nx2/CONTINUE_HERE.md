# Continue here — Ocarina of Time (Switch 2 `.nup`) on Kenji-NX

**Status: the game boots to the movie sequence without aborting, but the window is black.**
No 3D draws are submitted (`draws=0`), presented frames are all-zero, and the movie
decoder is still a fake. The goal is a visible, non-black frame (title screen).
Phase 0 triage routes this to **`ROUTE=GUEST_GATE`** (see below): the emulator's
GPFIFO/fence/characteristics paths are healthy and the guest produces no render
command buffers.

Last verified state: branch `kenji-nx2-oot-bringup` @ `cd73b65fb` (2026-11-19), run
`.nx2-logs/run-20261119-160446/ocarina.log`.
Older status tables in `NX2_IMPLEMENTATION_STATUS.md` are historical (2026-11-10) and
describe a different branch; use this file for current work.

## Phase 0 triage — run `run-20261119-160446`

Measured from `.nx2-logs/run-20261119-160446/ocarina.log` (binary build `cd73b65fb`):

- 4 submits on syncpoints 1 and 2 with `inc=1`/`inc=2`: `sp=1 value=0 inc=0`,
  `sp=1 value=1 inc=1`, `sp=2 value=0 inc=0`, `sp=2 value=2 inc=2`.
- Syncpoints advance: `NX2 spincr id=1 now=1 check=1`; `id=2 now=1 check=1`;
  `id=2 now=2 check=2`.
- Fence reached: `NX2SPT 201-slot=0 hostAtSig=2` (the `Request201` probe returned
  `sp=2 target=2 mode=0 signalledNow=False outOfRange=False nullHandle=False`).
- Each channel's main render buffer (`buffer=1`) was captured fully at entry with an
  empty method queue: `channel=0x02B1583F words=9038 captured=9038 pendingMethod=0x0`
  and `channel=0x02D8F83E words=2184 captured=2184 pendingMethod=0x0`. All 16 entry
  headers have `captured == words` and `pendingWords=0`; the later submits only carry
  the residual `pendingMethod=0xE` (count 0), so nothing is parked or split.
- Render counters stay at `draws=0`: `NX2 present #N ... draws=[total=0 direct=0
  indirect=0 clear=0 texture=0]` and `NX2 3dmethods ... draws=0` (168 3D methods,
  73 macros). This run emits no `NX2 progress` line — that logger fires every 3600
  calls — so the present/3dmethods counters carry the evidence.
- Characteristics tail written: `argument size 0xE0, inline size 0xD0; zero-filling
  characteristics past 0xA0`; TSG subcontext ids 2 and 3 created. No TSG error.

Conclusion: the emulator's GPFIFO, fence and characteristics paths are healthy;
**no render command buffers are produced by the guest**. The decision rule therefore
gives `ROUTE=GUEST_GATE`: localise the guest-side render gate (Task 1) before GPU
pipeline work is justified.

## Phase 1 triage — guest-side render gate (Task 1, ROUTE=GUEST_GATE)

Two 90 s **direct** `dotnet … Ryujinx.dll` captures of
`/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` (fresh build `7e45918ac`;
`NX2_PATCH=""`, `--dram-size MemoryConfiguration12GiB --memory-manager-mode
SoftwarePageTable --ignore-missing-services`, plus `NX2_PRESENT_TRACE=1
NX2_MISSING_LOG=1 NX2_SVC_COUNT=1`). `NX2_THREAD_DUMP_AT_PRESENT=100` →
`.nx2-logs/run-20261119-162552-gate100/ocarina.log`;
`NX2_THREAD_DUMP_AT_PRESENT=5000` →
`.nx2-logs/run-20261119-162735-gate5000/ocarina.log`. Both boot without abort and
reach >12 000 presents, so both stack dumps fire.

Stack comparison (`grep -a "NX2 td pid=109\|NX2 main mutex" <log>`): the two
snapshots are byte-for-byte identical apart from timestamps. `MainThread` (uid 28)
is caught at the same guest leaf `nnSdk:0x563c38` (pc `0xD790BC4`, wait object
`appmsg`) under
`nn::wsi::ounce::native::NativeWindow::Present(unsigned int, nn::gs::NativeSyncHandle const&):0x0038`
called by `nn::wsi::nv::OnPresentTexture` — it never leaves `Present` between the
two presents. Every `TaskQueue_Core0..5` (uid 34–39) is caught at
`nn::os::WaitEvent(nn::os::EventType*):0x0058` from the same `cengine.nss:0x1409d0`
call site (`Core3` also holds the `gpuid1` event); none leaves `WaitEvent`.
`NX2_SVC_THREAD` histograms confirm the queue cores issue at most 2
`SendSyncRequest` calls in 60 s (uid 37), otherwise only
`WaitProcessWideKeyAtomic`/lock primitives — no new GPU submits resume. `NX2 progress t=60s submits=4 presents=14305 draws=0 … gpuid=[gpuid0=3601
gpuid1=4824]`: GPU/vsync events are signalled thousands of times, and Task 0
recorded the 4 submits' fences reached with no unmet acquire. Only two auto-stubbed
IPC calls occur (`INx2GraphicsSystemSession:1501/1502`, at boot, off the render
path). There is therefore no stubbed reply, shared-memory flag or **unsignalled
event** at the INx2 service boundary, and no external (input/asset) blocker — the
guest is starved on the GPU-completion side of its render dispatch.

GATE=GPU_QMD3_RELEASE_NEVER_FIRED (HYPOTHESIS — not confirmed) — the captured
evidence establishes only that the guest is blocked on GPU completion (main thread
in `Present`, task queue idle, no new submits, `draws=0`). The specific attribution
to unfired QMD3 releases is inferred from the design spec / Eglantine rows
53/60/61/62 (words/slots 24/28/52, bit-19 = max release, `UsesMme64`) and has NOT
been verified at word level. This gate must be confirmed with `NX2_SYNCPT_TRACE`
+ `NX2_FIFO_TRACE` before any Task 2 GPU code is written.

evidence: run-20261119-162552-gate100/ocarina.log L595–612 MainThread frozen at
`nn::wsi::ounce::native::NativeWindow::Present:0x0038` / `nnSdk:0x563c38` plus
identical `NX2 main mutex addr=0x655E9EB3BC requester=0x8000`; L663–708 all
`TaskQueue_Core*` at `nn::os::WaitEvent` (`nnSdk:0x143f58`, `cengine.nss:0x1409d0`).
evidence: run-20261119-162735-gate5000/ocarina.log L649–666 MainThread identical
stack/mutex at present 5000; L717–757 TaskQueue cores identical; L973
`NX2 progress t=60s submits=4 presents=14305 draws=0 … gpuid0=3601 gpuid1=4824`.
evidence: both logs `NX2_SVC_THREAD t=60s` — queue-core uids 34–39 issue at most
2 `SendSyncRequest` (uid 37) and are otherwise only
`WaitProcessWideKeyAtomic`/lock primitives; `NX2 missing` only
`INx2GraphicsSystemSession:1501/1502` at boot.

Smallest candidate fix (if the hypothesis is confirmed): fire QMD3 release payloads
(slots 24/28/52, bit 19 = max) on dispatch (including rejected QMDs) and set
`UsesMme64`, in `src/Ryujinx.Graphics.Gpu/…/ComputeClass.cs` + the GPFIFO processor
— **not** an `INx2*Session` file, so no in-session fix or test applies in this task.
Next action (confirmation first, before any Task 2 GPU code): re-run the same
steady-state capture with `NX2_SYNCPT_TRACE=1 NX2_FIFO_TRACE=1` and name the exact
semaphore word / fence that the 3D queue waits on. Only if that trace confirms the
QMD-release hypothesis should Task 2 GPU changes begin; otherwise revisit the
finding with the human partner rather than starting GPU work against an
unconfirmed blocker.

## Phase 1b confirmation — QMD release / semaphore decode (Task 1b)

Re-ran the 90 s direct Fast Fusion capture with the new observe-only traces
(`NX2_QMD_TRACE=1 NX2_SEM_WATCH=1 NX2_SYNCPT_TRACE=1 NX2_FIFO_TRACE=1`,
`NX2_PATCH=""`, Fast Fusion profile) into
`.nx2-logs/run-20261119-173429-confirm1b/ocarina.log` (rc=124 expected timeout,
1150 lines). The new binary is confirmed live: the `NX2SEM` line below came from
the new `Nx2QmdTrace` class whose env gates are read at class init, and
`NX2QMD qmd=0x` is present in the built `Ryujinx.Graphics.Gpu.dll` — so both
gates were on.

- `NX2QMD` lines: **0**. `SendSignalingPcasB` is the only `Read<ComputeQmd>` / QMD
  dispatch entry in `ComputeClass.cs` (no alternate path); it was never called,
  so the guest issued **no compute QMD dispatch** in the whole capture.
- `NX2SEM` lines: 1 — `NX2SEM w addr=0x6782FF0010 value=0x7` at ~26 s, a GPFIFO
  `Semaphored` 4-byte release, not a QMD release slot.
- `NX2SPT`: the same 4 submits (sp 1/2, fences 0/1/0/2) with slot 0 signalled at
  target 2; `NX2FIFO`: 745 lines; `NX2 progress t=60s ... submits=4 ... draws=0`.

No QMD dispatch ⇒ there is no QMD address, release-slot address/payload or
QMD-driven semaphore word to name: the release slots were never reached. The
single semaphore word observed (`addr=0x6782FF0010 ← 7`) is a fifo semaphored
release, not a compute→3D QMD release.

CONFIRM=UPSTREAM_OF_QMD

Meaning: the guest never submits compute QMD work, so the blocker is upstream of
QMD dispatch — firing QMD releases / setting `UsesMme64` (Task 2) cannot fix it.
Per the plan gate, Task 2 must not start; report to the human partner with this
finding instead of writing GPU code against an unconfirmed blocker.

## Phase 1c — macro → PCAS → QMD link (Task 1c)

Re-ran the 90 s direct Fast Fusion capture with the new entry-of-PCAS trace on
(`NX2_PATCH="" NX2_PCAS_TRACE=1 NX2_MACRO_LOG=1 NX2_QMD_TRACE=1 NX2_METHOD_LOG=1`,
Fast Fusion profile) into the git-ignored scratch dir
`.nx2-logs/run-20261119-175653-confirm1c/ocarina.log` (rc=124 expected timeout,
684 lines). New binary confirmed live: UTF-16 string `NX2PCAS qmd=0x` present in
`Ryujinx.Graphics.Gpu.dll` (built 17:56).

- `NX2 progress t=60s ... mme=73/4159/0+73 pcas=[calls=0 zero=0 nonzero=0 mme64=0] ... draws=0`.
- `NX2PCAS` lines: **0** (entry of `SendSignalingPcasB` never reached — not even a
  qmd=0 Resume). `NX2QMD` lines: **0**.
- `NX2 mme exec` lines: **73** (`MacrosRun=73`); `emitted=4159` macro methods;
  all 73 lines show `dram27=0x00000000`.
- Top macro positions from `NX2 mme exec`: `pos=92` (×28), `pos=611` (×14),
  `pos=0` (×14), `pos=620` (×4), `pos=604` (×4), `pos=445` (×2), `pos=385` (×2),
  then singletons (`905, 808, 799, 453, 338, …`).
- `NX2 emit` lines: 30 (the `emitIndex <= 30` cap). Their addresses are
  `0xD00/0xD01/0xD02, 0x8C4, 0x44, 0x8E0–0x8E3, 0x376, …`.
  **No `NX2 emit` targets the compute-class PCAS register**: `SendPcasA`,
  `SendPcasB` and `SendSignalingPcasB` are method indices `0xAD/0xAE/0xAF`
  (byte offsets `0x2B4/0x2B8/0x2BC`, verified with `Marshal.OffsetOf`), and none
  of those indices appear in the `emitted=[…]` or `compute=[…]` histograms either
  (`compute=[0xEA7=8 0xE68=5 0xE69=5 0xE67=5 0xEA6=4 0xE89=4 …]`).

MACRO_QMD=MACROS_RUN_NO_PCAS

Meaning: MME macros run (73) but `SendSignalingPcasB` is never entered, so the
guest never emits the PCAS/QMD-launch method — the chain breaks before PCAS, at
macro emission. Per the plan gate the next task would be to fix macro emission;
do **not** begin it without a human decision on this recorded result.

## Phase 1d — disassemble the stalled guest loop (Task 1d)

Code bytes were extracted from `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup`
with `tools/nx2/dump-guest-code.sh <nup> main <guest-va> <length> <out.bin>`,
which reuses the repository's `NupArchive` + LibHac `NsoReader` (two runs gave an
identical SHA-256). `cengine.nss` is the ExeFS `main` NSO: its real load base is
`0x8520000` (Task 1's own symbolizer: `0x863d118 → cengine.nss:0x11d118`), its
text section is at NSO file offset `0x108`, memory offset 0, size `0x4E02F0`,
decompressed before slicing. `Nx2GuestCodeTests` locks down the same arithmetic
with the brief's worked-example values (base `0x85CC000`, text RVA `0x61000`,
file offset `0x11118`); see the task report for the reconciliation.

Disassembly (`aarch64-linux-gnu-objdump -D -b binary -m aarch64`, VMA adjusted):

- VA `0x863D118` (`cengine.nss:0x11d118`): the frame reached via `blr x8` at
  `0x863D114` then loops over an array of 16-byte records at `[x19,#248]` (count
  `[x19,#256]`), dispatching each via `blr [x22]` (`0x863D140`–`0x863D15C`) and
  ending `bl 0x867bc24` / `bl 0x863b408` before `retab`. This is the per-frame
  render-job dispatch that enters `nn::wsi::nv::OnPresentTexture` →
  `NativeWindow::Present`.
- VA `0x863B54C` (`cengine.nss:0x11b54c`): tiny thunk at `0x863B52C` —
  `blr [vtable+0x28]` then tail-call `br [vtable+0x20]` after restoring `x30`;
  a generic virtual forwarder.
- VA `0x860B87C` (`cengine.nss:0xeb87c`): raw bytes `ldr x0,[x22]; ldr x8,[x0];
  ldr x8,[x8,#272]; blr x8; tbz w0,#0,0x860B75C`. The enclosing function
  (`0x860B6E0` prologue) issues the same `blr [vtable+0x110]` at `0x860B724` and
  `0x860B754` and exits only on `tbnz w0,#0` (`0x860B728`/`0x860B758`).
  **Reading (no symbols):** a virtual *completion poll* — it spins while bit 0
  of the method's boolean return is 0.

GUEST_LOOP=cengine.nss:0xeb87c (VA 0x860B87C) — virtual completion poll: `blr [vtable+0x110]` then `tbz w0,#0,0x860B75C`, spinning until bit 0 of the returned bool is 1 (the awaited job / present-completion reports done); the frames it calls (`cengine.nss:0x11b54c` → `0x11d118`, VA 0x863D118) lead into the per-frame render-job dispatch that calls `OnPresentTexture` → `NativeWindow::Present`, blocked on the `appmsg` event at `nnSdk:0x563C38`.

evidence: `main.nso` text (decompressed) at file offsets `0x11d118`, `0x11b54c`,
`0xeb87c`; Task 1 logs `run-20261119-162552-gate100/ocarina.log` L598–607 (MainThread
stack identical at L658–661 of the gate5000 run), leaf `nnSdk:0x563c38` on `appmsg`.
Gate: stop for a human decision before acting on what it names — the ROM has no
symbols, so the function naming and the "what completes" part of the condition are
interpretations of the raw bytes/disassembly recorded above, not symbol-level proof.

## Phase 1e — watch the guest completion flag (Task 1e)

Added an observe-only 10 ms PC sampler (`NX2_PC_WATCH=<hex,hex,...>`) in
`Nx2ThreadDump.cs`. It logs `NX2PCW uid=… pc=0x… regs=[…] mem=[…]` for running
application threads at the configured PCs, reusing the 64-byte pointer-register
memory dump, and caps output at 200 lines (one line per uid/PC per second so
hits span the capture). The `x0` object dump happens only at `0x860B884`–`0x860B888`,
after `ldr x0,[x22]` at `0x860B87C` has executed and before `blr x8` at `0x860B888`
can clobber `x0`; the completion bool is logged as `w0=` at `0x860B88C`
(`tbz w0,#0`, the first instruction after the call). No guest execution or GPU
dispatch change.

Capture runs (Fast Fusion, `--dram-size MemoryConfiguration12GiB
--memory-manager-mode SoftwarePageTable --ignore-missing-services`, all with
`NX2_PATCH="" NX2_PC_SAMPLE=1 NX2_THREAD_DUMP=1` and the then-current watch
`NX2_PC_WATCH=0x860B87C,0x860B884`; after review the corrected watch is
`NX2_PC_WATCH=0x860B884,0x860B88C`. It was not re-captured because every run
below had zero hits, so no captured data is invalidated):

- `run-20261119-183301-1e-brief` (60 s, brief flags only): 1 `NX2PCW` line — the
  `enabled pcs=[…]` header, **0 hits**. `NX2_PC_SAMPLE` shows translation-block
  PCs (`0xD790B9C`, `0x85CC3E0`), not exact instructions: `Context.Pc` is the
  dispatch address unless `NX2_PRECISE_PC=1` (`NativeContext.GetPc`).
- `run-20261119-183420-1e-precise` (60 s + `NX2_PRECISE_PC=1`): 0 hits; debugger
  mode JIT still booting at t=57 s, never reached the poll.
- `run-20261119-183613-1e-precise240` (240 s + `NX2_PRECISE_PC=1`): 0 hits at
  both PCs. Game healthy: `NX2 progress t=130s submits=4 presents=50697 draws=0`.
- `run-20261119-184200-1e-loop` (150 s, + `NX2_PRECISE_PC=1`, watch widened to
  `0x860B724,0x860B754,0x860B75C,0x860B87C,0x860B884,0x860B888` — every `blr`
  and the loop back-edge in the poll function): **0 hits at any of the six PCs**.
- Control `run-20261119-184046-1e-control` (45 s, `NX2_PC_WATCH=0xD790BC4,0x85CC3B0`,
  + `NX2_PRECISE_PC=1`): the watch does fire —
  `NX2PCW uid=28 pc=0xD790BC4 regs=[x0=0x6000B … x22=0x49F0B08E14 …] mem x1@0x784E967200=[00000002 …]`
  — so the timer, PC match, register and 64-byte memory dumps work. The control
  hit is not the poll site and carries no flag information.

FLAG_WATCH=INCONCLUSIVE_NO_HITS — object pointer in x0: `<none captured>`,
vtable `mem[x0]`: `<none captured>`, 64-byte object dump: `<none captured>`,
`w0` at post-call PC: `<none captured>`, bytes changing across hits:
`<no hits to compare>`. No running application thread was observed executing
`0x860B87C`, `0x860B884`, or any other instruction of the poll function in
~6.5 minutes of precise-PC sampling, even though the game reached 50k presents.
The `0x860B87C` frame seen in Task 1's stack is therefore a suspended call
frame (the thread is blocked deeper in `NativeWindow::Present` on `appmsg`), not
an active spin. None of `FLAG_NEVER_SET`, `FLAG_SET_LATE` or `FLAG_SET_BUT_IGNORED`
is supported by this evidence — asserting one would be fabrication. Per the
brief's gate, stop for a human decision; a follow-up would have to read the
object through the poll frame's callee-saved slot (`ldr x0,[x22]` at 0x860B87C)
from the blocked thread's saved context, which is outside this watch's scope.

## Phase 1f — comparison with Eglantine's working title 4 (Task 1f)

Compared the reference tree at `/tmp/opencode/eglantine` (branch `nx2`, `NX2_NOTES.md` rows
45-57, 81-84, 115 and source) against this tree's macro / GPFIFO / nx2-service code. The
symptom to explain is **"no macro emits PCAS / no dispatch / no draws"**: Phase 1c measured
73 MME macros, 4159 emitted methods, `NX2PCAS`=0, `NX2QMD`=0, draws=0, and every macro logged
`dram27=0x00000000`. No fix code was written.

EG_COMPARE=
1. **PCAS/QMD dispatch is wired to a single method and a passive counter.** Here:
   `src/Ryujinx.Graphics.Gpu/Engine/Compute/ComputeClass.cs:67` registers only
   `SendSignalingPcasB` (compute method 0xAF); `:139-278` reads one QMD and calls
   `DispatchCompute` directly — no dependency accounting, no release slots, no ring;
   `src/Ryujinx.Graphics.Gpu/Engine/Compute/QmdDescriptor.cs:45-59` `QmdScheduler.Track`
   merely stores `_last` and increments `_dispatchCount`. Eglantine:
   `src/video_core/engines/kepler_compute.cpp:355-365` routes methods **0xAE/0xAF/0xB0** into
   `native_qmd.Signal`; `:378-450` loads the QMD, fires release slots 24/28/52 plus the
   bit-19 max release, then dispatches; `src/video_core/native_qmd.h:39-160` is the full
   scheduler (dependency counts, deferred actions, dependent-QMD chain, one process-global
   state table at `:158`). Consequence: the PCAS 3/7 increment/decrement actions emitted by
   macro 0xda (Eglantine `NX2_NOTES.md` row 84, line 142) arrive on 0xAE/0xB0, which this tree
   has no callback for, so they are dropped and the `NX2PCAS` trace (0xAF only) stays 0; and
   even a 0xAF launch fires no release, so the semaphore/fence the guest waits on is never
   written. **NEEDS_REFERENCE_PORT.** Candidate fix #1 if a human picks this: port the
   `NativeQmdScheduler` state machine and wire all three compute methods to it — do not start
   without the human gate decision.
2. **MME data-RAM word 0x27 (QMD ring base) is read only for logging; nothing schedules
   QMDs from it.** Here: `src/Ryujinx.Graphics.Gpu/Engine/MME/Macro.cs:121-126` and
   `MacroTuring.cs:100-102` expose `DataRamWord(0x27)` for the `NX2 mme exec` log only, and
   `ComputeClass.cs:174` passes it to `Nx2QmdTrace.Describe` as a trace string; every captured
   macro shows `dram27=0x00000000` (Phase 1c). Eglantine: the per-channel data RAM is wired as
   the compute macro's active RAM and word 0x27 is read/observed there
   (`kepler_compute.cpp:199-200, 280-283`), and macro 0xda reads the QMD ring base from that
   word and drives the ring (`NX2_NOTES.md` row 84, line 142: each channel sets its own base,
   0x21098d39 / 0x213e8ff8 / 0x213ea168). Consequence: the macro that should schedule the QMD
   chain has no ring base and
   cannot emit the launch PCAS — the most direct explanation for "no macro emits PCAS".
   **NEEDS_REFERENCE_PORT.** Caveat: whether this title's macro actually consumes word 0x27
   is inferred from Eglantine's title-1 notes, not verified at word level here (could not
   determine); the capability gap itself is certain.
3. **Area 1 — macro upload / positions / 0xE00 trigger mechanics match; the divergence is
   bank location and selection.** Sub-point (a) upload offsets **0x45-0x48**: here
   `ComputeClass.cs:22-25, 89-103` delegates to `GPFifoClass.cs:488-524`
   (`KeepComputeMmeCode`/`AddComputeMmeCode`/`SetComputeMmeBindPointer`/`BindComputeMmeStart`);
   Eglantine `kepler_compute.cpp:344-354` handles 0x45 `ClearCode`, 0x46 `AddCode`, 0x47
   `native_bind_pointer`, 0x48 `positions[pointer++]`. (b) positions/entry points: here
   3D `_macros[pointer] = new Macro(argument)` (`GPFifoClass.cs:406-413`) and compute
   `_computeMacros[_computeStartPointer++] = new Macro(argument)` (`GPFifoClass.cs:511-524`);
   Eglantine `maxwell_3d.cpp:1650` `positions[pointer] = value` and
   `kepler_compute.cpp:352` `native_macro_positions[native_bind_pointer++] = argument`
   (shared context at `macro.h:277-281`). (c) **0xE00-range trigger**: here
   `GPFifoProcessor.cs:756-765` routes it by subchannel engine with
   `Nx2MethodProbe.RecordMacro`, `:804-828` uses even offset = start / odd = push and index
   `(offset >> 1) & 0x7F`, `MacroTuring.cs:719` rejects emitted `address >= 0xe00`, and
   `Nx2MethodProbe.cs:34-35, 95, 150-157` classifies >=0xE00 as macro start/push words;
   Eglantine `kepler_compute.cpp:165-172` (even = start, odd = parameter,
   `((method - 0xe00) / 2) % positions.size()`) and `maxwell_3d.cpp:43, 1881`.
   **No divergence found in (a)-(c) themselves** — the mechanisms are equivalent at an
   0x80-position count. The actual divergence: here the compute macro bank is per-channel
   (`GPFifoProcessor.cs:489`, `GPFifoClass.cs:32-38, 485-549`) and `GPFifoProcessor.cs:804-838`
   picks it purely from `SubChannelEngine(subChannel)`; Eglantine `kepler_compute.cpp:157-159`
   switches to the compute engine/positions only when 0x45/0x47 were uploaded on the compute
   class, with all process channels sharing **one** macro context
   (`control/channel_state.cpp:33-42, 79-89`; `macro.h:277-281`; `NX2_NOTES.md` row 53, line 111).
   Consequence: a compute macro uploaded on one channel is not visible to another here, and a
   method on a subchannel our class table maps to 3D runs the 3D bank/state instead — either
   yields an empty/wrong compute macro and no PCAS emission. Unverified whether upload and
   invocation span channels in this title. **FIXABLE_HERE** (local bank/context change).
4. **`UsesMme64` is never set.** Here: `ComputeClass.cs:48` declares it with no assignment
   anywhere in the tree (grep confirms reads only at `:142`, `:156`, `:175`), so every dispatch is
   tagged `QmdDispatchKind.Compute`. Eglantine enables the Turing/MME64 path on the Ampere flag
   (`control/channel_state.cpp:79-89`, `engines/maxwell_3d.cpp:85`, `kepler_compute.cpp:156`).
   Consequence: the MME64/QMD-3.0
   descriptor is always wrong; low directness because dispatch itself is not gated on the flag.
   **FIXABLE_HERE** (set at Ampere class bind).
5. **No submit-time pushbuffer snapshot / `EDEN_NX2_PB_CHECK` equivalent.** Here: command words
   are fetched at parse time (`GPFifoProcessor.cs:523-533`) and a stalled channel re-reads its
   own captured tail (`:545-550`); there is no per-submit word hash or resnapshot. Eglantine:
   submit-time copy + resnapshot on resume (`src/video_core/dma_pusher.cpp:95-112`) and the
   changed-entry check (`dma_pusher.cpp:211-213, 935-945`). Consequence: guest-rewritten command
   words could be missed; Phase 0 measured `captured == words`, `pendingWords=0` and no
   `PB_CHECK` difference, so this is **not currently causal**. **NEEDS_REFERENCE_PORT.**
6. **No parked split methods keyed by resume address.** Here: `GPFifoProcessor.cs:536-579`
   runs a buffer linearly; a method split across GP entries or a called buffer has no
   park/resume machinery. Eglantine: `dma_pusher.cpp:185-213, 592-628` parks the unfinished
   method and resumes it at the next entry's address (`NX2_NOTES.md` rows 51, 56, 73).
   Consequence: split compute-dispatch parameters would be lost. Phase 0 shows no split in
   this run, so low directness. **NEEDS_REFERENCE_PORT.**
7. **Acquire/resume mechanism differs but is observably healthy.** Here: throw-based
   `StallOnAcquire` + deferred fetch (`GPFifoProcessor.cs:251-274, 523-533`,
   `GPFifoDevice.cs:238`). Eglantine: `puller.ResumeAcquire` (`dma_pusher.cpp:95-97`).
   Consequence: equivalent in effect; the 4 submits reached their fences with no unmet
   acquire (Phase 0), so this does **not** explain the symptom. **NEEDS_REFERENCE_PORT**
   only for exact parity.
8. **Window/native-image init: the core layout math matches, so it is not the blocker.**
   Here: `Nx2NativeObjects.cs:56-85` `Layout` and `INx2GraphicsSystemSession.cs:186-203`
   (gsv 1510), `:55-64` (111), `:73-80` (423), `:206-232` (429) reproduce Eglantine's
   `Nx2CalculateImageLayout` / handlers (`nx2_gsv.h:1561-1579, 4029-4043, 4079-4088,
   4119-4124, 4138-4152`) with identical width/height/pitch/block/size/alignment formulas;
   presents climb past 50k. Residual divergences: the gsv 428/426 image descriptor word
   placement here (`INx2GraphicsSystemSession.cs:265-330`) vs Eglantine row 126, and the CPU
   `Unswizzle`+`PresentSnapshot` composite (`INx2Vi2Session.cs:770-961`) vs Eglantine's GPU
   composite with native GOB tiling — these are downstream of "no draws" (frames are all zero
   because nothing renders), so they cannot explain it. Exact descriptor word placement
   could not be determined from the notes alone. **NEEDS_REFERENCE_PORT** (low priority).

Ranked by directness to "no macro emits PCAS / no dispatch / no draws": #1 and #2 are the
only candidates that break the chain before PCAS/dispatch; #3-#4 could prevent the macro from
emitting PCAS at all; #5-#7 are GPFIFO-stability gaps already observed not to fire in this
title; #8 is healthy. Stop for a human decision on which divergence to fix first.

## Phase 1g — cover all PCAS/QMD compute methods (Task 1g)

Wired observe-only callbacks for all three Kepler compute PCAS/QMD signal methods
(0xAE `SendPcasB`, 0xAF `SendSignalingPcasB`, 0xB0 `SendPcasC` — method index =
byte offset / 4; QMD address still read from register 0xAD `SendPcasA` for all
three). All three count into `Nx2PcasTrace` before any skip/return; dispatch
behaviour and the existing 0xAF path are unchanged. Method→field offsets verified
with `Marshal.OffsetOf` (0x2B4/0x2B8/0x2BC/0x2C0).

Re-ran the 90 s direct Fast Fusion capture with
`NX2_PATCH="" NX2_PCAS_TRACE=1 NX2_QMD_TRACE=1 NX2_METHOD_LOG=1` (Fast Fusion
profile) into `.nx2-logs/run-20261119-195906/ocarina.log` (rc=124 expected
timeout, 451 lines). New binary confirmed live: the `NX2 progress` line shows the
new `mAE=… mAF=… mB0=… act=…` Summary format.

- `NX2 progress t=60s … mme=73/4159/0+73 pcas=[calls=0 zero=0 nonzero=0 mme64=0 mAE=0 mAF=0 mB0=0 act=0] … draws=0`.
- `NX2PCAS` lines: **0**; `NX2QMD` lines: **0**.
- The `emitted=[…]` / `3d` histograms contain no `0xAE`/`0xAF`/`0xB0` (nor `0xAD`).
- `draws` unchanged (still 0); `submits=4`, `presents=14600`.

PCAS_COVERAGE=NONE (mAE=0 mAF=0 mB0=0, act=0) — no writes to any of 0xAE/0xAF/0xB0.
`NX2QMD` lines: **no** (0). `draws`: **unchanged** (0).

Meaning: the earlier "zero PCAS/QMD" reading is **not** a measurement artifact of
wiring the trace to only 0xAF — with all three methods observed, the guest still
writes none of them. This confirms Phase 1f's `MACRO_QMD=MACROS_RUN_NO_PCAS`: the
chain breaks before PCAS (macro emission / compute-class routing), so the blocker
is upstream. Per the brief's gate, stop for a human decision before further QMD
work; do not port QMD ring/release logic on this evidence.

## Phase 1h — NUP package completeness (Task 1h)

Ran `tools/nx2/Nx2DumpToc` on
`/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` to rule out the hypothesis that
the black screen is caused by booting an update-only NUP with no base content.
The tool reuses `NupTocInspector.DescribePackages` (package decision) and
`NupArchive`'s `OpenExeFs`/`OpenRomFs` (entry views); no ROM bytes were extracted.

- TOC: `packages=2; base=1; standalone=0; selected=base; selectedStandalone=False`;
  `isStandaloneUpdate=False`. Package 0 is a base (`kind=0`, ExeFS dir 1, RomFS dir 2);
  package 1 is a **non-standalone** update (`kind=1`, standalone flag 0, version 262144).
  `SelectProgramPackage` picks the base because a base package wins over any update.
- ExeFS modules (6): `main` 4,462,830 B; `main.npdm` 1,828 B; `rtld` 10,477 B;
  `sdk` 7,317,157 B; `subsdk0` 6,353,347 B; `subsdk1` 40,921,256 B.
- RomFS: 3,063 files, 3,542,187,320 bytes (~3.30 GiB) in the selected base package.

NUP_COMPLETE=YES

Meaning: the NUP is a complete base package with a full RomFS asset set — not an
update-only/stub package. This is consistent with the run log's `Mounted NUP base
package` line (a base package was found and selected). The observed
`fs=[bytes=0MiB reads=0 opens=5]` / only `/save.bin` opened is therefore **not** a
packaging problem: the guest simply never reads the assets. The missing-base
hypothesis is ruled out, so per the brief's gate the next step is NOT to obtain a
different NUP — continue with the guest-side blocker (Phases 1a–1g).

## TL;DR from a fresh clone

```bash
git clone https://github.com/kleinergoo7/Kenji-NX.git
cd Kenji-NX
git switch kenji-nx2-oot-bringup
dotnet build Ryujinx.slnx -c Debug                 # needs .NET SDK 11.0.100 (global.json)
tools/nx2/run-oot.sh /path/to/ocarina.nup 240      # ROM is NOT in the repository
grep -a "NX2 progress" .nx2-logs/run-*/ocarina.log | tail -1
```

Expected today: `submits=12 presents=<climbing> draws=0` and a black window.
Success is `draws > 0` **and** a screenshot that is not black — the log alone cannot
prove pixels.

## Repository rules

- Work only on `kenji-nx2-oot-bringup`. There is no `master` on the remote.
- `archive/nx2-oot-pre-kenji-rebase` is the pre-rebase snapshot; do not work on it.
- Commit as `kleinergoo7`, never `Reng`:
  ```bash
  git config user.name kleinergoo7
  git config user.email 348551191+kleinergoo7@users.noreply.github.com
  ```
- Keep run artifacts out of git. `*.log` is ignored; `.nx2-logs/` is the scratch area.
- The branch is rebased on Starforgejo Kenji-NX `master`; keep Switch 2 changes in
  small topic commits instead of importing all of Ryubing.

## Build and tests

```bash
dotnet build Ryujinx.slnx -c Debug                                   # expected: 0 errors
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj \
  --filter "FullyQualifiedName~Nx2SwizzleTests"                      # expected: 3 passed
```

The toolchain is .NET 11 (`global.json` pins 11.0.100). A net10 SDK cannot build this
tree.

## Run configuration

`tools/nx2/run-oot.sh` wraps the validated command and writes everything to
`.nx2-logs/run-<timestamp>/`:

```
dotnet src/Ryujinx/bin/Debug/net11.0/Ryujinx.dll \
  -r <run>/data <game.nup> \
  --dram-size MemoryConfiguration12GiB \
  --memory-manager-mode SoftwarePageTable \
  --ignore-missing-services
```

- `NX2_PATCH="0xD3CF86C:0x2A1F03E0,0xD3CF8A4:0x2A1F03E0,0xD3CF8DC:0x2A1F03E0"` is
  applied by the script. Without it the movie module aborts with `2243-0128`
  (result `0x000100f3`) before the first `ReadBuffer`.
- The validated runs used a prepared profile dir; the script creates a fresh `-r` dir
  instead. That should still boot because a Switch 2 `.nup` is self-contained and the
  loader has no keys gate, but this exact path was **not re-verified** during the
  documented runs. If a fresh profile fails early, point `-r` at a profile that
  previously booted.
- No `prod.keys` or firmware is needed for a Switch 2 `.nup`; `RYU-0001` must only fire
  for Switch 1 titles.
- The game takes ~1.5–2 minutes to reach the first movie session; give it 240 s.

## Current state (evidence-based)

| Area | State |
|---|---|
| Loader / NPDM / compatibility profile | Works for `.nup` (ACID signature 0x300→0x200, Switch 2 profile) |
| Applet + service bring-up (`appletOE`, `ISelfController`, `ICommonStateGetter`, font, `IApplicationProxy`, graphics-system) | Works; main thread cycles the SDK service loop, Owl code runs |
| Audio (`aud:u`, `AudioDevice` cmds 15–20) | Works; all aborts from audio are gone |
| NvDrv / NvHostAsGpu NX2 ioctls | Registered (VA regions, syncpoint shim, Ampere chars); submissions still frozen |
| Movie pipeline (decoder open, streams, poll, EOS) | Runs: 1–4 sessions per run, `presents` climbs (hundreds to thousands) |
| Real video decode | **Missing** — `Av1Decoder.Decode` is a no-op; frames are black |
| 3D draws | **Missing** — `NX2 progress` shows `submits=12 draws=0` from boot, before movies |

Frame dumps (`NX2_FRAME_DUMP=1`, written as `frame-*.rgba` under `.nx2-logs/`) are
100% zero bytes, which is the concrete proof that nothing is rendering yet.

## The active blocker: the movie decoder is fake

What the guest does (found by tracing `NX2_MISSING_LOG=1` and the movie logs):

- IPC `cmd 201` (TIPC `0xD9`) is the decoder pool registration. Request:
  `req=[0496B000 00000000 00000003 00000003 00000003 00000001]` — first word is a
  **guest address** (`0x496B000`), followed by counts. It was auto-stubbed; a real
  handler now lives at `INx2VideoDecSession.cs:122` and dumps the buffer under
  `NX2_DEC_MEM_DUMP=1`.
- `cmd 314` (`SubmitFrame`, `INx2VideoDecSession.cs:219`) carries only a stream handle
  plus zeros — **no bitstream pointer**.
- `cmd 213` (poll, `INx2VideoDecSession.cs:252`) returns a synthetic 40-byte descriptor
  so the movie advances.
- The movie file is read through FS into guest RAM (≈3.1 MB mapped), so the encoded
  bytes are in guest memory, not in IPC payloads.
- Decoded frames must be written into the presented image's backing guest memory
  because `INx2Vi2Session.Composite` (`:770`) reads it and un-swizzles block-linear
  RGBA8 before `PresentSnapshot` (`:875`).

Implemented so far:

- `Nx2NativeObjects.Swizzle` (`Nx2NativeObjects.cs:148`) — exact inverse of the
  existing `Unswizzle`, unit-tested by `src/Ryujinx.Tests/HLE/Nx2SwizzleTests.cs`
  (round trip on three layouts).
- Real FFmpeg AV1 wrapper exists and is functional:
  `src/Ryujinx.Graphics.Nvdec.FFmpeg/Av1/Decoder.cs` +
  `FFmpegContext.DecodeFrame` (`FFmpegContext.cs:119`).

Still unverified: whether `0x496B000` holds raw AV1 OBU bytes or descriptor records.
That single answer decides the next step, and it requires one run with the ROM.

## Next steps, in order

1. **Capture the cmd 201 buffer** (this is the immediate next action):
   ```bash
   NX2_DEC_MEM_DUMP=1 NX2_MISSING_LOG=1 tools/nx2/run-oot.sh /path/to/ocarina.nup 200
   grep -a "NX2 DEC_MEM\|NX2 missing INx2VideoDecoderSession" .nx2-logs/run-*/ocarina.log
   ```
   - Non-zero bytes starting with an AV1 OBU header (e.g. `0A`/`12` sequence header)
     → raw bitstream; wire the FFmpeg decoder.
   - Pointer/count-looking words → follow them (add a second env-gated dump), then feed
     the real payload to the decoder.
2. **Wire decode**: replace the no-op `src/Ryujinx.Graphics.Nvdec/Av1Decoder.cs`
   (or call `Ryujinx.Graphics.Nvdec.FFmpeg.Av1.Decoder` directly from the video-dec
   session) using `DecodeFrame(outSurface, bitstream)`; keep the existing fake poll
   until a real frame is produced.
3. **Convert YUV→RGBA** from the decoded `Surface` planes (BT.709 for 1920×1080), then
   `Nx2NativeObjects.Swizzle` into
   `Nx2NativeObjects.Memories[image.Memory].Address + (imageId - baseId) * image.Size`
   (the mapping `INx2Vi2Session.Composite` uses).
4. **Verify** with `NX2_FRAME_DUMP=1`: a frame dump with non-zero bytes, then
   `xdotool search --name Ryujinx` + `import -window <id> shot.png` for visible proof.
5. If the movie path still cannot produce pixels, instrument the independent blocker
   (`submits=12 draws=0` from boot) — it predates the movies and also blocks the title
   render. Start from `NX2 progress`, `INx2GpuSession` call counts, and
   `NX2_THREAD_DUMP=1` thread/wait dumps.

## Key files

- `src/Ryujinx.HLE/.../SystemAppletProxy/INx2VideoDecSession.cs` — decoder IPC
  (cmd 201 pool probe, 311 streams, 314 submit, 213 poll, EOS)
- `src/Ryujinx.HLE/.../SystemAppletProxy/INx2Vi2Session.cs` — composite/present,
  frame dumps, progress line
- `src/Ryujinx.HLE/.../SystemAppletProxy/Nx2NativeObjects.cs` — image/memory registry,
  `Layout`, `Unswizzle`, `Swizzle`
- `src/Ryujinx.HLE/.../SystemAppletProxy/INx2GraphicsSystemSession.cs` — gsv image and
  memory creation (cmds 111/114/423/429)
- `src/Ryujinx.Graphics.Nvdec/Av1Decoder.cs` — no-op stub to replace
- `src/Ryujinx.Graphics.Nvdec.FFmpeg/Av1/Decoder.cs`, `FFmpegContext.cs` — working AV1
- `src/Ryujinx.HLE/HOS/Services/Nv/NvDrvServices/NvHostAsGpu/NvHostAsGpuDeviceFile.cs`
  — NX2 ioctls
- `src/Ryujinx.Horizon/Sdk/Audio/Detail/AudioDevice.cs`, `Audio/AudioUserIpcServer.cs`
  — audio fixes
- `src/Ryujinx/Program.cs`, `src/Ryujinx/AppHost.cs` — keys gate removal, Switch 2
  profile selection

## Debug environment variables (all default off)

| Variable | Effect |
|---|---|
| `NX2_FRAME_DUMP=1` | per-composite stats + `frame-*.rgba` dumps under `.nx2-logs/` |
| `NX2_DEC_MEM_DUMP=1` | dump the guest buffer named by decoder cmd 201 |
| `NX2_MISSING_LOG=1` | first two request words of every auto-stubbed IPC command |
| `NX2_MOVIE_TRACE=1` | movie decoder traffic (streams, submits, polls, EOS) |
| `NX2_MOVIE_EOS_AFTER=<n>` | force end-of-stream after n submits (shortens runs) |
| `NX2_MOVIE_STREAM_X26=1` | stream id handling used by the validated runs |
| `NX2_THREAD_DUMP=1` | thread/wait snapshot (`NX2_THREAD ...`) |
| `NX2_PC_SAMPLE=1` | sampled guest PCs of the main thread |
| `NX2_TIPC_DUMP=1` | TIPC request/response words (verbose; capped) |
| `NX2_TIPC_LOG=1` | service-manager lookups |
| `NX2_LOG_DIR` / `NX2_CAP` | probe-file subdirectory under `.nx2-logs/` / per-file line cap |

## Gotchas

- **PPTC cache is stale-tolerant but patch-sensitive**: startup patches must run before
  the process starts, and a cached translation can hide a patch change. Fresh runs use
  a fresh `-r` dir, so this is only a problem with reused profiles.
- **TIPC command ids are the raw type minus 0x10** (`0x16`→6, `0xE5`→213, `0x147`→311,
  `0x14A`→314). If a handler "never runs", check the id.
- **Guest bases** (OoT): `Owl.nss=0x8520000`, movie/subsdk0=0xD3C0000,
  `nnSdk=0xD4A0000`, `nnrtld=0x8500000`. The codec client is at `0xD7D0000` in
  `sdk.text.bin`, not `0xD4A0000`.
- **Error codes seen**: `0x40299` = audio `NotImplemented`; `0x3F480` = Am
  `InvalidParameters` (boost-mode, fixed); `0x000100f3` = movie abort without
  `NX2_PATCH`; `0x00025a0b` = session closed (fixed by registering `aud:u`).
- **A green build is not a render.** Always check `NX2 progress` for `draws=`,
  and `NX2_FRAME_DUMP` non-zero bytes before claiming progress.

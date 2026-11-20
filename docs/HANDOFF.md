# HANDOFF — NX2 rendering bring-up (for a fresh session)

Last updated: 2026-11-20. Read this first; it supersedes all earlier status docs.

## TL;DR

Goal: get at least one Switch 2 title rendering in this C# Ryujinx fork. Fast Fusion is the current
test case; OoT is the original objective. We built a **working C++ oracle (Lichii)** that renders the
same NUP correctly, and are porting its GPU trace replayer into C# to isolate GPU correctness from
the guest-boot problem. **The GO/NO-GO gate PASSED**: our C# pipeline replayed the synthetic 4-frame
sample to non-black frames matching the oracle's brightness within 0.0001. HEAD is `98c5d8eb1`
(committed, NOT yet pushed). Next: review+push Task 2, then Task 3 (snapshot restore).

## Non-negotiable rules

- Commits authored `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>` (git config already set).
- Push only to `origin` = github.com/kleinergoo7/Kenji-NX. NEVER to `kenji-nx` (Forgejo — auth fails).
- Exactly two local branches: `kenji-nx2-oot-bringup` (work) and
  `archive/nx2-oot-pre-kenji-rebase` (snapshot, READ-ONLY, at `a1bd2f376`).
- Never commit `.nup`, `.log`, `.rgba`, `.png`, `.bin`, `.tga`, `.lxtrace`, or `.nx2-logs/` scratch.
- All agents must use model `opencode-go/hy4-flash-free` (human ruling; paid models have no balance).
- Build `dotnet build Ryujinx.slnx -c Debug` (SDK 11.0.100); smoke
  `dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter "FullyQualifiedName~Nx2SwizzleTests"` → 3 passed.
- Fast Fusion run profile: `NX2_PATCH=""` + `--dram-size MemoryConfiguration12GiB
  --memory-manager-mode SoftwarePageTable --ignore-missing-services`.

## Repository layout / current state

- Repo: `/home/reng/projects/11-09-switch2/Kenji-NX`
- HEAD: `98c5d8eb1` (`nx2: replay synthetic lxtrace sample headlessly`), on
  `kenji-nx2-oot-bringup`, **1 commit ahead of origin** (not pushed). Untracked scratch: `.nx2-logs/`,
  `tools/nx2/__pycache__/`, and this `docs/HANDOFF.md` (commit it if you want it in git).
- Recent commits (newest first):
  `98c5d8eb1` headless sample replay (gate PASSED), `0f89ffc6e` reader Config guard,
  `0f4288ea0` streaming LxTraceReader, `e695370a5` pixel-order doc,
  `a8a484ec9` sample traces doc, `70be7a223` replayer plan, `3043db794` mining verdict=NONE.
- Authoritative status docs (read in this order):
  1. `docs/superpowers/specs/2026-11-20-lxtrace-replayer-design.md` (current design)
  2. `docs/superpowers/plans/2026-11-20-lxtrace-replayer-plan.md` (current plan, Tasks 0-5)
  3. `docs/nx2/ORACLE.md` (oracle setup, exact commands, golden frame hashes, pixel-order caveat)
  4. `docs/nx2/LXTRACE.md` (trace format, miner commands, honest negative result)
  5. `.superpowers/sdd/2026-11-20-lxtrace-replayer-plan/progress.md` (execution ledger — task state)
- Older plans/specs are historical; do not follow them. `NX2_IMPLEMENTATION_STATUS.md` is a stale
  snapshot from a different branch — ignore it.

## In flight / next action (IMPORTANT)

Task 2 of `2026-11-20-lxtrace-replayer-plan.md` just completed on subagent
`ses_e0979f762ffeSq53QEOGy0dV4O` and its commit `98c5d8eb1` is **not pushed and not reviewed** yet.
Next actions on resume:
1. Review commit `98c5d8eb1` (task-scoped spec+quality reviewer, model `hy4-flash-free`); fix if needed.
2. Push it to `origin`.
3. Proceed to Task 3 (global snapshot restore: State kinds 9 syncpoints, 10 semaphore regions,
   8 QMD states) from the replayer plan.

**Task 2 gate PASSED** — synthetic sample replay produced 4 non-black frames: means
0.624517 / 0.500000 / 0.624517 / 0.621528 vs oracle 0.624571 / 0.5 / 0.624591 / 0.621467.
GPU-clear frames are pixel-exact; CPU-tiled presents differ in swizzle (hash mismatch) and there is
a ~10-pixel boundary difference on clears. Hash function verified against oracle pixels. Caveats:
sample-specific shortcuts (single map/channel, hardcoded block height), new Gpu→Vulkan project
reference, raw `.rgba` output (not PNG), and no snapshot restore (by design for this milestone).

## The oracle (working reference — the most valuable asset)

- Lichii (current upstream `nx2` branch, cloned from github.com/zortos293/Lichii), built locally:
  `/tmp/opencode/lichii/build/bin/eden-cli` (49 MB). Source at `/tmp/opencode/lichii`.
- It renders the Fast Fusion NUP correctly: title screen (FAST FUSION logo, ships, flames).
- Run command (Xvfb has NO present queue — use the real `DISPLAY=:0.0`):
  ```
  DISPLAY=:0.0 XDG_CONFIG_HOME=/tmp/opencode/oracle-run/config \
  XDG_DATA_HOME=/tmp/opencode/oracle-run/data XDG_CACHE_HOME=/tmp/opencode/oracle-run/cache \
  EDEN_NX2_AUTO_STUB=1 EDEN_NX2_FRAME_CAPTURE=<dir> \
  /tmp/opencode/lichii/build/bin/eden-cli -g /home/reng/Downloads/Fast_Fusion_Update_1.4.nup
  ```
  (rc=124 from `timeout -s INT` is expected.)
- Useful oracle trace flags: `EDEN_NX2_IPC_TRACE=1`, `EDEN_NX2_SVC_TRACE=1`,
  `EDEN_NX2_SHADER_DUMP=<dir>`, `EDEN_NX2_FRAME_CAPTURE=<dir>`, `EDEN_NX2_GPU_TRACE=<dir>`.
- A/B harness: `tools/nx2/oracle-ab.sh <secs>` runs both emulators and prints mean brightness +
  oracle shader/pipeline counts. Baseline: oracle 0.826 / 30 shaders / 15 pipelines; ours 0.0 / 0 / 0.

## Trace artifacts (all in /tmp — NEVER commit)

- Real Fast Fusion trace: `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace` (~672 MB).
  Recorded with `EDEN_NX2_GPU_TRACE=<dir>` + touching `record.now`.
- Real expected frames: `/tmp/opencode/oracle-replay/frames.json` + `frame-0000..4.png`
  (1280x720, mean ~0.826). Replay command: `eden-cli --replay <trace> --out <dir> --frames 5`.
- Synthetic sample trace: `/tmp/opencode/sample-trace/sample.lxtrace` (183 KB, 4 clear frames).
  Created with `eden-cli --gpu-trace-sample <path>`.
- Sample expected frames: `/tmp/opencode/sample-replay/frames.json` + `frame-0000..3.png`
  (256x144, means 0.624571 / 0.500000 / 0.624591 / 0.621467).
- Frame hashes are XXH3-128 over the **post R/B-swap RGBA buffer** (`replay.cpp:543-545`) — a C#
  replayer must match that channel order (documented in ORACLE.md).

## Trace format (verified byte-for-byte)

- 64-byte header: magic `LXGTRACE`, version 2, header_size 64; zstd stream from byte 64.
- Record header 16 bytes `<IIQ>`: type, reserved, size; then payload.
- Types: 1 Config(JSON), 2 AddressSpace, 3 GmmuMap, 4 GmmuUnmap, 5 SmmuMap, 6 SmmuUnmap,
  7 Channel, 8 State, 9 Blob, 10 Memory, 11 SnapshotEnd, 12 Submit, 13 Present, 14 Resume, 15 End.
- Submit = 32-byte `<iiIIQII>` (channel, entry_count, prefetch_count, offset_count, word_count:u64,
  deferred_count, padding), then arrays in order: u64 entries[], u32 prefetch[], u32 offsets[],
  u32 words[], u32 deferred[]. Measured `sizeof(SubmitRecord)=32` (four u32s end at byte 16,
  already 8-aligned, no implicit pad). The earlier 28-byte parse was WRONG (fixed in `bb36b48e8`).
- Present header `<QII>` time_ns/layer_count/fence_count; layer 72 bytes, fence 8 bytes.
- Real trace census: 1 Config, 1 AddressSpace, 1171 GmmuMap (all sparse), 1185 SmmuMap, 1 Channel,
  10 State (kinds 1-10), ~380k Blob, ~409k Memory, 360 Submit, 180 Present, 1 End. No Resume.

## What we learned (do NOT re-derive)

- "Zero DrawArrays" is a BOGUS metric — both Lichii and our fork log zero `DrawArrays`; NX2 renders
  via MME macros/QMD, not classic draw calls. Five plans chased this false symptom.
- Lichii lowers 30 shaders / builds 15 pipelines; our fork lowers ZERO (no shader bind reaches the cache).
- Our MME compute macros execute an all-zero code bank because methods 0x45-0x48 route to the 3D
  `_macroCode` bank, but compute read `_computeMacroCode`. Fixed in `c2ae0b341` (share code array,
  keep `_computeMacros[index]` state separate — sharing state caused a prior 14→12 collision).
  Result: `SUCCESS_EMITS` (compute macros emit registers) but frames still black.
- Ampere SM86 lowering exists on the archive branch and was re-introduced on HEAD in `f3659d59`
  (9 files under `src/Ryujinx.Graphics.Shader/Decoders/Ampere/`, integrated in ShaderCache.cs gated
  by IsAmpere, self-test passes). Unit-verified but never exercised end-to-end (no shader delivered).
- Present fence/pacing divergence exists (Lichii waits on current fence `(1,N)` at 60 Hz; ours
  parses `(0,0)` and paces only OoT at ~300/s) but is NOT the cause of black frames.
- Mining the oracle trace for a missing GPU method produced `DIVERGENCE=NONE` — the comparison was a
  category error (oracle all-words vs ours dispatched-methods); no missing method is provable.

## Replayer sample milestone (Task 2) — the key proof

Our C# pipeline, with NO snapshot restore, replayed the synthetic sample to 4 non-black frames
whose brightness matches the oracle within 0.0001. This proves the replayer approach is viable:
feed trace records → our GPU APIs → real frames. That is why Tasks 3-5 (state restore) are now
justified. The file is `src/Ryujinx.Graphics.Gpu/GpuTrace/LxTraceReplayer.cs` with
`LxTraceReplayerTests.cs`. Note the new Gpu→Vulkan project reference and raw `.rgba` output.

## Tools committed so far (reusable)

- `tools/nx2/oracle-ab.sh` — A/B harness (Task 0 of replayer plan).
- `tools/nx2/lxtrace_miner.py` + `lxtrace_miner_test.py` — streaming Python miner; `--full` for
  complete histogram; known-values check: first Submit entries=14, words=4979, words[0]=0x2001c00d.
- `tools/nx2/compare_nx2_methods.py` — comparison tool (now always emits DIVERGENCE=NONE with caveats).
- `tools/nx2/dump-guest-code.sh` + `tools/nx2/Nx2DumpGuestCode/` — extract guest code at a VA from a NUP.
- `tools/nx2/Nx2DumpToc/` — NUP completeness check (NUP_COMPLETE=YES).
- `src/Ryujinx.Graphics.Gpu/GpuTrace/LxTraceReader.cs` — C# streaming reader (Task 1, tests pass).
- `src/Ryujinx.Graphics.Gpu/GpuTrace/LxTraceReplayer.cs` — headless replayer (Task 2, sample gate PASSED).

## Task 3-5 (remaining — Task 2 gate PASSED, so unblocked)

Task 3: restore global snapshot state (State kinds 9 syncpoints, 10 semaphore regions, 8 QMD states)
via narrow internal restore methods. Task 4: per-channel state (kinds 1 Pusher, 2 Maxwell3D,
3 KeplerCompute, 7 MacroContext 263 KB code, shadow/root tables). Task 5: sparse GMMU/SMMU map
parity + replay real trace 5 frames vs oracle. Details in the plan doc. If the real-trace replay
produces a non-black frame from our pipeline, that isolates the GPU as working and proves the
remaining fault is guest-side (the MME macro → shader-bind chain that no trace method analysis
could pin down).

## Build/test notes

- `dotnet build Ryujinx.slnx -c Debug` — 3 pre-existing warnings, 0 errors is normal.
- ZstdSharp 0.8.8.1 uses `DecompressionStream` (not `DecompressStream`) — already used in NupArchive.
- `Ryujinx.Graphics.Gpu.csproj` has `InternalsVisibleTo("Ryujinx.Tests")`.
- Python deps (zstandard, pytest) were installed manually.
- `.nx2-logs/` accumulates run output; commit by explicit path only (`git add -A` would stage it).

## Git hygiene check (verify on resume)

```
git status -sb                      # expect: on kenji-nx2-oot-bringup, clean vs origin + untracked scratch
git log -3 --format='%h %an <%ae> %s'  # author must be kleinergoo7
git branch --format='%(refname:short)' # exactly the two branches
```

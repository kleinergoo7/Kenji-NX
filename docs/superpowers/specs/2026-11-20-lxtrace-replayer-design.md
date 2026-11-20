# C# LXTrace replayer design

Date: 2026-11-20

## Intent and why

Get a deterministic, known-good GPU workload — a Lichii `.lxtrace` — to render through our C#
Ryujinx GPU layer, so we can isolate GPU correctness from the guest boot problem that has blocked
every prior plan. The oracle renders Fast Fusion at mean 0.826; our fork is black. Five plans and a
trace-mining pass produced no identifiable emulator-side divergence. A replayer gives a fixed input
whose expected output we already have (`/tmp/opencode/oracle-replay/frames.json`).

## Goals

1. Parse `.lxtrace` in C# (header, zstd stream, typed records).
2. Bring up a headless GpuContext, feed stream records (Memory/Submit/Present) through our GPU APIs.
3. Prove the approach on the synthetic 4-frame `--gpu-trace-sample` trace (no snapshot restore).
4. Stage snapshot restore (globals → per-channel state → macro RAM/shadow state → sparse maps).
5. Replay the real 704 MB Fast Fusion trace, 5 frames, compare to oracle frames.

## Non-goals

- Game boot, HLE services, audio, keys, firmware, or playability.
- Bit-exact cross-driver matching (compare on the same Vulkan device class; precision differs).
- Porting Lichii's C++ classes wholesale — only the deterministic restore data paths.

## Constraints and available pieces

- Format (from `trace_file.h` + the working Python miner): 64-byte `LXGTRACE` v2 header, zstd from
  byte 64, 16-byte record headers `<IIQ`, typed payloads. Types 1 Config, 2 AddressSpace,
  3 GmmuMap, 5 SmmuMap, 7 Channel, 8 State, 9 Blob, 10 Memory, 11 SnapshotEnd, 12 Submit,
  13 Present, 15 End. Submit header is 32 bytes (`<iiIIQII>` — measured; no implicit pad).
- Libraries already referenced: `ZstdSharp` (`Directory.Packages.props:53`), `XXHash128` +
  `Hash128` (`src/Ryujinx.Common/XXHash128.cs`, `Hash128.cs`) — exactly Lichii's hash.
- C# GPU APIs: `GpuContext.CreateChannel/CreateMemoryManager/RegisterProcess`,
  `GpuChannel.PushEntries`/`PushHostCommandBuffer`/`BindMemory`, `GPFifoDevice.SignalNewEntries`,
  `MemoryManager.Write/Map`, `Window.EnqueueFrameThreadSafe`/`SignalFrameReady`,
  `SynchronizationManager.IncrementSyncpoint`, `IRenderer.Screenshot`.
- No `InitializeForGpuReplay` equivalent and no bulk engine-state restore — this is the hard part.
- Ampere SM86 lowering is now restored on this branch (Task 2 of the mining-era plan), so a
  delivered shader can be lowered.

## Approaches

1. **Full faithful replayer** (chosen): staged snapshot restore + stream feed; highest value, large.
2. Mining only (tried): honest negative — no identifiable method divergence; cannot isolate GPU.
3. Guest-side fixes (tried extensively): no concrete blocker found.

## Review focus

- Never load the 704 MB trace into memory — stream it.
- Correct Submit record header (32 bytes) and array order (entries, prefetch, offsets, words, deferred).
- Apply trace Memory writes before the next submit's prefetch (the trace carries words *as submitted*
  because the guest reuses its pushbuffer ring).
- A present must drain GPU work before compositing, or frames will be stale.
- Snapshot restore must not corrupt Maxwell (non-NX2) behavior — gate by the trace Config's `switch2`.

## Verification

- Milestone 1: replay `--gpu-trace-sample` → 4 PNG frames with non-black clears and hashes matching
  C++ `eden-cli --replay <sample>`.
- Milestone 2: replay the real trace, 5 frames → compare to `/tmp/opencode/oracle-replay/frames.json`
  (mean ~0.826). A non-black frame from our pipeline is the goal; exact hash match is stretch.

## Order

Task 0 (generate sample + expected outputs) → Task 1 (C# reader) → Task 2 (headless bring-up + stream
feed, sample milestone) → Task 3 (globals restore) → Task 4 (per-channel + macro/shadow restore) →
Task 5 (sparse map parity + real trace validation).

# LXTrace mining design (replaces the replayer port)

Date: 2026-11-20
Supersedes: `2026-11-19-oracle-driven-nx2-design.md` tasks 1-4 direction (keeps Task 0 harness).

## Why

A 704 MB `.lxtrace` was recorded from the working Lichii oracle and proven to replay to a
correct frame (mean 0.826). The intent was to port a C# replayer. Recon showed that is not bounded:
faithful replay needs bulk restore of Ampere macro RAM, QMD scheduler state, Maxwell3D
shadow/root tables, and 1171 sparse GMMU maps — none exposed by our C# engine classes — and a
replayer cannot prove our backend renders if our backend never enqueues draws.

But the trace is a **ground-truth artifact we already have**. We proved with a 15-line Python
script that zstd decompression + record streaming works and yields the exact command words the
working GPU consumes. **Mining** that trace (reading it, not replaying it) directly answers
"what method/macro/shader-bind are we missing?" in about a day, with no engine-state APIs.

## Goals

1. Build a Python trace miner that streams a `.lxtrace` and produces structured reports:
   method histogram, macro/RAM state, shader-bind addresses, submit/present sequence.
2. Compare the oracle's GPU command stream against what our fork consumes (our `NX2_METHOD_PROBE`
   / `NX2_MACRO_LOG` output), and name the exact first divergence.
3. Apply the single smallest fix that closes that divergence, verified by the A/B harness.

## Non-goals

- No full replay in C# (no snapshot restore, no engine bulk-load APIs).
- No C++ port, no guest-service work, no present-pacing work unless the divergence points there.

## The format (proven by recon + the Python probe)

- 64-byte header `LXGTRACE`, version 2, header_size 64; zstd stream from byte 64.
- 16-byte record header `type:u32, reserved:u32, size:u64`, then payload.
- Record types (from `trace_file.h`): 1 Config(JSON), 2 AddressSpace, 3 GmmuMap, 5 SmmuMap,
  7 Channel, 8 State, 9 Blob, 10 Memory, 11 SnapshotEnd, 12 Submit, 13 Present, 15 End.
- Submit payload: `channel:i32, entry_count:u32, prefetch_count:u32, offset_count:u32,
  word_count:u64, deferred_count:u32, pad:u32`, then `u64 entries[]`, `u32 prefetch[]`,
  `u32 offsets[]`, `u32 words[]`, `u32 deferred[]`.
- The probe decoded a real Submit: `ec=14 wc=4979`, words beginning `0x136e, 0x2001c00d,
  0x80000000, 0x2004c00a, ...` — real Maxwell/NX2 method words with macro start/parameter
  markers (high bits).

## Key insight for mining

NX2 GPFIFO words are (per the recon of `nvhost_gpu.cpp:816-838`): bits 0-39 GPU VA,
bits 42-62 length, bit 63 sync. The `words[]` array in a Submit is the *prefetched command stream*
— the actual 4-byte method/argument pairs the GPU executes. Decoding them (method = word>>2 for
non-incrementing, with macro ranges) yields the exact method histogram of a working frame.

Comparing **that histogram** to our `EmittedHistogram`/`ComputeHistogram` tells us precisely
which methods our GPU never sees — e.g. shader-bind offsets in the 0xE00 range or draw methods
0x585/0x586 — without any replay.

## Verification

- Python miner produces a method histogram from the oracle trace; we compare to our
  `NX2_METHOD_PROBE`/`NX2_DRAW_MACRO_SUMMARY` output from the same 60 s window.
- The A/B harness (`tools/nx2/oracle-ab.sh`) remains the end-to-end verifier:
  ours must reach mean > 0.1 and shaders-lowered > 0.

## Order

Task 0 (harness, done) → Task 1 (miner + oracle histogram) → Task 2 (diff with ours, name divergence) → Task 3 (single fix) → Task 4 (verify with harness).

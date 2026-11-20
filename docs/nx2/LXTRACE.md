# LXTrace mining

## Provenance

The oracle artifact is `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace`
(704,469,931 bytes). It was recorded from a working Lichii NX2 run of program
`040041E0212AE000` and replays to a correct frame (A/B harness mean 0.826,
30 shaders lowered). It is **mined, not replayed**: we read the GPU method stream
the working emulator consumed, rather than restoring engine state.

Config record (decoded by the miner):

```json
{"build":"Lichii 02efb7493a-nx2 ","height":1080,"memory_layout":4,
 "presents":180,"program_id":"040041E0212AE000","switch2":true,"width":1920}
```

The trace is scratch data (704 MB) and is deliberately **not** in git.

## Format

- 64-byte header: magic `LXGTRACE`, `version:u32 = 2`, `header_size:u32 = 64`.
- A zstd stream begins at byte 64. It is decompressed with a streaming reader;
  the file is never loaded into memory.
- Each record: 16-byte header `<IIQ` (`type`, `reserved`, `size`) followed by
  `size` bytes of payload.
- Record types: 1 Config (JSON), 2 AddressSpace, 3 GmmuMap, 5 SmmuMap,
  7 Channel, 8 State, 9 Blob, 10 Memory, 11 SnapshotEnd, 12 Submit,
  13 Present, 15 End.
- Submit payload: 32-byte fixed header `<iiIIQII` = `channel:i32,
  entry_count:u32, prefetch_count:u32, offset_count:u32, word_count:u64,
  deferred_count:u32, padding:u32`. This is the native layout of C++
  `SubmitRecord` (`trace_file.h:148`): `sizeof == 32`, fields at
  channel@0, entry@4, prefetch@8, offset@12, word_count@16, deferred@24,
  padding@28. `offset_count` ends on an 8-byte boundary, so no implicit pad is
  inserted. The writer emits the raw struct bytes and starts the arrays at
  `sizeof(SubmitRecord)`. Then follow
  `u64 entries[entry_count]`, `u32 prefetch[prefetch_count]`,
  `u32 submitted_offsets[offset_count]`, `u32 words[word_count]`,
  `u32 deferred[deferred_count]`.
- `words[]` is the prefetched 4-byte command stream the GPU executes.
  The method index is decoded as `word >> 2`; raw word frequency is also kept
  because macro/inline-data words do not all map cleanly to a plain method.
- State records begin with a `u32` kind discriminator. The oracle has 10
  distinct kinds.

The trace opens with ~786k Blob/Memory/GmmuMap snapshot records before the first
Submit; streaming keeps this cheap (a full 360-submit histogram takes ~2 s).

## Miner

```bash
# details of the Nth Submit (1-based), including first 16 words and decoded methods
python3 tools/nx2/lxtrace_miner.py <trace> --submit 1

# JSON histogram of methods and raw words across all Submits (redirect to a file)
python3 tools/nx2/lxtrace_miner.py <trace> --histogram > /tmp/opencode/oracle-histogram.json

# State record kinds and counts
python3 tools/nx2/lxtrace_miner.py <trace> --states

# flags can be combined; the one-line summary moves to stderr in that case
python3 tools/nx2/lxtrace_miner.py <trace> --submit 1 --histogram --states
```

Histograms are top-40 by count for both `methods` (`word >> 2`) and `raw_words`.
The JSON schema is `{trace, submit_count, word_count_total, methods[], raw_words[]}`
with each method entry `{method, index, count}` and each raw entry `{word, count}`.
This is the artifact Task 2 consumes to diff the oracle stream against our
`NX2_METHOD_PROBE` output.

## Known-values check

```bash
python3 tools/nx2/lxtrace_miner.py \
  /tmp/opencode/oracle-trace/trace-1795130242.lxtrace --submit 1
```

Observed with the corrected 32-byte parse:

```
Submit #1: channel=1 entries=14 prefetch=0 offsets=14 words=4979 deferred=0
  entries[0:16]:  0x3e22f078eec8 0x80000622f078ef04 ...
  offsets[0:16]:  0x0 0xf 0x10 ... 0x131b 0x136e
  words[0:16]:    0x2001c00d 0x80000000 0x2004c00a 0x0 ...
```

`entries=14`, `prefetch=0`, `words=4979` are unchanged. The **corrected first
words** are `0x2001c00d, 0x80000000, 0x2004c00a, 0x0, ...` — the earlier
recorded first word `0x136e` was actually the last `submitted_offsets[]` value
(4974), read as a word because of a 28-byte-header bug.

Sanity check on the corrected arrays:
`entries[0] = 0x3e22f078eec8` decodes as a valid GPFIFO entry — address bits
0-39 = `0x22f078eec8`, length bits 42-62 = 15 words, sync bit 63 = 0.
`offsets[]` is strictly ascending and ends at `0x136e` (4974), just below
`word_count = 4979`. `words[0] = 0x2001c00d` is a macro-marked method/argument
word (`word >> 2 = 0x8007003`).

### Resolved: the old 28-byte parse was wrong

The original Task 1 brief recorded a 28-byte header and the first word `0x136e`.
Both were artefacts of a misread: the 4 u32 fields before `word_count` end at
byte 16, which is already 8-byte aligned, so the compiler inserts **no** padding
there. Measuring `SubmitRecord` with g++ gives `sizeof=32`, `word_count@16`,
`deferred@24`, `padding@28`; the recorder writes those raw 32 bytes and the
replayer starts arrays at `sizeof(SubmitRecord)`. The miner and test now pin
this 32-byte layout. The C++ struct wins over the previously recorded values.

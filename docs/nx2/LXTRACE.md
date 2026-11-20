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

## Method-stream diff (Task 2)

### Inputs used

- **Oracle:** a full (untruncated) histogram re-mined from the same
  `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace` with the miner's own
  `parse_submit`/`decode_method`, saved to `/tmp/opencode/oracle-histogram-full.json`
  (360 submits, 896,220 words, 1,700 distinct methods / 2,272 distinct raw words).
  The top-40 `/tmp/opencode/oracle-histogram.json` from Task 1 is **insufficient**:
  it omits the draw words, inline data 0x6D and UBO 0x8E4 entirely.
- **Ours:** reused `.nx2-logs/run-20261119-224121-task2-drawmacro-DECISIVE/ocarina.log`
  (60 s Fast Fusion, `NX2_PATCH="" NX2_DRAW_MACRO_SUMMARY=1 NX2_MME_ENGINE=1
  NX2_MACRO_LOG=1`, 15165 presents). No more recent log has `NX2_METHOD_PROBE`,
  and that probe dumps to a separate `/tmp/nx2-methodprobe.log`, not to
  `ocarina.log`; re-running would not change the `emitted=`/`3d=` histograms
  consumed here. The last progress line is
  `mme=73/4159/0+73 mme3d=59 mmec=14 drawmacro=[macros=73 drawEmitHits=0 drawHashes=0] draws=[total=0 ...]`,
  `3d=total=168 top=...` and `emitted=[0x6D=3328 0x8E4=572 0x44=28 0xD00=28 0xD01=28 0xD02=28 0x8C4=28 ...]`.

### Normalization and its limits

Our fork's method address is `word & 0xFFF` (`CompressedMethod.MethodAddress`,
12-bit), **not** the classic Maxwell `(word >> 2) & 0x7FF`. The oracle's exact
`raw_words[].word` therefore maps directly with `word & 0xFFF`; the comparison
uses that and ignores the lossy `methods[].index = word >> 2` view for presence
decisions. Three limits are reported, not hidden:

1. `word >> 2` drops the low 2 bits: `0x2001c00d` decodes to offset 0xD exactly
   but to 0xC in the index view; draw offsets 0x585/0x586 collapse to 0x584.
   The draw rows below use the exact raw words.
2. The oracle histogram counts **all** command words (methods, inline arguments,
   macro markers); ours counts only method addresses actually dispatched by the
   engine. `oracle_count` and `ours_count` are **not comparable frequencies** —
   only presence/absence is compared.
3. The oracle's macro-emitted methods are generated from macro RAM and are not
   literal words in the stream; their presence is inferred from macro-start
   words (0xE00+) in the oracle and from our `emitted=` histogram.

### Divergence table

`python3 tools/nx2/compare_nx2_methods.py <full-oracle-hist.json> <ours-log>`:

```
method                                         oracle     ours  verdict
draw 0x585 (DrawBegin)                              1        0  ORACLE-ONLY  <-- DRAW
draw 0x586 (DrawEnd)                                1        0  ORACLE-ONLY  <-- DRAW
draw-family 0x584 (word&0xFFF)                      1        0  ORACLE-ONLY  <-- DRAW
shader/pipeline reserved 0xD00                   3780       28  BOTH (counts not comparable)  <-- SHADER
shader/pipeline reserved 0xD01                      0       28  OURS-ONLY  <-- SHADER
shader/pipeline reserved 0xD02                      0       28  OURS-ONLY  <-- SHADER
inline-data 0x6D                                    1     3328  BOTH (counts not comparable)
UBO 0x8E4                                           0      572  OURS-ONLY
```

- **Draw methods are present in the oracle's command stream (exact words
  `0x585`, `0x586`, one each, plus the collapsed `0x584` family) and entirely
  absent from ours** — `drawEmitHits=0`, `draws=0`, and neither the emitted nor
  the 3D histogram contains 0x584/0x585/0x586. This is the first highlighted
  family with a presence gap.
- Inline data 0x6D and UBO 0x8E4 are present on both sides (macro-emitted in
  ours), confirming our macros *do* execute and emit methods — the gap is
  specific to draws, not a general failure to execute macro code.
- The shader/pipeline reserved registers 0xD00-0xD02 are emitted by ours (28
  each), so the shader-bind *methods* are not absent from our stream; the gap
  there is in what our engine does with them, downstream of the draw gap.
  **Assumption:** the recon says Lichii reads native shader addresses from
  pipeline reserved registers but does not name them; mapping them to
  0xD00-0xD02 comes from the Task 2 brief's list of our emitted registers and
  was not independently verified against Lichii source.

### Top 5 oracle-only methods (offset + example argument)

| offset | oracle_count | example argument word | note |
|--------|--------------|-----------------------|------|
| 0x0    | 181081 | `0x0`                     | mostly inline argument data, not a standalone method |
| 0x40   | 58861  | `0x3A400040` (count 48600) | high-bit macro-marked word; collapse of index `0xe900010` |
| 0x1    | 29521  | `0x1`                     | inline argument data |
| 0xE3A  | 27361  | `0x80000E3A`              | macro-start/dispatched register (0xE00+ family) |
| 0x22   | 23401  | `0x22`                    | inline argument data |

The low offsets (0x0, 0x1, 0x22) dominate because the oracle histogram counts
argument words; they are not evidence of missing method handling. 0xE3A is the
most frequent macro-start word and is the meaningful oracle-only method family
among the top five.

### Result

**`DIVERGENCE=MISSING_DRAW_METHOD`** — the oracle's GPU method stream contains
draw methods (0x585/0x586) that our fork's stream never contains: our engine
runs 73 macros and emits 4,159 methods but zero draws (`drawEmitHits=0`). This
selects the single Task 3 fix: make the missing draw method reach our 3D engine.

Consequence noted, not chosen as the divergence: shader binding is also
non-functional (no shaders lowered), but its reserved-register methods
(0xD00-0xD02) are present in our emitted stream, so the *method-coverage* gap
that comes first is the draw method. If a human reviewer judges the shader bind
to be causally upstream, re-evaluate before applying the Task 3 fix.

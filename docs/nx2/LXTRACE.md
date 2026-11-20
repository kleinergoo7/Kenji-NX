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

# complete histogram (no top-40 truncation) - required by the method diff tool
python3 tools/nx2/lxtrace_miner.py <trace> --histogram --full > /tmp/opencode/oracle-histogram-full.json

# State record kinds and counts
python3 tools/nx2/lxtrace_miner.py <trace> --states

# flags can be combined; the one-line summary moves to stderr in that case
python3 tools/nx2/lxtrace_miner.py <trace> --submit 1 --histogram --states
```

Histograms are top-40 by count unless `--full` is given, which emits every entry.
The JSON schema is `{trace, submit_count, word_count_total, full, methods[], raw_words[]}`
with each method entry `{method, index, count}` and each raw entry `{word, count}`.
Task 2 requires the `--full` artifact (`"full": true`); its comparison tool rejects
truncated histograms rather than silently producing a wrong verdict.

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

- **Oracle (reproducible from committed code):** the miner's `--full` flag emits
  the complete histogram; the comparison tool refuses a top-40 one.

  ```bash
  python3 tools/nx2/lxtrace_miner.py \
    /tmp/opencode/oracle-trace/trace-1795130242.lxtrace \
    --histogram --full > /tmp/opencode/oracle-histogram-full.json
  # {"full": true, submit_count: 360, word_count_total: 896220,
  #  methods: 1700 entries, raw_words: 2272 entries}
  ```

  The default `--histogram` output (`"full": false`) omits the draw words,
  inline data 0x6D and UBO 0x8E4; feeding it to the comparison tool now fails
  loudly instead of yielding a silent wrong verdict.
- **Ours:** reused `.nx2-logs/run-20261119-224121-task2-drawmacro-DECISIVE/ocarina.log`
  (60 s Fast Fusion, `NX2_PATCH="" NX2_DRAW_MACRO_SUMMARY=1 NX2_MME_ENGINE=1
  NX2_MACRO_LOG=1`). No more recent log has `NX2_METHOD_PROBE`, and that probe
  dumps to a separate `/tmp/nx2-methodprobe.log`, not to `ocarina.log`;
  re-running would not change the `emitted=`/`3d=` histograms consumed here.
  The last progress line is
  `mme=73/4159/0+73 mme3d=59 mmec=14 drawmacro=[macros=73 drawEmitHits=0 drawHashes=0] draws=[total=0 ...]`,
  `3d=total=168 top=...` and `emitted=[0x6D=3328 0x8E4=572 0x44=28 0xD00=28 0xD01=28 0xD02=28 0x8C4=28 ...]`.

### Normalization and its limits (read before using the table)

Our fork's method address is `word & 0xFFF` (`CompressedMethod.MethodAddress`,
12-bit), **not** the classic Maxwell `(word >> 2) & 0x7FF`. The oracle's exact
`raw_words[].word` therefore maps with `word & 0xFFF`; the lossy
`methods[].index = word >> 2` view is never used for presence decisions.

1. **Low-bit loss.** `word >> 2` drops the low 2 bits: `0x2001c00d` is offset
   0xD exactly but 0xC in the index view; 0x585/0x586 collapse to 0x584.
2. **Mismatched universes (critical).** The oracle histogram counts **every**
   command word — methods, inline arguments, macro markers **and macro-code
   uploads into macro RAM**. Our `emitted=`/`3d=` histograms count only method
   addresses actually **dispatched** by our engine. A word present only in the
   oracle is therefore not evidence of a missing dispatched method, and the
   counts are not frequencies. The table is **evidence only, not presence proof**.
3. **Macro-code-upload caveat for 0x585/0x586.** These exact words occur **once
   each** in the oracle stream (e.g. submit 337), at the tail of a submit next
   to `0xe1def000`/`0x1000f010` — consistent with a macro program being uploaded
   into macro RAM, not with dispatched draws. Our own tool documents that
   macro-emitted methods are not literal stream words, so a genuine oracle draw
   would not appear as a literal `0x585` either. The earlier reading of these
   single words as "draw methods ours lacks" was a category error and is
   retracted.
4. **Time-window caveat.** The oracle trace is 360 submits / 180 presents; this
   ours log ran 60 s to 15165 presents. Both are steady-state Fast Fusion, so
   method *families* can be discussed, but absolute counts and per-submit order
   do not align.
5. **Shader register assumption.** The recon says Lichii reads native shader
   addresses from pipeline reserved registers but does not name them; mapping
   them to 0xD00-0xD02 comes from the Task 2 brief's list of our emitted
   registers and was not independently verified against Lichii source.

### Divergence table

`python3 tools/nx2/compare_nx2_methods.py <full-oracle-hist.json> <ours-log>`:

```
method                                         oracle     ours  verdict
draw 0x585 (DrawBegin)                              1        0  ORACLE-WORD-ONLY (macro upload?)  <-- DRAW
draw 0x586 (DrawEnd)                                1        0  ORACLE-WORD-ONLY (macro upload?)  <-- DRAW
draw-family 0x584 (word&0xFFF)                      1        0  ORACLE-WORD-ONLY (macro upload?)  <-- DRAW
shader/pipeline reserved 0xD00                   3780       28  BOTH (counts not comparable)  <-- SHADER
shader/pipeline reserved 0xD01                      0       28  OURS-ONLY  <-- SHADER
shader/pipeline reserved 0xD02                      0       28  OURS-ONLY  <-- SHADER
inline-data 0x6D                                    1     3328  BOTH (counts not comparable)
UBO 0x8E4                                           0      572  OURS-ONLY
```

Every row is a raw observation under caveat 2 above, not a method-coverage
verdict:

- The oracle's `0x585`/`0x586` are single literal words consistent with a
  macro-code upload (caveat 3), **not** proof of dispatched draw methods ours
  lacks. Nothing in these histograms establishes a missing draw method.
- Inline data 0x6D and UBO 0x8E4 appear on both sides (macro-emitted in ours),
  showing our macros do execute and emit methods.
- The pipeline reserved registers 0xD00-0xD02 are emitted by ours (28 each);
  the shader-bind gap is in what our engine does with them, not in receiving
  the methods — and the register mapping is itself an assumption (caveat 5).

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
most frequent macro-start word and is the most meaningful oracle-only family
among the top five, but under caveat 2 it is still evidence, not a verdict.

### Result

**`DIVERGENCE=NONE`** — this is **not** a method-coverage finding. The two
histograms are not the same universe (caveat 2), and the oracle's single
`0x585`/`0x586` words fit macro-code uploads (caveat 3), so the comparison
cannot establish that our fork is missing any dispatched method. Per the brief,
stop and report the evidence to a human; Task 3 should not be aimed at a
"missing draw method" on this evidence.

**Evidence handed to a human:** our fork runs 73 macros and emits 4,159 method
addresses yet `drawEmitHits=0`, `draws=0`, no shaders lowered; the oracle's
stream has macro-start words (0xE00+ family, e.g. 0xE3A 27,361 occurrences) and
single 0x585/0x586 words consistent with uploaded macro code. The real question
is therefore *what our engine does with the macros and reserved registers it
does receive* (e.g. shader binding from 0xD00-0xD02), or a non-method-coverage
cause — not absence of draw methods from our method stream. Time windows
(caveat 4) and the shader-register assumption (caveat 5) also need a human check
before choosing any Task 3 fix.

# NX2 A/B oracle harness

`tools/nx2/oracle-ab.sh` runs the working Lichii reference build and our fork on the
**same** Fast Fusion NUP for the same number of seconds, then reports captured-frame
mean brightness and the oracle's shader/pipeline totals. It is the measurement
front-end for the oracle-driven NX2 plan; it changes no emulator code.

## Usage

```bash
bash tools/nx2/oracle-ab.sh 60          # 60 s per side (default 60)
NUP=/path/to/game.nup bash tools/nx2/oracle-ab.sh 60
```

Optional environment:

| Variable | Default | Meaning |
|---|---|---|
| `NUP` / `NX2_ROM` | `/home/reng/Downloads/Fast_Fusion_Update_1.4.nup` | title to run |
| `EDEN_CLI` | `/tmp/opencode/lichii/build/bin/eden-cli` | oracle binary |

The NUP is not in the repository. Logs, captures and PNGs stay under
`/tmp/opencode/oracle-ab-<timestamp>-<pid>/`; nothing is written into git.
A missing frame on either side prints `N/A` rather than aborting the run.

## Oracle (Lichii, working reference)

Build: `/tmp/opencode/lichii`, current `nx2` branch, binary at
`/tmp/opencode/lichii/build/bin/eden-cli`.

Exact command the harness runs (fresh XDG dirs each time keep the shader cache cold):

```bash
DISPLAY=:0.0 \
XDG_CONFIG_HOME=<scratch>/cfg XDG_DATA_HOME=<scratch>/data XDG_CACHE_HOME=<scratch>/cache \
EDEN_NX2_AUTO_STUB=1 EDEN_NX2_FRAME_CAPTURE=<scratch>/oracle-frames \
    timeout -s INT 60 /tmp/opencode/lichii/build/bin/eden-cli -g \
    /home/reng/Downloads/Fast_Fusion_Update_1.4.nup
```

Counts come from the oracle's stdout log on shutdown:

- `Shader cache v2 programs: ... N lowered` → shaders lowered
- `Runtime pipelines this session: N ...` → pipelines built

The last (highest-numbered) `nx2-frame-*.tga` is converted and measured:

```bash
convert -size 1280x720 frame.tga oracle.png
identify -format '%[fx:mean]' oracle.png
```

## Ours (Kenji-NX, black frames)

```bash
NX2_PATCH="" NX2_FRAME_DUMP=1 \
    timeout -s INT 240 tools/nx2/run-oot.sh \
    /home/reng/Downloads/Fast_Fusion_Update_1.4.nup 60
```

`NX2_PATCH=""` is required: the default three-instruction movie-module patch in
`run-oot.sh` avoids an early `2243-0128` abort, but this comparison runs unpatched
to match the oracle. The newest `frame-*.rgba` created after the run started
(3840x2160) is converted and measured:

```bash
convert -size 3840x2160 -depth 8 rgba:frame.rgba ours.png
identify -format '%[fx:mean]' ours.png
```

Our fork has no `Shader cache v2 programs:` / `Runtime pipelines this session:`
logging, so its table counts are `0` (no shader lowering is observed).

## Caveats

- **DISPLAY must be `:0.0`.** Xvfb has no present queue, so a headless `xvfb-run`
  display never produces a frame. The harness requires the real X socket
  (`/tmp/.X11-unix/X0`) that is already present in this environment.
- **Relevant trace flags.** Oracle: `EDEN_NX2_AUTO_STUB=1` (stub unknown services
  without editing config), `EDEN_NX2_FRAME_CAPTURE=<dir>` (save a 1280x720 TGA every
  120th present, first at present 1), and optionally
  `EDEN_NX2_FRAME_CAPTURE_EVERY=N` to change that interval. The oracle tree has many
  more `EDEN_NX2_*` probes; none are needed here. Ours: `NX2_FRAME_DUMP=1` and
  `NX2_PATCH=""`; additional `NX2_*` probes are listed in `docs/nx2/CONTINUE_HERE.md`.
- **Cold cache matters.** A warm oracle cache reports `30 from records, 0 lowered`.
  Fresh XDG dirs per run are what make the baseline show `30 lowered`.
- **First oracle frame is black** (captured before the title screen renders), so the
  harness deliberately measures the last captured frame.
- **Our frame dumps always go to `.nx2-logs/` root.** `NX2_LOG_DIR` only redirects
  `Nx2ProbeLog.Append` output, not the `frame-*.rgba` dumps. The harness uses an mtime
  marker so stale frames from earlier runs are never measured. Our first composite
  appears ~29 s into a run, so short smoke runs print `N/A` for ours by design.
- `rc=124` on both sides is the expected `timeout -s INT` result, not a failure.
- ImageMagick 7 prints a `convert is deprecated` warning; it is harmless.

## Baseline (2026-11-19, 60 s, command: `bash tools/nx2/oracle-ab.sh 60`)

| Side | Mean brightness | Shaders lowered | Pipelines built | rc |
|---|---|---|---|---|
| Oracle (Lichii) | 0.826031 | 30 | 15 | 124 |
| Ours (Kenji-NX) | 0 | 0 | 0 | 124 |

Oracle frame used: `nx2-frame-03241.tga` (1280x720); our frame: `frame-early-3-c3-img2-3840x2160.rgba`.
This is the reference point for the rest of the plan: PASS for our fork is
mean > 0.1 and shader-lowered count > 0.

## Lxtrace replay oracle (golden reference frames)

The replayer plan uses Lichii's `--replay` output as the golden reference: a trace is
replayed on the working oracle build and each presented frame's pixels are hashed.
The hash is XXH3-128 over the post-swap RGBA buffer, formatted `{high64:016x}{low64:016x}`
as the `hash` field in `frames.json` (source: `video_core/gpu_trace/replay.cpp`,
`HashString(HashBytes(pixels))`). The PNG bytes are **not** hashed — the PNG is only
for visual and mean-brightness checks. The C# replayer's job is to reproduce these
hashes without a GPU.

- Pixel-format caveat: the oracle swaps the R and B channels of each 4-byte pixel
  before PNG encoding and computes the frame hash **after that swap**
  (`replay.cpp:543-545`). A downstream C# replayer MUST match this post-swap channel
  order (not native-order RGBA) to reproduce the hashes below.

Binary: `/tmp/opencode/lichii/build/bin/eden-cli` (`DISPLAY=:0.0` is required for the
Vulkan present path; RADV / AMD RX 580, Vulkan 1.4.354). Replay CLI:
`eden-cli --replay <trace> --out <dir> [--frames N]`.

### Synthetic 4-frame sample (milestone 1)

No game ROM and no snapshot state needed. Generated and replayed with:

```bash
mkdir -p /tmp/opencode/sample-trace /tmp/opencode/sample-replay
/tmp/opencode/lichii/build/bin/eden-cli --gpu-trace-sample \
    /tmp/opencode/sample-trace/sample.lxtrace
DISPLAY=:0.0 /tmp/opencode/lichii/build/bin/eden-cli --replay \
    /tmp/opencode/sample-trace/sample.lxtrace --out /tmp/opencode/sample-replay --frames 4
```

- Trace: `/tmp/opencode/sample-trace/sample.lxtrace` (186,922 bytes).
- Output: `/tmp/opencode/sample-replay/` — `frame-0000.png` … `frame-0003.png` plus `frames.json`.
- `program_id` `0400000000000001`, `renderer` `RADV`, frames 256x144.

| Frame | XXH3-128 hash (`frames.json`) | `identify` mean |
|---|---|---|
| frame-0000.png | `3169c48ce401c01f006b1769b588f387` | 0.624571 |
| frame-0001.png | `9117f2aef194bdb35c9838954f3b2e84` | 0.500000 |
| frame-0002.png | `652ddf85570efed087446b67a160398c` | 0.624591 |
| frame-0003.png | `c2d1b0548d3f50fc71b834e14f980697` | 0.621467 |

All four frames are non-black (means above). Milestone 1 for the C# replayer is to
reproduce these four hashes from the sample trace.

### Real Fast Fusion trace (final target)

- Trace: `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace` (704,469,931 bytes).
- Expected replay: `/tmp/opencode/oracle-replay/frames.json` — 5 frames, 1280x720,
  `program_id` `040041E0212AE000`, `renderer` `RADV`, mean ~0.826.
- Expected/inferred replay CLI (the original invocation was not recorded):
  `eden-cli --replay <trace> --out <dir> [--frames N]`.

| Frame | XXH3-128 hash (`frames.json`) | `identify` mean |
|---|---|---|
| frame-0000.png | `4cdfa6089661779b338328d7fd369012` | 0.825518 |
| frame-0001.png | `bf97632f20b1524f48d3202d207ea10c` | 0.825642 |
| frame-0002.png | `b451cc96eef12a31f4994a50b39433f0` | 0.825764 |
| frame-0003.png | `de821807760516a568f78849de94e671` | 0.825890 |
| frame-0004.png | `a32d1d27cae7f707e21bc3a3195d543a` | 0.826020 |

All trace, PNG and log artifacts stay under `/tmp/opencode/`; none are committed.

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

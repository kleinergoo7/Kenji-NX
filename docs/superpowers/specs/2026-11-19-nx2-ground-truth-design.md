# NX2 ground-truth bring-up design (bounded)

Date: 2026-11-19
Supersedes the open-ended diagnostic chain in `2026-11-19-nx2-rendering-design.md` for Fast Fusion.

## Intent and why

Twelve diagnostics and one unverified fix produced no rendering. The bottom-up
"find a difference from Eglantine, patch it" loop did not converge because it never
established *ground truth* about where this guest stops. This design replaces that
loop with a bounded, ground-truth-first plan: answer two decisive questions, fix only
the concrete blocker they reveal, and stop.

Eglantine renders this exact title, so a working configuration exists — progress is
possible. The question is whether we can find the load-bearing difference cheaply.

## Goals

1. Remove the unvalidated, possibly harmful Task 1l fix (do not build on it).
2. Answer decisively whether the guest can see its mounted content.
3. Answer decisively whether the guest ever invokes a draw macro.
4. Fix only the concrete blocker revealed, and verify with `draws > 0` / non-black pixels.
5. If neither question yields a concrete fix, stop and pivot to OoT (original objective).

## Non-goals / anti-goals

- No further broad "port all of Eglantine" work.
- No more one-line guesses without a binary decisive outcome.
- No unvalidated changes left as a foundation.
- No OoT work until the Fast Fusion gate resolves.

## Constraints

- Commits authored `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`, pushed to
  `origin` = github.com/kleinergoo7/Kenji-NX; never the Forgejo `kenji-nx` remote.
- Exactly two local branches; archive untouched.
- Build `dotnet build Ryujinx.slnx -c Debug`; smoke `Nx2SwizzleTests` 3 passed.
- Fast Fusion: `NX2_PATCH=""` + `--dram-size MemoryConfiguration12GiB
  --memory-manager-mode SoftwarePageTable --ignore-missing-services`.
- No ROM/scratch/`.nup`/`.rgba`/`.png`/`.bin` in git.
- Stop rule: after the two decisive diagnostics, if no concrete blocker is identified, stop
  the plan and pivot to OoT. Do not open a third diagnostic without human approval.

## Approaches

1. **Ground-truth-first, bounded** (chosen): revert, then two decisive diagnostics, then one
   fix or stop. Highest information per task; bounded cost.
2. Full Eglantine port (rows 45-77): proven recipe but large and slow; rejected for cost.
3. Pivot to OoT now: rejected for now — the two decisive questions are cheap and unanswered.

## Key facts already established (do not re-derive)

- `NUP_COMPLETE=YES`: base selected, 6 ExeFS modules, 3,063 RomFS files (~3.3 GiB).
- `fs=[bytes=0MiB reads=0 opens=5]`: the game opens only `/save.bin` (5x), no content reads.
- Present loop ~300/s, main thread polls `appmsg` non-blocking after ~17 s (not blocked).
- 59 3D macros run (emit inline data/UBO, no draws); 14 compute macros run on an empty bank.
- `PCAS_COVERAGE=NONE` on all three methods 0xAE/0xAF/0xB0; `QMD_RING=INCONCLUSIVE`
  (single zero DWRITE to word 0x27); `draws=0` throughout.
- Task 1l shared-bank fix is wired correctly but `MME_BANK_FIX=NO_COMPUTE_EMIT`; it also risks
  3D/compute `Macro` slot-state cross-talk. Must be reverted before proceeding.

## The decisive questions

**Phase 1 — Can the guest see its content?**
Instrument all filesystem-service operations from the application pid with path + result code
(OpenFile, OpenDirectory, GetEntryType, ReadFile count), plus RomFS/content-mount registration.
Run 90 s. Record `FS_VIEW=`:
- `CONTENT_OPEN_FAILED` — the game tries to open content and gets an error → fix mount/registration.
- `CONTENT_OK` — content opens succeed but no reads follow → stall after open.
- `NO_CONTENT_ATTEMPT` — the game never tries content → stall is in init/service/input, not assets.

**Phase 2 — Does the guest invoke a draw macro?**
Aggregate per-macro identity (position, hash) and whether its emitted register stream contains
draw methods. Cross-check against `MacroHLETable` draw hashes. Run 90 s. Record `DRAW_MACRO=`:
- `DRAW_MACRO_PRESENT_BUT_NO_DRAW` — a draw macro runs but produces no draw → fix recognition/emission.
- `NO_DRAW_MACRO` — no draw macro ever runs → upstream guest decision.

**Phase 3 — Fix only the concrete blocker** revealed, verify `draws > 0` and a non-black frame.

**Stop condition:** if Phase 1 = `NO_CONTENT_ATTEMPT` and Phase 2 = `NO_DRAW_MACRO`, there is no
concrete emulator-side blocker to fix; stop this plan and pivot to OoT. No third diagnostic
without explicit human approval.

# LXTrace Mining Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mine the working oracle's GPU trace to find the exact command methods our fork never emits, then apply the single smallest fix closes that divergence — verified by the A/B harness.

**Architecture:** Read (not replay) the `.lxtrace` artifact with a Python miner. Compare the oracle's real GPU method stream to our `NX2_METHOD_PROBE` histogram. No C# replay, no snapshot restore, no C++ port.

**Tech Stack:** Python 3 + `zstandard` 0.25 (both installed), our `tools/nx2/oracle-ab.sh` harness, `NX2_METHOD_PROBE`/`NX2_MACRO_LOG`/`NX2_DRAW_MACRO_SUMMARY` diagnostics.

**Spec:** `docs/superpowers/specs/2026-11-19-lxtrace-mining-design.md`

## Global Constraints

- Author every commit `kleinergoo7 <348551191+kleinergoo7@users.noreply.github.com>`; push to `origin` = github.com/kleinergoo7/Kenji-NX. Never push to `kenji-nx` (Forgejo).
- Keep exactly two local branches; archive branch read-only.
- No ROM, logs, frames, traces, or scratch in git (the `.lxtrace` is 704 MB and stays in `/tmp`).
- Agent model only `opencode-go/hy4-flash-free`.
- The harness `tools/nx2/oracle-ab.sh` is the end-to-end verifier: PASS = ours mean > 0.1 AND shaders-lowered > 0.

## Review Focus

1. **Reading the wrong field / wrong word shift** could produce a bogus method histogram — pinned in Task 1 by asserting the decoded first Submit matches the probe's known values (`ec=14, wc=4979`, first words `0x136e, 0x2001c00d, ...`).
2. **Comparing different time windows** (oracle trace vs our log) — pinned in Task 2 by aligning on present count / submit index.
3. **A method present in both but with different argument semantics** — pinned in Task 2 by comparing method *and* argument values, not just offsets.
4. **A fix that helps the oracle trace but not the live game** — pinned by the harness end-to-end check in Task 4.

---

### Task 0: (done — A/B harness exists, baseline recorded)

`tools/nx2/oracle-ab.sh` committed at `aa373db12`; baseline oracle 0.826/30 shaders vs ours 0.0/0.

### Task 1: Build the Python trace miner

**Files:**
- Create: `tools/nx2/lxtrace_miner.py`
- Create: `tools/nx2/lxtrace_miner_test.py`
- Create: `docs/nx2/LXTRACE.md`

**Interfaces:**
- Consumes: `.lxtrace` at `/tmp/opencode/oracle-trace/trace-1795130242.lxtrace` (Config from recon: program_id 040041E0212AE000, switch2=true, 1920x1080, 180 presents).
- Produces: a CLI `python3 tools/nx2/lxtrace_miner.py <trace> [--submit N] [--histogram] [--states]` printing method histograms, submit details, State record kinds, and Present info.

- [ ] **Step 1: Write the failing unit test**

In `lxtrace_miner_test.py`, build a synthetic minimal trace in memory (64-byte `LXGTRACE` v2 header + zstd stream with one Config, one Submit with known `entries`/`words`, one Present, one End). Assert the miner parses the header, decodes the Submit's `word_count`, and returns the exact `words[]` array. Run it: `python3 -m pytest tools/nx2/lxtrace_miner_test.py -q`. Expected: FAIL (miner does not exist).

- [ ] **Step 2: Implement the miner**

`tools/nx2/lxtrace_miner.py`: read 64-byte header (validate magic `LXGTRACE`, version 2, header_size 64); open `zstandard.ZstdDecompressor().stream_reader()` at byte 64; loop reading 16-byte record headers (`<IIQ`), payload by size; dispatch by type with struct unpack from the recon's layout table (Submit: `<iiIIQI I` = 28 bytes header, then arrays). Provide `--histogram`: for each Submit's `words[]`, decode methods (NX2 method words: `word >> 2` for the method index where applicable; also report raw word frequency) and print top 40 methods with counts. Provide `--states`: list State record kinds. Stream (do not load the 704 MB file into memory).

- [ ] **Step 3: Verify against the known oracle values**

Run: `python3 tools/nx2/lxtrace_miner.py /tmp/opencode/oracle-trace/trace-1795130242.lxtrace --submit 1`
Expected: `entries=14, prefetch=0, words=4979`, first words include `0x136e`, `0x2001c00d`, `0x80000000`, `0x2004c00a`. If these do not match, the parser word shift is wrong — fix before continuing.

Run: `python3 -m pytest tools/nx2/lxtrace_miner_test.py -q` → PASS.

- [ ] **Step 4: Document and commit**

Write `docs/nx2/LXTRACE.md`: where the trace came from, how it was recorded, format summary, miner commands, and the known-values check. Commit as kleinergoo7, do not push.

```bash
git add tools/nx2/lxtrace_miner.py tools/nx2/lxtrace_miner_test.py docs/nx2/LXTRACE.md
git commit -m "nx2: add lxtrace mining tool"
```

### Task 2: Diff the oracle method stream against ours

**Files:**
- Create: `tools/nx2/compare_nx2_methods.py`
- Modify: `docs/nx2/LXTRACE.md` (record the divergence table)

**Interfaces:**
- Consumes: the miner's `--histogram` output and our run's `NX2_METHOD_PROBE`/`NX2_DRAW_MACRO_SUMMARY` logs (from `.nx2-logs/run-*/ocarina.log`).
- Produces: a table `method | oracle_count | ours_count | verdict`, with rows for shader-bind and draw methods highlighted; and a single named `DIVERGENCE=` value.

- [ ] **Step 1: Capture our method histogram**

Run Fast Fusion 60 s with `NX2_PATCH="" NX2_METHOD_PROBE=1 NX2_FULL_METHOD_PROBE=1 NX2_DRAW_MACRO_SUMMARY=1`, capture the log path. Extract the top methods from our `emitted=[...]`/`3d=` histogram.

- [ ] **Step 2: Write the comparison tool**

`tools/nx2/compare_nx2_methods.py <oracle_hist_json> <ours_log>`: normalize method offsets (account for our RequestData/word-shift differences noted in the recon), and print rows for: draw methods (0x585/0x586), shader-bind / pipeline offsets, inline-data (0x6D), UBO (0x8E4), and any method present in the oracle but absent from ours. Output a `DIVERGENCE=` line choosing exactly one of: `MISSING_SHADER_BIND`, `MISSING_DRAW_METHOD`, `MACRO_CODE_MISMATCH`, `NONE`.

- [ ] **Step 3: Run it and record the divergence**

Run the tool. In `docs/nx2/LXTRACE.md`, record the divergence table and the `DIVERGENCE=` value with the top 5 oracle-only methods (offset + example argument). If `DIVERGENCE=NONE`, stop — the problem is not method coverage; report to a human with the evidence.

- [ ] **Step 4: Verify and commit**

Run: `python3 -m pytest tools/nx2/lxtrace_miner_test.py -q` → PASS. Commit as kleinergoo7, do not push.

```bash
git add tools/nx2/compare_nx2_methods.py docs/nx2/LXTRACE.md
git commit -m "nx2: compare oracle and fork NX2 method streams"
```

### Task 3: Apply the single smallest fix

**Files:** determined by the `DIVERGENCE=` value — written only after Task 2's result is known.

- [ ] **Step 1: State the fix**

Write a short note naming the exact missing method/shader-bind offset, the one file/function to change, and why this closes the divergence. Do not bundle other changes.

- [ ] **Step 2: Write the failing test** that pins the missing method's handling.

- [ ] **Step 3: Implement the single fix** (no cleanup, no other divergences).

- [ ] **Step 4: Verify with the harness**: `bash tools/nx2/oracle-ab.sh 90`. Record the result; PASS = ours mean > 0.1 and shaders-lowered > 0. If still black, stop — do not attempt a second fix.

- [ ] **Step 5: Commit** as kleinergoo7, do not push.

### Task 4: Final verification and handoff

- [ ] **Step 1:** `bash tools/nx2/oracle-ab.sh 90` final table.
- [ ] **Step 2:** Update `docs/nx2/LXTRACE.md` and `CONTINUE_HERE.md`; if PASS, capture an image of our frame; if not, record honestly. Commit and push to `origin`.

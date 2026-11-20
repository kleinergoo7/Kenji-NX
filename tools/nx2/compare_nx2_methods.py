#!/usr/bin/env python3
"""Diff the oracle's GPU method stream against our fork's observed method stream.

    python3 tools/nx2/compare_nx2_methods.py <oracle_hist_json> <ours_log>

Normalization and its limits
----------------------------
Our fork decodes an NX2 method address as `word & 0xFFF` (CompressedMethod.MethodAddress,
12-bit), not the classic Maxwell `(word >> 2) & 0x7FF`. The oracle histogram's
exact `raw_words[].word` therefore maps to our offset by `word & 0xFFF`; this tool
uses that exact mapping and requires the FULL (untruncated) histogram, because the
top-40 `methods[]` view omits the draw words and inline-data/UBO offsets.

The `methods[].index = word >> 2` view is lossy: it drops the low 2 bits, so
`(index & 0x3FF) << 2` can be 0-3 below the true offset (e.g. 0x2001c00d -> 0xc
instead of 0xd, and 0x585/0x586 both collapse to 0x584). It is never used for
presence decisions here. Two other limits are reported, not hidden:

  * The oracle histogram counts *every* command word (methods, inline arguments,
    macro markers); our `emitted=`/`3d=` histograms count only method addresses
    actually dispatched by our engine. Counts are printed but are NOT comparable
    as frequencies - only method-family presence/absence is compared.
  * The oracle's macro-emitted methods are generated from macro RAM and are not
    present as literal words in the stream; their presence can only be inferred
    from macro-start words (0xE00+) in the oracle and from our emitted histogram.

"First divergence" is the first highlighted family, in the order draws ->
shader/pipeline reserved registers -> macro output, that is present in the
oracle and absent from ours.
"""

import argparse
import json
import re
from collections import Counter

# Pipeline reserved registers assumed (per Task 2 recon) to carry native shader
# addresses in Lichii (shader_cache.cpp:214-216); our fork emits them as macro
# methods. The exact register numbers are an assumption, not independently verified.
PIPELINE_REGS = (0xD00, 0xD01, 0xD02)
DRAW_WORDS = (0x584, 0x585, 0x586)
DRAW_COLLAPSED = 0x584
INLINE_DATA = 0x6D
UBO = 0x8E4


def load_oracle(path):
    """Return (offsets, raw_counts, examples) from a miner histogram JSON."""
    with open(path) as f:
        doc = json.load(f)
    raw_counts = Counter()
    for entry in doc.get("raw_words", []):
        raw_counts[int(entry["word"], 16)] += entry["count"]
    # Exact offset == word & 0xFFF, from the exact raw words (index = word >> 2
    # loses the low 2 bits and is deliberately not used here).
    offsets = Counter()
    for word, count in raw_counts.items():
        offsets[word & 0xFFF] += count
    # Most frequent exact raw word for each normalized offset (example argument).
    examples = {}
    for word, count in raw_counts.items():
        off = word & 0xFFF
        if off not in examples or count > examples[off][1]:
            examples[off] = (word, count)
    return offsets, raw_counts, examples, doc


def _parse_pairs(text):
    return Counter({int(off, 16): int(n)
                    for off, n in re.findall(r"0x([0-9A-Fa-f]+)=(\d+)", text)})


def load_ours(path):
    """Parse the last NX2 progress line's emitted/3d/draw histograms."""
    text = open(path, errors="replace").read()
    lines = [ln for ln in text.splitlines()
             if "NX2 progress" in ln and "emitted=[" in ln]
    if not lines:
        raise ValueError("no NX2 progress line with emitted=[...] in " + path)
    line = lines[-1]

    # Anchor on whitespace: the line also has cemitted=[] before emitted=[...].
    emitted = _parse_pairs(re.search(r"(?:^|\s)emitted=\[([^\]]*)\]", line).group(1))
    m = re.search(r"3d=total=(\d+)\s+top=(.*?)\s+emitted=\[", line)
    d3 = _parse_pairs(m.group(2)) if m else Counter()
    dm = re.search(r"drawmacro=\[([^\]]*)\]", line)
    macros = int(re.search(r"macros=(\d+)", dm.group(1)).group(1)) if dm else 0
    hits = int(re.search(r"drawEmitHits=(\d+)", dm.group(1)).group(1)) if dm else 0
    draws = 0
    dr = re.search(r"draws=\[([^\]]*)\]", line)
    if dr:
        total = re.search(r"total=(\d+)", dr.group(1))
        draws = int(total.group(1)) if total else 0

    return {"emitted": emitted, "d3": d3, "macros": macros,
            "draw_emit_hits": hits, "draws": draws, "line": line}


def _verdict(oracle_has, ours_has, note=""):
    if oracle_has and ours_has:
        return "BOTH" + (f" ({note})" if note else "")
    if oracle_has:
        return "ORACLE-ONLY"
    if ours_has:
        return "OURS-ONLY"
    return "NEITHER"


def compare(oracle_offsets, oracle_raw, examples, ours, oracle_only_top=8):
    emitted, d3 = ours["emitted"], ours["d3"]
    ours_offsets = Counter()
    ours_offsets.update(emitted)
    ours_offsets.update(d3)

    def ours_count(off):
        return emitted.get(off, 0) + d3.get(off, 0)

    rows = []
    rows.append(("draw 0x585 (DrawBegin)", oracle_raw.get(0x585, 0),
                 ours_count(0x585), _verdict(0x585 in oracle_raw, 0x585 in ours_offsets),
                 "DRAW"))
    rows.append(("draw 0x586 (DrawEnd)", oracle_raw.get(0x586, 0),
                 ours_count(0x586), _verdict(0x586 in oracle_raw, 0x586 in ours_offsets),
                 "DRAW"))
    rows.append((f"draw-family 0x{DRAW_COLLAPSED:X} (word&0xFFF)",
                 oracle_offsets.get(DRAW_COLLAPSED, 0), ours_count(DRAW_COLLAPSED),
                 _verdict(DRAW_COLLAPSED in oracle_offsets, DRAW_COLLAPSED in ours_offsets),
                 "DRAW"))
    for reg in PIPELINE_REGS:
        rows.append((f"shader/pipeline reserved 0x{reg:X}",
                     oracle_offsets.get(reg, 0), ours_count(reg),
                     _verdict(reg in oracle_offsets, reg in ours_offsets,
                              "counts not comparable"), "SHADER"))
    rows.append((f"inline-data 0x{INLINE_DATA:X}", oracle_offsets.get(INLINE_DATA, 0),
                 ours_count(INLINE_DATA),
                 _verdict(INLINE_DATA in oracle_offsets, INLINE_DATA in ours_offsets,
                          "counts not comparable"), ""))
    rows.append((f"UBO 0x{UBO:X}", oracle_offsets.get(UBO, 0), ours_count(UBO),
                 _verdict(UBO in oracle_offsets, UBO in ours_offsets,
                          "ours is macro-emitted"), ""))

    oracle_only = sorted(
        [(off, cnt, examples.get(off, (0, 0))[0])
         for off, cnt in oracle_offsets.items() if off not in ours_offsets],
        key=lambda r: (-r[1], r[0]))

    oracle_has_draw = any(oracle_raw.get(w, 0) for w in DRAW_WORDS)
    ours_has_draw = any(ours_count(w) for w in DRAW_WORDS)
    oracle_has_pipe = any(oracle_offsets.get(r, 0) for r in PIPELINE_REGS)
    ours_has_pipe = any(ours_count(r) for r in PIPELINE_REGS)
    # A macro-output mismatch means our macros emit offsets the oracle stream has no
    # counterpart for at all (checked only after the two presence gaps above).
    macro_mismatch = bool(set(emitted) - set(oracle_offsets))

    if oracle_has_draw and not ours_has_draw:
        divergence = "MISSING_DRAW_METHOD"
    elif oracle_has_pipe and not ours_has_pipe:
        divergence = "MISSING_SHADER_BIND"
    elif macro_mismatch:
        divergence = "MACRO_CODE_MISMATCH"
    else:
        divergence = "NONE"

    return rows, oracle_only[:oracle_only_top], divergence


def print_report(rows, oracle_only, divergence, ours, doc):
    print(f"oracle: submits={doc.get('submit_count')} words={doc.get('word_count_total')}")
    print(f"ours:   macros={ours['macros']} drawEmitHits={ours['draw_emit_hits']} "
          f"draws={ours['draws']} emitted={sum(ours['emitted'].values())} "
          f"3d={sum(ours['d3'].values())}")
    print()
    print("=== Divergence table (method | oracle_count | ours_count | verdict) ===")
    print(f"{'method':<44} {'oracle':>8} {'ours':>8}  verdict")
    for name, oc, nc, verdict, flag in rows:
        mark = "  <-- " + flag if flag else ""
        print(f"{name:<44} {oc:>8} {nc:>8}  {verdict}{mark}")
    print()
    print("=== Top oracle-only offsets (offset | count | example argument word) ===")
    print(f"{'offset':<10} {'oracle_count':>12}  example_word")
    for off, cnt, ex in oracle_only:
        print(f"0x{off:<8X} {cnt:>12}  0x{ex:X}")
    print()
    print("=== Decision ===")
    print("Note: oracle counts ALL command words; ours counts dispatched method addresses.")
    print("      Only presence/absence is compared; counts are not frequencies.")
    print(f"DIVERGENCE={divergence}")


def self_check():
    """Assert the normalization facts the whole comparison rests on."""
    # Exact offset for our fork is word & 0xFFF (CompressedMethod.MethodAddress).
    assert 0x2001C00D & 0xFFF == 0x00D
    assert 0xA0040E02 & 0xFFF == 0xE02
    # The miner's word >> 2 view loses the low 2 bits: off by 1 and by 2 here.
    assert ((0x2001C00D >> 2) & 0x3FF) << 2 == 0x00C
    assert ((0xA0040E02 >> 2) & 0x3FF) << 2 == 0xE00
    # Draw offsets 0x585/0x586 collapse to 0x584 in the lossy word >> 2 view.
    assert (0x585 & ~0x3) == (0x586 & ~0x3) == 0x584
    print("self-check OK")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("oracle_hist_json", nargs="?")
    ap.add_argument("ours_log", nargs="?")
    ap.add_argument("--self-check", action="store_true",
                    help="assert normalization invariants and exit")
    args = ap.parse_args(argv)
    if args.self_check:
        self_check()
        return

    offsets, raw, examples, doc = load_oracle(args.oracle_hist_json)
    ours = load_ours(args.ours_log)
    rows, oracle_only, divergence = compare(offsets, raw, examples, ours)
    print_report(rows, oracle_only, divergence, ours, doc)


if __name__ == "__main__":
    main()

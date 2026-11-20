#!/usr/bin/env python3
"""Stream a Lichii .lxtrace and report GPU submit / state / present data.

The 704 MB oracle trace is never loaded whole: the 64-byte header is read from
disk, then a zstd stream_reader yields 16-byte record headers and their payloads
one at a time.

Usage: python3 tools/nx2/lxtrace_miner.py <trace> [--submit N] [--histogram] [--states]
"""

import argparse
import json
import struct
import sys
from collections import Counter

import zstandard

MAGIC = b"LXGTRACE"
VERSION = 2
HEADER_SIZE = 64
RECORD_HEADER = struct.Struct("<IIQ")  # type:u32, reserved:u32, size:u64
# sizeof(SubmitRecord) from trace_file.h is 32 (measured with g++): channel@0,
# entry@4, prefetch@8, offset@12, word_count@16, deferred@24, padding@28.
# offset_count already ends on an 8-byte boundary, so no implicit pad is inserted.
SUBMIT_HEADER = struct.Struct("<iiIIQII")

STATE, SUBMIT, PRESENT, CONFIG, END = 8, 12, 13, 1, 15
TOP_N = 40


def read_exact(stream, size):
    """Read exactly size bytes from a stream, raising on truncation."""
    if size == 0:
        return b""
    chunks = []
    remaining = size
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            raise EOFError(f"truncated trace: wanted {size} bytes, got {size - remaining}")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def parse_header(stream):
    """Validate the 64-byte LXGTRACE v2 header and leave the stream at byte 64."""
    header = read_exact(stream, HEADER_SIZE)
    magic, version, header_size = struct.unpack_from("<8sII", header, 0)
    if magic != MAGIC:
        raise ValueError(f"bad lxtrace magic: {magic!r}")
    if version != VERSION:
        raise ValueError(f"unsupported lxtrace version: {version}")
    if header_size != HEADER_SIZE:
        raise ValueError(f"unsupported header size: {header_size}")
    return {"version": version, "header_size": header_size}


def iter_records(stream):
    """Yield (record_type, payload) for every record in the zstd stream."""
    reader = zstandard.ZstdDecompressor().stream_reader(stream)
    while True:
        header = reader.read(RECORD_HEADER.size)
        if not header:
            return
        if len(header) < RECORD_HEADER.size:
            raise EOFError("truncated record header")
        record_type, _reserved, size = RECORD_HEADER.unpack(header)
        yield record_type, read_exact(reader, size)


def parse_submit(payload):
    """Decode a Submit payload into its fixed fields and five arrays."""
    channel, ec, pc, oc, word_count, dc, _padding = SUBMIT_HEADER.unpack_from(payload, 0)
    pos = SUBMIT_HEADER.size
    entries = struct.unpack_from(f"<{ec}Q", payload, pos)
    pos += ec * 8
    prefetch = struct.unpack_from(f"<{pc}I", payload, pos)
    pos += pc * 4
    offsets = struct.unpack_from(f"<{oc}I", payload, pos)
    pos += oc * 4
    words = struct.unpack_from(f"<{word_count}I", payload, pos)
    pos += word_count * 4
    deferred = struct.unpack_from(f"<{dc}I", payload, pos)
    return {
        "channel": channel, "entry_count": ec, "prefetch_count": pc,
        "offset_count": oc, "word_count": word_count, "deferred_count": dc,
        "entries": entries, "prefetch": prefetch, "offsets": offsets,
        "words": words, "deferred": deferred,
    }


def decode_method(word):
    """NX2 method-word index: the method offset is word >> 2."""
    return word >> 2


def state_kind(payload):
    """State records begin with a u32 kind discriminator."""
    return struct.unpack_from("<I", payload, 0)[0] if len(payload) >= 4 else 0


def _top(counter):
    return sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))[:TOP_N]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("trace", help="path to a .lxtrace file")
    parser.add_argument("--submit", type=int, metavar="N",
                        help="print details of the Nth Submit (1-based)")
    parser.add_argument("--histogram", action="store_true",
                        help="print a JSON histogram of methods and raw words")
    parser.add_argument("--states", action="store_true",
                        help="list State record kinds and their counts")
    args = parser.parse_args(argv)

    methods = Counter()
    raw_words = Counter()
    state_kinds = Counter()
    config = None
    submit_index = 0
    present_count = 0
    first_present = None
    selected = None

    with open(args.trace, "rb") as stream:
        parse_header(stream)
        for record_type, payload in iter_records(stream):
            if record_type == CONFIG:
                config = json.loads(payload)
            elif record_type == SUBMIT:
                submit_index += 1
                submit = parse_submit(payload)
                if args.submit == submit_index:
                    selected = submit
                if args.histogram:
                    for word in submit["words"]:
                        raw_words[word] += 1
                        methods[decode_method(word)] += 1
            elif record_type == STATE:
                state_kinds[state_kind(payload)] += 1
            elif record_type == PRESENT:
                present_count += 1
                if first_present is None:
                    first_present = payload
            elif record_type == END:
                break

    if args.histogram:
        histogram = {
            "trace": args.trace,
            "submit_count": submit_index,
            "word_count_total": sum(raw_words.values()),
            "methods": [{"method": hex(m), "index": m, "count": n}
                        for m, n in _top(methods)],
            "raw_words": [{"word": hex(w), "count": n} for w, n in _top(raw_words)],
        }
        print(json.dumps(histogram, indent=2))

    if args.submit is not None:
        if selected is None:
            print(f"submit {args.submit} not found (trace has {submit_index})",
                  file=sys.stderr)
        else:
            print(f"Submit #{args.submit}: channel={selected['channel']} "
                  f"entries={selected['entry_count']} "
                  f"prefetch={selected['prefetch_count']} "
                  f"offsets={selected['offset_count']} "
                  f"words={selected['word_count']} "
                  f"deferred={selected['deferred_count']}")
            print("  entries[0:16]:  " + " ".join(hex(x) for x in selected["entries"][:16]))
            print("  offsets[0:16]:  " + " ".join(hex(x) for x in selected["offsets"][:16]))
            print("  words[0:16]:    " + " ".join(hex(x) for x in selected["words"][:16]))
            print("  methods[0:16]:  " + " ".join(hex(decode_method(x))
                                                     for x in selected["words"][:16]))

    if args.states:
        for kind, count in sorted(state_kinds.items()):
            print(f"state kind {kind}: {count}")

    summary = (f"submits={submit_index} presents={present_count} "
               f"state_kinds={len(state_kinds)}")
    if config:
        summary += (f" program_id={config.get('program_id')} "
                    f"switch2={config.get('switch2')} "
                    f"{config.get('width')}x{config.get('height')}")
    if first_present is not None:
        summary += " first_present=" + first_present[:16].hex()
    if not (args.histogram or args.submit is not None or args.states):
        print(summary)
    else:
        print(summary, file=sys.stderr)


if __name__ == "__main__":
    main()

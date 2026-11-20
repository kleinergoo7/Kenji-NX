"""Unit tests for the streaming lxtrace miner.

Builds a tiny in-memory trace (64-byte header + zstd records) so the parser can be
tested without the 704 MB oracle artifact.
"""

import io
import json
import os
import struct
import sys

import zstandard

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lxtrace_miner as miner  # noqa: E402

MAGIC = b"LXGTRACE"
VERSION = 2
HEADER_SIZE = 64
RECORD_HEADER = struct.Struct("<IIQ")
SUBMIT_HEADER = struct.Struct("<iiIIQI")  # 28-byte header per format brief

CONFIG = {"program_id": "040041E0212AE000", "switch2": True,
          "width": 1920, "height": 1080, "presents": 180}

# Known Submit fixture. The word list includes the four values pinned by the
# oracle known-values check so word decoding is exercised end to end.
SUBMIT = {
    "channel": 1,
    "entries": [0x0000223EF078EEC8, 0x0000223EF078EF04],
    "prefetch": [0x1234],
    "offsets": [0x0, 0x136E],
    "words": [0x136E, 0x2001C00D, 0x80000000, 0x2004C00A, 0x0007003B],
    "deferred": [0xDEADBEEF],
}


def make_submit_payload(s):
    payload = bytearray(SUBMIT_HEADER.pack(
        s["channel"], len(s["entries"]), len(s["prefetch"]),
        len(s["offsets"]), len(s["words"]), len(s["deferred"])))
    for value in s["entries"]:
        payload += struct.pack("<Q", value)
    for values, fmt in ((s["prefetch"], "<I"), (s["offsets"], "<I"),
                        (s["words"], "<I"), (s["deferred"], "<I")):
        for value in values:
            payload += struct.pack(fmt, value)
    return bytes(payload)


def make_record(record_type, payload):
    return RECORD_HEADER.pack(record_type, 0, len(payload)) + payload


def make_trace():
    records = [
        make_record(1, json.dumps(CONFIG).encode()),                  # Config
        make_record(12, make_submit_payload(SUBMIT)),                # Submit
        make_record(8, struct.pack("<II", 3, 4)),                    # State kind 3
        make_record(13, struct.pack("<Q", 99)),                      # Present
        make_record(15, b""),                                        # End
    ]
    header = bytearray(HEADER_SIZE)
    header[:len(MAGIC)] = MAGIC
    struct.pack_into("<II", header, 8, VERSION, HEADER_SIZE)
    return bytes(header) + zstandard.ZstdCompressor().compress(b"".join(records))


def test_parse_header_validates_magic_version_and_size():
    parsed = miner.parse_header(io.BytesIO(make_trace()))
    assert parsed == {"version": VERSION, "header_size": HEADER_SIZE}

    bad = bytearray(make_trace()[:HEADER_SIZE])
    bad[:8] = b"BADMAGIC"
    try:
        miner.parse_header(io.BytesIO(bytes(bad) + make_trace()[HEADER_SIZE:]))
        assert False, "bad magic must be rejected"
    except ValueError:
        pass


def test_iter_records_decodes_all_record_types():
    data = make_trace()
    f = io.BytesIO(data)
    miner.parse_header(f)
    records = [(t, p) for t, p in miner.iter_records(f)]
    assert [t for t, _ in records] == [1, 12, 8, 13, 15]
    assert json.loads(records[0][1]) == CONFIG
    assert records[-1][1] == b""


def test_parse_submit_returns_exact_arrays():
    payload = make_submit_payload(SUBMIT)
    s = miner.parse_submit(payload)
    assert s["channel"] == SUBMIT["channel"]
    assert s["entry_count"] == len(SUBMIT["entries"])
    assert s["word_count"] == len(SUBMIT["words"])
    assert s["prefetch_count"] == len(SUBMIT["prefetch"])
    assert s["offset_count"] == len(SUBMIT["offsets"])
    assert s["deferred_count"] == len(SUBMIT["deferred"])
    assert list(s["entries"]) == SUBMIT["entries"]
    assert list(s["prefetch"]) == SUBMIT["prefetch"]
    assert list(s["offsets"]) == SUBMIT["offsets"]
    assert list(s["words"]) == SUBMIT["words"]
    assert list(s["deferred"]) == SUBMIT["deferred"]


def test_decode_method_shifts_word_right_by_two():
    assert miner.decode_method(0x2001C00D) == 0x2001C00D >> 2
    assert miner.decode_method(0x136E) == 0x136E >> 2


def test_cli_submit_prints_words_and_state_kinds(tmp_path, capsys):
    trace = tmp_path / "synthetic.lxtrace"
    trace.write_bytes(make_trace())
    miner.main([str(trace), "--submit", "1", "--states"])
    out = capsys.readouterr().out
    assert "entries=2" in out
    assert "words=5" in out
    assert "0x136e" in out
    assert "0x2001c00d" in out
    assert "state kind 3" in out


def test_cli_histogram_is_json(tmp_path, capsys):
    trace = tmp_path / "synthetic.lxtrace"
    trace.write_bytes(make_trace())
    miner.main([str(trace), "--histogram"])
    data = json.loads(capsys.readouterr().out)
    assert data["submit_count"] == 1
    assert data["word_count_total"] == 5
    assert data["methods"][0]["count"] == 1
    assert data["raw_words"][0]["word"].startswith("0x")

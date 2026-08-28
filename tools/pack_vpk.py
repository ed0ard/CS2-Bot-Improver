#!/usr/bin/env python3
"""Repack CS2-Bot-Improver botprofile.db files into VPK v2 single-file archives.

The game loads botprofile.db from overrides/*.vpk, not from loose .db files.
This tool rebuilds the vpks from the current repo dbs, using an existing vpk
as the layout template (v2, single embedded file, 48-byte self-hashes).

Usage:
    python tools/pack_vpk.py --cs2 <game/csgo/overrides> [--template-dir <dir>]

The template vpks are read from <cs2>/overrides (or --template-dir). Each
difficulty vpk is rebuilt from overrides/{Low,Medium,High}/botprofile.db and
the root botprofile.vpk follows the current difficulty selection (Medium).
"""

import argparse
import struct
import zlib
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DIFFICULTIES = ("Low", "Medium", "High")
ROOT_DIFFICULTY = "Medium"


def read_cstring(data: bytes, pos: int) -> tuple[bytes, int]:
    end = data.index(b"\x00", pos)
    return data[pos:end], end + 1


def parse_tree(tree: bytes) -> dict:
    pos = 0
    ext, pos = read_cstring(tree, pos)
    path, pos = read_cstring(tree, pos)
    name, pos = read_cstring(tree, pos)
    assert name == b"botprofile", f"unexpected name {name!r}"
    crc, preload_len, archive_index, offset, length, suffix = struct.unpack_from("<IHHIIH", tree, pos)
    assert suffix == 0xFFFF, f"bad suffix {suffix:#x}"
    return {
        "crc": crc, "preload_len": preload_len, "archive_index": archive_index,
        "offset": offset, "length": length, "struct_pos": pos,
    }


def pack(src_db: Path, template_vpk: Path, out_vpk: Path) -> None:
    data = src_db.read_bytes()
    tpl = template_vpk.read_bytes()

    sig, ver, tree_len, embed_len, chunk_hashes_len, self_hashes_len, sig_len = struct.unpack_from("<7I", tpl, 0)
    assert sig == 0x55AA1234 and ver == 2, "template is not a v2 vpk"
    assert chunk_hashes_len == 0 and sig_len == 0, "unsupported vpk sections"

    hdr = bytearray(tpl[:28])
    struct.pack_into("<I", hdr, 12, len(data))

    tree = bytearray(tpl[28:28 + tree_len])
    entry = parse_tree(bytes(tree))
    struct.pack_into("<I", tree, entry["struct_pos"], zlib.crc32(data) & 0xFFFFFFFF)
    struct.pack_into("<I", tree, entry["struct_pos"] + 12, len(data))

    data_off = 28 + tree_len
    self_hashes = tpl[data_off + embed_len: data_off + embed_len + self_hashes_len]
    assert len(self_hashes) == self_hashes_len

    out = hdr + tree + data + self_hashes
    out_vpk.write_bytes(bytes(out))
    print(f"[ok] {out_vpk.name}: db {len(data)} bytes -> vpk {len(out)} bytes")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cs2", required=True, help="game/csgo/overrides directory of the CS2 install")
    ap.add_argument("--template-dir", default=None, help="dir holding the original vpks (defaults to --cs2)")
    args = ap.parse_args()

    cs2 = Path(args.cs2)
    templates = Path(args.template_dir) if args.template_dir else cs2

    for diff in DIFFICULTIES:
        db = REPO / "overrides" / diff / "botprofile.db"
        vpk_path = f"{diff}/botprofile.vpk"
        pack(db, templates / vpk_path, cs2 / vpk_path)

    pack(REPO / "overrides" / ROOT_DIFFICULTY / "botprofile.db",
         templates / "botprofile.vpk", cs2 / "botprofile.vpk")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

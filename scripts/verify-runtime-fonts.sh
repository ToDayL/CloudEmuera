#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
font_root="$repo_root/assets/runtime-fonts"

FONT_ROOT="$font_root" python3 - <<'PY'
import hashlib
import json
import os
import struct
import unicodedata
from pathlib import Path

root = Path(os.environ["FONT_ROOT"])
catalog_path = root / "catalog.json"

def fail(message):
    raise SystemExit(f"runtime font verification failed: {message}")

def regular_file(path, label):
    try:
        stat = path.lstat()
    except FileNotFoundError:
        fail(f"{label} is missing")
    if not path.is_file() or path.is_symlink() or not os.path.isfile(path):
        fail(f"{label} is not a regular file")
    if stat.st_mode & 0o111:
        fail(f"{label} is executable")

def safe_relative(value, label):
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        fail(f"{label} is not a safe relative path")
    path = Path(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in value.split("/")):
        fail(f"{label} escapes the catalog root")
    if unicodedata.normalize("NFC", value) != value:
        fail(f"{label} is not NFC-normalized")
    return path

def digest(path):
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()

def read_u16(data, offset):
    if offset < 0 or offset + 2 > len(data):
        fail("TTF table is truncated")
    return struct.unpack_from(">H", data, offset)[0]

def ttf_tables(data, label):
    if len(data) < 12 or data[:4] not in (b"\x00\x01\x00\x00", b"true", b"typ1"):
        fail(f"{label} has no valid TrueType signature")
    count = read_u16(data, 4)
    end = 12 + count * 16
    if count == 0 or end > len(data):
        fail(f"{label} has an invalid table directory")
    tables = {}
    for offset in range(12, end, 16):
        tag = data[offset:offset + 4].decode("latin1")
        table_offset, table_length = struct.unpack_from(">II", data, offset + 8)
        if tag in tables or table_offset > len(data) or table_length > len(data) - table_offset:
            fail(f"{label} has an invalid or duplicate {tag} table")
        tables[tag] = data[table_offset:table_offset + table_length]
    return tables

def name_strings(table):
    if len(table) < 6:
        fail("name table is truncated")
    _, count, storage = struct.unpack_from(">HHH", table, 0)
    record_end = 6 + count * 12
    if record_end > len(table):
        fail("name table records are truncated")
    strings = []
    for offset in range(6, record_end, 12):
        platform, _, language, name_id, length, string_offset = struct.unpack_from(">HHHHHH", table, offset)
        start = storage + string_offset
        raw = table[start:start + length]
        if len(raw) != length or name_id not in (1, 2, 16, 17):
            continue
        try:
            value = raw.decode("utf-16-be" if platform in (0, 3) else "mac_roman" if platform == 1 else "latin1")
        except UnicodeDecodeError:
            continue
        if value:
            strings.append((name_id, value, language))
    return strings

def cmap_codepoints(table):
    if len(table) < 4:
        fail("cmap table is truncated")
    version, count = struct.unpack_from(">HH", table, 0)
    if version != 0 or 4 + count * 8 > len(table):
        fail("cmap table header is invalid")
    points = set()
    for offset in range(4, 4 + count * 8, 8):
        _, _, sub_offset = struct.unpack_from(">HHI", table, offset)
        if sub_offset >= len(table):
            continue
        fmt = read_u16(table, sub_offset)
        if fmt == 4 and sub_offset + 14 <= len(table):
            seg_count = read_u16(table, sub_offset + 6) // 2
            end_array = sub_offset + 14
            start_array = end_array + seg_count * 2 + 2
            if start_array + seg_count * 2 <= len(table):
                for index in range(seg_count):
                    first = read_u16(table, start_array + index * 2)
                    last = read_u16(table, end_array + index * 2)
                    if first <= last and first != 0xFFFF:
                        points.update(range(first, min(last, first + 4096) + 1))
        elif fmt in (12, 13) and sub_offset + 16 <= len(table):
            groups = struct.unpack_from(">I", table, sub_offset + 12)[0]
            cursor = sub_offset + 16
            for _ in range(groups):
                if cursor + 12 > len(table):
                    break
                first, last, _ = struct.unpack_from(">III", table, cursor)
                points.update(range(first, min(last, first + 4096) + 1))
                cursor += 12
    return points

if not root.is_dir() or catalog_path.is_symlink() or not catalog_path.is_file():
    fail(f"runtime font root or catalog is missing: {root}")
regular_file(catalog_path, "catalog.json")
catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
if catalog.get("schemaVersion") != 1 or catalog.get("defaultFaceId") != "sarasa-fixed-sc-1.0.40-regular":
    fail("catalog schema or default face is not the pinned v1 contract")
items = catalog.get("items")
if not isinstance(items, list) or len(items) != 6:
    fail("catalog must contain exactly six faces")

expected = {
    "sarasa-fixed-sc-1.0.40-light": ("sarasa-fixed-sc", "1.0.40", 300),
    "sarasa-fixed-sc-1.0.40-regular": ("sarasa-fixed-sc", "1.0.40", 400),
    "sarasa-fixed-sc-1.0.40-medium": ("sarasa-fixed-sc", "1.0.40", 600),
    "lxgw-bright-code-2.922-extralight": ("lxgw-bright-code", "2.922", 200),
    "lxgw-bright-code-2.922-light": ("lxgw-bright-code", "2.922", 300),
    "lxgw-bright-code-2.922-regular": ("lxgw-bright-code", "2.922", 400),
}
seen_ids = set()
seen_paths = set()
declared_ttf = set()
declared_woff2 = set()
declared_licenses = set()
required_codepoints = {0x20, 0x41, 0x3000, 0x4E2D, 0x65E5, 0x3042}

for item in items:
    if not isinstance(item, dict):
        fail("catalog item is not an object")
    face_id = item.get("faceId")
    if face_id not in expected or face_id in seen_ids:
        fail(f"unexpected or duplicate face ID: {face_id!r}")
    seen_ids.add(face_id)
    family, source_version, weight = expected[face_id]
    if item.get("family") != family or item.get("sourceVersion") != source_version or item.get("weight") != weight:
        fail(f"catalog metadata mismatch for {face_id}")
    paths = []
    for field, kind in (("runtimeTtfPath", "TTF"), ("webWoff2Path", "WOFF2"), ("licenseFile", "license")):
        relative = safe_relative(item.get(field), f"{face_id}.{field}")
        collision = (unicodedata.normalize("NFC", str(relative)).casefold(), kind)
        if collision in seen_paths and kind != "license":
            fail(f"case-insensitive path collision for {relative}")
        seen_paths.add(collision)
        paths.append((relative, kind))
    ttf_path, _ = paths[0]
    woff_path, _ = paths[1]
    license_path, _ = paths[2]
    ttf_path = root / ttf_path
    woff_path = root / woff_path
    license_path = root / license_path
    regular_file(ttf_path, f"{face_id} TTF")
    regular_file(woff_path, f"{face_id} WOFF2")
    regular_file(license_path, f"{face_id} license")
    ttf_data = ttf_path.read_bytes()
    woff_data = woff_path.read_bytes()
    if len(ttf_data) != item.get("runtimeTtfByteLength") or digest(ttf_path) != item.get("runtimeTtfSha256"):
        fail(f"TTF digest or length mismatch for {face_id}")
    woff_digest = digest(woff_path)
    if len(woff_data) != item.get("webWoff2ByteLength") or woff_digest != item.get("webWoff2Sha256") or woff_digest != woff_path.stem:
        fail(f"WOFF2 digest, filename or length mismatch for {face_id}")
    if woff_data[:4] != b"wOF2":
        fail(f"WOFF2 signature mismatch for {face_id}")
    tables = ttf_tables(ttf_data, f"{face_id} TTF")
    # Bright Code is a fixed-pitch merge and its pinned upstream TTFs do not
    # carry a GPOS table. The advance-width authority lives in hhea/hmtx;
    # Sarasa keeps its upstream GPOS table and remains checked explicitly.
    required_tables = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "GSUB"]
    if family == "sarasa-fixed-sc":
        required_tables.append("GPOS")
    for required in required_tables:
        if required not in tables:
            fail(f"{face_id} TTF misses required {required} table")
    families = {value for name_id, value, _ in name_strings(tables["name"]) if name_id in (1, 16)}
    if item.get("runtimeFamilyName") not in families:
        fail(f"runtime family name is not present in {face_id} name table")
    if len(tables["OS/2"]) < 8 or struct.unpack_from(">H", tables["OS/2"], 4)[0] != weight:
        fail(f"OS/2 weight mismatch for {face_id}")
    missing = required_codepoints - cmap_codepoints(tables["cmap"])
    if missing:
        fail(f"{face_id} misses required glyphs: {', '.join(hex(value) for value in sorted(missing))}")
    license_text = license_path.read_text(encoding="utf-8", errors="strict")
    if len(license_text.strip()) < 100 or "OFL" not in license_text:
        fail(f"license text is incomplete for {face_id}")
    declared_ttf.add(ttf_path.relative_to(root))
    declared_woff2.add(woff_path.relative_to(root))
    declared_licenses.add(license_path.relative_to(root))

if seen_ids != set(expected):
    fail("catalog face set is not the six-face baseline")

def relative_files(directory):
    result = set()
    for path in directory.rglob("*"):
        if path.is_symlink():
            fail(f"symlink is not allowed: {path.relative_to(root)}")
        if path.is_file():
            result.add(path.relative_to(root))
        elif not path.is_dir():
            fail(f"special file is not allowed: {path.relative_to(root)}")
    return result

if relative_files(root / "runtime-ttf") != declared_ttf:
    fail("runtime-ttf contains an undeclared or missing file")
if relative_files(root / "web-woff2") != declared_woff2:
    fail("web-woff2 contains an undeclared or missing file")
if relative_files(root / "licenses") != declared_licenses:
    fail("licenses contains an undeclared or missing file")

print(f"runtime fonts OK: {len(items)} faces, {len(declared_ttf)} TTF, {len(declared_woff2)} WOFF2")
PY

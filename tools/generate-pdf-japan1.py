#!/usr/bin/env python3
"""Generate the DocRedock Adobe-Japan1-UCS2 Unicode CMap data source.

The only source of truth is the public Adobe mapping file, pinned by revision
and SHA256 below; nothing is downloaded and no PostScript is executed.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import io
import re
import sys
from pathlib import Path

UPSTREAM_REVISION = "2dd5e53fb74a01718b9dfd448a0d1cce6fff2aa5"
UPSTREAM_URL = (
    "https://raw.githubusercontent.com/adobe-type-tools/mapping-resources-pdf/"
    f"{UPSTREAM_REVISION}/pdf2unicode/Adobe-Japan1-UCS2"
)
UPSTREAM_SHA256 = "6a9693361647a37996312cc57071bb79f8c06411207be7c730a83fda1254cd82"
UPSTREAM_NOTICE_PATH = "LICENSE.txt"  # BSD-3-Clause text at the repo root
SUPPLEMENT = 7
CNAME = "Adobe-Japan1-UCS2"
NAMESPACE = "DocRedock.Formats.Pdf"
CLASS_NAME = "PdfJapan1UnicodeData"
FIELD_NAME = "CMapGzipBase64"
B64_LINE = 120

_HEX = set("0123456789abcdefABCDEF")


class CMapError(ValueError):
    """The CMap text is not a well formed, counted bfchar/bfrange mapping."""


def _decode_hex(token: str, what: str) -> bytes:
    if not token or len(token) & 1 or any(c not in _HEX for c in token):
        raise CMapError(f"malformed {what} hex string: {token!r}")
    return bytes.fromhex(token)


def _decode_utf16be(raw: bytes, what: str) -> str:
    if not raw or len(raw) & 1:
        raise CMapError(f"malformed {what}: {raw.hex()!r} is not UTF16BE")
    try:
        text = raw.decode("utf-16be")
    except UnicodeDecodeError as exc:
        raise CMapError(f"invalid {what} {raw.hex()!r}: {exc}") from None
    for ch in text:
        if "\ud800" <= ch <= "\udfff":  # unpaired surrogate
            raise CMapError(f"surrogate destination in {what} {raw.hex()!r}")
    return text


def _decode_source(raw: bytes, what: str) -> int:
    if len(raw) > 2:  # codespace range of the pinned CMap is 16 bit
        raise CMapError(f"{what} {raw.hex()!r} is outside 16 bits")
    return int.from_bytes(raw, "big")


def _strip_comments(text: str) -> str:
    out = []
    for line in text.splitlines():
        cut = line.find("%")
        out.append(line if cut < 0 else line[:cut])
    return "\n".join(out)


def _read_hex(tokens: list[str], i: int, what: str) -> tuple[bytes, int]:
    if i + 2 >= len(tokens) or tokens[i] != "<" or tokens[i + 2] != ">":
        raise CMapError(f"expected a <hex> {what} literal")
    return _decode_hex(tokens[i + 1], what), i + 3


def _parse_bfchar(tokens: list[str], i: int, count: int, entries: dict[int, str]) -> int:
    for _ in range(count):
        src_raw, i = _read_hex(tokens, i, "source code")
        dst_raw, i = _read_hex(tokens, i, "destination")
        src = _decode_source(src_raw, "source code")
        dst = _decode_utf16be(dst_raw, "destination")
        if src in entries:
            raise CMapError(f"duplicate CID {src:#06x}")
        entries[src] = dst
    return i


def _parse_bfrange(tokens: list[str], i: int, count: int, entries: dict[int, str]) -> int:
    for _ in range(count):
        lo_raw, i = _read_hex(tokens, i, "range start")
        hi_raw, i = _read_hex(tokens, i, "range end")
        lo = _decode_source(lo_raw, "range start")
        hi = _decode_source(hi_raw, "range end")
        if lo > hi:
            raise CMapError(f"descending range {lo:#06x}..{hi:#06x}")
        span = hi - lo + 1
        targets: list[str] = []
        if i < len(tokens) and tokens[i] == "[":
            i += 1
            while i < len(tokens) and tokens[i] != "]":
                raw, i = _read_hex(tokens, i, "destination")
                targets.append(_decode_utf16be(raw, "destination"))
            if i >= len(tokens):
                raise CMapError("unterminated bfrange destination array")
            i += 1
            if len(targets) != span:
                raise CMapError(f"bfrange array has {len(targets)} entries for {span} codes")
        else:
            raw, i = _read_hex(tokens, i, "destination")
            first = _decode_utf16be(raw, "destination")
            if len(first) != 1:
                if span > 1:
                    raise CMapError("multi-character bfrange destination cannot span a range")
                targets.append(first)
            else:
                # Adobe increments the destination code point, which may leave
                # the BMP, so the pair is decoded, offset and re-encoded.
                for offset in range(span):
                    value = ord(first) + offset
                    if value > 0x10FFFF or 0xD800 <= value <= 0xDFFF:
                        raise CMapError(f"bfrange destination {value:#x} is not a scalar value")
                    targets.append(chr(value))
        for offset, target in enumerate(targets):
            cid = lo + offset
            if cid in entries:
                raise CMapError(f"duplicate CID {cid:#06x}")
            entries[cid] = target
    return i


def _skip_records(tokens: list[str], i: int, count: int, per_record: int) -> int:
    for _ in range(count):
        for _ in range(per_record):
            _, i = _read_hex(tokens, i, "code range bound")
    return i


def parse_cmap(text: str) -> dict[int, str]:
    """Expand counted bfchar/bfrange records of a CMap into CID -> Unicode text."""
    body = _strip_comments(text)
    # A <hex> literal becomes the three tokens "<", hex, ">" so record parsing
    # stays independent of the spacing around the delimiters and the digits.
    tokens: list[str] = []
    for match in re.finditer(r"<([^>]*)>|\[|\]|(\S+)", body):
        hexed = match.group(1)
        if hexed is None:
            tokens.append(match.group(0))
        else:
            tokens.extend(("<", hexed, ">"))
    entries: dict[int, str] = {}
    blocks = {"beginbfchar", "beginbfrange", "begincodespacerange"}
    i = 0
    while i < len(tokens):
        # The PostScript preamble, trailer and resource keys carry no mappings;
        # only "<count> begin<something>" starts a record block.
        if not tokens[i].isdigit() or i + 1 >= len(tokens) or tokens[i + 1] not in blocks:
            i += 1
            continue
        count = int(tokens[i])
        kind = tokens[i + 1]
        i += 2
        if kind == "beginbfchar":
            i = _parse_bfchar(tokens, i, count, entries)
            closer = "endbfchar"
        elif kind == "beginbfrange":
            i = _parse_bfrange(tokens, i, count, entries)
            closer = "endbfrange"
        else:
            i = _skip_records(tokens, i, count, 2)
            closer = "endcodespacerange"
        if i >= len(tokens) or tokens[i] != closer:
            raise CMapError(f"missing {closer} after {kind}")
        i += 1
    return entries


def _csharp(data: bytes) -> str:
    buffer = io.BytesIO()
    with gzip.GzipFile(filename="", fileobj=buffer, mode="wb", compresslevel=9, mtime=0) as stream:
        stream.write(data)
    packed = base64.b64encode(buffer.getvalue()).decode("ascii")
    lines = "\n".join(f'        {"" if k == 0 else "+ "}"{packed[k:k + B64_LINE]}"' for k in range(0, len(packed), B64_LINE))
    return f"""// <auto-generated/>
// Generated by generate-pdf-japan1.py. Do not edit.
//
// Source: Adobe-Japan1-UCS2 CMap (Supplement {SUPPLEMENT})
//   {UPSTREAM_URL}
//   upstream revision {UPSTREAM_REVISION}
//   SHA-256 {UPSTREAM_SHA256}
// Copyright 1990-2023 Adobe. All rights reserved.
// SPDX-License-Identifier: BSD-3-Clause
// License text: {UPSTREAM_NOTICE_PATH} in the upstream repository.

namespace {NAMESPACE};

/// <summary>Gzipped Adobe-Japan1-UCS2 CMap bytes, base64 encoded.</summary>
internal static class {CLASS_NAME}
{{
    internal const int Supplement = {SUPPLEMENT};

    internal const string {FIELD_NAME} =
{lines};
}}
"""


def _def_value(text: str, key: str) -> str:
    for line in _strip_comments(text).splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[0] == key and parts[2] == "def":
            return parts[1]
    raise ValueError(f"{key} not found")


def generate(data: bytes) -> str:
    """Return the deterministic C# data source for the pinned upstream file."""
    digest = hashlib.sha256(data).hexdigest()
    if digest != UPSTREAM_SHA256:
        raise ValueError(f"unexpected SHA-256 {digest} for {CNAME}; expected {UPSTREAM_SHA256}")
    text = data.decode("ascii")
    entries = parse_cmap(text)
    if _def_value(text, "/CMapName").lstrip("/") != CNAME:
        raise ValueError(f"input is not the {CNAME} CMap")
    if int(_def_value(text, "/Supplement")) != SUPPLEMENT:
        raise ValueError(f"input is not Supplement {SUPPLEMENT}")
    if not entries:
        raise ValueError("no CMap mappings parsed")
    return _csharp(data)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate the Adobe-Japan1-UCS2 C# data source.")
    parser.add_argument("--input", required=True, type=Path, help="path of the offline Adobe-Japan1-UCS2 file")
    parser.add_argument("--output", required=True, type=Path, help="path of the generated C# file")
    parser.add_argument("--check", action="store_true", help="verify the existing output instead of writing it")
    args = parser.parse_args(argv)
    generated = generate(args.input.read_bytes())
    if args.check:
        try:
            current = args.output.read_text(encoding="utf-8")
        except OSError as exc:
            print(f"check failed: {exc}", file=sys.stderr)
            return 1
        if current != generated:
            print(f"check failed: {args.output} is not up to date", file=sys.stderr)
            return 1
        print(f"ok: {args.output} is up to date")
        return 0
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(generated)
    print(f"wrote {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

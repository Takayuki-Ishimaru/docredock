#!/usr/bin/env python3
"""Publication privacy gate: reject personal build paths inside CodeView PDB records.

Scans a distribution directory for executable/library/apphost files and inspects
every CodeView RSDS record. Only relative filenames, byte offsets and rule ids
are reported; the matched path or root is never printed.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path
from typing import Iterator

MAGIC = b"RSDS"
GUID_AGE_LEN = 20
MAX_PATH_LEN = 4096
RECORD_HEADER_LEN = len(MAGIC) + GUID_AGE_LEN
MAX_RECORD_LEN = RECORD_HEADER_LEN + MAX_PATH_LEN + 1
# Retain enough trailing bytes that a record (or its magic) split across a chunk
# boundary is still seen in full on the next chunk.
TAIL_KEEP = MAX_RECORD_LEN - 1 + len(MAGIC)
CHUNK_SIZE = 1 << 16

TARGET_SUFFIXES = (".exe", ".dll", ".pdb")
# Extensionless / custom-suffixed single-file apphost names (macOS/Linux).
HOST_NAMES = frozenset({"docredock", "docredock.cli"})

RULE_ID = "CV001"

# Personal rooted build paths, anchored to the start so mapped /_/... and
# relative names stay safe. Case-insensitive for Windows drive paths.
_WINDOWS_USER = re.compile(r"[A-Za-z]:[\\/]users[\\/]", re.IGNORECASE)
_UNIX_HOME = re.compile(r"^/home/[^/]+/")
_MAC_USERS = re.compile(r"^/users/[^/]+/", re.IGNORECASE)


def is_unsafe_path(path: str) -> bool:
    """True when a CodeView path is a personal rooted build path."""
    probe = path.replace("\\", "/")
    if _WINDOWS_USER.search(probe):
        return True
    if _UNIX_HOME.search(probe):
        return True
    if _MAC_USERS.search(probe):
        return True
    return False


def _decode(raw: bytes) -> str:
    return raw.decode("utf-8", "replace")


def is_target(name: str) -> bool:
    """True when a filename is an executable/library/apphost to scan."""
    lowered = name.lower()
    return lowered.endswith(TARGET_SUFFIXES) or lowered in HOST_NAMES


def scan_bytes(data: bytes, complete: bool):
    """Yield (rel_offset, kind, next_pos) for each CodeView record start.

    kind is "unsafe"/"safe" for a fully resolved record (next_pos is just past
    its terminator) or "incomplete" when the record is not yet finished and the
    scan must retain from next_pos for the next chunk.
    """
    pos = 0
    n = len(data)
    while True:
        idx = data.find(MAGIC, pos)
        if idx == -1:
            if not complete:
                keep_from = n - (len(MAGIC) - 1)
                if keep_from < pos:
                    keep_from = pos
                yield keep_from, "incomplete", keep_from
            return
        if not complete and idx > n - len(MAGIC):
            yield idx, "incomplete", idx
            return
        path_start = idx + RECORD_HEADER_LEN
        if not complete and path_start > n:
            yield idx, "incomplete", idx
            return
        end = data.find(b"\x00", path_start)
        if end == -1:
            if not complete and n - path_start <= MAX_PATH_LEN:
                yield idx, "incomplete", idx
                return
            pos = path_start
            continue
        if end - path_start > MAX_PATH_LEN:
            pos = path_start
            continue
        kind = "unsafe" if is_unsafe_path(_decode(data[path_start:end])) else "safe"
        yield idx, kind, end + 1
        pos = end + 1


def scan_file(path: Path):
    """Stream one file, yielding (offset, rule_id) for unsafe records."""
    with path.open("rb") as handle:
        buffer = b""
        buffer_start = 0
        eof = False
        while not eof:
            chunk = handle.read(CHUNK_SIZE)
            if not chunk:
                eof = True
            buffer += chunk
            consumed = len(buffer)
            for rel, kind, next_pos in scan_bytes(buffer, eof):
                if kind == "unsafe":
                    yield (buffer_start + rel, RULE_ID)
                consumed = next_pos
                if kind == "incomplete":
                    break
            buffer_start += consumed
            buffer = buffer[consumed:]
            if not buffer:
                buffer_start = handle.tell()
            elif len(buffer) > TAIL_KEEP:
                drop = len(buffer) - TAIL_KEEP
                buffer = buffer[drop:]
                buffer_start += drop


def redact_file(path: Path) -> None:
    """Remove private CodeView filenames without moving any binary content.

    Run before signing and checksums. Only debug-record pathname bytes change;
    retain the PDB basename and replace the rest with NUL padding in place.
    """
    with path.open("r+b") as output:
        for offset, _ in scan_file(path):
            start = offset + RECORD_HEADER_LEN
            output.seek(start)
            raw = output.read(MAX_PATH_LEN + 1)
            end = raw.find(b"\x00")
            if end < 0:
                raise OSError("CodeView record changed during redaction")
            original = raw[:end]
            basename = original.replace(b"\\", b"/").rsplit(b"/", 1)[-1]
            replacement = b"/_/" + basename
            if len(replacement) > len(original):
                raise OSError("CodeView pathname cannot be redacted in place")
            output.seek(start)
            output.write(replacement + b"\x00" * (len(original) - len(replacement)))
            output.flush()


def iter_target_files(root: Path) -> Iterator[Path]:
    """Yield regular target files under root without following symlinks."""
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = [
            name
            for name in dirnames
            if not os.path.islink(os.path.join(dirpath, name))
        ]
        for name in filenames:
            if not is_target(name):
                continue
            full = Path(dirpath) / name
            if full.is_symlink() or not full.is_file():
                continue
            yield full


def check_root(root: Path) -> tuple[Iterator[tuple[str, int, str]], list[str]]:
    """Return (streamed findings, errors) for a distribution root."""
    errors: list[str] = []
    scanned = 0

    def findings() -> Iterator[tuple[str, int, str]]:
        nonlocal scanned
        for path in sorted(iter_target_files(root)):
            scanned += 1
            rel = path.relative_to(root).as_posix()
            try:
                for offset, rule in scan_file(path):
                    yield (rel, offset, rule)
            except OSError:
                errors.append(rel)
        if scanned == 0:
            errors.append("no executable/library files")

    return findings(), errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--redact", action="store_true",
                        help="Remove private debug paths in place before signing/checksums.")
    args = parser.parse_args(argv)
    root = args.root
    if not root.is_dir():
        print("privacy gate: root is not a directory", file=sys.stderr)
        return 2
    if args.redact:
        for path in iter_target_files(root):
            try:
                redact_file(path)
            except OSError:
                print("privacy gate: debug-path redaction failed", file=sys.stderr)
                return 1
    findings, errors = check_root(root)
    found = False
    for rel, offset, rule in findings:
        found = True
        print(f"privacy gate: {rule} {rel} @{offset}")
    for rel in errors:
        found = True
        print(f"privacy gate: cannot read file: {rel}", file=sys.stderr)
    if found:
        return 1
    print("privacy gate: no personal build paths found")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

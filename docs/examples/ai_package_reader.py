"""Read DocRedock AI packages (directory or ZIP) with the standard library only.

See docs/reference/ai-package.md for the package format. Manifest schema 1.x
carries node ids and per-node sources inline; 2.x moves them to
source-index.json and shares locations across parts.

load_manifest checks the manifest before anything is read through it: every
field this reader uses must be present with its documented type, ids and paths
must be unique, and every file the manifest refers to (document, review,
report, source index, parts) must be listed in "files" with its size and
SHA-256, so verification can never pass without checking it. A manifest that
fails a check raises PackageError (UnsupportedSchemaError for a schema version
this reader does not know) instead of being read in part.

Usage:
    python3 docs/examples/ai_package_reader.py PACKAGE [--verify] [--part PART_ID] [--fail-on-review]

Checks run before anything is printed. --verify prints "files ok" or one line
per listed file that is missing, changed or refused (a link, a pipe or device,
an encrypted or unreadable ZIP entry); with --part those lines go to stderr and
the part is printed, as the exact bytes that were verified, only when every
listed file matches. The .NET AiPackageManifestReader reports the same lines.
Exit codes: 0 success; 1 --verify found a listed file missing, changed
or refused; 2 the package cannot be read (not a package directory or ZIP, an
ambiguous ZIP, unsupported schema, malformed JSON or manifest, a manifest path
that would leave the package, unknown part id); 3 --fail-on-review and
report.json says the conversion needs review.
"""

import argparse
import errno
import hashlib
import json
import posixpath
import re
import stat
import struct
import sys
import zipfile
import zlib
from pathlib import Path

SCHEMA_RE = re.compile(r"(1|2)\.([0-9]+)")
# The source index and report have their own schema versions; this reader knows 1.x of both.
SUBSCHEMA_RE = re.compile(r"1\.[0-9]+")
SHA256_RE = re.compile(r"[0-9a-fA-F]{64}")
SUPPORTED_MESSAGE = "supported: 1.x, 2.x"
# The nesting System.Text.Json reads by default, so both example readers accept the same files.
MAX_DEPTH = 64
LOCATION_FIELDS = ("partition_id", "label", "page_number", "slide_number", "sheet_name", "heading_path")
MAX_COUNT = 2 ** 31 - 1
MAX_BYTES = 2 ** 63 - 1
# Files larger than this are refused rather than read into memory (the .NET reader uses the same limit).
MAX_FILE_BYTES = 2147483520


class PackageError(ValueError):
    """The package cannot be read safely: it is not a package directory or ZIP, a JSON file is
    malformed or lacks a field this reader relies on, an id, path or ZIP entry appears twice, a
    path would leave the package, or a referenced file is missing from the manifest's file list."""


class UnsupportedSchemaError(PackageError):
    """Raised when a manifest schema_version is absent, malformed or unsupported."""


def _safe_rel(rel):
    """Return a relative package path, rejecting absolute or drive paths, "." and ".." segments,
    backslashes, colons (C:x, C:/..., ADS names) and NUL: package names never contain them."""
    if not isinstance(rel, str) or not rel or any(c in rel for c in "\\:\0") or posixpath.isabs(rel):
        raise PackageError("unsafe package path: " + repr(rel))
    if any(segment in ("", ".", "..") for segment in rel.split("/")):
        raise PackageError("unsafe package path: " + repr(rel))
    return rel


def _extra_ids(extra):
    """The ids of a ZIP extra field's blocks."""
    ids = set()
    while len(extra) >= 4:
        block, length = struct.unpack_from("<HH", extra)
        ids.add(block)
        extra = extra[4 + length:]
    return ids


def _too_large(rel):
    return PackageError("cannot read %r: it is larger than %d bytes" % (rel, MAX_FILE_BYTES))


def _missing(rel):
    return FileNotFoundError(errno.ENOENT, "file not found in the package", rel)


def _decode(data, name):
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise PackageError("%s is not UTF-8 text (%s)" % (name, exc))


def _zip_end(path):
    """Start, size and entry count of the central directory and the position of the end record that
    describes it, found as Python's zipfile finds them: the last 22 bytes, else the last signature in
    the final 64 KiB, then the Zip64 records its locator points to."""
    with open(path, "rb") as fp:
        fp.seek(0, 2)
        size = fp.tell()
        start = max(size - 65535 - 22, 0)
        fp.seek(start)
        tail = fp.read()
        at = (len(tail) - 22 if len(tail) >= 22 and tail[-22:-18] == b"PK\x05\x06" and tail[-2:] == b"\0\0"
              else tail.rfind(b"PK\x05\x06"))
        if at < 0 or at + 22 > len(tail):
            raise PackageError("not a package folder or ZIP archive: %s (File is not a zip file)" % path)
        disk, directory_disk, _, count, directory_size, directory_offset = struct.unpack_from("<HHHHII", tail, at + 4)
        location = start + at
        if location >= 20:
            fp.seek(location - 20)
            locator = fp.read(20)
            if locator[:4] == b"PK\x06\x07":
                record = struct.unpack_from("<Q", locator, 8)[0]
                fp.seek(record)
                data = fp.read(56)
                if len(data) == 56 and data[:4] == b"PK\x06\x06":
                    disk, directory_disk, _, count, directory_size, directory_offset = struct.unpack_from("<IIQQQQ", data, 16)
                    location = record
    return disk, directory_disk, directory_offset, directory_size, count, location


def _entry_name(info):
    """The name other ZIP readers (.NET included) see for an entry. A name without the UTF-8 flag
    that is valid UTF-8 (as macOS ditto writes them) is read as UTF-8, not as code page 437."""
    name = info.orig_filename
    if not info.flag_bits & 0x800:
        try:
            name = name.encode("cp437").decode("utf-8")
        except (UnicodeEncodeError, UnicodeDecodeError):
            pass
    return name


class Package:
    """Read-only view of an AI package directory or ZIP archive."""

    def __init__(self, path):
        self.path = Path(path)
        self._zip = None
        self._entries = {}
        if self.path.is_dir():
            self._kind = "dir"
        elif self.path.is_file():
            try:
                self._zip = zipfile.ZipFile(self.path)
            except NotImplementedError as exc:
                raise PackageError("%s uses a ZIP feature this reader does not read (%s)" % (self.path, exc))
            except (zipfile.BadZipFile, EOFError, ValueError) as exc:
                raise PackageError("not a package folder or ZIP archive: %s (%s)" % (self.path, exc))
            self._kind = "zip"
            try:
                self._index_entries()
            except PackageError:
                self.close()
                raise
        else:
            raise FileNotFoundError(errno.ENOENT, "package not found", str(self.path))

    def _index_entries(self):
        # A package ZIP is read only when it is plain, so that every reader reads it the same way: the
        # .NET AiPackageManifestReader makes the same checks in the same order with the same messages.
        # Readers disagree on which of two equal names they return (Python the last, .NET the first),
        # on names with a NUL (Python cuts them there) or a backslash (Python on Windows turns it into
        # "/"), on a Unicode Path extra field (Python 3.12 renames the entry by it), and on end records
        # that do not describe the directory they end.
        infos = self._zip.infolist()
        disk, directory_disk, offset, size, count, location = _zip_end(self.path)
        if disk or directory_disk:
            raise PackageError("the ZIP spans several disks")
        if offset + size != location:
            raise PackageError("the ZIP's central directory does not end where its end record begins")
        if count != len(infos):
            raise PackageError("the ZIP's end record counts %d entries, but its central directory holds %d"
                               % (count, len(infos)))
        twice = set()
        for info in infos:
            name = _entry_name(info)
            if "\0" in name or "\\" in name:
                raise PackageError("the ZIP holds an entry name with a %s: %r"
                                   % ("NUL" if "\0" in name else "backslash", name))
            if _extra_ids(info.extra) & {0x7075}:
                raise PackageError("the ZIP gives %r a Unicode Path extra field" % name)
            if name in self._entries:
                twice.add(name)
            self._entries[name] = info
        if twice:
            raise PackageError("the ZIP holds more than one entry named " + ", ".join(map(repr, sorted(twice))))
        # Where each entry must end: at the next local header or the central directory (Python 3.12).
        self._ends = {}
        end = self._zip.start_dir
        for info in reversed(sorted(infos, key=lambda item: item.header_offset)):
            self._ends[id(info)] = end
            end = info.header_offset

    def _dir_file(self, rel):
        # A link inside a package folder could point anywhere, and a pipe or device could block or
        # never end; DocRedock writes neither. A folder where a file should be is a missing file.
        current = self.path
        for segment in rel.split("/"):
            current = current / segment
            if current.is_symlink() or getattr(current, "is_junction", lambda: False)():
                raise PackageError("package path is a link: " + repr(rel))
        try:
            mode = current.stat().st_mode
        except (FileNotFoundError, NotADirectoryError):
            raise _missing(rel)
        if stat.S_ISDIR(mode):
            raise _missing(rel)
        if not stat.S_ISREG(mode):
            raise PackageError("package path is not a regular file: " + repr(rel))
        return current

    def _entry(self, rel):
        info = self._entries.get(rel)
        if info is None or info.is_dir():
            raise _missing(rel)
        if info.flag_bits & 0x1:
            raise PackageError("%r is encrypted in the ZIP" % rel)
        if info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            raise PackageError("%r uses ZIP compression method %d; packages use deflate" % (rel, info.compress_type))
        return info

    def size(self, rel):
        """Size in bytes of a package file, found without reading the file."""
        rel = _safe_rel(rel)
        if self._kind == "dir":
            return self._dir_file(rel).stat().st_size
        return self._entry(rel).file_size

    def _open_checks(self, rel, info):
        # What zipfile checks when it opens an entry, in its order and with its messages, including
        # the overlap check only Python 3.12 has, so every Python version refuses the same entries.
        def unreadable(reason):
            return PackageError("cannot read %r from the ZIP: %s" % (rel, reason))
        with open(self.path, "rb") as fp:
            fp.seek(info.header_offset)
            header = fp.read(30)
            if len(header) != 30:
                raise unreadable("Truncated file header")
            if header[:4] != b"PK\x03\x04":
                raise unreadable("Bad magic number for file header")
            flags, = struct.unpack_from("<H", header, 6)
            name_length, extra_length = struct.unpack_from("<HH", header, 26)
            name = fp.read(name_length)
        if info.flag_bits & 0x20:
            raise unreadable("compressed patched data (flag bit 5)")
        if info.flag_bits & 0x40:
            raise unreadable("strong encryption (flag bit 6)")
        try:
            local = name.decode("utf-8" if flags & 0x800 else "cp437")
        except UnicodeDecodeError:
            raise unreadable("the file name in its header is not UTF-8")
        if local != info.orig_filename:
            raise unreadable("File name in directory %r and header %r differ." % (info.orig_filename, name))
        end = self._ends[id(info)]
        if info.header_offset + 30 + name_length + extra_length + info.compress_size > end and end != info.header_offset:
            raise unreadable("Overlapped entries: %r (possible zip bomb)" % info.orig_filename)

    def read_bytes(self, rel):
        rel = _safe_rel(rel)
        if self._kind == "dir":
            path = self._dir_file(rel)
            if path.stat().st_size > MAX_FILE_BYTES:
                raise _too_large(rel)
            try:
                return path.read_bytes()
            except FileNotFoundError:
                raise _missing(rel)
        info = self._entry(rel)
        self._open_checks(rel, info)
        if info.file_size > MAX_FILE_BYTES:
            raise _too_large(rel)
        try:
            data = self._zip.read(info)
        except EOFError:
            data = None
        except (zipfile.BadZipFile, NotImplementedError, zlib.error) as exc:
            raise PackageError("cannot read %r from the ZIP: %s" % (rel, exc))
        if data is None or len(data) != info.file_size:
            raise PackageError("cannot read %r from the ZIP: it ends before its size in the ZIP" % rel)
        return data

    def read_text(self, rel):
        return _decode(self.read_bytes(rel), rel)

    def exists(self, rel):
        try:
            self.size(rel)
        except (FileNotFoundError, ValueError):
            return False
        return True

    def nested_manifest(self):
        """A manifest.json one folder down, which means the package's parent was given or the
        package folder itself was zipped; None when there is none."""
        if self._kind == "zip":
            names = sorted(n for n in self._entries if n.count("/") == 1 and n.endswith("/manifest.json"))
        else:
            names = sorted(p.relative_to(self.path).as_posix() for p in self.path.glob("*/manifest.json"))
        return names[0] if names else None

    def close(self):
        if self._zip is not None:
            self._zip.close()
            self._zip = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        self.close()
        return False


def open_package(path):
    """Open an AI package directory or .zip file."""
    return Package(path)


def _parse_json(data, name):
    """Parse a package JSON file. Duplicate keys and NaN/Infinity are refused: readers that keep
    the first or the last of two values for one key would disagree about the package."""
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise PackageError("%s has the key %r more than once" % (name, key))
            result[key] = value
        return result

    def constant(value):
        raise PackageError("%s contains %s, which JSON does not allow" % (name, value))

    def integer(text):
        # Python refuses to convert more than 4300 digits; such a number is no valid count anyway,
        # and an unknown field holding one is ignored like any other.
        return int(text) if len(text) <= 4300 else float("inf")

    text = _decode(data, name)
    try:
        value = json.loads(text, object_pairs_hook=unique, parse_constant=constant, parse_int=integer)
    except PackageError:
        raise
    except RecursionError:
        raise PackageError("%s is nested more than %d levels deep" % (name, MAX_DEPTH))
    except ValueError as exc:
        raise PackageError("%s is not readable JSON: %s" % (name, exc))
    stack = [(value, 1)]
    while stack:
        item, depth = stack.pop()
        if isinstance(item, (dict, list)):
            if depth > MAX_DEPTH:
                raise PackageError("%s is nested more than %d levels deep" % (name, MAX_DEPTH))
            stack.extend((child, depth + 1) for child in (item.values() if isinstance(item, dict) else item))
    try:
        # "\ud800" without its pair parses, but is no text: it cannot be printed or compared reliably.
        json.dumps(value, ensure_ascii=False).encode("utf-8")
    except UnicodeEncodeError:
        raise PackageError("%s contains an unpaired surrogate escape (\\ud800-\\udfff)" % name)
    return value


# Field checks. "where" names the field in messages, e.g. parts[2].table_block.index.
def _kind_of(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "a boolean"
    if isinstance(value, (int, float)):
        return "a number"
    if isinstance(value, str):
        return "a string"
    return "an array" if isinstance(value, list) else "an object"


def _object(value, where):
    if not isinstance(value, dict):
        raise PackageError("%s must be an object, got %s" % (where, _kind_of(value)))
    return value


def _get(obj, key, where, optional):
    name = where + "." + key if where else key
    if obj.get(key) is None:
        if optional:
            return name, None
        raise PackageError(name + (" is null" if key in obj else " is missing"))
    return name, obj[key]


def _text(obj, key, where="", optional=False):
    name, value = _get(obj, key, where, optional)
    if value is not None and not isinstance(value, str):
        raise PackageError("%s must be a string, got %s" % (name, _kind_of(value)))
    return value


def _count(obj, key, where="", optional=False, maximum=MAX_COUNT):
    name, value = _get(obj, key, where, optional)
    if value is not None and (isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= maximum):
        raise PackageError("%s must be a whole number from 0 to %d, got %s" % (name, maximum, json.dumps(value)))
    return value


def _flag(obj, key, where=""):
    name, value = _get(obj, key, where, True)
    if value is not None and not isinstance(value, bool):
        raise PackageError("%s must be true or false, got %s" % (name, _kind_of(value)))
    return bool(value)


def _array(obj, key, where="", optional=False):
    name, value = _get(obj, key, where, optional)
    if value is None:
        return None
    if not isinstance(value, list):
        raise PackageError("%s must be an array, got %s" % (name, _kind_of(value)))
    return value


def _strings(obj, key, where="", optional=False):
    values = _array(obj, key, where, optional)
    if values is not None and not all(isinstance(value, str) for value in values):
        raise PackageError("%s must hold only strings" % (where + "." + key if where else key))
    return values


def _path(obj, key, where="", optional=False):
    value = _text(obj, key, where, optional)
    if value is None:
        return None
    try:
        return _safe_rel(value)
    except PackageError as exc:
        raise PackageError("%s: %s" % (where + "." + key if where else key, exc))


def _sha256(obj, key, where):
    value = _text(obj, key, where)
    if not SHA256_RE.fullmatch(value):
        raise PackageError("%s.%s must be 64 hexadecimal digits, got %r" % (where, key, value))
    return value.lower()


def _parse_version(raw):
    if not isinstance(raw, str):
        raise UnsupportedSchemaError(
            "manifest schema_version must be a string like \"1.0\", got "
            + repr(raw) + " (" + SUPPORTED_MESSAGE + ")")
    match = SCHEMA_RE.fullmatch(raw)
    if not match:
        raise UnsupportedSchemaError(
            "manifest schema_version " + repr(raw) + " is unsupported (" + SUPPORTED_MESSAGE + ")")
    return int(match.group(1)), int(match.group(2))


def _hashable(value):
    if isinstance(value, list):
        return tuple(_hashable(item) for item in value)
    return value


def _location_key(source):
    return tuple(_hashable(source.get(field)) for field in LOCATION_FIELDS)


def _location_fields(raw, where):
    return {
        "partition_id": _text(raw, "partition_id", where),
        "label": _text(raw, "label", where),
        "page_number": _count(raw, "page_number", where, optional=True),
        "slide_number": _count(raw, "slide_number", where, optional=True),
        "sheet_name": _text(raw, "sheet_name", where, optional=True),
        "heading_path": _strings(raw, "heading_path", where, optional=True),
    }


def _table_block(raw_part, where):
    where += ".table_block"
    raw = raw_part.get("table_block")
    if raw is None:
        return None
    _object(raw, where)
    index, count = _count(raw, "index", where), _count(raw, "count", where)
    if not 1 <= index <= count:
        raise PackageError("%s.index must be from 1 to count (%d), got %d" % (where, count, index))
    return {
        "table_id": _text(raw, "table_id", where),
        "index": index,
        "count": count,
        "header_range": _text(raw, "header_range", where, optional=True),
        "row_range": _text(raw, "row_range", where),
        # 2.2: the cells of the title or unit note repeated before the header, when there is one.
        "context_range": _text(raw, "context_range", where, optional=True),
        "previous_part": _text(raw, "previous_part", where, optional=True),
        "next_part": _text(raw, "next_part", where, optional=True),
    }


def _normalize_v1(manifest):
    locations = []
    id_by_key = {}
    parts = []
    for position, raw_part in enumerate(_array(manifest, "parts")):
        where = "parts[%d]" % position
        _object(raw_part, where)
        node_ids = _strings(raw_part, "node_ids", where)
        sources = _array(raw_part, "sources", where)
        if len(sources) != len(node_ids):
            raise PackageError("%s lists %d node_ids but %d sources; schema 1.x pairs them one to one"
                               % (where, len(node_ids), len(sources)))
        location_ids = []
        inline = []
        for index, raw_source in enumerate(sources):
            source_where = "%s.sources[%d]" % (where, index)
            _object(raw_source, source_where)
            node_id = _text(raw_source, "node_id", source_where, optional=True)
            if node_id is not None and node_id != node_ids[index]:
                raise PackageError("%s.node_id %r is not node_ids[%d] %r" % (source_where, node_id, index, node_ids[index]))
            fields = _location_fields(raw_source, source_where)
            key = _location_key(fields)
            if key not in id_by_key:
                id_by_key[key] = "source-%04d" % (len(locations) + 1)
                locations.append(dict({"id": id_by_key[key]}, **fields))
            if id_by_key[key] not in location_ids:
                location_ids.append(id_by_key[key])
            inline.append({"location_id": id_by_key[key],
                           "cell_address": _text(raw_source, "cell_address", source_where, optional=True)})
        parts.append(_part(raw_part, where, len(node_ids), location_ids, None, None))
        parts[-1]["node_ids"] = node_ids
        parts[-1]["inline_sources"] = inline
    return locations, parts


def _normalize_v2(manifest):
    locations = []
    by_id = {}
    for position, raw in enumerate(_array(manifest, "locations")):
        where = "locations[%d]" % position
        _object(raw, where)
        location_id = _text(raw, "id", where)
        if location_id in by_id:
            raise PackageError("locations list %r twice" % location_id)
        by_id[location_id] = dict({"id": location_id}, **_location_fields(raw, where))
        locations.append(by_id[location_id])
    parts = []
    for position, raw_part in enumerate(_array(manifest, "parts")):
        where = "parts[%d]" % position
        _object(raw_part, where)
        part = _part(raw_part, where, _count(raw_part, "node_count", where), _strings(raw_part, "source_ids", where),
                     _count(raw_part, "estimated_tokens", where, optional=True), _table_block(raw_part, where))
        for location_id in part["location_ids"]:
            if location_id not in by_id:
                raise PackageError("part %s names an unknown location %r" % (part["id"], location_id))
        if len(set(part["location_ids"])) != len(part["location_ids"]):
            raise PackageError("part %s names a location twice in source_ids" % part["id"])
        parts.append(part)
    return locations, parts


def _part(raw_part, where, node_count, location_ids, estimated_tokens, table_block):
    return {
        "id": _text(raw_part, "id", where),
        "path": _path(raw_part, "path", where),
        "node_count": node_count,
        "location_ids": location_ids,
        "locations": None,
        "exceeds_target": _flag(raw_part, "exceeds_target", where),
        "estimated_tokens": estimated_tokens,
        "table_block": table_block,
        # Schema 1.x only: node ids, and each node's location id and cell address in the same order.
        "node_ids": None,
        "inline_sources": None,
    }


def _check_parts(parts, locations):
    by_id = {location["id"]: location for location in locations}
    ids = set()
    paths = set()
    for part in parts:
        if part["id"] in ids:
            raise PackageError("parts list the id %r twice" % part["id"])
        if part["path"] in paths:
            raise PackageError("parts list the path %r twice" % part["path"])
        ids.add(part["id"])
        paths.add(part["path"])
        part["locations"] = [by_id[location_id] for location_id in part["location_ids"]]
    for part in parts:
        block = part["table_block"]
        for key in ("previous_part", "next_part"):
            if block and block[key] is not None and block[key] not in ids:
                raise PackageError("part %s %s names an unknown part %r" % (part["id"], key, block[key]))


def _files(manifest):
    entries = []
    seen = set()
    for position, raw in enumerate(_array(manifest, "files")):
        where = "files[%d]" % position
        _object(raw, where)
        path = _path(raw, "path", where)
        if path in seen:
            raise PackageError("files list %r twice" % path)
        seen.add(path)
        entries.append({"path": path, "bytes": _count(raw, "bytes", where, maximum=MAX_BYTES),
                        "sha256": _sha256(raw, "sha256", where)})
    return entries


def load_manifest(package):
    """Read manifest.json, check it and normalize it across schema versions.

    Raises UnsupportedSchemaError for a missing or unknown schema_version and PackageError for
    every other problem: a missing field or one of the wrong type, an id or path listed twice, a
    part naming an unknown location or part, a 1.x part whose node_ids and sources differ in
    length, or a referenced file that "files" does not list with a size and SHA-256."""
    try:
        data = package.read_bytes("manifest.json")
    except FileNotFoundError:
        nested = package.nested_manifest()
        hint = "; found %r, but package files must be at the top level" % nested if nested else ""
        raise UnsupportedSchemaError("manifest.json is missing" + hint + " (" + SUPPORTED_MESSAGE + ")")
    manifest = _parse_json(data, "manifest.json")
    if not isinstance(manifest, dict):
        raise UnsupportedSchemaError("manifest.json is not a JSON object (" + SUPPORTED_MESSAGE + ")")
    if "schema_version" not in manifest:
        raise UnsupportedSchemaError("manifest schema_version is missing (" + SUPPORTED_MESSAGE + ")")
    major, minor = _parse_version(manifest["schema_version"])
    source = _object(_get(manifest, "source", "", False)[1], "source")
    files = _files(manifest)
    if major == 1:
        locations, parts = _normalize_v1(manifest)
        source_index = None
    else:
        locations, parts = _normalize_v2(manifest)
        source_index = _path(manifest, "source_index", optional=True)
    _check_parts(parts, locations)
    result = {
        "schema_version": manifest["schema_version"],
        "major": major,
        "minor": minor,
        "generator_version": _text(manifest, "generator_version", optional=True),
        "source": {
            "file_name": _text(source, "file_name", "source"),
            "sha256": _sha256(source, "sha256", "source"),
            "format": _text(source, "format", "source"),
        },
        "content_policy": _text(manifest, "content_policy"),
        "target_characters": _count(manifest, "target_characters", optional=True),
        "table_row_blocks": _flag(manifest, "table_row_blocks"),
        "document": _path(manifest, "document", optional=True) or "document.md",
        "review": _path(manifest, "review", optional=True) or "review.md",
        "report": _path(manifest, "report", optional=True) or "report.json",
        "source_index": source_index,
        "locations": locations,
        "parts": parts,
        "files": files,
    }
    # A file the manifest names but does not list would pass --verify unchecked.
    listed = {entry["path"] for entry in files}
    named = [result["document"], result["review"], result["report"]]
    named += ([source_index] if source_index else []) + [part["path"] for part in parts]
    unlisted = [path for path in named if path not in listed]
    if unlisted:
        shown = ", ".join(map(repr, unlisted[:5])) + (" and %d more" % (len(unlisted) - 5) if len(unlisted) > 5 else "")
        raise PackageError("manifest files does not list " + shown + ", so its size and SHA-256 cannot be checked")
    return result


def _source_index_v1(manifest):
    index = {}
    for part in manifest["parts"]:
        groups = []
        by_location = {}
        for node_id, source in zip(part["node_ids"], part["inline_sources"]):
            group = by_location.get(source["location_id"])
            if group is None:
                group = {"source_id": source["location_id"], "node_ids": [], "cell_addresses": []}
                by_location[source["location_id"]] = group
                groups.append(group)
            group["node_ids"].append(node_id)
            group["cell_addresses"].append(source["cell_address"])
        for group in groups:
            if all(cell is None for cell in group["cell_addresses"]):
                group["cell_addresses"] = None
        index[part["id"]] = groups
    return index


def load_source_index(package, manifest, data=None):
    """Return part id -> [{"source_id", "node_ids", "cell_addresses"}] for every manifest part.

    Schema 1.x groups the inline node ids. For 2.x the index must agree with the manifest: its
    schema_version is 1.x, it lists each manifest part exactly once, a part's groups name exactly
    that part's locations, their node ids add up to its node_count, and cell_addresses, when
    present, has one entry per node id. data, when given, is the index's bytes already read."""
    if manifest["major"] == 1:
        return _source_index_v1(manifest)
    if manifest["source_index"] is None:
        raise PackageError("manifest has no source_index")
    name = manifest["source_index"]
    raw = _object(_parse_json(package.read_bytes(name) if data is None else data, name), name)
    version = _text(raw, "schema_version", name)
    if not SUBSCHEMA_RE.fullmatch(version):
        raise UnsupportedSchemaError("%s schema_version %r is unsupported (supported: 1.x)" % (name, version))
    parts = {part["id"]: part for part in manifest["parts"]}
    index = {}
    for position, raw_part in enumerate(_array(raw, "parts", name)):
        where = "%s parts[%d]" % (name, position)
        _object(raw_part, where)
        part_id = _text(raw_part, "id", where)
        if part_id not in parts:
            raise PackageError("%s names a part the manifest does not list: %r" % (where, part_id))
        if part_id in index:
            raise PackageError("%s lists part %r twice" % (name, part_id))
        groups = []
        for group_position, raw_group in enumerate(_array(raw_part, "sources", where)):
            group_where = "%s.sources[%d]" % (where, group_position)
            _object(raw_group, group_where)
            node_ids = _strings(raw_group, "node_ids", group_where)
            cells = _array(raw_group, "cell_addresses", group_where, optional=True)
            if cells is not None and (len(cells) != len(node_ids) or
                                      not all(cell is None or isinstance(cell, str) for cell in cells)):
                raise PackageError("%s.cell_addresses must hold one address or null for each of its %d node_ids"
                                   % (group_where, len(node_ids)))
            groups.append({"source_id": _text(raw_group, "source_id", group_where), "node_ids": node_ids,
                           "cell_addresses": cells})
        named = [group["source_id"] for group in groups]
        if len(set(named)) != len(named) or set(named) != set(parts[part_id]["location_ids"]):
            raise PackageError("%s groups name the locations %s, but the manifest part lists %s"
                               % (where, named, parts[part_id]["location_ids"]))
        total = sum(len(group["node_ids"]) for group in groups)
        if total != parts[part_id]["node_count"]:
            raise PackageError("%s lists %d node ids, but the manifest part counts %d"
                               % (where, total, parts[part_id]["node_count"]))
        index[part_id] = groups
    absent = [part_id for part_id in parts if part_id not in index]
    if absent:
        raise PackageError("%s has no entry for %s" % (name, ", ".join(absent)))
    return index


def review_required(package, manifest, data=None):
    """True when report.json says something needs comparison with the source (review pages, OCR
    items, worksheet table boundaries). Read review.md before giving such a package to a model.
    data, when given, is the report's bytes already read."""
    name = manifest["report"]
    report = _object(_parse_json(package.read_bytes(name) if data is None else data, name), name)
    version = _text(report, "schema_version", name)
    if not SUBSCHEMA_RE.fullmatch(version):
        raise UnsupportedSchemaError("%s schema_version %r is unsupported (supported: 1.x)" % (name, version))
    review = _object(_get(report, "review", name, False)[1], name + ".review")
    name, value = _get(review, "required", name + ".review", False)
    if not isinstance(value, bool):
        raise PackageError("%s must be true or false, got %s" % (name, _kind_of(value)))
    return value


def read_part(package, part):
    """Return the Markdown text of one manifest part."""
    return package.read_text(part["path"])


def verify_files(package, manifest, contents=None):
    """Return one human-readable problem per listed file that is missing, refused or changed; an
    empty list means every file matches its size and SHA-256. load_manifest has already made sure
    that every file the manifest refers to is listed. contents maps package paths to bytes the
    caller already read; those bytes are checked instead of reading the file again, so a caller
    can use exactly the bytes that were verified."""
    contents = contents or {}
    problems = []
    for entry in manifest["files"]:
        path = entry["path"]
        data = contents.get(path)
        try:
            if data is None:
                # A different size already says the content changed; it is found without reading.
                size = package.size(path)
                if size != entry["bytes"]:
                    problems.append("%s: size mismatch (expected %d bytes, got %d)" % (path, entry["bytes"], size))
                    continue
                data = package.read_bytes(path)
        except FileNotFoundError:
            problems.append(path + ": missing")
            continue
        except ValueError as error:
            problems.append(path + ": refused (" + str(error) + ")")
            continue
        if len(data) != entry["bytes"]:
            problems.append("%s: size mismatch (expected %d bytes, got %d)" % (path, entry["bytes"], len(data)))
            continue
        actual = hashlib.sha256(data).hexdigest()
        if actual != entry["sha256"]:
            problems.append("%s: sha256 mismatch (expected %s, got %s)" % (path, entry["sha256"], actual))
    return problems


def _part_line(part):
    tokens = "-" if part["estimated_tokens"] is None else str(part["estimated_tokens"])
    labels = "; ".join(location["label"] for location in part["locations"])
    line = "%s %s tokens=%s %s" % (part["id"], part["path"], tokens, labels)
    block = part["table_block"]
    if block:
        line += " block %d/%d rows %s" % (block["index"], block["count"], block["row_range"])
        if block["context_range"]:
            line += " context " + block["context_range"]
    return line


def _write_part(data, name):
    """Print a part's Markdown as the exact bytes that were read (and verified), whatever the console
    encoding; the bytes must still be UTF-8 text."""
    text = _decode(data, name)
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is None:
        sys.stdout.write(text)
        return
    sys.stdout.flush()
    buffer.write(data)
    buffer.flush()


def _describe(exc):
    if isinstance(exc, OSError) and exc.filename is not None and exc.strerror:
        return "%s: %s" % (exc.strerror, exc.filename)
    return str(exc)


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="ai_package_reader.py", description="Inspect a DocRedock AI package.",
        epilog="exit codes: 0 ok, 1 --verify found a listed file missing, changed or refused, "
               "2 the package cannot be read, 3 review required (--fail-on-review)")
    parser.add_argument("package", help="package directory or .zip file")
    parser.add_argument("--verify", action="store_true",
                        help="check every listed file's size and SHA-256 before printing anything else")
    parser.add_argument("--part", metavar="PART_ID", help="print one part's Markdown")
    parser.add_argument("--fail-on-review", action="store_true",
                        help="exit 3 instead of printing when report.json says the conversion needs review")
    args = parser.parse_args(argv)

    try:
        with open_package(args.package) as package:
            manifest = load_manifest(package)
            part = None
            if args.part is not None:
                part = next((item for item in manifest["parts"] if item["id"] == args.part), None)
                if part is None:
                    print("unknown part id: " + args.part, file=sys.stderr)
                    return 2
            # Files used after the checks are read once, so the bytes checked are the bytes used.
            listed = {entry["path"]: entry["bytes"] for entry in manifest["files"]}
            contents = {}
            for rel in ([part["path"]] if part else []) + ([manifest["report"]] if args.fail_on_review else []):
                try:
                    # A file of another size is reported by --verify below without being read.
                    if not args.verify or package.size(rel) == listed[rel]:
                        contents[rel] = package.read_bytes(rel)
                except (FileNotFoundError, ValueError):
                    if not args.verify:
                        raise  # with --verify the problem is reported below
            if args.verify:
                problems = verify_files(package, manifest, contents)
                # With --part, stdout carries only the part's Markdown.
                for problem in problems:
                    print(problem, file=sys.stderr if part else sys.stdout)
                if problems:
                    return 1
                if part is None:
                    print("files ok")
            if args.fail_on_review and review_required(package, manifest, contents.get(manifest["report"])):
                print("review required: read %s before giving this package to a model" % manifest["review"],
                      file=sys.stderr)
                return 3
            if part is not None:
                data = contents.get(part["path"])
                _write_part(package.read_bytes(part["path"]) if data is None else data, part["path"])
                return 0
            if not args.verify:
                print("schema " + manifest["schema_version"])
                print("source " + manifest["source"]["file_name"])
                for item in manifest["parts"]:
                    print(_part_line(item))
            return 0
    except (OSError, ValueError) as exc:
        # PackageError and UnsupportedSchemaError are ValueErrors; FileNotFoundError is an OSError.
        named = isinstance(exc, (UnsupportedSchemaError, FileNotFoundError))
        print(("" if named else "cannot read package: ") + _describe(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

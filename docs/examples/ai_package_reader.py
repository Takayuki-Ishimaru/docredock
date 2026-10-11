"""Read DocRedock AI packages (directory or ZIP) with the standard library only.

See docs/reference/ai-package.md for the package format. Manifest schema 1.x
carries node ids and per-node sources inline; 2.x moves them to
source-index.json and shares locations across parts.

load_manifest checks the manifest before anything is read through it: every
field this reader uses must be present with its documented type, ids and paths
must be unique, the blocks of a cut table must form one chain, and every file
the manifest refers to (document, review, report, source index, parts) must be
listed in "files" with its size and SHA-256, so verification can never pass
without checking it. load_source_index checks that the source index agrees with
the manifest and puts every node at one location and cell. A package that fails
a check raises PackageError (UnsupportedSchemaError for a schema version this
reader does not know) instead of being read in part.

Usage:
    python3 docs/examples/ai_package_reader.py PACKAGE [--verify] [--part PART_ID | --search TEXT]
                                               [--fail-on-review] [--json]

Checks run before anything is printed. --verify prints "files ok" when every
listed file matches its size and SHA-256, the package holds no other file and
its source index and report are well formed; otherwise it prints one line per
file that is missing, changed, refused (a link, a pipe or device, an encrypted
or unreadable ZIP entry) or not listed. With --part or --search those lines go
to stderr, and the part (as the exact bytes that were verified) or the matches
are printed only when every file matches. The .NET AiPackageManifestReader
reports the same lines. --json prints one JSON object instead: the source,
whether the files were verified, the review state from report.json, and the
part with its locations, table block and source-index entries, every part, or
the matches.

Exit codes: 0 success; 1 --verify found a file missing, changed, refused or not
listed; 2 the package cannot be read (not a package directory or ZIP, an
ambiguous ZIP, unsupported schema, malformed JSON, manifest, source index or
report, a manifest path that would leave the package, unknown part id); 3
--fail-on-review and report.json says the conversion needs review; 141 the
output pipe was closed early (as by head).
"""

import argparse
import errno
import hashlib
import html
import json
import os
import posixpath
import re
import stat
import struct
import sys
import unicodedata
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
# Characters that move the cursor, start a terminal escape sequence, break a line or reorder the text
# around them when printed: C0 and C1 controls, DEL, line and paragraph separators, bidirectional
# formatting characters, and lone surrogates (file names that are not UTF-8). Package paths never
# contain them, and printed text shows them as \xNN or \uNNNN.
UNSHOWN_RE = re.compile("[\x00-\x1f\x7f-\x9f\u061c\u200e\u200f\u2028\u2029\u202a-\u202e\u2066-\u2069\ud800-\udfff]")
# The same characters except C0 controls, which JSON escapes by itself.
UNSHOWN_IN_JSON_RE = re.compile("[\x7f-\x9f\u061c\u200e\u200f\u2028\u2029\u202a-\u202e\u2066-\u2069]")
# Files operating systems leave in folders and archives (Finder, Explorer); never part of a package.
SYSTEM_FILES = (".DS_Store", "Thumbs.db", "desktop.ini")
# A Markdown backslash escape (DocRedock escapes literal text this way), undone before searching.
MARKDOWN_ESCAPE_RE = re.compile(r"\\([!-/:-@\[-`{-~])")
# A backslash escape, or else the inline markup readable Markdown writes around text: underline
# (<u>...</u>), strikethrough (~~...~~) and bold (**...**, ***...*** with italics). Matching escapes first
# keeps \~ and \* the characters they are; a character reference (&lt;u&gt;) holds none of these.
INLINE_MARKUP_RE = re.compile(r"\\([!-/:-@\[-`{-~])|</?(?:u|ins)>|~~|\*\*\*|\*\*")


class PackageError(ValueError):
    """The package cannot be read safely: it is not a package directory or ZIP, a JSON file is
    malformed or lacks a field this reader relies on, an id, path or ZIP entry appears twice, a
    path would leave the package, a referenced file is missing from the manifest's file list, or
    the source index does not agree with the manifest."""


class UnsupportedSchemaError(PackageError):
    """Raised when a manifest schema_version is absent, malformed or unsupported."""


class PackageVerificationError(Exception):
    """verify_files found a file missing, changed, refused or not listed; problems holds its lines."""

    def __init__(self, problems):
        super().__init__("; ".join(problems))
        self.problems = list(problems)


def _shown(text):
    """text with the characters UNSHOWN_RE matches written as escapes, safe to print on one line."""
    return UNSHOWN_RE.sub(lambda match: ("\\x%02x" if ord(match.group()) < 0x100 else "\\u%04x") % ord(match.group()),
                          text)


def _safe_rel(rel):
    """Return a relative package path, rejecting absolute or drive paths, "." and ".." segments,
    backslashes, colons (C:x, C:/..., ADS names), NUL and the other characters UNSHOWN_RE matches:
    package names never contain them."""
    if (not isinstance(rel, str) or not rel or "\\" in rel or ":" in rel or UNSHOWN_RE.search(rel)
            or posixpath.isabs(rel)):
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
    """The entry count of a plain ZIP's central directory. The end record is found as Python's zipfile
    finds it (the last 22 bytes, else the last signature in the final 64 KiB), then the Zip64 records
    its locator points to. The archive must be on one disk, a Zip64 end record must end where its
    locator begins, and the central directory must end where the (Zip64) end record begins. Python
    3.12's zipfile checks the Zip64 records this way and earlier versions do not, so every version
    checks here, before zipfile reads the archive."""
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
                locator_disk, record, disks = struct.unpack_from("<IQI", locator, 4)
                if locator_disk or disks > 1:
                    raise PackageError("the ZIP spans several disks")
                if record > location - 20 - 56:
                    raise PackageError("the ZIP's Zip64 end record locator points past the record")
                fp.seek(record)
                data = fp.read(56)
                if len(data) != 56 or data[:4] != b"PK\x06\x06":
                    raise PackageError("the ZIP's Zip64 end record is not where its locator points")
                if record + 12 + struct.unpack_from("<Q", data, 4)[0] != location - 20:
                    raise PackageError("the ZIP's Zip64 end record does not end where its locator begins")
                disk, directory_disk, _, count, directory_size, directory_offset = struct.unpack_from("<IIQQQQ", data, 16)
                location = record
    if disk or directory_disk:
        raise PackageError("the ZIP spans several disks")
    if directory_offset + directory_size != location:
        raise PackageError("the ZIP's central directory does not end where its end record begins")
    return count


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


def _left_by_system(name):
    """Whether a file is one an operating system leaves behind: .DS_Store, Thumbs.db, desktop.ini,
    an AppleDouble "._" file or anything in a __MACOSX folder (Finder's archives)."""
    base = name.rsplit("/", 1)[-1]
    return base in SYSTEM_FILES or base.startswith("._") or name.split("/", 1)[0] == "__MACOSX"


class Package:
    """Read-only view of an AI package directory or ZIP archive."""

    def __init__(self, path):
        self.path = Path(path)
        self._zip = None
        self._entries = {}
        self._listings = {}
        if self.path.is_dir():
            self._kind = "dir"
        elif self.path.is_file():
            count = _zip_end(self.path)
            try:
                self._zip = zipfile.ZipFile(self.path)
            except NotImplementedError as exc:
                raise PackageError("%s uses a ZIP feature this reader does not read (%s)" % (self.path, exc))
            except (zipfile.BadZipFile, EOFError, ValueError) as exc:
                raise PackageError("not a package folder or ZIP archive: %s (%s)" % (self.path, exc))
            self._kind = "zip"
            try:
                self._index_entries(count)
            except PackageError:
                self.close()
                raise
        else:
            raise FileNotFoundError(errno.ENOENT, "package not found", str(self.path))

    def _index_entries(self, count):
        # A package ZIP is read only when it is plain, so that every reader reads it the same way: the
        # .NET AiPackageManifestReader makes the same checks in the same order with the same messages.
        # Readers disagree on which of two equal names they return (Python the last, .NET the first),
        # on names with a NUL (Python cuts them there) or a backslash (Python on Windows turns it into
        # "/"), on a Unicode Path extra field (Python 3.12 renames the entry by it), and on end records
        # that do not describe the directory they end.
        infos = self._zip.infolist()
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

    def _names(self, folder):
        """The exact names in a package folder. A name differing in letter case or Unicode form is a
        missing file everywhere, as in a ZIP, not only where the file system tells the names apart."""
        names = self._listings.get(folder)
        if names is None:
            try:
                names = set(os.listdir(folder))
            except (FileNotFoundError, NotADirectoryError):
                names = set()
            self._listings[folder] = names
        return names

    def _dir_file(self, rel):
        # A link inside a package folder could point anywhere, and a pipe or device could block or
        # never end; DocRedock writes neither. A folder where a file should be is a missing file.
        current = self.path
        for segment in rel.split("/"):
            if segment not in self._names(current):
                raise _missing(rel)
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

    def files(self):
        """Every file in the package as a package path, sorted: the ZIP entries that are not folders,
        or everything in the folder that is not a folder (links, pipes and devices included), without
        following links."""
        if self._kind == "zip":
            return sorted(name for name, info in self._entries.items() if not info.is_dir())
        names = []

        def walk(folder, prefix):
            with os.scandir(folder) as entries:
                for entry in entries:
                    if entry.is_dir(follow_symlinks=False) and not getattr(entry, "is_junction", lambda: False)():
                        walk(entry.path, prefix + entry.name + "/")
                    else:
                        names.append(prefix + entry.name)
        walk(self.path, "")
        return sorted(names)

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
        except (FileNotFoundError, PackageError):
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


def _boolean(obj, key, where=""):
    """An optional true/false field: None when it is absent or null."""
    name, value = _get(obj, key, where, True)
    if value is not None and not isinstance(value, bool):
        raise PackageError("%s must be true or false, got %s" % (name, _kind_of(value)))
    return value


def _flag(obj, key, where=""):
    return bool(_boolean(obj, key, where))


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
    tables = {}
    for part in parts:
        block = part["table_block"]
        for key in ("previous_part", "next_part"):
            if block and block[key] is not None and block[key] not in ids:
                raise PackageError("part %s %s names an unknown part %r" % (part["id"], key, block[key]))
        if block:
            tables.setdefault(block["table_id"], []).append(part)
    # The blocks of a cut table form one chain, 1 to count, each naming its neighbours, so a reader
    # following next_part reaches every block once and never runs into another table or in a circle.
    for table_id, blocks in tables.items():
        by_index = {}
        for part in blocks:
            block = part["table_block"]
            if block["count"] != len(blocks):
                raise PackageError("part %s says table %r has %d blocks, but the manifest lists %d"
                                   % (part["id"], table_id, block["count"], len(blocks)))
            if block["index"] in by_index:
                raise PackageError("table %r gives index %d to parts %s and %s"
                                   % (table_id, block["index"], by_index[block["index"]]["id"], part["id"]))
            by_index[block["index"]] = part
        for index in range(1, len(blocks) + 1):
            part = by_index[index]
            for key, neighbour in (("previous_part", index - 1), ("next_part", index + 1)):
                expected = by_index[neighbour]["id"] if neighbour in by_index else None
                actual = part["table_block"][key]
                if actual != expected:
                    raise PackageError("part %s table_block.%s is %s, but %s" % (
                        part["id"], key, "null" if actual is None else repr(actual),
                        "it is the %s block of table %r" % ("first" if neighbour < index else "last", table_id)
                        if expected is None else "block %d/%d of table %r is %s" % (neighbour, len(blocks), table_id, expected)))


def _files(manifest):
    entries = []
    seen = set()
    for position, raw in enumerate(_array(manifest, "files")):
        where = "files[%d]" % position
        _object(raw, where)
        path = _path(raw, "path", where)
        if path == "manifest.json":
            raise PackageError("%s.path is manifest.json, which cannot hold its own size and SHA-256" % where)
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
    part naming an unknown location or part, table blocks that do not form one chain per table, a
    1.x part whose node_ids and sources differ in length, a 2.x manifest without source_index, or a
    referenced file that "files" does not list with a size and SHA-256."""
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
    locations, parts = (_normalize_v1 if major == 1 else _normalize_v2)(manifest)
    _check_parts(parts, locations)
    # 2.x keeps node identities only in the source index, so a 2.x package always has one.
    source_index = None if major == 1 else _path(manifest, "source_index")
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


def _place(place):
    return "%s %s" % (place[0], "without a cell" if place[1] is None else "cell %r" % place[1])


def _check_nodes(where, part_id, groups, places):
    """A part lists a node once, and every part that lists a node puts it at the same location and
    cell: joining text to its source by node id must have one answer. places maps the nodes of the
    parts checked so far to (part id, (location id, cell address))."""
    listed = set()
    for group in groups:
        cells = group["cell_addresses"] or [None] * len(group["node_ids"])
        for node, cell in zip(group["node_ids"], cells):
            if node in listed:
                raise PackageError("%s lists node %r twice" % (where, node))
            listed.add(node)
            place = (group["source_id"], cell)
            first_part, first_place = places.setdefault(node, (part_id, place))
            if first_place != place:
                raise PackageError("%s puts node %r at %s, but part %s puts it at %s"
                                   % (where, node, _place(place), first_part, _place(first_place)))


def load_source_index(package, manifest, data=None):
    """Return part id -> [{"source_id", "node_ids", "cell_addresses"}] for every manifest part.

    Schema 1.x groups the inline node ids. For 2.x the index must agree with the manifest: its
    schema_version is 1.x, it lists each manifest part exactly once, a part's groups name exactly
    that part's locations, their node ids add up to its node_count, and cell_addresses, when
    present, has one entry per node id. In every schema a part lists a node once, and all parts
    put a node at the same location and cell. data, when given, is the index's bytes already read."""
    places = {}
    if manifest["major"] == 1:
        index = _source_index_v1(manifest)
        for position, part in enumerate(manifest["parts"]):
            _check_nodes("manifest.json parts[%d]" % position, part["id"], index[part["id"]], places)
        return index
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
        _check_nodes(where, part_id, groups, places)
        index[part_id] = groups
    absent = [part_id for part_id in parts if part_id not in index]
    if absent:
        raise PackageError("%s has no entry for %s" % (name, ", ".join(absent)))
    return index


def read_review(package, manifest, data=None):
    """What report.json says about review: {"required": whether something needs comparison with the
    source (review pages, OCR items, worksheet table boundaries), "review_items_detected" and
    "hidden_content_included": the summary's flags, None where the report predates them}. Read
    review.md before giving a package that requires review to a model. data, when given, is the
    report's bytes already read."""
    name = manifest["report"]
    report = _object(_parse_json(package.read_bytes(name) if data is None else data, name), name)
    version = _text(report, "schema_version", name)
    if not SUBSCHEMA_RE.fullmatch(version):
        raise UnsupportedSchemaError("%s schema_version %r is unsupported (supported: 1.x)" % (name, version))
    review = _object(_get(report, "review", name, False)[1], name + ".review")
    field, value = _get(review, "required", name + ".review", False)
    if not isinstance(value, bool):
        raise PackageError("%s must be true or false, got %s" % (field, _kind_of(value)))
    summary = _get(report, "summary", name, True)[1]
    summary = {} if summary is None else _object(summary, name + ".summary")
    return {"required": value,
            "review_items_detected": _boolean(summary, "review_items_detected", name + ".summary"),
            "hidden_content_included": _boolean(summary, "hidden_content_included", name + ".summary")}


def review_required(package, manifest, data=None):
    """True when report.json says something needs comparison with the source (see read_review)."""
    return read_review(package, manifest, data)["required"]


def read_part(package, part):
    """Return the Markdown text of one manifest part, unverified (see read_verified_part)."""
    return package.read_text(part["path"])


def verify_files(package, manifest, contents=None):
    """Return one human-readable problem per file that is missing, refused, changed or not listed;
    an empty list means the package holds exactly the files the manifest lists (and manifest.json),
    each matching its size and SHA-256. load_manifest has already made sure that every file the
    manifest refers to is listed. Files an operating system leaves behind (.DS_Store, Thumbs.db,
    desktop.ini, AppleDouble "._" files, __MACOSX folders) are not reported. contents maps package
    paths to bytes the caller already read; those bytes are checked instead of reading the file
    again, so a caller can use exactly the bytes that were verified."""
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
        except PackageError as error:
            problems.append(path + ": refused (" + str(error) + ")")
            continue
        if len(data) != entry["bytes"]:
            problems.append("%s: size mismatch (expected %d bytes, got %d)" % (path, entry["bytes"], len(data)))
            continue
        actual = hashlib.sha256(data).hexdigest()
        if actual != entry["sha256"]:
            problems.append("%s: sha256 mismatch (expected %s, got %s)" % (path, entry["sha256"], actual))
    # Anything else in the package is not covered by the manifest, whatever refers to it. A link
    # where a folder of listed files belongs has already made those files refused.
    listed = {entry["path"] for entry in manifest["files"]}
    folders = {path.rsplit("/", 1)[0] for path in listed if "/" in path}
    folders |= {folder[:end] for folder in folders for end in range(len(folder)) if folder[end] == "/"}
    for name in package.files():
        if name != "manifest.json" and name not in listed and name not in folders and not _left_by_system(name):
            problems.append(_shown(name) + ": not listed in the manifest")
    return problems


def _read_files(package, manifest, paths, verify):
    """Read each listed path once, so the bytes checked are the bytes used. With verify a file whose
    size differs from its listed size is not read, and a missing or refused file is left for
    verify_files to report; without it the error is raised."""
    listed = {entry["path"]: entry["bytes"] for entry in manifest["files"]}
    contents = {}
    for rel in paths:
        if rel in contents:
            continue
        try:
            if not verify or package.size(rel) == listed[rel]:
                contents[rel] = package.read_bytes(rel)
        except (FileNotFoundError, PackageError):
            if not verify:
                raise
    return contents


def _check_contents(package, manifest, contents):
    """The source index and review state, from bytes already read and verified."""
    index_name = manifest["source_index"]
    index = load_source_index(package, manifest, contents.get(index_name) if index_name else None)
    return index, read_review(package, manifest, contents.get(manifest["report"]))


def _find_part(manifest, part_id):
    part = next((item for item in manifest["parts"] if item["id"] == part_id), None)
    if part is None:
        raise KeyError(part_id)
    return part


def _verified_contents(package, manifest, paths):
    """Read paths with the report and source index, verify the whole package and check its source
    index and report: (contents, index, review)."""
    paths = list(paths) + [manifest["report"]] + ([manifest["source_index"]] if manifest["source_index"] else [])
    contents = _read_files(package, manifest, paths, True)
    problems = verify_files(package, manifest, contents)
    if problems:
        raise PackageVerificationError(problems)
    index, review = _check_contents(package, manifest, contents)
    return contents, index, review


def read_verified_part(package, manifest, part_id):
    """Check the package and return one part with what it was checked against, in one call:
    {"part": the manifest part, "markdown": its text, exactly the bytes verified, "sources": its
    source-index groups, "review": read_review's result}. Every file must pass verify_files and the
    source index and report must be well formed. Raises KeyError for an unknown part id,
    PackageVerificationError when a file is missing, changed, refused or not listed, and PackageError
    when the package is malformed."""
    part = _find_part(manifest, part_id)
    contents, index, review = _verified_contents(package, manifest, [part["path"]])
    return {"part": part, "markdown": _decode(contents[part["path"]], part["path"]), "sources": index[part["id"]],
            "review": review}


def _plain(text):
    """text as search compares it letter for letter: Markdown escapes and character references undone,
    then NFKC (full-width letters and digits as ASCII) and case folded."""
    return unicodedata.normalize("NFKC", html.unescape(MARKDOWN_ESCAPE_RE.sub(r"\1", text))).casefold()


def _searchable(text):
    """_plain(text) with the inline markup of readable Markdown taken out (<u>, </u>, ~~, **), so that
    "a <u>word</u> here" is searched as "a word here". Only markup goes: an escaped delimiter (\\~) or a
    character reference (&lt;u&gt;) is the character it stands for."""
    return unicodedata.normalize(
        "NFKC", html.unescape(INLINE_MARKUP_RE.sub(lambda match: match.group(1) or "", text))).casefold()


def _lines(text):
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    return [line[:-1] if line.endswith("\r") else line for line in lines]


def search_parts(package, manifest, text, contents=None):
    """The parts whose Markdown contains text, as the line reads (_searchable) or as it is written
    (_plain); case, width, Markdown escapes and, in the first, inline markup do not matter:
    [(part, [(line number, line), ...]), ...] in manifest order. contents maps package paths to bytes
    already read (and verified)."""
    query = _plain(text)
    contents = contents or {}
    matches = []
    for part in manifest["parts"]:
        data = contents.get(part["path"])
        markdown = _decode(package.read_bytes(part["path"]) if data is None else data, part["path"])
        lines = [(number, line) for number, line in enumerate(_lines(markdown), 1)
                 if query in _searchable(line) or query in _plain(line)]
        if lines:
            matches.append((part, lines))
    return matches


def _part_line(part):
    tokens = "-" if part["estimated_tokens"] is None else str(part["estimated_tokens"])
    labels = "; ".join(location["label"] for location in part["locations"])
    line = "%s %s tokens=%s %s" % (part["id"], part["path"], tokens, labels)
    block = part["table_block"]
    if block:
        line += " block %d/%d rows %s" % (block["index"], block["count"], block["row_range"])
        if block["context_range"]:
            line += " context " + block["context_range"]
    return _shown(line)


def _excerpt(line, text, width=160):
    """At most width characters of line, around the first place text appears in it."""
    if len(line) <= width:
        return line
    at = max(line.casefold().find(text.casefold()), 0)
    begin = max(min(at - width // 3, len(line) - width), 0)
    return ("..." if begin else "") + line[begin:begin + width] + ("..." if begin + width < len(line) else "")


def _to_stdout(write):
    """Run a write to stdout. Windows reports a pipe whose reader went away as EINVAL, not EPIPE;
    either way it raises BrokenPipeError, so the CLI stops quietly."""
    try:
        write()
    except OSError as exc:
        if not isinstance(exc, BrokenPipeError) and os.name == "nt" and exc.errno == errno.EINVAL:
            raise BrokenPipeError(errno.EPIPE, "the output pipe was closed")
        raise


def _say(line):
    """Print one line on stdout."""
    _to_stdout(lambda: print(line))


def _write_bytes(data):
    """Write UTF-8 bytes to stdout as they are, whatever the console encoding."""
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is None:
        _to_stdout(lambda: sys.stdout.write(data.decode("utf-8")))
        return

    def write():
        sys.stdout.flush()
        buffer.write(data)
        buffer.flush()
    _to_stdout(write)


def _write_part(data, name):
    """Print a part's Markdown as the exact bytes that were read (and verified); they must be UTF-8."""
    _decode(data, name)
    _write_bytes(data)


def _print_json(value):
    """One JSON object in UTF-8; the characters UNSHOWN_RE matches are \\u escapes, as JSON allows."""
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    _write_bytes(UNSHOWN_IN_JSON_RE.sub(lambda match: "\\u%04x" % ord(match.group()), text).encode("utf-8"))


def _part_json(part):
    return {"id": part["id"], "path": part["path"], "node_count": part["node_count"],
            "estimated_tokens": part["estimated_tokens"], "exceeds_target": part["exceeds_target"],
            "locations": [dict(location) for location in part["locations"]], "table_block": part["table_block"]}


def _document_json(manifest, verified, review=None):
    document = {"schema_version": manifest["schema_version"], "generator_version": manifest["generator_version"],
                "source": dict(manifest["source"]), "content_policy": manifest["content_policy"],
                "verified": verified}
    if review is not None:
        document["review"] = dict(review, file=manifest["review"])
    return document


def _describe(exc):
    if isinstance(exc, OSError) and exc.filename is not None and exc.strerror:
        return "%s: %s" % (exc.strerror, exc.filename)
    return str(exc)


def _silence_stdout():
    """Point stdout at the null device, so the interpreter's final flush cannot fail again."""
    try:
        sys.stdout.flush()
    except (OSError, ValueError):
        pass
    try:
        os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
    except (OSError, ValueError, AttributeError):
        pass


def _run(package, args):
    manifest = load_manifest(package)
    part = None
    if args.part is not None:
        part = next((item for item in manifest["parts"] if item["id"] == args.part), None)
        if part is None:
            print(_shown("unknown part id: " + args.part), file=sys.stderr)
            return 2
    # Files used after the checks are read once, so the bytes checked are the bytes used.
    paths = [part["path"]] if part else []
    if args.search is not None:
        paths += [item["path"] for item in manifest["parts"]]
    if args.verify or args.json or args.fail_on_review:
        paths.append(manifest["report"])
    if manifest["source_index"] and (args.verify or (args.json and part)):
        paths.append(manifest["source_index"])
    contents = _read_files(package, manifest, paths, args.verify)
    index = review = None
    if args.verify:
        problems = verify_files(package, manifest, contents)
        if problems:
            if args.json:
                _print_json(dict(_document_json(manifest, False), problems=problems))
            else:
                # With --part or --search, stdout carries only what was asked for.
                for problem in problems:
                    if part is None and args.search is None:
                        _say(problem)
                    else:
                        print(problem, file=sys.stderr)
            return 1
        # Intact files must also be readable: the source index and report agree with the manifest.
        index, review = _check_contents(package, manifest, contents)
        if part is None and args.search is None and not args.json:
            _say("files ok")
    elif args.json or args.fail_on_review:
        review = read_review(package, manifest, contents.get(manifest["report"]))
    if args.fail_on_review and review["required"]:
        if args.json:
            _print_json(_document_json(manifest, args.verify, review))
        print("review required: read %s before giving this package to a model" % manifest["review"], file=sys.stderr)
        return 3
    if part is not None:
        data = contents.get(part["path"])
        if data is None:
            data = package.read_bytes(part["path"])
        if not args.json:
            _write_part(data, part["path"])
            return 0
        if index is None:
            index_name = manifest["source_index"]
            index = load_source_index(package, manifest, contents.get(index_name) if index_name else None)
        document = _document_json(manifest, args.verify, review)
        document["part"] = dict(_part_json(part), sources=index[part["id"]], markdown=_decode(data, part["path"]))
        _print_json(document)
        return 0
    if args.search is not None:
        matches = search_parts(package, manifest, args.search, contents)
        if args.json:
            document = _document_json(manifest, args.verify, review)
            document["search"] = {"text": args.search, "parts": [
                dict(_part_json(item), lines=[{"number": number, "text": line} for number, line in lines])
                for item, lines in matches]}
            _print_json(document)
            return 0
        if not matches:
            print(_shown("no part contains %r" % args.search), file=sys.stderr)
        for item, lines in matches:
            _say(_part_line(item))
            for number, line in lines[:3]:
                _say(_shown("  %d: %s" % (number, _excerpt(line, args.search))))
            if len(lines) > 3:
                _say("  ... %d more matching lines" % (len(lines) - 3))
        return 0
    if args.json:
        document = _document_json(manifest, args.verify, review)
        document["parts"] = [_part_json(item) for item in manifest["parts"]]
        _print_json(document)
    elif not args.verify:
        _say(_shown("schema " + manifest["schema_version"]))
        _say(_shown("source " + manifest["source"]["file_name"]))
        for item in manifest["parts"]:
            _say(_part_line(item))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="ai_package_reader.py", description="Inspect a DocRedock AI package.",
        epilog="exit codes: 0 ok, 1 --verify found a file missing, changed, refused or not listed, "
               "2 the package cannot be read, 3 review required (--fail-on-review), "
               "141 the output pipe was closed early")
    parser.add_argument("package", help="package directory or .zip file")
    parser.add_argument("--verify", action="store_true",
                        help="check every listed file's size and SHA-256, that no other file is in the package, "
                             "and the source index and report, before printing anything else")
    shown = parser.add_mutually_exclusive_group()
    shown.add_argument("--part", metavar="PART_ID", help="print one part's Markdown")
    shown.add_argument("--search", metavar="TEXT",
                       help="list the parts containing TEXT (ignoring case, full-width forms, Markdown "
                            "escapes and underline, strikethrough and bold marks) with their matching lines")
    parser.add_argument("--fail-on-review", action="store_true",
                        help="exit 3 instead of printing when report.json says the conversion needs review")
    parser.add_argument("--json", action="store_true",
                        help="print one JSON object: source, verification, review state, and the part "
                             "(Markdown, locations, table block, source-index entries), every part or the matches")
    args = parser.parse_args(argv)
    if args.search is not None and not args.search.strip():
        parser.error("--search needs text to look for")
    # Names and labels come from documents: a console that cannot show a character gets an escape
    # rather than an error.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(errors="backslashreplace")
            except (OSError, ValueError):
                pass
    try:
        with open_package(args.package) as package:
            return _run(package, args)
    except BrokenPipeError:
        # Whatever read our output stopped (head, a closed pager): stop quietly, as cat does.
        _silence_stdout()
        return 141
    except (OSError, PackageError) as exc:
        # PackageError and UnsupportedSchemaError are PackageErrors; FileNotFoundError is an OSError.
        named = isinstance(exc, (UnsupportedSchemaError, FileNotFoundError))
        print(_shown(("" if named else "cannot read package: ") + _describe(exc)), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

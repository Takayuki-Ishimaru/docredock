"""Read DocRedock AI packages (directory or ZIP) with the standard library only.

See docs/reference/ai-package.md for the package format. Manifest schema 1.x
carries node ids and per-node sources inline; 2.x moves them to
source-index.json and shares locations across parts.

Usage:
    python3 docs/examples/ai_package_reader.py PACKAGE [--verify] [--part PART_ID]
"""

import argparse
import hashlib
import json
import posixpath
import re
import sys
import zipfile
from pathlib import Path

SCHEMA_RE = re.compile(r"^(1|2)\.([0-9]+)$")
SUPPORTED_MESSAGE = "supported: 1.x, 2.x"
LOCATION_FIELDS = ("partition_id", "label", "page_number", "slide_number", "sheet_name", "heading_path")


class UnsupportedSchemaError(ValueError):
    """Raised when a manifest schema_version is absent, malformed or unsupported."""


def _safe_rel(rel):
    """Return a relative package path, rejecting absolute or drive paths, "." and ".." segments,
    backslashes and colons (C:x, C:/..., ADS names): package names never contain them."""
    if not isinstance(rel, str) or not rel or "\\" in rel or ":" in rel or posixpath.isabs(rel):
        raise ValueError("unsafe package path: " + repr(rel))
    if any(segment in ("", ".", "..") for segment in rel.split("/")):
        raise ValueError("unsafe package path: " + repr(rel))
    return rel


class Package:
    """Read-only view of an AI package directory or ZIP archive."""

    def __init__(self, path):
        self.path = Path(path)
        self._zip = None
        self._names = set()
        if self.path.is_dir():
            self._kind = "dir"
        elif self.path.is_file():
            self._zip = zipfile.ZipFile(self.path)
            self._kind = "zip"
            self._names = {i.filename for i in self._zip.infolist() if not i.is_dir()}
        else:
            raise FileNotFoundError("package not found: " + str(self.path))

    def read_bytes(self, rel):
        rel = _safe_rel(rel)
        if self._kind == "dir":
            # A link inside a package folder could point anywhere; DocRedock never writes one.
            current = self.path
            for segment in rel.split("/"):
                current = current / segment
                if current.is_symlink():
                    raise ValueError("package path is a link: " + repr(rel))
            try:
                return current.read_bytes()
            except OSError:
                raise FileNotFoundError(rel)
        try:
            return self._zip.read(rel)
        except KeyError:
            raise FileNotFoundError(rel)

    def read_text(self, rel):
        return self.read_bytes(rel).decode("utf-8")

    def exists(self, rel):
        try:
            rel = _safe_rel(rel)
        except ValueError:
            return False
        if self._kind == "dir":
            return (self.path / rel).is_file()
        return rel in self._names

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


def _parse_version(raw):
    if not isinstance(raw, str):
        raise UnsupportedSchemaError(
            "manifest schema_version must be a string like \"1.0\", got "
            + repr(raw) + " (" + SUPPORTED_MESSAGE + ")")
    match = SCHEMA_RE.match(raw)
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


def _location_record(source, counter):
    counter[0] += 1
    record = {"id": "source-%04d" % counter[0]}
    for field in LOCATION_FIELDS:
        record[field] = source.get(field)
    return record


def _normalize_v1(manifest):
    counter = [0]
    locations = []
    keys = []
    parts = []
    for raw_part in manifest.get("parts") or []:
        node_ids = list(raw_part.get("node_ids") or [])
        location_ids = []
        for source in raw_part.get("sources") or []:
            key = _location_key(source)
            if key not in keys:
                keys.append(key)
                locations.append(_location_record(source, counter))
            location_id = locations[keys.index(key)]["id"]
            if location_id not in location_ids:
                location_ids.append(location_id)
        parts.append({
            "id": raw_part.get("id"),
            "path": raw_part.get("path"),
            "node_count": len(node_ids),
            "location_ids": location_ids,
            "locations": [locations[[record["id"] for record in locations].index(i)] for i in location_ids],
            "exceeds_target": bool(raw_part.get("exceeds_target")),
            "estimated_tokens": None,
            "table_block": None,
            "node_ids": node_ids,
        })
    return locations, parts


def _known_location(by_id, part_id, location_id):
    if location_id not in by_id:
        raise ValueError("part %s names an unknown location %r" % (part_id, location_id))
    return by_id[location_id]


def _normalize_v2(manifest):
    by_id = {}
    locations = []
    for raw in manifest.get("locations") or []:
        record = {"id": raw.get("id")}
        for field in LOCATION_FIELDS:
            record[field] = raw.get(field)
        locations.append(record)
        by_id[record["id"]] = record
    parts = []
    for raw_part in manifest.get("parts") or []:
        location_ids = list(raw_part.get("source_ids") or [])
        parts.append({
            "id": raw_part.get("id"),
            "path": raw_part.get("path"),
            "node_count": int(raw_part.get("node_count") or 0),
            "location_ids": location_ids,
            "locations": [_known_location(by_id, raw_part.get("id"), i) for i in location_ids],
            "exceeds_target": bool(raw_part.get("exceeds_target")),
            "estimated_tokens": raw_part.get("estimated_tokens"),
            "table_block": raw_part.get("table_block"),
            "node_ids": None,
        })
    return locations, parts


def load_manifest(package):
    """Read manifest.json and normalize it across schema versions."""
    try:
        manifest = json.loads(package.read_text("manifest.json"))
    except FileNotFoundError:
        raise UnsupportedSchemaError("manifest.json is missing (" + SUPPORTED_MESSAGE + ")")
    except (ValueError, UnicodeDecodeError) as exc:
        raise UnsupportedSchemaError("manifest.json is not readable JSON: " + str(exc))
    if not isinstance(manifest, dict):
        raise UnsupportedSchemaError("manifest.json is not a JSON object (" + SUPPORTED_MESSAGE + ")")
    if "schema_version" not in manifest:
        raise UnsupportedSchemaError("manifest schema_version is missing (" + SUPPORTED_MESSAGE + ")")
    major, minor = _parse_version(manifest["schema_version"])
    if major == 1:
        locations, parts = _normalize_v1(manifest)
        source_index = None
    else:
        locations, parts = _normalize_v2(manifest)
        source_index = manifest.get("source_index")
    source = manifest.get("source") or {}
    return {
        "schema_version": manifest["schema_version"],
        "major": major,
        "minor": minor,
        "generator_version": manifest.get("generator_version"),
        "source": {
            "file_name": source.get("file_name"),
            "sha256": source.get("sha256"),
            "format": source.get("format"),
        },
        "content_policy": manifest.get("content_policy"),
        "target_characters": manifest.get("target_characters"),
        "table_row_blocks": bool(manifest.get("table_row_blocks", False)),
        "document": manifest.get("document"),
        "review": manifest.get("review"),
        "report": manifest.get("report"),
        "source_index": source_index,
        "locations": locations,
        "parts": parts,
        "files": [
            {"path": f.get("path"), "bytes": f.get("bytes"), "sha256": f.get("sha256")}
            for f in (manifest.get("files") or [])
        ],
    }


def _source_index_v1(package, manifest):
    # The normalized part drops the inline per-node sources, so re-read them.
    raw = json.loads(package.read_text("manifest.json"))
    id_by_key = {}
    for location in manifest["locations"]:
        id_by_key[_location_key(location)] = location["id"]
    index = {}
    for part in raw.get("parts") or []:
        groups = []
        by_key = {}
        sources_by_key = {}
        for node_id, source in zip(part.get("node_ids") or [], part.get("sources") or []):
            key = _location_key(source)
            group = by_key.get(key)
            if group is None:
                group = {"source_id": id_by_key.get(key), "node_ids": [], "cell_addresses": None}
                by_key[key] = group
                sources_by_key[key] = []
                groups.append(group)
            group["node_ids"].append(node_id)
            sources_by_key[key].append(source)
        for key, group in by_key.items():
            sources = sources_by_key[key]
            if any(source.get("cell_address") for source in sources):
                group["cell_addresses"] = [source.get("cell_address") for source in sources]
        index[part["id"]] = groups
    return index


def load_source_index(package, manifest):
    """Return part id -> [{"source_id", "node_ids", "cell_addresses"}]."""
    if manifest["major"] == 1:
        return _source_index_v1(package, manifest)
    raw = json.loads(package.read_text(manifest["source_index"]))
    index = {}
    for part in raw.get("parts") or []:
        groups = []
        for group in part.get("sources") or []:
            groups.append({
                "source_id": group.get("source_id"),
                "node_ids": list(group.get("node_ids") or []),
                "cell_addresses": group.get("cell_addresses"),
            })
        index[part["id"]] = groups
    return index


def read_part(package, part):
    """Return the Markdown text of one manifest part."""
    return package.read_text(part["path"])


def verify_files(package, manifest):
    """Return human-readable problems for the files listed in the manifest."""
    problems = []
    for entry in manifest["files"]:
        path = entry.get("path")
        try:
            data = package.read_bytes(path)
        except FileNotFoundError:
            problems.append(path + ": missing")
            continue
        except ValueError as error:
            problems.append(path + ": refused (" + str(error) + ")")
            continue
        # One message per file: a different size already says the content changed.
        expected_bytes = entry.get("bytes")
        if expected_bytes is not None and len(data) != expected_bytes:
            problems.append(path + ": size mismatch (expected " + str(expected_bytes)
                            + " bytes, got " + str(len(data)) + ")")
            continue
        expected_hash = entry.get("sha256")
        actual = hashlib.sha256(data).hexdigest()
        if expected_hash is not None and actual != str(expected_hash).lower():
            problems.append(path + ": sha256 mismatch (expected " + str(expected_hash)
                            + ", got " + actual + ")")
    return problems


def _part_line(part):
    tokens = "-" if part["estimated_tokens"] is None else str(part["estimated_tokens"])
    labels = "; ".join(location.get("label") or "" for location in part["locations"])
    line = "%s %s tokens=%s %s" % (part["id"], part["path"], tokens, labels)
    block = part["table_block"]
    if block:
        line += " block %s/%s rows %s" % (block.get("index"), block.get("count"), block.get("row_range"))
    return line


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="ai_package_reader.py", description="Inspect a DocRedock AI package.")
    parser.add_argument("package", help="package directory or .zip file")
    parser.add_argument("--verify", action="store_true", help="check file sizes and hashes")
    parser.add_argument("--part", metavar="PART_ID", help="print one part's Markdown")
    args = parser.parse_args(argv)

    try:
        with open_package(args.package) as package:
            manifest = load_manifest(package)
            if args.part:
                for part in manifest["parts"]:
                    if part["id"] == args.part:
                        sys.stdout.write(read_part(package, part))
                        return 0
                print("unknown part id: " + args.part, file=sys.stderr)
                return 2
            if args.verify:
                problems = verify_files(package, manifest)
                for problem in problems:
                    print(problem)
                if not problems:
                    print("files ok")
                return 1 if problems else 0
            print("schema " + manifest["schema_version"])
            print("source " + str(manifest["source"]["file_name"]))
            for part in manifest["parts"]:
                print(_part_line(part))
            return 0
    except UnsupportedSchemaError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    except FileNotFoundError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    except (OSError, ValueError) as exc:
        print("cannot read package: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

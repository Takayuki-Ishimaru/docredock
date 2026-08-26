#!/usr/bin/env python3
"""Generate artifact-linked CycloneDX metadata, provenance, and checksums."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--source-sbom", required=True, type=Path)
    parser.add_argument("--provenance-name", default="ARTIFACT-PROVENANCE.json")
    parser.add_argument("--checksums-name", default="BINARY-SHA256SUMS")
    parser.add_argument("--sbom-name", default="sbom.cdx.json")
    args = parser.parse_args()

    root = args.root.resolve()
    if not root.is_dir():
        raise SystemExit(f"artifact root is not a directory: {root}")

    excluded = {args.provenance_name, args.checksums_name, args.sbom_name}
    records = []
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        records.append(
            {
                "path": relative,
                "size": path.stat().st_size,
                "sha256": sha256(path),
            }
        )

    with args.source_sbom.open("r", encoding="utf-8") as stream:
        sbom = json.load(stream)
    metadata = sbom.setdefault("metadata", {})
    properties = metadata.setdefault("properties", [])
    properties.extend(
        [
            {"name": "docredock:runtime-identifier", "value": args.rid},
            {"name": "docredock:source-commit", "value": args.commit},
            {"name": "docredock:artifact-file-count", "value": str(len(records))},
        ]
    )
    components = sbom.setdefault("components", [])
    known_refs = {item.get("bom-ref") for item in components}
    for record in records:
        bom_ref = f"file:{args.rid}:{record['sha256']}:{record['path']}"
        if bom_ref in known_refs:
            continue
        components.append(
            {
                "type": "file",
                "name": record["path"],
                "version": args.commit[:12],
                "bom-ref": bom_ref,
                "hashes": [{"alg": "SHA-256", "content": record["sha256"]}],
                "properties": [
                    {"name": "docredock:runtime-identifier", "value": args.rid},
                    {"name": "docredock:size-bytes", "value": str(record["size"])},
                ],
            }
        )

    sbom_path = root / args.sbom_name
    sbom_path.write_text(
        json.dumps(sbom, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    sbom_record = {
        "path": args.sbom_name,
        "size": sbom_path.stat().st_size,
        "sha256": sha256(sbom_path),
    }
    records.append(sbom_record)
    records.sort(key=lambda item: item["path"])

    provenance = {
        "schema": "https://docredock.app/schemas/artifact-provenance/v1",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "runtime_identifier": args.rid,
        "source_commit": args.commit,
        "github_repository": os.environ.get("GITHUB_REPOSITORY"),
        "github_run_id": os.environ.get("GITHUB_RUN_ID"),
        "github_run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "sbom": sbom_record,
        "files": records,
    }
    provenance_path = root / args.provenance_name
    provenance_path.write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    records.append(
        {
            "path": args.provenance_name,
            "size": provenance_path.stat().st_size,
            "sha256": sha256(provenance_path),
        }
    )

    checksum_path = root / args.checksums_name
    checksum_path.write_text(
        "".join(f"{item['sha256']}  {item['path']}\n" for item in sorted(records, key=lambda item: item["path"])),
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Assemble the PDF semantic portion of v0.2.3 package-smoke evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import tempfile
import time
from pathlib import Path

RIDS = {"win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"}
KINDS = {"table", "diagram", "bounded", "diagnostic"}


def run_source_suite(cli: Path, output: Path) -> int:
    """Run source-built CLI evidence before packaging, including positive jitter cases."""
    from visual_semantics_assertions import parse_markdown
    from visual_semantics_qa import CliRunResult, generate_perturbation_corpus, run_materialized_corpus
    if not cli.is_file(): raise SystemExit(f"source CLI does not exist: {cli}")
    repo = Path(__file__).resolve().parents[1]
    producer = repo / "tests/DocRedock.Tests/Fixtures/Pdf/complex-layout.pdf"
    started = time.monotonic(); errors = []; cases = []
    with tempfile.TemporaryDirectory(prefix="docredock-v023-source-") as temporary:
        root = Path(temporary)
        # This checked-in ReportLab producer fixture protects real PDF text/table output;
        # three independent exports give a median duration and deterministic bytes.
        hashes, durations = [], []
        for index in range(3):
            target = root / f"producer-{index}.md"; begin = time.monotonic()
            result = subprocess.run([str(cli), "export", str(producer), "--profile", "readable", "--output", str(target), "--ocr", "off"], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120, check=False, env={**__import__('os').environ, "DOCREDOCK_ENABLE_EXPERIMENTAL": "1"})
            durations.append(round((time.monotonic() - begin) * 1000));
            if result.returncode not in (0, 1) or not target.is_file(): errors.append(f"producer export {index} failed")
            else:
                text = target.read_text(encoding="utf-8"); hashes.append(hashlib.sha256(target.read_bytes()).hexdigest())
                if "PDF-COMPLEX-001" not in text: errors.append("producer PDF lost text sentinel")
        def export_pdf(name, payload, *extra):
            source, target = root / f"{name}.pdf", root / f"{name}.md"; source.write_bytes(payload)
            result = subprocess.run([str(cli), "export", str(source), "--profile", "readable", "--output", str(target), "--ocr", "off", *extra], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120, check=False, env={**__import__('os').environ, "DOCREDOCK_ENABLE_EXPERIMENTAL": "1"})
            return source, target, result
        labels = [(10 + column * 100, 10 + row * 30, f"R{row + 1}C{column + 1}") for row in range(3) for column in range(4)]
        text_ops = b" ".join(f"BT 1 0 0 1 {x} {y} Tm ({label}) Tj ET".encode() for x, y, label in labels)
        grid = b" ".join(f"0 {y} m 400 {y} l S".encode() for y in (0, 30, 60, 90)) + b" " + b" ".join(f"{x} 0 m {x} 90 l S".encode() for x in (0, 100, 200, 300, 400))
        table_pdf = b"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " + str(len(text_ops + b" " + grid)).encode() + b" >> stream\n" + text_ops + b" " + grid + b"\nendstream\n%%EOF"
        _, table_markdown, table_result = export_pdf("simple-3x4-table", table_pdf)
        table_text = table_markdown.read_text(encoding="utf-8") if table_markdown.is_file() else ""
        table_rows = [line for line in table_text.splitlines() if re.fullmatch(r"\|(?:[^|]*\|){4}", line.strip())]
        table_passed = (table_result.returncode in (0, 1)
                        and all(table_text.count(label) == 1 for _, _, label in labels)
                        and len(table_rows) >= 3)
        if not table_passed:
            errors.append(f"synthetic 3x4 PDF table was not structurally reconstructed (exit={table_result.returncode}, markdown_bytes={len(table_text.encode('utf-8'))})")
        dense_paths = b"BT 1 0 0 1 10 10 Tm (DENSE_NATIVE_TEXT) Tj ET\n" + b"".join(b"0 0 m 10 10 l S\n" for _ in range(10_000))
        dense_pdf = b"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " + str(len(dense_paths)).encode() + b" >> stream\n" + dense_paths + b"endstream\n%%EOF"
        dense_started = time.monotonic()
        _, dense_markdown, dense_result = export_pdf("dense-vector", dense_pdf)
        dense_duration_ms = round((time.monotonic() - dense_started) * 1000)
        dense_bytes = dense_markdown.stat().st_size if dense_markdown.is_file() else None
        dense_text = dense_markdown.read_text(encoding="utf-8") if dense_markdown.is_file() else ""
        dense_passed = (dense_result.returncode in (0, 1) and dense_bytes is not None
                        and dense_bytes <= 128 * 1024 and "DENSE_NATIVE_TEXT" in dense_text)
        dense_passed = dense_passed and dense_duration_ms <= 5000
        if not dense_passed:
            errors.append(f"dense 10k-vector PDF violated bounded/native-text/runtime contract (exit={dense_result.returncode}, markdown_bytes={dense_bytes}, duration_ms={dense_duration_ms})")
        text_only = (b"BT 1 0 0 1 10 10 Tm (NO_DIAGRAM_NATIVE_TEXT) Tj 100 0 Td (NO_DIAGRAM_END) Tj ET\n"
                     b"0 0 20 20 re S 100 0 20 20 re S 20 10 m 100 10 l S\n")
        _, no_diagram_markdown, no_diagram_result = export_pdf(
            "no-diagrams", b"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length "
            + str(len(text_only)).encode() + b" >> stream\n" + text_only + b"endstream\n%%EOF", "--no-diagrams")
        no_diagram_text = no_diagram_markdown.read_text(encoding="utf-8") if no_diagram_markdown.is_file() else ""
        no_diagram_passed = (no_diagram_result.returncode in (0, 1) and "NO_DIAGRAM_NATIVE_TEXT" in no_diagram_text
                             and "NO_DIAGRAM_END" in no_diagram_text and "[PDF visual content:" not in no_diagram_text and "```mermaid" not in no_diagram_text)
        if not no_diagram_passed:
            errors.append(f"no-diagram PDF emitted visual fallback or lost native text (exit={no_diagram_result.returncode}, markdown_bytes={len(no_diagram_text.encode('utf-8'))})")
        def runner(spec, fixture, mode):
            target = root / f"{spec.case_id}.md"; repeat = root / f"repeat-{spec.case_id}.md"
            command = ["export", str(fixture), "--profile", "readable", "--output", str(target), "--ocr", "off", "--visual-inference", mode]
            first = subprocess.run([str(cli), *command], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120, check=False, env={**__import__('os').environ, "DOCREDOCK_ENABLE_EXPERIMENTAL": "1"})
            command[command.index(str(target))] = str(repeat)
            second = subprocess.run([str(cli), *command], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120, check=False, env={**__import__('os').environ, "DOCREDOCK_ENABLE_EXPERIMENTAL": "1"})
            markdown = target.read_text(encoding="utf-8") if target.is_file() else ""
            return CliRunResult(markdown, first.stdout, first.returncode, hashlib.sha256(target.read_bytes()).hexdigest() if target.is_file() else None, hashlib.sha256(repeat.read_bytes()).hexdigest() if repeat.is_file() else None, target.is_file() and repeat.is_file() and target.read_bytes() == repeat.read_bytes() and first.returncode == second.returncode)
        _, records = run_materialized_corpus(root / "jitter", generate_perturbation_corpus(), runner, parse_markdown)
        cases = list(records)
        errors.extend(f"semantic case failed: {r['case_id']}" for r in records if r["status"] != "passed")
    document = {"schema_version": 1, "version": "0.2.3", "status": "pass" if not errors else "fail", "errors": errors,
                "producer_pdf": str(producer), "producer_hashes": hashes, "producer_deterministic": len(set(hashes)) == 1,
                "producer_duration_median_ms": sorted(durations)[len(durations)//2] if durations else None,
                "table_fixture": {"rows": 3, "columns": 4, "text_cells": 12, "markdown_bytes": len(table_text.encode("utf-8")), "exit_code": table_result.returncode, "passed": table_passed, "assertions": ["all-12-cell-labels-exactly-once", "markdown-table"]},
                "dense_vector_fixture": {"primitives": 10_000, "max_markdown_bytes": 128 * 1024, "max_duration_ms": 5000, "markdown_bytes": dense_bytes, "duration_ms": dense_duration_ms, "exit_code": dense_result.returncode, "passed": dense_passed, "assertions": ["bounded-output", "native-text-retained", "runtime-bound"]},
                "no_diagram_fixture": {"option": "--no-diagrams", "markdown_bytes": len(no_diagram_text.encode("utf-8")), "exit_code": no_diagram_result.returncode, "passed": no_diagram_passed, "assertions": ["no-raw-fallback", "no-mermaid", "native-text-retained"]},
                "positive_jitter_cases": [x for x in cases if x.get("operation") in {"endpoint-gap", "translation", "rotation", "label-offset"}],
                "duration_ms": round((time.monotonic() - started) * 1000)}
    if not document["producer_deterministic"]: document["errors"].append("producer output was non-deterministic"); document["status"] = "fail"
    output.parent.mkdir(parents=True, exist_ok=True); output.write_text(json.dumps(document, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0 if document["status"] == "pass" else 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--smoke-evidence", action="append", type=Path)
    parser.add_argument("--source-cli", type=Path)
    parser.add_argument("--source-evidence", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.source_cli:
        return run_source_suite(args.source_cli, args.output)
    if not args.smoke_evidence:
        parser.error("--smoke-evidence is required unless --source-cli is supplied")
    records, errors, seen, provenance = [], [], set(), None
    source_suite = None
    if args.source_evidence:
        try:
            source_suite = json.loads(args.source_evidence.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            errors.append(f"source evidence: {error}")
        else:
            required_source = ("table_fixture", "dense_vector_fixture", "no_diagram_fixture")
            if source_suite.get("status") != "pass" or any(source_suite.get(key, {}).get("passed") is not True for key in required_source):
                errors.append("source semantic suite is incomplete or failed")
    for path in args.smoke_evidence:
        try:
            item = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            errors.append(f"{path}: {error}"); continue
        rid = item.get("rid")
        if rid not in RIDS or rid in seen:
            errors.append(f"{path}: invalid or duplicate RID {rid!r}"); continue
        seen.add(rid)
        version, commit = item.get("version"), item.get("product_source_commit")
        if version != "0.2.3" or not isinstance(commit, str) or len(commit) < 7 or commit == "local":
            errors.append(f"{rid}: stale or unverifiable version/commit evidence")
        elif provenance is None:
            provenance = (version, commit)
        elif provenance != (version, commit):
            errors.append(f"{rid}: version/commit differs from other RID evidence")
        if item.get("status") != "passed": errors.append(f"{rid}: smoke status is not passed")
        kinds_for_rid = set()
        for case in item.get("pdf_semantic_cases", []):
            if not isinstance(case, dict) or case.get("kind") not in KINDS:
                errors.append(f"{rid}: malformed semantic case"); continue
            record = dict(case); record["rid"] = rid; records.append(record)
            kinds_for_rid.add(case.get("kind"))
            if case.get("status") != "pass": errors.append(f"{rid}: {case.get('fixture', '?')} failed")
        missing_kinds = KINDS - kinds_for_rid
        if missing_kinds: errors.append(f"{rid}: missing semantic evidence: " + ", ".join(sorted(missing_kinds)))
    missing = RIDS - seen
    if missing: errors.append("missing package evidence: " + ", ".join(sorted(missing)))
    output = {"schema_version": 1, "version": "0.2.3", "cases": records, "source_suite": source_suite,
              "rids": sorted(seen), "status": "pass" if not errors else "fail", "errors": errors}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())

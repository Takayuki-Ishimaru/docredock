#!/usr/bin/env python3
"""Fail-closed v0.2.3 release quality score aggregation.

This is deliberately a gate, rather than a marketing score.  Every input is
evidence produced by an earlier job.  Missing, skipped, malformed, or failed
evidence lowers the relevant section to zero and prevents a release.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any

VERSION = "0.2.3"
REQUIRED_RIDS = frozenset(("win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"))
WEIGHTS = {
    "office_readable_semantics": 25,
    "pdf_table_semantics": 20,
    "pdf_diagram_semantics": 15,
    "bounded_output": 10,
    "diagnostics_consistency": 10,
    "capability_ux": 5,
    "packaging_integrity": 10,
    "determinism": 5,
}


class EvidenceError(ValueError):
    pass


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise EvidenceError(f"cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise EvidenceError(f"{path}: root must be an object")
    return value


def ratio(passed: int, total: int) -> float:
    return passed / total if total else 0.0


def section(name: str, passed: int, total: int, reasons: list[str]) -> dict[str, Any]:
    fraction = ratio(passed, total)
    return {
        "weight": WEIGHTS[name], "passed": passed, "total": total,
        "percent": round(fraction * 100, 2), "points": round(fraction * WEIGHTS[name], 3),
        "gate_passed": total > 0 and fraction >= .90 and not reasons,
        "reasons": reasons,
    }


def conversion_section(root: Path) -> dict[str, Any]:
    reports = sorted(root.glob("**/report.json"))
    required = {".docx", ".xlsx", ".pptx"}
    seen: set[str] = set()
    passed = total = 0
    reasons: list[str] = []
    for report in reports:
        item = load_json(report)
        extension = Path(str(item.get("target_path", ""))).suffix.lower()
        if extension not in required:
            continue
        seen.add(extension)
        for check in item.get("checks", []):
            if not isinstance(check, dict):
                reasons.append(f"{report}: malformed check")
                continue
            total += 1
            if check.get("skipped"):
                reasons.append(f"{report}: skipped check {check.get('id', '?')}")
            elif check.get("pass") is True:
                passed += 1
            else:
                reasons.append(f"{report}: failed check {check.get('id', '?')}")
    missing = required - seen
    if missing:
        reasons.append("missing Office conversion evidence: " + ", ".join(sorted(missing)))
    return section("office_readable_semantics", passed, total, reasons)


def smoke_sections(paths: list[Path]) -> tuple[dict[str, dict[str, Any]], list[str]]:
    # A smoke record is trusted only when it was generated from an extracted
    # package, carries a verified checksum, and explicitly names a supported RID.
    records: list[tuple[Path, dict[str, Any]]] = [(path, load_json(path)) for path in paths]
    rids: set[str] = set(); release_commit: str | None = None
    critical: list[str] = []
    table = [0, 0, []]
    diagram = [0, 0, []]
    bounded = [0, 0, []]
    diagnostics = [0, 0, []]
    ux = [0, 0, []]
    integrity = [0, 0, []]
    deterministic = [0, 0, []]
    for path, evidence in records:
        rid = evidence.get("rid")
        if rid not in REQUIRED_RIDS:
            critical.append(f"{path}: unsupported or missing RID {rid!r}")
            continue
        if rid in rids:
            critical.append(f"duplicate smoke evidence for {rid}")
            continue
        rids.add(rid)
        if evidence.get("version") != VERSION:
            critical.append(f"{rid}: stale or wrong evidence version {evidence.get('version')!r}")
        commit = evidence.get("product_source_commit")
        if not isinstance(commit, str) or re.fullmatch(r"[0-9a-f]{40}", commit) is None:
            critical.append(f"{rid}: missing immutable product-source commit")
        elif release_commit is None:
            release_commit = commit
        elif release_commit != commit:
            critical.append(f"{rid}: product-source commit differs from the other RID evidence")
        status = evidence.get("status") == "passed"
        visual = evidence.get("visual_semantics") if isinstance(evidence.get("visual_semantics"), dict) else {}
        assertions = visual.get("relation_assertions") if isinstance(visual.get("relation_assertions"), list) else []
        cases = evidence.get("pdf_semantic_cases") if isinstance(evidence.get("pdf_semantic_cases"), list) else []
        kinds_for_rid: set[str] = set()
        for case in cases:
            if not isinstance(case, dict):
                critical.append(f"{path}: malformed PDF semantic case")
                continue
            kind = case.get("kind")
            kinds_for_rid.add(kind)
            outcome = case.get("status") == "pass"
            required_case_fields = ("command", "fixture_sha256", "output_sha256", "exit_code", "diagnostics", "assertions")
            valid_fields = (isinstance(case.get("command"), list) and bool(case["command"])
                            and all(isinstance(case.get(key), str) and re.fullmatch(r"[0-9a-f]{64}", case[key]) for key in ("fixture_sha256", "output_sha256"))
                            and case.get("exit_code") in (0, 1) and isinstance(case.get("assertions"), list) and bool(case["assertions"]))
            if case.get("critical") is not True or any(field not in case for field in required_case_fields) or not valid_fields:
                critical.append(f"{rid}: non-auditable critical semantic assertion {case.get('fixture', kind)!r}")
                outcome = False
            target = {"table": table, "diagram": diagram, "bounded": bounded, "diagnostic": diagnostics}.get(kind)
            if target is not None:
                target[1] += 1
                target[0] += int(outcome)
                if not outcome:
                    target[2].append(f"{rid}:{case.get('fixture', kind)}")
            if case.get("critical") is True and not outcome:
                critical.append(f"{rid}: critical PDF assertion failed: {case.get('fixture', kind)}")
        missing_kinds = {"table", "diagram", "bounded", "diagnostic"} - kinds_for_rid
        if missing_kinds:
            critical.append(f"{rid}: missing PDF semantic kinds: " + ", ".join(sorted(missing_kinds)))
        # Existing structured relation evidence is a separate diagram assertion.
        if assertions:
            diagram[1] += 1
            ok = status and all(row.get("status") == "passed" for row in assertions if isinstance(row, dict))
            # Guard against a corpus that only proves conservative rejection.
            # A release must also preserve at least one resolved, jittered
            # relation (the deterministic corpus supplies endpoint-gap,
            # translation, rotation, and label-offset variants).
            positive = [row for row in assertions if isinstance(row, dict)
                        and row.get("operation") in {"endpoint-gap", "translation", "rotation", "label-offset"}
                        and row.get("expected_relations")]
            ok = ok and any(row.get("status") == "passed" and row.get("deterministic") is True for row in positive)
            diagram[0] += int(ok)
            if not ok: diagram[2].append(f"{rid}: relation assertions or positive jitter coverage")
        else:
            critical.append(f"{rid}: missing structured relation assertions")
        ux[1] += 1; ux[0] += int(status and evidence.get("doctor_capability_status") == "passed")
        if not (status and evidence.get("doctor_capability_status") == "passed"): ux[2].append(f"{rid}: doctor/help capability evidence")
        integrity_ok = (status and evidence.get("version") == VERSION and isinstance(commit, str) and commit != "local"
                        and evidence.get("distribution_kind") == "extracted-package"
                        and isinstance(evidence.get("package_checksum_sha256"), str)
                        and len(evidence["package_checksum_sha256"]) == 64)
        integrity[1] += 1; integrity[0] += int(integrity_ok)
        if not integrity_ok: integrity[2].append(f"{rid}: extracted checksum/integrity")
        deterministic_ok = (status and bool(assertions) and isinstance(visual.get("determinism_failures"), list)
                            and not visual["determinism_failures"]
                            and all(row.get("deterministic") is True for row in assertions if isinstance(row, dict)))
        deterministic[1] += 1; deterministic[0] += int(deterministic_ok)
        if not deterministic_ok: deterministic[2].append(f"{rid}: deterministic visual output")
    missing = REQUIRED_RIDS - rids
    if missing: critical.append("missing package smoke evidence: " + ", ".join(sorted(missing)))
    result = {
        "pdf_table_semantics": section("pdf_table_semantics", table[0], table[1], table[2]),
        "pdf_diagram_semantics": section("pdf_diagram_semantics", diagram[0], diagram[1], diagram[2]),
        "bounded_output": section("bounded_output", bounded[0], bounded[1], bounded[2]),
        "diagnostics_consistency": section("diagnostics_consistency", diagnostics[0], diagnostics[1], diagnostics[2]),
        "capability_ux": section("capability_ux", ux[0], ux[1], ux[2]),
        "packaging_integrity": section("packaging_integrity", integrity[0], integrity[1], integrity[2]),
        "determinism": section("determinism", deterministic[0], deterministic[1], deterministic[2]),
    }
    return result, critical


def score(conversion_root: Path, smoke_paths: list[Path]) -> dict[str, Any]:
    sections = {"office_readable_semantics": conversion_section(conversion_root)}
    smoke, critical = smoke_sections(smoke_paths)
    sections.update(smoke)
    score_value = round(sum(item["points"] for item in sections.values()), 3)
    failed_sections = [name for name, item in sections.items() if not item["gate_passed"]]
    return {"schema_version": 1, "version": VERSION, "sections": sections,
            "total": score_value, "threshold": 95, "section_threshold_percent": 90,
            "critical_failures": critical, "passed": score_value >= 95 and not failed_sections and not critical,
            "failed_sections": failed_sections,
            "inputs": {"conversion_root": str(conversion_root), "smoke_evidence": [str(p) for p in smoke_paths]}}


def main() -> int:
    parser = argparse.ArgumentParser(description="Aggregate fail-closed v0.2.3 release-quality evidence.")
    parser.add_argument("--conversion-root", required=True, type=Path)
    parser.add_argument("--smoke-evidence", required=True, action="append", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        result = score(args.conversion_root, args.smoke_evidence)
    except EvidenceError as error:
        result = {"schema_version": 1, "version": VERSION, "passed": False, "critical_failures": [str(error)]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Quality score: {result.get('total', 0)}/100 ({'passed' if result['passed'] else 'failed'})")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

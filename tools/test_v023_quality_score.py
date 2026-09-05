import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("quality", Path(__file__).with_name("v023_quality_score.py"))
quality = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(quality)
sys.path.insert(0, str(Path(__file__).parent))
SMOKE_SPEC = importlib.util.spec_from_file_location("release_smoke", Path(__file__).with_name("release-smoke-test.py"))
release_smoke = importlib.util.module_from_spec(SMOKE_SPEC)
assert SMOKE_SPEC and SMOKE_SPEC.loader
SMOKE_SPEC.loader.exec_module(release_smoke)


class QualityScoreTests(unittest.TestCase):
    def test_doctor_schema_accepts_actual_partial_capability_report_and_rejects_invalid_variants(self):
        required = ["docx-readable", "xlsx-readable", "pptx-readable", "pdf-text", "ocr-engine", "ocr-jpn", "ocr-eng", "ocr-native", "pdf-rasterizer", "mermaid-render"]
        good = {"schema_version": "1", "capabilities": [{"id": name, "status": "partial" if name == "ocr-native" else "ready"} for name in required]}
        class Result:
            def __init__(self, stdout): self.stdout = stdout; self.returncode = 0
        original = release_smoke.invoke
        try:
            def fake_invoke(_cli, arguments, **_kwargs):
                return Result(json.dumps(good) if arguments == ["doctor", "--json"] else "help")
            release_smoke.invoke = fake_invoke
            self.assertEqual("passed", release_smoke.exercise_capability_ux(Path("doctor")))
            for mutation in (
                lambda value: value.pop("schema_version"),
                lambda value: value["capabilities"].pop(),
                lambda value: value["capabilities"].append(dict(value["capabilities"][0])),
                lambda value: value["capabilities"][0].update(status="broken"),
            ):
                with self.subTest(mutation=mutation):
                    invalid = json.loads(json.dumps(good)); mutation(invalid)
                    release_smoke.invoke = lambda _cli, arguments, **_kwargs: Result(json.dumps(invalid) if arguments == ["doctor", "--json"] else "help")
                    with self.assertRaises(RuntimeError): release_smoke.exercise_capability_ux(Path("doctor"))
        finally:
            release_smoke.invoke = original

    def test_platform_ocr_helpers_are_required_in_distribution_shape(self):
        original_platform = release_smoke.sys.platform
        try:
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                release_smoke.sys.platform = "win32"
                with self.assertRaisesRegex(RuntimeError, "windows-ocr"):
                    release_smoke.inspect_distribution(root, require_checksum=False)
                (root / "windows-ocr.ps1").write_text("# helper", encoding="utf-8")
                self.assertIsNone(release_smoke.inspect_distribution(root, require_checksum=False))
                release_smoke.sys.platform = "darwin"
                with self.assertRaisesRegex(RuntimeError, "vision-ocr.swift beside the CLI"):
                    release_smoke.inspect_distribution(root, require_checksum=False)
                (root / "vision-ocr.swift").write_text("// helper", encoding="utf-8")
                app = root / "DocRedock.app" / "Contents" / "MacOS"; app.mkdir(parents=True)
                with self.assertRaisesRegex(RuntimeError, "beside the GUI"):
                    release_smoke.inspect_distribution(root, require_checksum=False)
                (app / "vision-ocr.swift").write_text("// helper", encoding="utf-8")
                self.assertIsNone(release_smoke.inspect_distribution(root, require_checksum=False))
        finally:
            release_smoke.sys.platform = original_platform

    def write_report(self, root, extension, skipped=False):
        directory = root / extension[1:]
        directory.mkdir()
        (directory / "report.json").write_text(json.dumps({"target_path": "fixture" + extension, "checks": [{"id": extension, "pass": not skipped, "skipped": skipped}]}), encoding="utf-8")

    def smoke(self, path, rid, *, status="passed", checksum=True, table=True):
        cases = [{"kind": "table", "fixture": "simple-table", "status": "pass", "critical": True},
                 {"kind": "diagram", "fixture": "flow", "status": "pass", "critical": True},
                 {"kind": "bounded", "fixture": "dense", "status": "pass", "critical": True},
                 {"kind": "diagnostic", "fixture": "invariant", "status": "pass", "critical": True}]
        for case in cases:
            case.update({"command": ["export"], "fixture_sha256": "b" * 64, "output_sha256": "c" * 64,
                         "exit_code": 0, "diagnostics": [], "assertions": ["contract"]})
        if not table: cases[0]["status"] = "fail"
        document = {"rid": rid, "version": "0.2.3", "product_source_commit": "a" * 40, "status": status, "distribution_kind": "extracted-package", "package_checksum_sha256": "a" * 64,
                                    "doctor_capability_status": "passed", "pdf_semantic_cases": cases,
                                    "visual_semantics": {"determinism_failures": [], "relation_assertions": [{"status": "passed", "operation": "endpoint-gap", "expected_relations": [["START", "END", "directed", None]], "deterministic": True}]}}
        if not checksum:
            document["package_checksum_sha256"] = "tampered"
        path.write_text(json.dumps(document), encoding="utf-8")

    def test_complete_evidence_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for ext in (".docx", ".xlsx", ".pptx"): self.write_report(root, ext)
            evidence = []
            for rid in quality.REQUIRED_RIDS:
                path = root / f"{rid}.json"; self.smoke(path, rid); evidence.append(path)
            result = quality.score(root, evidence)
            self.assertTrue(result["passed"])
            self.assertEqual(100, result["total"])

    def test_skipped_or_missing_section_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.write_report(root, ".docx", skipped=True)
            result = quality.score(root, [])
            self.assertFalse(result["passed"])
            self.assertIn("office_readable_semantics", result["failed_sections"])

    def test_missing_rid_and_tampered_integrity_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for ext in (".docx", ".xlsx", ".pptx"): self.write_report(root, ext)
            path = root / "one.json"; self.smoke(path, "win-x64", checksum=False)
            result = quality.score(root, [path])
            self.assertFalse(result["passed"])
            self.assertTrue(any("missing package smoke evidence" in x for x in result["critical_failures"]))
            self.assertFalse(result["sections"]["packaging_integrity"]["gate_passed"])

    def test_stale_version_is_a_critical_failure_even_when_counts_are_perfect(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for ext in (".docx", ".xlsx", ".pptx"): self.write_report(root, ext)
            evidence = []
            for rid in quality.REQUIRED_RIDS:
                path = root / f"{rid}.json"; self.smoke(path, rid)
                document = json.loads(path.read_text(encoding="utf-8")); document["version"] = "0.2.2"
                path.write_text(json.dumps(document), encoding="utf-8"); evidence.append(path)
            result = quality.score(root, evidence)
            self.assertFalse(result["passed"])
            self.assertTrue(any("stale or wrong" in item for item in result["critical_failures"]))

    def test_duplicate_rid_is_critical_even_with_another_complete_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for ext in (".docx", ".xlsx", ".pptx"): self.write_report(root, ext)
            first = root / "first.json"; second = root / "second.json"
            self.smoke(first, "win-x64"); self.smoke(second, "win-x64")
            result = quality.score(root, [first, second])
            self.assertFalse(result["passed"])
            self.assertTrue(any("duplicate smoke evidence" in item for item in result["critical_failures"]))

    def test_each_single_rid_evidence_mutation_fails_closed(self):
        """Every field guarded by smoke_sections must independently reject a release."""
        mutations = {
            "missing-kind": lambda d: d["pdf_semantic_cases"].pop(),
            "empty-relations": lambda d: d["visual_semantics"].update(relation_assertions=[]),
            "nondeterministic-relation": lambda d: d["visual_semantics"]["relation_assertions"][0].update(deterministic=False),
            "missing-determinism-field": lambda d: d["visual_semantics"].pop("determinism_failures"),
            "empty-hash": lambda d: d["pdf_semantic_cases"][0].update(fixture_sha256=""),
            "empty-command": lambda d: d["pdf_semantic_cases"][0].update(command=[]),
            "empty-assertions": lambda d: d["pdf_semantic_cases"][0].update(assertions=[]),
            "bad-exit": lambda d: d["pdf_semantic_cases"][0].update(exit_code=7),
            "different-commit": lambda d: d.update(product_source_commit="b" * 40),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for ext in (".docx", ".xlsx", ".pptx"): self.write_report(root, ext)
                evidence = []
                for rid in quality.REQUIRED_RIDS:
                    path = root / f"{rid}.json"; self.smoke(path, rid); evidence.append(path)
                document = json.loads(evidence[0].read_text(encoding="utf-8")); mutate(document)
                evidence[0].write_text(json.dumps(document), encoding="utf-8")
                self.assertFalse(quality.score(root, evidence)["passed"])

    def test_semantic_aggregate_requires_source_and_all_kinds_per_rid(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); smoke = []
            for rid in quality.REQUIRED_RIDS:
                path = root / f"{rid}.json"; self.smoke(path, rid); smoke.append(path)
            # One RID drops a case; source evidence reports a failed bounded fixture.
            document = json.loads(smoke[0].read_text(encoding="utf-8")); document["pdf_semantic_cases"].pop()
            smoke[0].write_text(json.dumps(document), encoding="utf-8")
            source = root / "source.json"
            source.write_text(json.dumps({"status": "fail", "table_fixture": {"passed": True}, "dense_vector_fixture": {"passed": False}, "no_diagram_fixture": {"passed": True}}), encoding="utf-8")
            output = root / "aggregate.json"
            command = ["python3", "tools/v023_semantic_quality.py", "--source-evidence", str(source), "--output", str(output)]
            for path in smoke: command.extend(("--smoke-evidence", str(path)))
            result = subprocess.run(command, cwd=Path(__file__).parents[1], check=False)
            self.assertNotEqual(0, result.returncode)
            aggregate = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("fail", aggregate["status"])
            self.assertTrue(any("missing semantic evidence" in item for item in aggregate["errors"]))
            self.assertIn("source semantic suite", " ".join(aggregate["errors"]))


if __name__ == "__main__":
    unittest.main()

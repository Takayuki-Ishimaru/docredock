import contextlib
import importlib.util
import io
import json
import subprocess
import sys
from unittest import mock
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "corpus_regression", Path(__file__).with_name("corpus-regression") / "run.py"
)
corpus_regression = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(corpus_regression)


class TokenizerTests(unittest.TestCase):
    def test_latin_words_are_lowercased_whole_run_tokens(self):
        self.assertEqual(["hello", "world"], corpus_regression.tokenize("Hello World"))

    def test_digits_are_kept_as_a_single_run_token(self):
        self.assertEqual(["2024"], corpus_regression.tokenize("2024"))

    def test_digit_run_inside_a_word_splits_out_as_its_own_token(self):
        # "hello123world" is one whitespace-delimited word but three character-class
        # sub-runs: a latin word, a digit run, and another latin word.
        self.assertEqual(["hello", "123", "world"], corpus_regression.tokenize("hello123world"))

    def test_mixed_script_word_splits_into_sub_run_tokens(self):
        # "2024" -> digit run, "年" -> a lone CJK char (its own 1-char token).
        self.assertEqual(["2024", "年"], corpus_regression.tokenize("2024年"))

    def test_japanese_text_becomes_overlapping_character_bigrams(self):
        tokens = corpus_regression.tokenize("日本語です")
        self.assertEqual(["日本", "本語", "語で", "です"], tokens)

    def test_lone_cjk_character_is_its_own_token(self):
        self.assertEqual(["語"], corpus_regression.tokenize("語"))

    def test_punctuation_is_a_separator_not_a_token(self):
        self.assertEqual(["foo", "bar"], corpus_regression.tokenize("foo, bar!"))

    def test_empty_text_yields_no_tokens(self):
        self.assertEqual([], corpus_regression.tokenize(""))
        self.assertEqual([], corpus_regression.tokenize("   \n\t  "))


class MarkdownStripTests(unittest.TestCase):
    def test_front_matter_block_is_removed(self):
        text = "---\ndocument_id: abc123\nsource_format: docx\n---\nHello world\n"
        stripped = corpus_regression.strip_markdown(text)
        self.assertNotIn("document_id", stripped)
        self.assertIn("Hello world", stripped)

    def test_drmd_control_comments_are_removed_but_surrounding_text_kept(self):
        text = "<!--drmd:block id=b1 kind=paragraph-->Hello<!--drmd:block id=b2-->World"
        tokens = corpus_regression.tokenize(corpus_regression.strip_markdown(text))
        self.assertIn("hello", tokens)
        self.assertIn("world", tokens)
        for leaked in ("drmd", "block", "kind", "paragraph"):
            self.assertNotIn(leaked, tokens)

    def test_link_url_is_dropped_but_link_text_is_kept(self):
        text = "See [Example](https://example.com/secret/path) for details."
        tokens = corpus_regression.tokenize(corpus_regression.strip_markdown(text))
        self.assertIn("example", tokens)  # from the link text
        self.assertNotIn("secret", tokens)
        self.assertNotIn("path", tokens)

    def test_code_fence_markers_are_removed(self):
        text = "```mermaid\ngraph TD\n```\n"
        stripped = corpus_regression.strip_markdown(text)
        self.assertNotIn("```", stripped)


class ContentMetricsTests(unittest.TestCase):
    def test_identical_text_has_full_retention_and_no_spurious_tokens(self):
        result = corpus_regression.compute_content_metrics("hello world", "hello world")
        self.assertEqual(1.0, result["retention"])
        self.assertEqual(0.0, result["spurious_rate"])

    def test_dropped_content_lowers_retention_without_adding_spurious_tokens(self):
        result = corpus_regression.compute_content_metrics("hello world foo", "hello world")
        self.assertAlmostEqual(2 / 3, result["retention"])
        self.assertEqual(0.0, result["spurious_rate"])

    def test_duplicated_output_token_is_counted_as_spurious(self):
        # Source has "hello" once; output repeats it, so half the output is spurious
        # even though every source token is still present (full retention).
        result = corpus_regression.compute_content_metrics("hello", "hello hello")
        self.assertEqual(1.0, result["retention"])
        self.assertEqual(0.5, result["spurious_rate"])

    def test_missing_top_tokens_are_sorted_by_missing_count_descending(self):
        result = corpus_regression.compute_content_metrics("alpha beta beta gamma", "alpha")
        self.assertEqual(
            [{"token": "beta", "missing_count": 2}, {"token": "gamma", "missing_count": 1}],
            result["missing_top_tokens"],
        )

    def test_empty_source_yields_null_retention(self):
        result = corpus_regression.compute_content_metrics("", "some output text")
        self.assertIsNone(result["retention"])

    def test_empty_output_yields_null_spurious_rate(self):
        result = corpus_regression.compute_content_metrics("some source text", "")
        self.assertIsNone(result["spurious_rate"])
        self.assertEqual(0.0, result["retention"])


class DiagnosticsParserTests(unittest.TestCase):
    def test_parses_warning_and_information_lines_and_ignores_others(self):
        stdout = (
            "Exported: /out/report.docx.md\n"
            "Format:   docx\n"
            "WARNING TrackedRevisionsPresent: DOCX contains tracked revisions; "
            "edits crossing revision markup are strict-rejected.\n"
            "INFORMATION ExternalRelationshipPresent: one external relationship.\n"
        )
        diagnostics = corpus_regression.parse_diagnostics(stdout)
        self.assertEqual(2, len(diagnostics))
        self.assertEqual({"severity": "WARNING", "code": "TrackedRevisionsPresent",
                           "message": "DOCX contains tracked revisions; edits crossing revision markup are strict-rejected."},
                          diagnostics[0])
        self.assertEqual("INFORMATION", diagnostics[1]["severity"])
        self.assertEqual("ExternalRelationshipPresent", diagnostics[1]["code"])

    def test_error_lines_are_also_recognized(self):
        diagnostics = corpus_regression.parse_diagnostics("ERROR WorkspaceCorrupt: manifest missing.\n")
        self.assertEqual([{"severity": "ERROR", "code": "WorkspaceCorrupt", "message": "manifest missing."}], diagnostics)

    def test_visual_summary_line_is_parsed_into_counters(self):
        line = "Visual summary: diagrams=1; vector_pages=2; native=3; high=4; medium=5; unresolved=6; fallback=7; rejected=8"
        summary = corpus_regression.parse_visual_summary(line)
        self.assertEqual(
            {"diagrams": 1, "vector_pages": 2, "native": 3, "high": 4, "medium": 5,
             "unresolved": 6, "fallback": 7, "rejected": 8},
            summary,
        )

    def test_exit_code_classification_matches_cli_exit_code_enum(self):
        self.assertEqual("success", corpus_regression.classify_status(0, False))
        self.assertEqual("success_with_warnings", corpus_regression.classify_status(1, False))
        self.assertEqual("failed", corpus_regression.classify_status(2, False))
        self.assertEqual("failed", corpus_regression.classify_status(10, False))
        self.assertEqual("timeout", corpus_regression.classify_status(None, True))
        self.assertEqual("error", corpus_regression.classify_status(None, False))
        self.assertFalse(corpus_regression.is_failure_status("success"))
        self.assertFalse(corpus_regression.is_failure_status("success_with_warnings"))
        self.assertTrue(corpus_regression.is_failure_status("failed"))
        self.assertTrue(corpus_regression.is_failure_status("timeout"))
        self.assertTrue(corpus_regression.is_failure_status("error"))


class CorpusDiscoveryTests(unittest.TestCase):
    def test_hidden_directories_and_lock_files_are_skipped(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "visible").mkdir()
            (root / "visible" / "report.docx").write_bytes(b"x")
            (root / "visible" / "~$report.docx").write_bytes(b"x")  # Word lock file
            hidden = root / ".git"
            hidden.mkdir()
            (hidden / "should-be-ignored.pdf").write_bytes(b"x")
            (root / "notes.txt").write_bytes(b"x")  # unsupported extension
            (root / "sheet.XLSX").write_bytes(b"x")  # case-insensitive match

            found = corpus_regression.discover_corpus_files(root)
            relative = sorted(str(path.relative_to(root)) for path in found)
            self.assertEqual(["sheet.XLSX", "visible/report.docx"], relative)


class CliResolutionTests(unittest.TestCase):
    def test_explicit_executable_path_is_used_as_is(self):
        with tempfile.TemporaryDirectory() as temporary:
            fake_cli = Path(temporary) / "DocRedock.Cli"
            fake_cli.write_text("#!/bin/sh\necho fake\n", encoding="utf-8")
            fake_cli.chmod(0o755)
            prefix, kind = corpus_regression.resolve_cli(str(fake_cli))
            self.assertEqual([str(fake_cli)], prefix)
            self.assertEqual("binary", kind)

    def test_unknown_nonexistent_path_raises(self):
        with self.assertRaises(corpus_regression.CliResolutionError):
            corpus_regression.resolve_cli("/definitely/not/a/real/path/DocRedock.Cli")

    def test_dotnet_sentinel_builds_dotnet_run_command(self):
        # Only meaningful when dotnet is actually resolvable in this environment;
        # skip otherwise rather than asserting about the test machine's PATH.
        import shutil
        if shutil.which("dotnet") is None:
            self.skipTest("dotnet is not on PATH in this environment")
        prefix, kind = corpus_regression.resolve_cli("dotnet")
        self.assertEqual("dotnet-run", kind)
        self.assertIn("run", prefix)
        self.assertIn("--project", prefix)


class BaselineComparisonTests(unittest.TestCase):
    @staticmethod
    def _bucket(failure_rate=0.0, warning_rate=0.0, retention_mean=1.0, spurious_mean=0.0, f0_identical_rate=1.0):
        return {
            "count": 10, "failure_rate": failure_rate, "warning_rate": warning_rate,
            "retention_mean": retention_mean, "spurious_mean": spurious_mean,
            "f0_identical_rate": f0_identical_rate,
            "wall_time_readable_sec": {"median": 1.0, "p95": 2.0, "count": 10},
            "wall_time_roundtrip_sec": {"median": 1.0, "p95": 2.0, "count": 10},
        }

    def _report(self, overall_bucket, files=None):
        return {
            "schema_version": "1",
            "aggregate": {"overall": overall_bucket, "by_format": {"docx": overall_bucket}},
            "files": files or [],
        }

    def test_no_regression_when_metrics_are_stable(self):
        current = self._report(self._bucket())
        baseline = self._report(self._bucket())
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertFalse(comparison["has_regression"])
        self.assertEqual([], comparison["aggregate_regressions"])

    def test_retention_drop_beyond_tolerance_is_flagged(self):
        current = self._report(self._bucket(retention_mean=0.80))
        baseline = self._report(self._bucket(retention_mean=0.90))
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertTrue(comparison["has_regression"])
        metrics = {item["metric"] for item in comparison["aggregate_regressions"]}
        self.assertIn("retention_mean", metrics)

    def test_retention_drop_within_tolerance_is_not_flagged(self):
        current = self._report(self._bucket(retention_mean=0.895))
        baseline = self._report(self._bucket(retention_mean=0.90))
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertFalse(comparison["has_regression"])

    def test_failure_rate_increase_beyond_tolerance_is_flagged(self):
        current = self._report(self._bucket(failure_rate=0.10))
        baseline = self._report(self._bucket(failure_rate=0.02))
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertTrue(comparison["has_regression"])
        metrics = {item["metric"] for item in comparison["aggregate_regressions"]}
        self.assertIn("failure_rate", metrics)

    def test_per_file_new_failure_and_new_warning_code_are_detected(self):
        baseline_files = [
            {"file_id": "f1", "display_name": "f1", "failed": False, "warning_codes": ["A"],
             "profiles": {"readable": {"content": {"retention": 0.9}}}},
        ]
        current_files = [
            {"file_id": "f1", "display_name": "f1", "failed": True, "warning_codes": ["A", "B"],
             "profiles": {"readable": {"content": {"retention": 0.9}}}},
        ]
        current = self._report(self._bucket(), files=current_files)
        baseline = self._report(self._bucket(), files=baseline_files)
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertTrue(comparison["has_regression"])
        kinds = {item["kind"] for item in comparison["file_regressions"]}
        self.assertIn("new_failure", kinds)
        self.assertIn("new_warning_code", kinds)
        codes = {item.get("code") for item in comparison["file_regressions"] if item["kind"] == "new_warning_code"}
        self.assertEqual({"B"}, codes)

    def test_per_file_retention_drop_beyond_tolerance_is_detected(self):
        baseline_files = [
            {"file_id": "f1", "display_name": "f1", "failed": False, "warning_codes": [],
             "profiles": {"readable": {"content": {"retention": 0.95}}}},
        ]
        current_files = [
            {"file_id": "f1", "display_name": "f1", "failed": False, "warning_codes": [],
             "profiles": {"readable": {"content": {"retention": 0.80}}}},
        ]
        current = self._report(self._bucket(), files=current_files)
        baseline = self._report(self._bucket(), files=baseline_files)
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        file_regressions = [item for item in comparison["file_regressions"] if item["kind"] == "retention_drop"]
        self.assertEqual(1, len(file_regressions))
        self.assertAlmostEqual(15.0, file_regressions[0]["delta_pct_points"])

    def test_files_only_present_in_one_run_are_not_compared(self):
        current_files = [{"file_id": "new-file", "display_name": "new", "failed": True, "warning_codes": [],
                           "profiles": {}}]
        current = self._report(self._bucket(), files=current_files)
        baseline = self._report(self._bucket(), files=[])
        comparison = corpus_regression.compute_baseline_comparison(current, baseline, tolerance=1.0)
        self.assertEqual([], comparison["file_regressions"])


class RunCliTests(unittest.TestCase):
    def test_timeout_decodes_partial_bytes_and_preserves_diagnostics(self):
        command = ["fake-cli"]
        error = subprocess.TimeoutExpired(command, 1, output=b"WARNING Slow: partial\n\xff",
                                          stderr="途中".encode("utf-8") + b"\xe3")
        with mock.patch.object(corpus_regression.subprocess, "run", side_effect=error):
            result = corpus_regression.run_cli(command, 1)
        self.assertTrue(result["timed_out"])
        self.assertIsNone(result["returncode"])
        self.assertEqual("WARNING Slow: partial\n\ufffd", result["stdout"])
        self.assertIn("途中\ufffd", result["stderr"])
        self.assertIn("timed out after 1s", result["stderr"])
        self.assertEqual("Slow", corpus_regression.parse_diagnostics(result["stdout"])[0]["code"])
        json.dumps(result)

    def test_timeout_accepts_text_or_absent_partial_streams(self):
        for stdout, stderr in [("partial", "error"), (None, None)]:
            with self.subTest(stdout=stdout):
                error = subprocess.TimeoutExpired(["fake-cli"], 1, output=stdout, stderr=stderr)
                with mock.patch.object(corpus_regression.subprocess, "run", side_effect=error):
                    result = corpus_regression.run_cli(["fake-cli"], 1)
                self.assertEqual(stdout or "", result["stdout"])
                self.assertTrue(result["stderr"].startswith(stderr or ""))
                json.dumps(result)

    def test_real_timeout_returns_partial_output_without_crashing(self):
        result = corpus_regression.run_cli([sys.executable, "-c",
            'import sys,time; print("partial output",flush=True); '
            'print("partial error",file=sys.stderr,flush=True); time.sleep(5)'], timeout=1)
        self.assertTrue(result["timed_out"])
        self.assertIn("partial output", result["stdout"])
        self.assertIn("partial error", result["stderr"])
        json.dumps(result)


class RestoreResultTests(unittest.TestCase):
    def process_roundtrip(self, restore_status, restore_diagnostics=()):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.docx"
            source.write_bytes(b"original")
            exported = {"status": "success", "returncode": 0, "elapsed_sec": 0.1,
                        "timed_out": False, "output_exists": True, "warning_codes": []}
            restored = {"status": restore_status, "returncode": None, "elapsed_sec": 0.1,
                        "timed_out": restore_status == "timeout", "output_exists": False,
                        "diagnostics": list(restore_diagnostics)}
            with mock.patch.object(corpus_regression, "export_file", return_value=exported), \
                 mock.patch.object(corpus_regression, "restore_file", return_value=restored):
                return corpus_regression.process_file(["fake-cli"], root, source, root / "out",
                    ["roundtrip"], "off", 1, True, {})

    def test_failed_restores_count_as_failures_and_baseline_regressions(self):
        for status in ("failed", "timeout", "error"):
            with self.subTest(status=status):
                record = self.process_roundtrip(status)
                self.assertTrue(record["failed"])
                current = {"aggregate": corpus_regression.aggregate_results([record]), "files": [record]}
                baseline_record = dict(record, failed=False)
                baseline = {"aggregate": corpus_regression.aggregate_results([baseline_record]),
                            "files": [baseline_record]}
                self.assertEqual(1.0, current["aggregate"]["overall"]["failure_rate"])
                comparison = corpus_regression.compute_baseline_comparison(current, baseline, 1.0)
                self.assertTrue(comparison["has_regression"])
                self.assertIn("new_failure", {item["kind"] for item in comparison["file_regressions"]})

    def test_restore_warnings_are_included_in_warning_metrics(self):
        record = self.process_roundtrip("success_with_warnings",
            [{"severity": "WARNING", "code": "RestoreFallback", "message": "fallback"}])
        self.assertFalse(record["failed"])
        self.assertEqual(["RestoreFallback"], record["warning_codes"])
        self.assertTrue(record["has_warning"])
        aggregate = corpus_regression.aggregate_results([record])
        self.assertEqual(1.0, aggregate["overall"]["warning_rate"])
        self.assertEqual({"RestoreFallback": 1}, aggregate["warning_code_histogram"])


class HarnessInputTests(unittest.TestCase):
    def test_invalid_numeric_options_stop_before_launching_the_cli(self):
        with tempfile.TemporaryDirectory() as temporary:
            for option, value in [("--tolerance", "nan"), ("--tolerance", "inf"),
                                  ("--tolerance", "-inf"), ("--timeout", "0"),
                                  ("--timeout", "-1"), ("--limit", "0"), ("--limit", "-1")]:
                with self.subTest(option=option, value=value):
                    stderr = io.StringIO()
                    with contextlib.redirect_stderr(stderr), \
                         mock.patch.object(corpus_regression, "resolve_cli") as resolve:
                        code = corpus_regression.main(["--corpus", temporary, option + "=" + value])
                    self.assertEqual(2, code)
                    resolve.assert_not_called()
                    self.assertIn(option, stderr.getvalue())

    def test_non_object_or_invalid_utf8_baselines_are_reported_without_a_traceback(self):
        with tempfile.TemporaryDirectory() as temporary:
            baseline = Path(temporary) / "baseline.json"
            for data in (b"[]", b"null", b'"text"', b"1", b"\xff"):
                with self.subTest(data=data):
                    baseline.write_bytes(data)
                    stderr = io.StringIO()
                    with contextlib.redirect_stderr(stderr), \
                         mock.patch.object(corpus_regression, "resolve_cli") as resolve:
                        code = corpus_regression.main(["--corpus", temporary, "--baseline", str(baseline)])
                    self.assertEqual(2, code)
                    resolve.assert_not_called()
                    self.assertIn("baseline", stderr.getvalue())
                    self.assertNotIn("Traceback", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Unit tests for compare_ai_package.py using fake MCP replies (no user ZIP)."""
import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

_spec = importlib.util.spec_from_file_location(
    "compare_ai_package", Path(__file__).resolve().parent / "conversion-qa" / "compare_ai_package.py")
cap = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cap)

CONTEXT = "応答時間の単位はミリ秒です。\n"
ROW = "| REQ-0173 | 受信 | データ173を読み込む | 正常終了 173 | 1730 | TARGET\\_SENTINEL\\_173 特定行の確認 |"
PART_MD = CONTEXT + "\n| ID | 区分 | 内容 | 期待値 | 値 | 備考 |\n|---|---|---|---|---|---|\n" + ROW + "\n"

def search_json(part_ids):
    return json.dumps({"review_required": False,
                       "matches": [{"part_id": p, "lines": [ROW]} for p in part_ids]},
                      ensure_ascii=False)

def read_json(part_id, markdown=PART_MD, sources=None):
    body = {"part_id": part_id, "markdown": markdown, "review_required": False,
            "estimated_tokens": 100}
    if sources is not None:
        body["sources"] = sources
        body["locations"] = [{"id": "source-0001", "sheet_name": "試験一覧"}]
    return json.dumps(body, ensure_ascii=False)

class FakeMcp:
    def __init__(self, replies):
        self.replies, self.calls, self.closed, self.seen = replies, [], False, {}
    def call_tool(self, name, arguments):
        self.calls.append((name, arguments))
        reply = self.replies[name]
        if isinstance(reply, Exception):
            raise reply
        if isinstance(reply, list):
            i = self.seen.get(name, 0)
            self.seen[name] = i + 1
            reply = reply[min(i, len(reply) - 1)]
        return [reply]
    def close(self):
        self.closed = True

class EvidenceTest(unittest.TestCase):
    def test_exact_expected_row_passes(self):
        failures = []
        cap.check_evidence(PART_MD, "part", failures)
        self.assertEqual([], failures)

    def test_wrong_value_fails(self):
        bad = PART_MD.replace("| 1730 |", "| 1731 |")
        failures = []
        cap.check_evidence(bad, "part", failures)
        self.assertTrue(any("1730" in f for f in failures))

    def test_substring_value_is_not_exact_cell(self):
        bad = PART_MD.replace("| 1730 |", "| 17300 |")
        failures = []
        cap.check_evidence(bad, "part", failures)
        self.assertTrue(any("exact cell" in f for f in failures))

    def test_distinct_query_has_no_row(self):
        self.assertIsNone(cap.find_row(PART_MD, "REQ-9999"))
        self.assertEqual(cap.unescape(ROW), cap.find_row(PART_MD, cap.QUERY))

    def test_missing_unit_fails(self):
        failures = []
        cap.check_evidence(PART_MD.replace("ミリ秒", "秒"), "part", failures)
        self.assertTrue(any("ミリ秒" in f for f in failures))

    def test_sentinel_must_be_in_the_answer_row(self):
        failures = []
        cap.check_evidence(PART_MD.replace("REQ-0173", "REQ-0174") + "\n| REQ-0173 | 1730 |", "part", failures)
        self.assertTrue(failures)

class ZipTest(unittest.TestCase):
    def test_missing_entry_fails_clearly(self):
        with tempfile.TemporaryDirectory() as wd:
            path = Path(wd, "eval.zip")
            with zipfile.ZipFile(path, "w") as z:
                z.writestr("evaluation/other.xlsx", b"x")
            with self.assertRaises(cap.McpError):
                cap.find_input_in_zip(path)

class ComparisonTest(unittest.TestCase):
    def setUp(self):
        self._zip = cap.find_input_in_zip
        self._client = cap.McpClient
        self._counter = cap.load_counter
        cap.find_input_in_zip = lambda p: b"fake-xlsx"
        cap.load_counter = lambda: None

    def tearDown(self):
        cap.find_input_in_zip = self._zip
        cap.McpClient = self._client
        cap.load_counter = self._counter

    def run_with(self, replies):
        holder = {}
        def factory(argv, timeout=None):
            holder["mcp"] = FakeMcp(replies)
            return holder["mcp"]
        cap.McpClient = factory
        with tempfile.TemporaryDirectory() as wd:
            package = Path(wd, "pkg")
            (package / "parts").mkdir(parents=True)
            (package / "document.md").write_text(CONTEXT + "\n" + ROW + "\n", encoding="utf-8")
            replies["export_ai_package"] = f"package_path: {package}"
            report = cap.run_comparison("eval.zip", ["unused"])
        return holder["mcp"], report

    def test_happy_path_counts_and_export_args(self):
        mcp, report = self.run_with({
            "package_search": search_json(["part-0006"]),
            "package_read": [read_json("part-0006"),
                             read_json("part-0006", sources=[
                                 {"source_id": "source-0001", "cell_addresses": ["F177"]},
                                 {"source_id": "source-0002", "cell_addresses": ["A1"]}])],
        })
        self.assertTrue(report["pass"], report["failures"])
        self.assertEqual("part-0006", report["part_id"])
        self.assertEqual(1, report["search_part_count"])
        self.assertEqual(2, report["source_count"])
        self.assertIsNone(report["full_markdown_tokens"])  # tiktoken unavailable
        export_args = [a for n, a in mcp.calls if n == "export_ai_package"][0]
        self.assertEqual(2000, export_args["chunk_chars"])
        self.assertTrue(export_args["table_row_blocks"])
        self.assertEqual("off", export_args["ocr"])
        self.assertTrue(mcp.closed)

    def test_wrong_part_id_fails(self):
        mcp, report = self.run_with({
            "package_search": search_json(["part-0006"]),
            "package_read": [read_json("part-0007"), read_json("part-0007", sources=[])],
        })
        self.assertFalse(report["pass"])
        self.assertTrue(any("part_id" in f for f in report["failures"]))

    def test_no_matches_fails(self):
        mcp, report = self.run_with({"package_search": search_json([])})
        self.assertFalse(report["pass"])
        self.assertEqual(0, report["search_part_count"])
        self.assertTrue(any("no matches" in f for f in report["failures"]))
        self.assertEqual(0, report["source_count"])

    def test_tool_error_propagates(self):
        with self.assertRaises(cap.McpError):
            self.run_with({"package_search": cap.McpError("package_search tool error")})

    def test_full_markdown_missing_unit_fails(self):
        with tempfile.TemporaryDirectory() as wd:
            package = Path(wd, "pkg"); package.mkdir()
            (package / "document.md").write_text(ROW + "\n", encoding="utf-8")
            mcp = FakeMcp({"export_ai_package": [f"package_path: {package}"],
                           "package_search": search_json(["part-0006"]),
                           "package_read": [read_json("part-0006"),
                                            read_json("part-0006", sources=[])]})
            cap.McpClient = lambda argv, timeout=None: mcp
            report = cap.run_comparison("eval.zip", ["unused"])
        self.assertFalse(report["pass"])
        self.assertTrue(any("full document.md" in f and "ミリ秒" in f for f in report["failures"]))

if __name__ == "__main__":
    unittest.main()

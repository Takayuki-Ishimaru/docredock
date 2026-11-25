import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL = Path(__file__).resolve().parent / "check-release-documents.py"
REPO_ROOT = Path(__file__).resolve().parent.parent
DOCS = REPO_ROOT / "release-docs"

SAFE_JA = "# v0.4.1\n`docredock export sample.xlsx --format html` で変換できます。"
SAFE_EN = "# v0.4.1\nImproved Office viewing."

# One representative forbidden value per rule family.
RULE_SAMPLES = {
    "private-email": "private.person@example.invalid",
    "windows-user-path": r"C:\Users\PrivatePerson\file.docx",
    "home-user-path": "/home/privateperson/file",
    "home-user-path-mac": "/Users/privateperson/file",
    "private-lan-url": "http://192.168.4.2:8080",
    "internal-metric-ja": "内部評価: 92",
    "internal-metric-ja2": "開発指標: 4",
    "internal-metric-ja3": "検証メモ: pending",
    "internal-metric-en": "pass rate: 98%",
    "internal-metric-en2": "quality score: 92",
    "api-token": "ghp_" + "x" * 36,
    "private-key": "-----BEGIN PRIVATE KEY-----",
}

# Round-2 regressions: uppercase wording, non-Users rooted drive path,
# sk-proj- token shape, uppercase URL scheme.
EXPANDED_SAMPLES = [
    "Internal Score: 4",
    "DEVELOPMENT METRIC: 4",
    r"D:\work\private\report.docx",
    "sk-proj-" + "a" * 48,
    "HTTP://192.168.1.8:9000",
]


class CheckReleaseDocuments(unittest.TestCase):
    def make_root(self, ja=None, en=None, reports=None):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        docs = root / "release-docs"
        docs.mkdir()
        if ja is not None:
            (docs / "RELEASE_NOTES_v0.4.1.md").write_text(ja, encoding="utf-8")
        if en is not None:
            (docs / "RELEASE_NOTES_v0.4.1.en.md").write_text(en, encoding="utf-8")
        for name, text in (reports or {}).items():
            (docs / name).write_text(text, encoding="utf-8")
        return root

    def run_tool(self, root, tag="v0.4.1"):
        return subprocess.run(
            [sys.executable, str(TOOL), "--root", str(root), "--tag", tag],
            capture_output=True, text=True, timeout=10)

    def assert_flags(self, value):
        root = self.make_root(ja="# v0.4.1\n" + value, en=SAFE_EN)
        r = self.run_tool(root)
        self.assertNotEqual(r.returncode, 0, value)
        combined = r.stdout + r.stderr
        self.assertNotIn(value, combined)
        self.assertNotIn(str(root), combined)

    def test_clean_pair_optional_reports_absent(self):
        root = self.make_root(ja=SAFE_JA, en=SAFE_EN)
        r = self.run_tool(root)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(r.stdout.strip(), "")

    def test_clean_with_safe_optional_reports(self):
        root = self.make_root(ja=SAFE_JA, en=SAFE_EN, reports={
            "CONVERSION_REPORT_v0.4.1.md": "# 変換レポート\n対応範囲を記載します。",
            "CONVERSION_REPORT_v0.4.1.en.md": "# Conversion report\nScope notes.",
        })
        self.assertEqual(self.run_tool(root).returncode, 0)

    def test_optional_report_not_required(self):
        root = self.make_root(ja=SAFE_JA, en=SAFE_EN)
        self.assertFalse(
            (root / "release-docs" / "CONVERSION_REPORT_v0.4.1.md").exists())
        self.assertEqual(self.run_tool(root).returncode, 0)

    def test_missing_english(self):
        root = self.make_root(ja=SAFE_JA)
        r = self.run_tool(root)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("missing-required-document", r.stderr)

    def test_missing_japanese(self):
        root = self.make_root(en=SAFE_EN)
        r = self.run_tool(root)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("missing-required-document", r.stderr)

    def test_unsafe_optional_report_blocks(self):
        root = self.make_root(ja=SAFE_JA, en=SAFE_EN, reports={
            "CONVERSION_REPORT_v0.4.1.md": "internal memo: hidden"})
        r = self.run_tool(root)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("CONVERSION_REPORT_v0.4.1.md", r.stdout)
        self.assertIn("internal-metric", r.stdout)
        self.assertNotIn("hidden", r.stdout + r.stderr)

    def test_each_rule_family_flags(self):
        for label, value in RULE_SAMPLES.items():
            with self.subTest(rule=label):
                self.assert_flags(value)

    def test_expanded_rule_family_flags(self):
        for value in EXPANDED_SAMPLES:
            with self.subTest(value=value):
                self.assert_flags(value)

    def test_finding_format_is_relative_line_rule(self):
        root = self.make_root(ja="# v0.4.1\nprivate.person@example.invalid",
                              en=SAFE_EN)
        r = self.run_tool(root)
        relname, lineno, rule_id = r.stdout.strip().splitlines()[0].split(":")
        self.assertEqual(relname, "release-docs/RELEASE_NOTES_v0.4.1.md")
        self.assertFalse(relname.startswith("/"))
        self.assertEqual(lineno, "2")
        self.assertEqual(rule_id, "private-email")

    def test_invalid_tag_rejected(self):
        for bad in ["../escape", "v1", "V1.2.3", "v1.2", "1.2.3",
                    "v1.2.3.4", "", "v1.2.3\n", "v1.2.3 "]:
            with self.subTest(tag=bad):
                root = self.make_root(ja=SAFE_JA, en=SAFE_EN)
                r = self.run_tool(root, tag=bad)
                self.assertNotEqual(r.returncode, 0)
                self.assertIn("invalid-tag", r.stderr)
                self.assertNotIn(str(root), r.stdout + r.stderr)

    def test_valid_tag_forms_accepted(self):
        for good in ["v0.4.1", "v10.20.30"]:
            with self.subTest(tag=good):
                root = self.make_root()
                docs = root / "release-docs"
                (docs / f"RELEASE_NOTES_{good}.md").write_text("# ok", encoding="utf-8")
                (docs / f"RELEASE_NOTES_{good}.en.md").write_text("# ok", encoding="utf-8")
                self.assertEqual(self.run_tool(root, tag=good).returncode, 0)

    def test_allowed_content_not_flagged(self):
        allowed = "\n".join([
            "# DocRedock v0.4.0",
            "[日本語](RELEASE_NOTES_v0.4.0.md)",
            "run `docredock export report.xlsx --format html`",
            "launch `./DocRedock` or `DocRedock.exe`",
            "schemas remain `2.2` and `1.2`",
            "[Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)",
            "[guide](../docs/ja/quick-tasks.md)",
            "verify against `SHA256SUMS` and `sbom.cdx.json`",
        ])
        root = self.make_root(ja=allowed, en=SAFE_EN)
        r = self.run_tool(root)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_real_release_notes_are_clean(self):
        ja = (DOCS / "RELEASE_NOTES_v0.4.0.md").read_text(encoding="utf-8")
        en = (DOCS / "RELEASE_NOTES_v0.4.0.en.md").read_text(encoding="utf-8")
        root = self.make_root()
        docs = root / "release-docs"
        (docs / "RELEASE_NOTES_v0.4.0.md").write_text(ja, encoding="utf-8")
        (docs / "RELEASE_NOTES_v0.4.0.en.md").write_text(en, encoding="utf-8")
        r = self.run_tool(root, tag="v0.4.0")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()

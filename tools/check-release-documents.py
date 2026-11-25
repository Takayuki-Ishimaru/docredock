#!/usr/bin/env python3
"""Privacy gate for tag-specific bilingual release documents (stdlib only).

Exit 0 when the required Japanese and English release notes are present and
clean. Exit nonzero when the tag is invalid, a required document is missing, or
any scanned document contains private or internal developer content. Optional
conversion reports are scanned only when present. Findings print only the
relative filename, line number and rule id -- never the matched value or the
absolute root.
"""

import argparse
import re
import sys
from pathlib import Path

# Strict ASCII tag: vN.N.N with no trailing newline (fullmatch, not $).
TAG_RE = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+")

# Each rule matches a forbidden *shape*, not incidental numbers, so ordinary
# filenames, relative CLI examples, versions, public GitHub links and product
# limitations stay clean. Matching is case-insensitive so uppercase wording and
# URL schemes are caught too.
RULES = [
    ("private-email", re.compile(
        r"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", re.IGNORECASE)),
    # Rooted Windows path: single drive letter, colon, one separator (not a URL
    # scheme's "//"), then a path segment. Lookbehind skips "https:" etc.
    ("windows-user-path", re.compile(
        r"(?<![A-Za-z0-9])[A-Za-z]:[\\/](?![\\/])[^\s\\/]+")),
    ("home-user-path", re.compile(
        r"(?<![\w.])/(?:Users|home)/[^\s/]+(?:/|\b)", re.IGNORECASE)),
    ("private-lan-url", re.compile(
        r"(?:https?|ftp|ws|wss)://(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}"
        r"|192\.168\.\d{1,3}\.\d{1,3}"
        r"|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}"
        r"|127\.\d{1,3}\.\d{1,3}\.\d{1,3}"
        r"|localhost"
        r"|169\.254\.\d{1,3}\.\d{1,3})"
        r"(?::\d{1,5})?(?:[/?#\s]|$)", re.IGNORECASE)),
    ("api-token", re.compile(
        r"(?:ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}"
        r"|ghu_[A-Za-z0-9]{20,}|ghs_[A-Za-z0-9]{20,}"
        r"|github_pat_[A-Za-z0-9_]{20,}"
        r"|sk-(?:proj-)?[A-Za-z0-9]{20,}|sk_live_[A-Za-z0-9]{20,}"
        r"|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}"
        r"|AIza[0-9A-Za-z_\-]{30,}|SG\.[A-Za-z0-9_\-]{20,})")),
    ("private-key", re.compile(
        r"-----BEGIN [A-Z ]*PRIVATE KEY-----", re.IGNORECASE)),
    ("internal-metric", re.compile(
        r"(?:quality[-\s ]score|evaluation[-\s ]score|benchmark[-\s ]score"
        r"|internal[-\s ]score|internal[-\s ]metric|internal[-\s ]memo"
        r"|development[-\s ]metric|test[-\s ]count|pass[-\s ]rate"
        r"|品質スコア|内部評価|内部スコア|内部点数|内部指標|開発指標"
        r"|内部メモ|検証メモ|テスト件数|合格率)", re.IGNORECASE)),
]


def scan_text(text):
    """Yield (line_number, rule_id) for every forbidden match in text."""
    for lineno, line in enumerate(text.splitlines(), start=1):
        for rule_id, pattern in RULES:
            if pattern.search(line):
                yield lineno, rule_id


def scan_file(path, relname):
    """Scan one file; return list of (relname, lineno, rule_id) findings."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return [(relname, 0, "unreadable-file")]
    return [(relname, lineno, rule_id)
            for lineno, rule_id in scan_text(text)]


def build_parser():
    parser = argparse.ArgumentParser(
        prog="check-release-documents.py",
        description="Privacy gate for bilingual release documents.")
    parser.add_argument("--root", required=True,
                        help="Repository root containing release-docs/.")
    parser.add_argument("--tag", required=True,
                        help="Release tag in strict vN.N.N form.")
    return parser


def run_check(root, tag):
    """Return (findings, error_ids). findings: (relname, lineno, rule_id)."""
    error_ids = []
    if not TAG_RE.fullmatch(tag):
        return [], ["invalid-tag"]

    docs = "release-docs"
    required = [f"{docs}/RELEASE_NOTES_{tag}.md",
                f"{docs}/RELEASE_NOTES_{tag}.en.md"]
    optional = [f"{docs}/CONVERSION_REPORT_{tag}.md",
                f"{docs}/CONVERSION_REPORT_{tag}.en.md"]

    findings = []
    for relname in required:
        path = Path(root) / relname
        if not path.is_file():
            error_ids.append("missing-required-document:" + relname)
            continue
        findings.extend(scan_file(path, relname))

    for relname in optional:
        path = Path(root) / relname
        if path.is_file():
            findings.extend(scan_file(path, relname))

    return findings, error_ids


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)

    findings, error_ids = run_check(args.root, args.tag)

    for relname, lineno, rule_id in findings:
        print(f"{relname}:{lineno}:{rule_id}")

    for err in error_ids:
        print(f"error: {err}", file=sys.stderr)

    if findings or error_ids:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""QA: full Markdown vs AI-package search+read on the 240-row Excel case.

Usage: compare_ai_package.py <evaluation.zip> -- dotnet .../DocRedock.Cli.dll mcp
Reads only ZIP entry evaluation/office/fixtures/office-large.xlsx (no extraction, nothing run).
Token counts measure evidence retention / context cost, not downstream LLM scoring.
No external LLM, no upload. tiktoken o200k_base is optional.
"""
import argparse
import json
import queue
import subprocess
import sys
import tempfile
import threading
import zipfile
from pathlib import Path

ZIP_ENTRY = "evaluation/office/fixtures/office-large.xlsx"
QUERY = "TARGET_SENTINEL_173"
REQUIREMENT = "REQ-0173"
TIMEOUT = 120.0

def unescape(t):
    return t.replace("\\_", "_")

def find_row(markdown, needle):
    for line in markdown.splitlines():
        decoded = unescape(line)
        if decoded.lstrip().startswith("|") and needle in decoded:
            return decoded
    return None

def check_evidence(markdown, label, failures):
    row = find_row(markdown, QUERY)
    if row is None:
        failures.append(f"{label}: no table row containing {QUERY!r}")
    else:
        cells = [cell.strip() for cell in row.strip().strip("|").split("|")]
        if len(cells) != 6 or cells[0] != REQUIREMENT or cells[4] != "1730" or QUERY not in cells[5]:
            failures.append(f"{label}: expected requirement {REQUIREMENT} and exact cell '1730' in the matching row")
    if "ミリ秒" not in markdown:
        failures.append(f"{label}: unit 'ミリ秒' not found")

def find_input_in_zip(zip_path):
    with zipfile.ZipFile(zip_path) as z:
        try:
            return z.read(ZIP_ENTRY)
        except KeyError as error:
            raise McpError(f"{zip_path} has no entry {ZIP_ENTRY!r}") from error

def load_counter():
    try:
        import tiktoken
    except ImportError:
        return None
    enc = tiktoken.get_encoding("o200k_base")
    return lambda t: len(enc.encode_ordinary(t))

class McpError(Exception):
    pass

class McpClient:
    """MCP stdio client: JSON-RPC 2.0, one object per line (see McpServer.cs)."""

    def __init__(self, argv, timeout=TIMEOUT):
        self.timeout, self.next_id = timeout, 0
        self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        self.lines = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        try:
            self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                        "clientInfo": {"name": "compare-ai-package", "version": "0.1"}})
            self.notify("notifications/initialized")
        except Exception:
            self.close()
            raise

    def _read(self):
        try:
            for line in self.proc.stdout:
                if line.strip():
                    self.lines.put(line)
        except Exception:
            pass
        self.lines.put(None)

    def _recv(self):
        try:
            line = self.lines.get(timeout=self.timeout)
        except queue.Empty:
            raise McpError(f"timeout: no reply within {self.timeout}s")
        if line is None:
            raise McpError("MCP server closed its output stream")
        return json.loads(line)

    def request(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()
        reply = self._recv()
        if reply.get("id") != self.next_id:
            raise McpError(f"{method}: unexpected response id")
        if "error" in reply:
            raise McpError(f"{method}: {reply['error']}")
        return reply.get("result", {})

    def notify(self, method):
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
        self.proc.stdin.flush()

    def call_tool(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        texts = [c.get("text", "") for c in result.get("content", []) if c.get("type") == "text"]
        if result.get("isError"):
            raise McpError(f"{name} tool error: {(texts[:1] or [''])[0][:200]}")
        return texts

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()
            self.proc.wait()

def package_path_of(texts):
    for t in texts:
        if t.startswith("package_path: "):
            return t[len("package_path: "):].strip()
    raise McpError("export_ai_package reply has no package_path line")

def run_comparison(zip_path, cli_argv, timeout=TIMEOUT, compact_sources=False):
    counter = load_counter()
    tok = lambda t: None if counter is None or t is None else counter(t)
    with tempfile.TemporaryDirectory(prefix="cmp-ai-pkg-") as wd:
        src = Path(wd, "office-large.xlsx")
        src.write_bytes(find_input_in_zip(zip_path))
        out = Path(wd, "out")
        out.mkdir()
        mcp = McpClient(cli_argv, timeout)
        try:
            texts = mcp.call_tool("export_ai_package", {
                "path": str(src), "output_dir": str(out), "overwrite": True,
                "chunk_chars": 2000, "table_row_blocks": True, "ocr": "off"})
            package = package_path_of(texts)
            doc_md = Path(package) / "document.md"
            if not doc_md.is_file():
                raise McpError(f"package has no document.md at {doc_md}")
            full = doc_md.read_text(encoding="utf-8")
            failures = []
            check_evidence(full, "full document.md", failures)
            search_text = mcp.call_tool("package_search", {"package": package, "query": QUERY})[0]
            part_ids = [m["part_id"] for m in json.loads(search_text).get("matches", [])
                        if m.get("part_id")]
            if not part_ids:
                failures.append(f"package_search found no matches for {QUERY!r}")
            report = {"zip": str(zip_path), "zip_entry": ZIP_ENTRY, "package": Path(package).name,
                      "query": QUERY, "token_backend": "tiktoken o200k_base" if counter else "unavailable",
                      "note": "Token counts are evidence retention / context costs, not LLM answer scores.",
                      "full_markdown_tokens": tok(full), "search_result_tokens": tok(search_text),
                      "search_part_count": len(part_ids), "part_id": part_ids[0] if part_ids else None,
                      "part_markdown_tokens": None, "read_json_tokens": None,
                      "read_overhead_tokens": None, "source_count": 0, "full_sources_tokens": None,
                      "failures": failures, "pass": False}
            if part_ids:
                read_text = mcp.call_tool("package_read",
                                          {"package": package, "part": part_ids[0]})[0]
                read = json.loads(read_text)
                if read.get("part_id") != part_ids[0]:
                    failures.append(f"package_read returned part_id {read.get('part_id')!r}, "
                                    f"expected {part_ids[0]!r}")
                part = read.get("markdown", "")
                check_evidence(part, f"part {part_ids[0]}", failures)
                report.update(part_markdown_tokens=tok(part), read_json_tokens=tok(read_text))
                if None not in (report["read_json_tokens"], report["part_markdown_tokens"]):
                    report["read_overhead_tokens"] = report["read_json_tokens"] - report["part_markdown_tokens"]
                sourced_text = mcp.call_tool("package_read", {
                    "package": package, "part": part_ids[0], "with_sources": True})[0]
                sourced = json.loads(sourced_text)
                sources = sourced.get("sources", [])
                sheet_ids = {location["id"] for location in sourced.get("locations", [])
                             if location.get("sheet_name") == "試験一覧"}
                if not any(source.get("source_id") in sheet_ids and "F177" in source.get("cell_addresses", [])
                           for source in sources):
                    failures.append("full sources: missing 試験一覧!F177")
                report["read_with_sources_tokens"] = tok(sourced_text)
                report["source_count"] = len(sources)
                report["full_sources_tokens"] = tok(json.dumps(
                    {"sources": sources, "locations": sourced.get("locations", [])},
                    ensure_ascii=False, separators=(",", ":")))
                if compact_sources:
                    compact_text = mcp.call_tool("package_read", {
                        "package": package, "part": part_ids[0], "source_detail": "compact"})[0]
                    compact = json.loads(compact_text)
                    ranges = compact.get("source_ranges", [])
                    if compact.get("sources") or compact.get("markdown") != part or not ranges:
                        failures.append("compact sources: expected the same Markdown with ranges and no full source records")
                    report["read_with_compact_sources_tokens"] = tok(compact_text)
                    report["compact_source_ranges"] = ranges
            report["pass"] = not failures
        finally:
            mcp.close()
    return report

def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    sep = argv.index("--") if "--" in argv else None
    if sep is None or sep == len(argv) - 1:
        print("usage: compare_ai_package.py <evaluation.zip> -- <cli argv...>", file=sys.stderr)
        return 2
    ap = argparse.ArgumentParser(add_help=False)
    ap.add_argument("--timeout", type=float, default=TIMEOUT)
    ap.add_argument("--compact-sources", action="store_true")
    ns, pre = ap.parse_known_args(argv[:sep])
    if len(pre) != 1:
        print("usage: compare_ai_package.py <evaluation.zip> -- <cli argv...>", file=sys.stderr)
        return 2
    try:
        report = run_comparison(pre[0], argv[sep + 1:], ns.timeout, compact_sources=ns.compact_sources)
    except (McpError, OSError, json.JSONDecodeError, zipfile.BadZipFile) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["pass"] else 1

if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""DRMD conversion QA harness.

変換 (export) -> 契約チェック (expectations.json) -> レンダリング (原本の PNG 化) -> レポート
を 1 コマンドで回す検証ハーネス。使い方は同ディレクトリの README.md を参照。

標準ライブラリ + Pillow のみに依存する。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Optional

try:
    from PIL import Image
except ImportError:  # pragma: no cover - README に Pillow 導入を前提として明記する
    Image = None

# --------------------------------------------------------------------------
# パス定数
# --------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUT_ROOT = REPO_ROOT / "artifacts" / "conversion-qa"
FIXTURES_ROOT = REPO_ROOT / "tests" / "DocRedock.Tests" / "Fixtures"
REFERENCE_XLSX = REPO_ROOT / "経費精算システム_設計書_検証用.xlsx"

EXPORT_TIMEOUT_SEC = 180
RENDER_TIMEOUT_SEC = 120

VALID_TYPES = {"contains", "not_contains", "unique", "regex", "count"}
VALID_SEVERITIES = {"guard", "goal"}

# --------------------------------------------------------------------------
# 道具の発見: 環境変数 -> PATH -> 既知パス
# --------------------------------------------------------------------------

def _is_executable(path: Optional[str]) -> bool:
    return bool(path) and Path(path).is_file() and os.access(path, os.X_OK)

def discover_tool(env_var: str, path_names: list, known_paths: list) -> dict:
    env_value = os.environ.get(env_var)
    if env_value:
        if _is_executable(env_value):
            return {"path": env_value, "source": f"env:{env_var}"}
        return {"path": None, "source": None, "note": f"{env_var}={env_value!r} is set but not executable"}

    for name in path_names:
        found = shutil.which(name)
        if found:
            return {"path": found, "source": "PATH"}

    for candidate in known_paths:
        candidate_str = str(candidate)
        if _is_executable(candidate_str):
            return {"path": candidate_str, "source": f"known:{candidate_str}"}

    return {"path": None, "source": None}

def discover_all_tools() -> dict:
    home = Path.home()
    codex_native = (
        home / ".cache" / "codex-runtimes" / "codex-primary-runtime" / "dependencies" / "native"
    )
    codex_override = (
        home / ".cache" / "codex-runtimes" / "codex-primary-runtime" / "dependencies" / "bin" / "override"
    )

    dotnet = discover_tool(
        "DRMD_DOTNET",
        ["dotnet"],
        [REPO_ROOT / ".tmp" / "dotnet" / "dotnet"],
    )
    soffice = discover_tool(
        "DRMD_SOFFICE",
        ["soffice"],
        [
            codex_native / "libreoffice-headless" / "libreoffice" / "LibreOfficeDev.app" / "Contents" / "MacOS" / "soffice",
            Path("/Applications/LibreOffice.app/Contents/MacOS/soffice"),
            Path("/opt/homebrew/bin/soffice"),
            Path("/usr/local/bin/soffice"),
        ],
    )
    pdftoppm = discover_tool(
        "DRMD_PDFTOPPM",
        ["pdftoppm"],
        [
            codex_override / "pdftoppm",
            codex_native / "poppler" / "bin" / "pdftoppm",
            codex_native / "poppler" / "poppler" / "bin" / "pdftoppm",
            Path("/opt/homebrew/bin/pdftoppm"),
            Path("/usr/local/bin/pdftoppm"),
        ],
    )
    qlmanage = discover_tool(
        "DRMD_QLMANAGE",
        ["qlmanage"],
        [Path("/usr/bin/qlmanage")],
    )
    return {"dotnet": dotnet, "soffice": soffice, "pdftoppm": pdftoppm, "qlmanage": qlmanage}

# --------------------------------------------------------------------------
# サブプロセス実行
# --------------------------------------------------------------------------

def run_cmd(cmd: list, cwd: Optional[Path] = None, timeout: int = 120) -> dict:
    start = time.time()
    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd) if cwd else None,
            capture_output=True, text=True, timeout=timeout,
        )
        return {
            "command": cmd,
            "returncode": proc.returncode,
            "stdout": proc.stdout,
            "stderr": proc.stderr,
            "elapsed_sec": round(time.time() - start, 2),
            "timed_out": False,
        }
    except subprocess.TimeoutExpired as exc:
        return {
            "command": cmd,
            "returncode": None,
            "stdout": exc.stdout or "",
            "stderr": (exc.stderr or "") + f"\n[harness] timed out after {timeout}s",
            "elapsed_sec": round(time.time() - start, 2),
            "timed_out": True,
        }
    except OSError as exc:
        return {
            "command": cmd,
            "returncode": None,
            "stdout": "",
            "stderr": f"[harness] failed to launch: {exc}",
            "elapsed_sec": round(time.time() - start, 2),
            "timed_out": False,
        }

def classify_export_status(returncode: Optional[int]) -> str:
    if returncode is None:
        return "error"
    if returncode == 0:
        return "success"
    if returncode == 1:
        return "success_with_warnings"
    return "failed"

def run_export(dotnet_path: str, source: Path, profile: str, output: Path,
                timeout: int = EXPORT_TIMEOUT_SEC, ocr_mode: str = "off") -> dict:
    output.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        dotnet_path, "run", "--project", "src/DocRedock.Cli", "-c", "Release", "--",
        "export", str(source), "--profile", profile, "--output", str(output), "--force", "--quiet",
        # 変換ロジックの検証を OCR エンジン差 (Vision/Tesseract の有無) から切り離す。
        # 画像内限定文字列の not_contains guard も OCR off が前提。
        # "ocr": true の項目だけは ocr_mode="auto" の第2エクスポートで評価する。
        "--ocr", ocr_mode,
    ]
    result = run_cmd(cmd, cwd=REPO_ROOT, timeout=timeout)
    result["profile"] = profile
    result["output_path"] = str(output)
    result["status"] = classify_export_status(result["returncode"])
    return result

# --------------------------------------------------------------------------
# expectations.json 契約 (COMPLEX_DESIGN_DOC_SPEC.md #5)
# --------------------------------------------------------------------------

def load_expectations(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as fh:
        return json.load(fh)

def evaluate_item(md_text: str, item: dict) -> dict:
    item_id = item.get("id", "?")
    desc = item.get("desc", "")
    severity = item.get("severity", "goal")
    severity_note = ""
    if severity not in VALID_SEVERITIES:
        severity_note = f" [unknown severity '{severity}', treated as goal]"
        severity = "goal"
    item_type = item.get("type")
    value = item.get("value", "")

    ok = False
    detail = ""
    try:
        if item_type == "contains":
            ok = value in md_text
            detail = "found" if ok else "not found"
        elif item_type == "not_contains":
            ok = value not in md_text
            detail = "absent (ok)" if ok else "unexpectedly present"
        elif item_type == "unique":
            count = md_text.count(value)
            ok = count == 1
            detail = f"count={count} (want 1)"
        elif item_type == "regex":
            match = re.search(value, md_text, re.MULTILINE)
            ok = match is not None
            detail = f"matched {match.group(0)!r}" if match else "no match"
        elif item_type == "count":
            count = md_text.count(value)
            min_v = item.get("min")
            max_v = item.get("max")
            ok = True
            if min_v is not None and count < min_v:
                ok = False
            if max_v is not None and count > max_v:
                ok = False
            detail = f"count={count} (min={min_v}, max={max_v})"
        else:
            detail = f"unknown type '{item_type}'"
    except re.error as exc:
        ok = False
        detail = f"invalid regex: {exc}"

    return {
        "id": item_id,
        "desc": desc,
        "severity": severity,
        "type": item_type,
        "value": value,
        "pass": ok,
        "detail": detail + severity_note,
    }

def evaluate_expectations(md_text: str, data: dict) -> list:
    return [evaluate_item(md_text, item) for item in data.get("items", [])]

def run_ocr_item_checks(dotnet_path, target: Path, out_dir: Path, items: list, result: dict) -> list:
    """"ocr": true の項目は OCR 有効 (--ocr auto) の第2エクスポートに対して評価する。

    OCR エンジンが無い環境では判定せず skipped として報告する (fail 扱いにしない)。"""
    checks = []
    ocr_md = None
    skip_detail = None
    if dotnet_path is None:
        skip_detail = "skipped: dotnet not found"
    else:
        ocr_out = out_dir / "ocr" / (target.stem + ".md")
        info = run_export(dotnet_path, target, "readable", ocr_out, ocr_mode="auto")
        result.setdefault("export", {})["readable_ocr"] = info
        if info["status"] in ("success", "success_with_warnings"):
            try:
                ocr_md = ocr_out.read_text(encoding="utf-8")
            except OSError as exc:
                skip_detail = f"skipped: failed to read {ocr_out}: {exc}"
        else:
            skip_detail = "skipped: readable export with --ocr auto failed"
        if ocr_md is not None and '<details class="ocr-extraction"' not in ocr_md:
            ocr_md = None
            skip_detail = "skipped: no OCR engine/extraction available on this machine"
    for item in items:
        if ocr_md is None:
            check = evaluate_item("", item)
            check["pass"] = False
            check["skipped"] = True
            check["detail"] = skip_detail or "skipped"
        else:
            check = evaluate_item(ocr_md, item)
            check["detail"] += " [ocr export]"
        checks.append(check)
    return checks

def summarize_checks(checks: list) -> dict:
    summary = {
        "guard": {"total": 0, "pass": 0, "fail": 0, "skipped": 0},
        "goal": {"total": 0, "pass": 0, "fail": 0, "skipped": 0},
    }
    for check in checks:
        bucket = summary["guard"] if check["severity"] == "guard" else summary["goal"]
        bucket["total"] += 1
        if check.get("skipped"):
            bucket["skipped"] += 1
        else:
            bucket["pass" if check["pass"] else "fail"] += 1
    summary["all_guards_pass"] = summary["guard"]["fail"] == 0
    return summary

# --------------------------------------------------------------------------
# レンダリング: 原本 -> PDF -> ページ PNG (150dpi)。道具が無ければ警告してスキップ。
# --------------------------------------------------------------------------

_LO_PROFILE_DIR: Optional[str] = None  # soffice 呼び出し間で使い回す一時プロファイル

def _lo_profile_uri() -> str:
    global _LO_PROFILE_DIR
    if _LO_PROFILE_DIR is None:
        _LO_PROFILE_DIR = tempfile.mkdtemp(prefix="docredock-conversion-qa-lo-")
    return Path(_LO_PROFILE_DIR).as_uri()

def _inspect_png(path: Path) -> dict:
    if Image is None:
        return {"path": str(path), "width": None, "height": None, "valid": None, "note": "Pillow not available"}
    try:
        with Image.open(path) as img:
            img.verify()
        with Image.open(path) as img:  # verify() は同じハンドルを再利用できないため開き直す
            width, height = img.size
        return {"path": str(path), "width": width, "height": height, "valid": True}
    except Exception as exc:  # noqa: BLE001 - Pillow は壊れた画像に対し様々な例外を投げる
        return {"path": str(path), "width": None, "height": None, "valid": False, "error": str(exc)}

def _soffice_to_pdf(soffice_path: str, source: Path, work_dir: Path):
    cmd = [
        soffice_path, "--headless", "--norestore",
        f"-env:UserInstallation={_lo_profile_uri()}",
        "--convert-to", "pdf", "--outdir", str(work_dir), str(source),
    ]
    result = run_cmd(cmd, cwd=REPO_ROOT, timeout=RENDER_TIMEOUT_SEC)
    pdf_path = work_dir / (source.stem + ".pdf")
    if result["returncode"] == 0 and pdf_path.is_file():
        return pdf_path, result
    return None, result

def _pdftoppm_pages(pdftoppm_path: str, pdf_path: Path, work_dir: Path, dpi: int = 150):
    prefix = work_dir / "raw-page"
    cmd = [pdftoppm_path, "-png", "-r", str(dpi), str(pdf_path), str(prefix)]
    result = run_cmd(cmd, cwd=REPO_ROOT, timeout=RENDER_TIMEOUT_SEC)
    pages = sorted(
        work_dir.glob("raw-page-*.png"),
        key=lambda p: int(re.search(r"-(\d+)\.png$", p.name).group(1)),
    )
    return pages, result

def _qlmanage_thumbnail(qlmanage_path: str, source: Path, work_dir: Path):
    cmd = [qlmanage_path, "-t", "-s", "1600", "-o", str(work_dir), str(source)]
    result = run_cmd(cmd, cwd=REPO_ROOT, timeout=30)
    produced = [p for p in work_dir.glob("*.png")]
    return (produced[0] if produced else None), result

def render_original(source: Path, out_dir: Path, tools: dict, requested: bool) -> dict:
    render_info = {
        "requested": requested, "tool_used": None, "page_count": 0,
        "png_paths": [], "pages": [], "warnings": [], "commands": [],
    }
    if not requested:
        return render_info

    soffice = tools["soffice"]["path"]
    pdftoppm = tools["pdftoppm"]["path"]
    qlmanage = tools["qlmanage"]["path"]

    with tempfile.TemporaryDirectory(prefix="docredock-conversion-qa-render-") as tmp:
        tmp_dir = Path(tmp)
        pdf_path = None

        if source.suffix.lower() == ".pdf":
            pdf_path = source
        elif soffice:
            pdf_path, cmd_result = _soffice_to_pdf(soffice, source, tmp_dir)
            render_info["commands"].append(cmd_result)
            if pdf_path is None:
                render_info["warnings"].append(
                    f"soffice failed to produce a PDF (returncode={cmd_result['returncode']}); see report stderr"
                )
        else:
            render_info["warnings"].append("soffice not found; cannot rasterize a non-PDF original")

        if pdf_path is not None and pdftoppm:
            pages, cmd_result = _pdftoppm_pages(pdftoppm, pdf_path, tmp_dir)
            render_info["commands"].append(cmd_result)
            if pages:
                out_dir.mkdir(parents=True, exist_ok=True)
                width = max(2, len(str(len(pages))))
                final_paths = []
                for idx, page in enumerate(pages, start=1):
                    dest = out_dir / f"original-page-{idx:0{width}d}.png"
                    shutil.copyfile(page, dest)
                    final_paths.append(dest)
                render_info["tool_used"] = "pdftoppm" if source.suffix.lower() == ".pdf" else "soffice+pdftoppm"
                render_info["page_count"] = len(final_paths)
                render_info["png_paths"] = [str(p) for p in final_paths]
                render_info["pages"] = [_inspect_png(p) for p in final_paths]
            else:
                render_info["warnings"].append("pdftoppm produced no pages")
        elif pdf_path is not None and qlmanage:
            thumb, cmd_result = _qlmanage_thumbnail(qlmanage, pdf_path, tmp_dir)
            render_info["commands"].append(cmd_result)
            render_info["warnings"].append(
                "pdftoppm not found; used qlmanage for a first-page-only thumbnail (not a full page render)"
            )
            if thumb:
                out_dir.mkdir(parents=True, exist_ok=True)
                dest = out_dir / "original-page-01.png"
                shutil.copyfile(thumb, dest)
                render_info["tool_used"] = "qlmanage(pdf,1p)"
                render_info["page_count"] = 1
                render_info["png_paths"] = [str(dest)]
                render_info["pages"] = [_inspect_png(dest)]
        elif pdf_path is None and qlmanage:
            thumb, cmd_result = _qlmanage_thumbnail(qlmanage, source, tmp_dir)
            render_info["commands"].append(cmd_result)
            render_info["warnings"].append(
                "soffice not found; used qlmanage directly on the original for a first-page-only thumbnail"
            )
            if thumb:
                out_dir.mkdir(parents=True, exist_ok=True)
                dest = out_dir / "original-page-01.png"
                shutil.copyfile(thumb, dest)
                render_info["tool_used"] = "qlmanage(original,1p)"
                render_info["page_count"] = 1
                render_info["png_paths"] = [str(dest)]
                render_info["pages"] = [_inspect_png(dest)]
        elif pdf_path is None:
            render_info["warnings"].append("no rendering tool available (soffice/pdftoppm/qlmanage); render skipped")

    return render_info

# --------------------------------------------------------------------------
# 1 target あたりの処理: export -> expectations -> render -> report
# --------------------------------------------------------------------------

def process_target(target: Path, expectations_path: Optional[Path], out_dir: Path,
                    tools: dict, do_render: bool) -> dict:
    result = {
        "target": target.name,
        "target_path": str(target),
        "expectations_path": str(expectations_path) if expectations_path else None,
        "out_dir": str(out_dir),
        "generated_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "tooling": tools,
        "warnings": [],
    }

    out_dir.mkdir(parents=True, exist_ok=True)

    dotnet = tools["dotnet"]["path"]
    export_info = {}
    md_text = None

    if not dotnet:
        result["warnings"].append("dotnet not found; export skipped entirely")
        export_info["readable"] = {"status": "error", "returncode": None}
        export_info["roundtrip"] = {"status": "error", "returncode": None}
    else:
        # 実運用と同じ資産ディレクトリ名 (<stem>.assets/) になるよう、出力名は元ファイルの stem に合わせる
        readable_out = out_dir / (target.stem + ".md")
        export_info["readable"] = run_export(dotnet, target, "readable", readable_out)
        if export_info["readable"]["status"] in ("success", "success_with_warnings"):
            try:
                md_text = readable_out.read_text(encoding="utf-8")
            except OSError as exc:
                result["warnings"].append(f"failed to read {readable_out}: {exc}")

        roundtrip_out = out_dir / "roundtrip" / (target.stem + ".md")
        export_info["roundtrip"] = run_export(dotnet, target, "roundtrip", roundtrip_out)

    result["export"] = export_info

    checks = []
    if expectations_path is not None:
        try:
            data = load_expectations(expectations_path)
            declared_profile = data.get("profile", "readable")
            result["expectations_declared_profile"] = declared_profile
            if declared_profile != "readable":
                result["warnings"].append(
                    f"expectations declares profile={declared_profile!r} but harness always grades the readable export"
                )
            items = data.get("items", [])
            normal_items = [item for item in items if not item.get("ocr")]
            ocr_items = [item for item in items if item.get("ocr")]
            if md_text is not None:
                checks = [evaluate_item(md_text, item) for item in normal_items]
            else:
                result["warnings"].append("readable export unavailable; expectations were not evaluated")
            if ocr_items:
                checks.extend(run_ocr_item_checks((tools.get("dotnet") or {}).get("path"), target, out_dir, ocr_items, result))
        except (OSError, json.JSONDecodeError) as exc:
            result["warnings"].append(f"failed to load expectations {expectations_path}: {exc}")

    result["checks"] = checks
    result["check_summary"] = summarize_checks(checks)

    result["render"] = render_original(target, out_dir / "render", tools, do_render)

    readable_rc = export_info["readable"].get("returncode")
    roundtrip_rc = export_info["roundtrip"].get("returncode")
    export_failed = (
        readable_rc is None or readable_rc >= 2
        or roundtrip_rc is None or roundtrip_rc >= 2
    )
    guard_failed = result["check_summary"]["guard"]["fail"] > 0
    result["target_failed"] = bool(export_failed or guard_failed)
    if expectations_path is None:
        result["status"] = "fail" if export_failed else "reference"
    else:
        result["status"] = "fail" if result["target_failed"] else "pass"

    write_report_json(result, out_dir)
    write_report_md(result, out_dir)
    return result

def error_result(target: Path, expectations_path: Optional[Path], out_dir: Path, exc: Exception) -> dict:
    """process_target が例外を投げた場合の最小限のフォールバック結果 (--all のバッチを止めない)。"""
    return {
        "target": target.name,
        "target_path": str(target),
        "expectations_path": str(expectations_path) if expectations_path else None,
        "out_dir": str(out_dir),
        "status": "fail",
        "target_failed": True,
        "check_summary": {
            "guard": {"total": 0, "pass": 0, "fail": 0},
            "goal": {"total": 0, "pass": 0, "fail": 0},
            "all_guards_pass": True,
        },
        "export": {"readable": {}, "roundtrip": {}},
        "render": {"requested": False, "png_paths": []},
        "error": f"{type(exc).__name__}: {exc}",
    }

# --------------------------------------------------------------------------
# レポート出力: report.json / report.md / summary.md
# --------------------------------------------------------------------------

def write_report_json(result: dict, out_dir: Path) -> None:
    path = out_dir / "report.json"
    with path.open("w", encoding="utf-8") as fh:
        json.dump(result, fh, ensure_ascii=False, indent=2)
        fh.write("\n")

def _md_cell(value: Any) -> str:
    text = "" if value is None else str(value)
    text = text.replace("|", "\\|").replace("\n", "<br>")
    if len(text) > 160:
        text = text[:157] + "..."
    return text

def render_report_md(result: dict) -> str:
    lines = [f"# conversion-qa report: {result['target']}", ""]
    lines.append(f"- target: `{result['target_path']}`")
    if result["expectations_path"]:
        lines.append(f"- expectations: `{result['expectations_path']}`")
    else:
        lines.append("- expectations: (none — reference entry)")
    lines.append(f"- generated_at: {result.get('generated_at', '')}")
    lines.append(f"- status: **{result['status']}**")
    if result.get("error"):
        lines.append(f"- error: {result['error']}")
    lines.append("")

    lines.append("## 変換 (export)")
    lines.append("")
    lines.append("| profile | exit code | status | elapsed(s) | output |")
    lines.append("| --- | --- | --- | --- | --- |")
    for key in ("readable", "roundtrip"):
        info = result["export"].get(key, {})
        lines.append(
            f"| {key} | {info.get('returncode')} | {info.get('status')} | "
            f"{info.get('elapsed_sec', '')} | `{info.get('output_path', '')}` |"
        )
    lines.append("")

    summary = result.get("check_summary", {
        "guard": {"pass": 0, "total": 0, "fail": 0}, "goal": {"pass": 0, "total": 0, "fail": 0},
    })
    lines.append("## スコア")
    lines.append("")
    lines.append(
        f"- guard: {summary['guard']['pass']}/{summary['guard']['total']} pass (fail {summary['guard']['fail']})"
    )
    lines.append(
        f"- goal: {summary['goal']['pass']}/{summary['goal']['total']} pass (fail {summary['goal']['fail']}"
        + (f", skipped {summary['goal'].get('skipped', 0)}" if summary['goal'].get('skipped') else "")
        + ")"
    )
    lines.append("")

    checks = result.get("checks", [])
    failing = [c for c in checks if not c["pass"] and not c.get("skipped")]
    skipped = [c for c in checks if c.get("skipped")]
    lines.append("## fail 項目")
    lines.append("")
    if failing:
        lines.append("| id | severity | type | desc | detail |")
        lines.append("| --- | --- | --- | --- | --- |")
        for check in failing:
            lines.append(
                f"| {_md_cell(check['id'])} | {_md_cell(check['severity'])} | {_md_cell(check['type'])} | "
                f"{_md_cell(check['desc'])} | {_md_cell(check['detail'])} |"
            )
    else:
        lines.append("(fail した項目はありません)")
    lines.append("")

    if skipped:
        lines.append("## skipped 項目 (環境依存のため未判定)")
        lines.append("")
        for check in skipped:
            lines.append(f"- {_md_cell(check['id'])}: {_md_cell(check['detail'])}")
        lines.append("")

    render = result.get("render", {"requested": False, "png_paths": [], "warnings": [], "pages": []})
    lines.append("## レンダリング")
    lines.append("")
    if not render.get("requested"):
        lines.append("(--render 未指定)")
    else:
        lines.append(f"- tool_used: {render.get('tool_used')}")
        lines.append(f"- page_count: {render.get('page_count', 0)}")
        for warning in render.get("warnings", []):
            lines.append(f"- warning: {warning}")
        for page in render.get("pages", []):
            size = f"{page['width']}x{page['height']}" if page.get("width") else "?"
            lines.append(f"  - `{page['path']}` ({size})")
    lines.append("")

    if result.get("warnings"):
        lines.append("## ハーネス警告")
        lines.append("")
        for warning in result["warnings"]:
            lines.append(f"- {warning}")
        lines.append("")

    lines.append("## 生成物")
    lines.append("")
    _readable_path = (result.get("export", {}).get("readable") or {}).get("output_path")
    _roundtrip_path = (result.get("export", {}).get("roundtrip") or {}).get("output_path")
    lines.append(f"- `{_readable_path or str(Path(result['out_dir']) / 'export.md')}`")
    lines.append(f"- `{_roundtrip_path or str(Path(result['out_dir']) / 'roundtrip' / 'export.md')}`")
    if render.get("png_paths"):
        lines.append(f"- `{result['out_dir']}/render/` ({render.get('page_count', 0)} PNG)")
    lines.append(f"- `{result['out_dir']}/report.json`")
    lines.append("")
    return "\n".join(lines)

def write_report_md(result: dict, out_dir: Path) -> None:
    (out_dir / "report.md").write_text(render_report_md(result), encoding="utf-8")

def render_summary_md(results: list) -> str:
    lines = ["# conversion-qa summary", ""]
    lines.append(f"generated_at: {datetime.now().astimezone().isoformat(timespec='seconds')}")
    lines.append("")
    lines.append("| target | status | guard pass/total | goal pass/total | readable exit | roundtrip exit | report |")
    lines.append("| --- | --- | --- | --- | --- | --- | --- |")
    for r in results:
        summary = r.get("check_summary", {
            "guard": {"pass": 0, "total": 0}, "goal": {"pass": 0, "total": 0},
        })
        readable = r.get("export", {}).get("readable", {})
        roundtrip = r.get("export", {}).get("roundtrip", {})
        report_path = Path(r["out_dir"]) / "report.md"
        lines.append(
            f"| {_md_cell(r['target'])} | {r['status']} | "
            f"{summary['guard']['pass']}/{summary['guard']['total']} | "
            f"{summary['goal']['pass']}/{summary['goal']['total']} | "
            f"{readable.get('returncode')} | {roundtrip.get('returncode')} | `{report_path}` |"
        )
    lines.append("")

    total_targets = len(results)
    failed_targets = sum(1 for r in results if r.get("target_failed"))
    lines.append(f"- targets: {total_targets} (failed: {failed_targets})")
    lines.append("")
    return "\n".join(lines)

def write_summary_md(results: list, out_root: Path) -> None:
    out_root.mkdir(parents=True, exist_ok=True)
    (out_root / "summary.md").write_text(render_summary_md(results), encoding="utf-8")

# --------------------------------------------------------------------------
# --all 用の探索: tests/DocRedock.Tests/Fixtures/**/*.expectations.json
# --------------------------------------------------------------------------

def discover_all_expectations() -> list:
    if not FIXTURES_ROOT.is_dir():
        return []
    return sorted(FIXTURES_ROOT.glob("**/*.expectations.json"))

# --------------------------------------------------------------------------
# CLI エントリポイント
# --------------------------------------------------------------------------

def _resolve_out_dir(default_root: Path, target: Path, override: Optional[str]) -> Path:
    if override:
        return Path(override).resolve()
    return default_root / target.name

def parse_args(argv: Optional[list] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="DRMD conversion QA harness: export -> expectations check -> render -> report",
    )
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--file", help="単一の変換対象ファイル")
    mode.add_argument(
        "--all", action="store_true",
        help="Fixtures 配下の *.expectations.json を全て処理し、xlsx 参考エントリも処理する",
    )
    parser.add_argument(
        "--expectations",
        help="--file 用の expectations.json を明示指定 (省略時は <stem>.expectations.json を隣接探索)",
    )
    parser.add_argument("--render", action="store_true", help="原本を PDF 経由で PNG にレンダリングする")
    parser.add_argument(
        "--out",
        help="--file 用の出力ディレクトリ (既定: artifacts/conversion-qa/<target名>/)",
    )
    args = parser.parse_args(argv)
    if args.all and (args.expectations or args.out):
        parser.error("--expectations / --out は --file 専用です")
    return args

def _print_tools(tools: dict) -> None:
    print("[conversion-qa] tools:")
    for name, info in tools.items():
        line = f"  - {name}: {info.get('path') or '(not found)'} ({info.get('source') or 'n/a'})"
        if info.get("note"):
            line += f" -- {info['note']}"
        print(line)

def main(argv: Optional[list] = None) -> int:
    args = parse_args(argv)
    tools = discover_all_tools()
    _print_tools(tools)

    if args.all:
        results = []

        for expectations_path in discover_all_expectations():
            try:
                data = load_expectations(expectations_path)
            except (OSError, json.JSONDecodeError) as exc:
                print(f"[conversion-qa] skip {expectations_path}: failed to parse ({exc})", file=sys.stderr)
                continue
            target = expectations_path.parent / data.get("target", "")
            if not target.is_file():
                print(f"[conversion-qa] skip {expectations_path}: target not found ({target})", file=sys.stderr)
                continue
            out_dir = _resolve_out_dir(DEFAULT_OUT_ROOT, target, None)
            print(f"[conversion-qa] processing {target} ...")
            try:
                result = process_target(target, expectations_path, out_dir, tools, args.render)
            except Exception as exc:  # noqa: BLE001 - 1 target の例外でバッチ全体を止めない
                print(f"[conversion-qa] ERROR processing {target}: {exc}", file=sys.stderr)
                result = error_result(target, expectations_path, out_dir, exc)
            results.append(result)
            print(f"  -> status={result['status']}")

        if REFERENCE_XLSX.is_file():
            out_dir = _resolve_out_dir(DEFAULT_OUT_ROOT, REFERENCE_XLSX, None)
            print(f"[conversion-qa] processing reference entry {REFERENCE_XLSX.name} ...")
            try:
                result = process_target(REFERENCE_XLSX, None, out_dir, tools, args.render)
            except Exception as exc:  # noqa: BLE001
                print(f"[conversion-qa] ERROR processing {REFERENCE_XLSX}: {exc}", file=sys.stderr)
                result = error_result(REFERENCE_XLSX, None, out_dir, exc)
            results.append(result)
            print(f"  -> status={result['status']}")
        else:
            print(f"[conversion-qa] reference xlsx not found: {REFERENCE_XLSX}", file=sys.stderr)

        write_summary_md(results, DEFAULT_OUT_ROOT)
        print(f"[conversion-qa] summary: {DEFAULT_OUT_ROOT / 'summary.md'}")
        failed = sum(1 for r in results if r.get("target_failed"))
        print(f"[conversion-qa] {len(results)} targets, {failed} failed")
        return 1 if failed else 0

    target = Path(args.file).resolve()
    if not target.is_file():
        print(f"[conversion-qa] file not found: {target}", file=sys.stderr)
        return 2

    if args.expectations:
        expectations_path = Path(args.expectations).resolve()
        if not expectations_path.is_file():
            print(f"[conversion-qa] expectations file not found: {expectations_path}", file=sys.stderr)
            return 2
    else:
        candidate = target.parent / f"{target.stem}.expectations.json"
        expectations_path = candidate if candidate.is_file() else None

    out_dir = _resolve_out_dir(DEFAULT_OUT_ROOT, target, args.out)
    try:
        result = process_target(target, expectations_path, out_dir, tools, args.render)
    except Exception as exc:  # noqa: BLE001
        print(f"[conversion-qa] ERROR processing {target}: {exc}", file=sys.stderr)
        result = error_result(target, expectations_path, out_dir, exc)
        write_report_json(result, out_dir)
        write_report_md(result, out_dir)

    print(f"[conversion-qa] status={result['status']} report={out_dir / 'report.md'}")
    return 1 if result["target_failed"] else 0

if __name__ == "__main__":
    sys.exit(main())

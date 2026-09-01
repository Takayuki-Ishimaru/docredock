#!/usr/bin/env python3
"""Smoke-test an extracted DocRedock distribution, including structural visual-semantics checks."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path

from visual_semantics_assertions import assert_expectation, parse_markdown
from visual_semantics_qa import (
    CliRunResult,
    build_evidence,
    generate_perturbation_corpus,
    run_materialized_corpus,
)


INFERENCE_MODES = ("native-only", "safe", "balanced")

HIDDEN_SENTINELS = {
    "docx": "DOCREDOCK_RELEASE_HIDDEN_DOCX",
    "xlsx": "DOCREDOCK_RELEASE_HIDDEN_XLSX",
    "pptx": "DOCREDOCK_RELEASE_HIDDEN_PPTX",
}


def write_zip(path: Path, parts: dict[str, str]) -> None:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, value in sorted(parts.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            archive.writestr(info, value.encode("utf-8"))


def create_docx(path: Path) -> None:
    merged_table = """<w:tbl>
      <w:tblGrid><w:gridCol w:w="3000"/><w:gridCol w:w="3000"/></w:tblGrid>
      <w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>Merged origin</w:t></w:r></w:p></w:tc></w:tr>
      <w:tr><w:tc><w:p><w:r><w:t>Left</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Right</w:t></w:r></w:p></w:tc></w:tr>
      <w:tr><w:tc><w:tcPr><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>Vertical origin</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>V1</w:t></w:r></w:p></w:tc></w:tr>
      <w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p><w:r><w:t>V2</w:t></w:r></w:p></w:tc></w:tr>
    </w:tbl>"""
    write_zip(
        path,
        {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "word/document.xml": f'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Before</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>{HIDDEN_SENTINELS["docx"]}</w:t></w:r></w:p>{merged_table}</w:body></w:document>',
        },
    )


def create_xlsx(path: Path) -> None:
    write_zip(
        path,
        {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "xl/workbook.xml": '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>',
            "xl/_rels/workbook.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
            "xl/sharedStrings.xml": '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Before</t></si></sst>',
            "xl/styles.xml": '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/></font></fonts><cellXfs count="1"><xf/></cellXfs></styleSheet>',
            "xl/worksheets/sheet1.xml": f'<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row><row r="2" hidden="1"><c r="A2" t="inlineStr"><is><t>{HIDDEN_SENTINELS["xlsx"]}</t></is></c></row></sheetData></worksheet>',
        },
    )


def create_pptx(path: Path) -> None:
    write_zip(
        path,
        {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "ppt/presentation.xml": '<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst></p:presentation>',
            "ppt/_rels/presentation.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/></Relationships>',
            "ppt/slides/slide1.xml": f'<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>Before</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:cNvPr id="3" name="Hidden" hidden="1"/></p:nvSpPr><p:txBody><a:bodyPr/><a:p><a:r><a:t>{HIDDEN_SENTINELS["pptx"]}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>',
        },
    )


def exercise_visual_semantics(root: Path, cli: Path) -> dict:
    corpus = generate_perturbation_corpus()
    output_root = root / "materialized-visual-semantics-output"
    output_root.mkdir(parents=True, exist_ok=True)

    def cli_runner(spec, fixture: Path, mode: str) -> CliRunResult:
        output = output_root / f"{spec.case_id}.{mode}.md"
        result = invoke(
            cli,
            ["export", str(fixture), "--profile", "readable", "--output", str(output),
             "--ocr", "off", "--visual-inference", mode],
            allowed=(0, 1),
            experimental=True,
        )
        if not output.is_file():
            raise RuntimeError(f"CLI did not create Markdown for {spec.case_id}")
        repeated = output_root / ".determinism" / output.name
        repeated.parent.mkdir(parents=True, exist_ok=True)
        repeated_result = invoke(
            cli,
            ["export", str(fixture), "--profile", "readable", "--output", str(repeated),
             "--ocr", "off", "--visual-inference", mode],
            allowed=(0, 1),
            experimental=True,
        )
        if not repeated.is_file():
            raise RuntimeError(f"CLI did not create repeated Markdown for {spec.case_id}")
        output_sha256 = digest(output)
        repeat_output_sha256 = digest(repeated)
        return CliRunResult(markdown=output.read_text(encoding="utf-8"),
                            diagnostics=result.stdout,
                            exit_code=result.returncode,
                            output_sha256=output_sha256,
                            repeat_output_sha256=repeat_output_sha256,
                            deterministic=(result.returncode == repeated_result.returncode
                                           and output_sha256 == repeat_output_sha256))

    metric_cases, records = run_materialized_corpus(
        root / "materialized-visual-semantics", corpus, cli_runner, parse_markdown
    )
    if len(records) != len(corpus):
        raise RuntimeError("materialized perturbation corpus did not execute every case")
    determinism_failures = [record["case_id"] for record in records
                            if record.get("deterministic") is False]
    tier_metrics = {
        tier: {
            "pass": sum(record["status"] == "passed" for record in records if record["tier"] == tier),
            "total": sum(record["tier"] == tier for record in records),
            "fail": sum(record["status"] != "passed" for record in records if record["tier"] == tier),
        }
        for tier in ("A", "B", "C")
    }
    return {"relation_assertions": list(records), "tier_metrics": tier_metrics,
            "determinism_failures": determinism_failures,
            "metric_cases": list(metric_cases), "materialized_records": list(records)}

def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def invoke(
    cli: Path,
    arguments: list[str],
    allowed: tuple[int, ...] = (0, 1),
    *,
    experimental: bool = False,
) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    if experimental:
        environment["DOCREDOCK_ENABLE_EXPERIMENTAL"] = "1"
    else:
        environment.pop("DOCREDOCK_ENABLE_EXPERIMENTAL", None)
    result = subprocess.run(
        [str(cli), *arguments],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=120,
        check=False,
        env=environment,
    )
    if result.returncode not in allowed:
        raise RuntimeError(
            f"command failed ({result.returncode}): {cli} {' '.join(arguments)}\n{result.stdout}"
        )
    return result


def validate_restored(path: Path, member: str) -> None:
    with zipfile.ZipFile(path) as archive:
        text = archive.read(member).decode("utf-8")
    if "After" not in text:
        raise RuntimeError(f"F1 restore did not update {member} in {path}")


def exercise_format(root: Path, cli: Path, extension: str, member: str, creator) -> Path:
    source = root / f"source.{extension}"
    readable = root / f"{extension}-readable.md"
    projection = root / f"{extension}-roundtrip.md"
    f0 = root / f"{extension}-f0.{extension}"
    f1 = root / f"{extension}-f1.{extension}"
    creator(source)

    readable_arguments = ["export", str(source), "--output", str(readable), "--ocr", "off"]
    if extension != "docx":
        readable_arguments.extend(["--profile", "readable"])
    invoke(cli, readable_arguments)
    if extension == "docx" and readable.with_suffix(".drmd").exists():
        raise RuntimeError("profile-omitted DOCX export unexpectedly created a round-trip sidecar")
    complete = root / f"{extension}-complete.md"
    sanitized = root / f"{extension}-sanitized.md"
    complete_result = invoke(
        cli,
        ["export", str(source), "--output", str(complete), "--profile", "readable", "--content-policy", "complete", "--ocr", "off"],
        experimental=True,
    )
    invoke(
        cli,
        ["export", str(source), "--output", str(sanitized), "--profile", "readable", "--content-policy", "sanitized", "--ocr", "off"],
        experimental=True,
    )
    sentinel = HIDDEN_SENTINELS[extension]
    if sentinel in readable.read_text(encoding="utf-8") or sentinel in sanitized.read_text(encoding="utf-8"):
        raise RuntimeError(f"{extension} hidden content leaked through a safe readable policy")
    if sentinel not in complete.read_text(encoding="utf-8") or "HiddenContentIncluded" not in complete_result.stdout:
        raise RuntimeError(f"{extension} complete policy did not include and warn about hidden content")

    invoke(
        cli,
        ["export", str(source), "--output", str(projection), "--profile", "roundtrip", "--ocr", "off"],
        experimental=True,
    )
    invoke(cli, ["verify", str(projection)])
    initial_diff = invoke(cli, ["diff", str(projection)], experimental=True)
    if "Operations: 0" not in initial_diff.stdout:
        raise RuntimeError(f"{extension} export-immediate diff was not empty:\n{initial_diff.stdout}")
    invoke(cli, ["restore", str(projection), "--output", str(f0)], experimental=True)
    if digest(source) != digest(f0):
        raise RuntimeError(f"{extension} F0 restore is not byte-identical")

    markdown = projection.read_text(encoding="utf-8")
    if "Before" not in markdown:
        raise RuntimeError(f"{extension} projection is missing the edit sentinel")
    projection.write_text(markdown.replace("Before", "After", 1), encoding="utf-8")
    invoke(cli, ["diff", str(projection)], experimental=True)
    invoke(cli, ["restore", str(projection), "--output", str(f1)], experimental=True)
    validate_restored(f1, member)
    return projection


def exercise_pack_and_tamper(root: Path, cli: Path, projection: Path) -> None:
    bundle = root / "roundtrip.drmdpkg"
    unpacked = root / "unpacked"
    invoke(cli, ["pack", str(projection), "--output", str(bundle)], experimental=True)
    invoke(cli, ["unpack", str(bundle), "--output", str(unpacked)], experimental=True)
    if not any(unpacked.rglob("*.md")):
        raise RuntimeError("unpacked package does not contain Markdown")

    sidecar = projection.with_suffix(".drmd")
    originals = [item for item in sidecar.rglob("*") if item.is_file() and "source" in item.parts]
    if not originals:
        raise RuntimeError("round-trip sidecar does not contain an original source payload")
    with originals[0].open("ab") as stream:
        stream.write(b"TAMPER")
    result = invoke(cli, ["verify", str(projection)], allowed=(2, 3, 5, 6, 7, 10))
    if result.returncode in (0, 1):
        raise RuntimeError("tampered sidecar was accepted")


def inspect_gui_binary(gui: Path) -> None:
    if gui.stat().st_size < 1024 * 1024:
        raise RuntimeError("GUI executable is unexpectedly small")
    if os.name != "nt" and not os.access(gui, os.X_OK):
        raise RuntimeError("GUI executable does not have its executable bit set")

    with gui.open("rb") as stream:
        header = stream.read(65536)
    host_machine = (
        os.environ.get("PROCESSOR_ARCHITEW6432")
        or os.environ.get("PROCESSOR_ARCHITECTURE", "")
        if sys.platform.startswith("win")
        else os.uname().machine
    ).lower()
    expected_architecture = {
        "amd64": "x86_64",
        "x86_64": "x86_64",
        "arm64": "arm64",
        "aarch64": "arm64",
    }.get(host_machine)
    if expected_architecture is None:
        raise RuntimeError(f"unsupported runner CPU architecture: {host_machine or 'unknown'}")

    if sys.platform.startswith("win"):
        if len(header) < 64 or header[:2] != b"MZ":
            raise RuntimeError("GUI executable is not a PE image")
        pe_offset = int.from_bytes(header[60:64], "little")
        if pe_offset + 6 > len(header) or header[pe_offset:pe_offset + 4] != b"PE\0\0":
            raise RuntimeError("GUI executable has an invalid PE header")
        actual_architecture = {
            0x8664: "x86_64",
            0xAA64: "arm64",
        }.get(int.from_bytes(header[pe_offset + 4:pe_offset + 6], "little"))
    elif sys.platform == "darwin":
        byte_order = {
            b"\xcf\xfa\xed\xfe": "little",
            b"\xfe\xed\xfa\xcf": "big",
        }.get(header[:4])
        if byte_order is None:
            raise RuntimeError("GUI executable is not a 64-bit Mach-O image")
        actual_architecture = {
            0x01000007: "x86_64",
            0x0100000C: "arm64",
        }.get(int.from_bytes(header[4:8], byte_order))
    elif sys.platform.startswith("linux"):
        if len(header) < 20 or header[:4] != b"\x7fELF" or header[4] != 2:
            raise RuntimeError("GUI executable is not a 64-bit ELF image")
        if header[5] not in (1, 2):
            raise RuntimeError("GUI executable has an invalid ELF byte-order marker")
        byte_order = "little" if header[5] == 1 else "big"
        actual_architecture = {
            62: "x86_64",
            183: "arm64",
        }.get(int.from_bytes(header[18:20], byte_order))
    else:
        raise RuntimeError(f"unsupported GUI smoke-test platform: {sys.platform}")

    if actual_architecture is None:
        raise RuntimeError("GUI executable has an unsupported CPU architecture")
    if actual_architecture != expected_architecture:
        raise RuntimeError(
            f"GUI architecture {actual_architecture} does not match runner {expected_architecture}"
        )


def exercise_gui(gui: Path) -> None:
    command = [str(gui)]
    if sys.platform.startswith("linux") and not os.environ.get("DISPLAY"):
        xvfb = shutil.which("xvfb-run")
        if not xvfb:
            raise RuntimeError("xvfb-run is required for the Linux GUI startup smoke test")
        command = [xvfb, "-a", str(gui)]
    environment = os.environ.copy()
    environment["DOCREDOCK_DISABLE_UPDATE_CHECK"] = "1"
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=environment)
    try:
        time.sleep(4)
        if process.poll() is not None:
            raise RuntimeError(f"GUI exited during startup with code {process.returncode}")

        # When xvfb-run is used, its wrapper can remain alive after a child
        # failure. Confirm that the packaged GUI executable itself is running.
        if sys.platform.startswith("linux"):
            expected = str(gui.resolve())
            pending = [process.pid]
            descendants = set()
            while pending:
                parent = pending.pop()
                children_file = Path(f"/proc/{parent}/task/{parent}/children")
                try:
                    children = [int(value) for value in children_file.read_text().split()]
                except (FileNotFoundError, PermissionError, ValueError):
                    children = []
                for child in children:
                    if child in descendants:
                        continue
                    descendants.add(child)
                    pending.append(child)
            running_paths = []
            for pid in descendants | {process.pid}:
                try:
                    running_paths.append(os.path.realpath(f"/proc/{pid}/exe"))
                except (FileNotFoundError, PermissionError):
                    pass
            if expected not in running_paths:
                raise RuntimeError("GUI executable did not remain running after startup")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)


FONT_SUFFIXES = {".ttf", ".ttc", ".otf", ".otc", ".woff", ".woff2"}


def inspect_distribution(root: Path, *, require_checksum: bool = True) -> str | None:
    font_files = [
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and path.suffix.lower() in FONT_SUFFIXES
    ]
    if font_files:
        raise RuntimeError(f"release package contains font files: {', '.join(sorted(font_files))}")

    checksum_file = root / "BINARY-SHA256SUMS"
    if not checksum_file.is_file():
        if require_checksum:
            raise RuntimeError("release package is missing BINARY-SHA256SUMS")
        return None
    root_resolved = root.resolve()
    checked = 0
    for line in checksum_file.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            expected, relative = line.split("  ", 1)
        except ValueError as exc:
            raise RuntimeError(f"malformed checksum line: {line!r}") from exc
        target = (root / relative).resolve()
        if root_resolved not in target.parents or not target.is_file():
            raise RuntimeError(f"checksum target is unsafe or missing: {relative}")
        actual = digest(target)
        if actual != expected:
            raise RuntimeError(f"checksum mismatch for {relative}")
        checked += 1
    if checked == 0:
        raise RuntimeError("BINARY-SHA256SUMS did not contain any files")
    return digest(checksum_file)


def exercise_pdf_render(root: Path, cli: Path) -> None:
    sentinel = "DocRedock 日本語PDF検証 ABC123"
    markdown = root / "japanese-pdf.md"
    pdf = root / "japanese-pdf.pdf"
    extracted = root / "japanese-pdf-extracted.md"
    markdown.write_text(f"# PDF smoke\n\n{sentinel}\n", encoding="utf-8")

    rendered = invoke(
        cli,
        ["render", str(markdown), "--format", "pdf", "--output", str(pdf)],
        allowed=(0,),
        experimental=True,
    )
    if not pdf.is_file():
        raise RuntimeError("Japanese PDF render did not create a PDF")
    if "PdfFontSelected" not in rendered.stdout:
        raise RuntimeError(f"Japanese PDF render did not report PdfFontSelected:\n{rendered.stdout}")

    invoke(
        cli,
        ["export", str(pdf), "--profile", "readable", "--output", str(extracted), "--ocr", "off"],
        experimental=True,
    )
    if sentinel not in extracted.read_text(encoding="utf-8"):
        raise RuntimeError("Japanese PDF re-extraction did not preserve the sentinel")

    vector_pdf = root / "vector-pdf.pdf"
    vector_extracted = root / "vector-pdf-extracted.md"
    # Two painted rectangles plus one open stroked path form a uniquely resolvable,
    # deliberately undirected vector edge. Native labels exercise the graph-to-Mermaid path.
    vector_pdf.write_bytes(
        b"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n"
        b"2 0 obj << /Length 145 >> stream\n"
        b"BT 1 0 0 1 0 0 Tm (Start) Tj 100 100 Td (End) Tj ET\n"
        b"0 0 20 20 re S 100 100 20 20 re S 0 0 m 100 100 l S\n"
        b"endstream\n%%EOF"
    )
    vector_result = invoke(
        cli,
        ["export", str(vector_pdf), "--profile", "readable", "--output", str(vector_extracted), "--ocr", "off"],
        allowed=(0, 1),
        experimental=True,
    )
    if not vector_extracted.is_file():
        raise RuntimeError("vector PDF smoke export did not create Markdown")
    vector_markdown = vector_extracted.read_text(encoding="utf-8")
    if "Start" not in vector_markdown or "End" not in vector_markdown:
        raise RuntimeError("vector PDF smoke export lost native text labels")
    mermaid = chr(96) * 3 + "mermaid"
    if mermaid not in vector_markdown or " ---" not in vector_markdown:
        raise RuntimeError("vector PDF smoke did not project a resolved undirected Mermaid edge")
    stable_visual_codes = ("VisualSemanticProjectionUnavailable", "VisualSemanticProjectionPartial", "VisualVectorUnresolved")
    if "VisualConnectorUnresolved" in vector_result.stdout or "[PDF visual content:" in vector_markdown:
        raise RuntimeError("resolved vector PDF smoke unexpectedly emitted connector fallback")

    partial_pdf = root / "partial-vector-pdf.pdf"
    partial_extracted = root / "partial-vector-pdf-extracted.md"
    partial_pdf.write_bytes(
        b"%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n"
        b"2 0 obj << /Length 56 >> stream\n"
        b"0 0 m 10 10 20 0 30 10 c S\n"
        b"endstream\n%%EOF"
    )
    partial_result = invoke(
        cli,
        ["export", str(partial_pdf), "--profile", "readable", "--output", str(partial_extracted), "--ocr", "off"],
        allowed=(0, 1),
        experimental=True,
    )
    partial_markdown = partial_extracted.read_text(encoding="utf-8")
    if not any(code in partial_result.stdout for code in stable_visual_codes) and "[PDF visual content:" not in partial_markdown:
        raise RuntimeError("partial vector PDF smoke did not retain diagnostic or source fallback")




MARKDOWN_LINK_PATTERN = re.compile(r"\[[^\]]*\]\(([^)]+)\)")


def verify_local_markdown_links(package_root: Path) -> None:
    resolved_root = package_root.resolve()
    documents: list[Path] = []
    visited_directories: set[Path] = set()
    for current, directory_names, file_names in os.walk(
        resolved_root, topdown=True, followlinks=True
    ):
        current_path = Path(current)
        resolved_current = current_path.resolve()
        try:
            resolved_current.relative_to(resolved_root)
        except ValueError as exception:
            raise RuntimeError(
                f"packaged Markdown directory escapes package root: {current_path}"
            ) from exception
        if resolved_current in visited_directories:
            directory_names.clear()
            continue
        visited_directories.add(resolved_current)

        safe_directories: list[str] = []
        for directory_name in sorted(directory_names):
            directory = current_path / directory_name
            resolved_directory = directory.resolve()
            try:
                resolved_directory.relative_to(resolved_root)
            except ValueError as exception:
                raise RuntimeError(
                    f"packaged Markdown directory escapes package root: {directory}"
                ) from exception
            if resolved_directory not in visited_directories:
                safe_directories.append(directory_name)
        directory_names[:] = safe_directories
        documents.extend(
            current_path / file_name
            for file_name in sorted(file_names)
            if file_name.lower().endswith(".md")
        )

    for document in sorted(documents):
        resolved_document = document.resolve()
        try:
            resolved_document.relative_to(resolved_root)
        except ValueError as exception:
            raise RuntimeError(
                f"packaged Markdown document escapes package root: {document}"
            ) from exception

        for match in MARKDOWN_LINK_PATTERN.finditer(resolved_document.read_text(encoding="utf-8")):
            target = match.group(1).strip().strip("<>").split("#", 1)[0]
            lowered_target = target.lower()
            if not target or target.startswith("#") or lowered_target.startswith("mailto:"):
                continue
            if lowered_target.startswith("file:"):
                raise RuntimeError(
                    f"packaged Markdown file URI is not allowed in {document}: {target}"
                )
            if lowered_target.startswith(("http://", "https://")) or "://" in target:
                continue
            resolved_target = (resolved_document.parent / target).resolve()
            try:
                resolved_target.relative_to(resolved_root)
            except ValueError as exception:
                raise RuntimeError(
                    f"packaged Markdown link escapes package root in {document}: {target}"
                ) from exception
            if not resolved_target.exists():
                raise RuntimeError(f"broken packaged Markdown link in {document}: {target}")


def verify_cli_launcher(cli: Path, expected_version: str) -> None:
    launcher = cli.parent / ("docredock.cmd" if os.name == "nt" else "docredock")
    if not launcher.is_file():
        raise RuntimeError(f"distribution is missing the documented CLI launcher: {launcher.name}")
    if os.name != "nt" and not os.access(launcher, os.X_OK):
        raise RuntimeError("documented CLI launcher is not executable")
    command = ["cmd", "/d", "/c", str(launcher), "--version"] if os.name == "nt" else [str(launcher), "--version"]
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            timeout=30, check=False)
    if result.returncode != 0 or not result.stdout.strip().startswith(f"DocRedock {expected_version}"):
        raise RuntimeError(f"documented CLI launcher failed: {result.stdout.strip()}")


def exercise_linux_install(package_root: Path, expected_version: str) -> None:
    if not sys.platform.startswith("linux") or not (package_root / "install.sh").is_file():
        return
    with tempfile.TemporaryDirectory(prefix="docredock-install-smoke-") as temporary:
        temporary_root = Path(temporary)
        prefix = temporary_root / "prefix"
        outside = temporary_root / "outside"
        outside.mkdir()

        prefix_link = temporary_root / "prefix-link"
        prefix_link.symlink_to(outside, target_is_directory=True)
        rejected = subprocess.run([str(package_root / "install.sh"), "--prefix", str(prefix_link)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if rejected.returncode == 0 or any(outside.iterdir()):
            raise RuntimeError("Linux installer accepted a symlinked prefix")

        desktop_target = outside / "desktop-target"
        desktop_target.write_text("outside sentinel", encoding="utf-8")
        desktop = prefix / "share" / "applications" / "docredock.desktop"
        desktop.parent.mkdir(parents=True)
        desktop.symlink_to(desktop_target)
        rejected = subprocess.run([str(package_root / "install.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if rejected.returncode == 0 or desktop_target.read_text(encoding="utf-8") != "outside sentinel":
            raise RuntimeError("Linux installer accepted a symlinked desktop entry")
        desktop.unlink()
        shutil.rmtree(prefix)

        unmanaged_application = prefix / "lib" / "docredock"
        unmanaged_application.mkdir(parents=True)
        unmanaged_sentinel = unmanaged_application / "sentinel"
        unmanaged_sentinel.write_text("unmanaged application", encoding="utf-8")
        rejected = subprocess.run([str(package_root / "install.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if (rejected.returncode == 0 or
                unmanaged_sentinel.read_text(encoding="utf-8") != "unmanaged application" or
                (prefix / "bin" / "docredock").exists()):
            raise RuntimeError("Linux installer replaced an unmanaged application directory")
        shutil.rmtree(prefix)

        unmanaged_icon = prefix / "share" / "icons" / "hicolor" / "256x256" / "apps" / "docredock.png"
        unmanaged_icon.parent.mkdir(parents=True)
        unmanaged_icon.write_text("unmanaged icon", encoding="utf-8")
        rejected = subprocess.run([str(package_root / "install.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if (rejected.returncode == 0 or
                unmanaged_icon.read_text(encoding="utf-8") != "unmanaged icon" or
                (prefix / "lib" / "docredock").exists()):
            raise RuntimeError("Linux installer replaced an unmanaged icon")
        shutil.rmtree(prefix)

        subprocess.run([str(package_root / "install.sh"), "--prefix", str(prefix)],
                       text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       timeout=30, check=True)
        launcher = prefix / "bin" / "docredock"
        desktop = prefix / "share" / "applications" / "docredock.desktop"
        marker = prefix / "lib" / "docredock" / ".docredock-managed"
        if not launcher.exists() or not desktop.is_file():
            raise RuntimeError("Linux installer did not create CLI and desktop launchers")
        if marker.read_text(encoding="utf-8") != "DocRedock-managed-install-v1\n":
            raise RuntimeError("Linux installer did not create the exact ownership marker")
        if f"Exec={prefix}/bin/docredock-gui" not in desktop.read_text(encoding="utf-8"):
            raise RuntimeError("Linux desktop entry does not contain the installed absolute GUI path")
        result = subprocess.run([str(launcher), "--version"], text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=30, check=False)
        if result.returncode != 0 or not result.stdout.strip().startswith(f"DocRedock {expected_version}"):
            raise RuntimeError("installed Linux CLI launcher failed")

        marker.unlink()
        rejected = subprocess.run([str(package_root / "uninstall.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if rejected.returncode == 0 or not launcher.exists() or not desktop.exists():
            raise RuntimeError("Linux uninstaller accepted a missing ownership marker")
        marker.write_text("tampered\n", encoding="utf-8")
        rejected = subprocess.run([str(package_root / "uninstall.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if rejected.returncode == 0 or not launcher.exists() or not desktop.exists():
            raise RuntimeError("Linux uninstaller accepted a tampered ownership marker")
        marker.write_text("DocRedock-managed-install-v1\n", encoding="utf-8")

        external_docs = outside / "external-docs"
        external_docs.mkdir()
        sentinel = external_docs / "sentinel"
        sentinel.write_text("keep", encoding="utf-8")
        installed_docs = prefix / "lib" / "docredock" / "docs"
        if installed_docs.exists():
            shutil.rmtree(installed_docs)
        installed_docs.symlink_to(external_docs, target_is_directory=True)
        rejected = subprocess.run([str(package_root / "uninstall.sh"), "--prefix", str(prefix)],
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=30, check=False)
        if rejected.returncode == 0 or not sentinel.is_file() or not launcher.exists():
            raise RuntimeError("Linux uninstaller accepted a symlinked managed directory")
        installed_docs.unlink()

        subprocess.run([str(package_root / "uninstall.sh"), "--prefix", str(prefix)],
                       text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       timeout=30, check=True)
        if launcher.exists() or desktop.exists():
            raise RuntimeError("Linux uninstaller left managed launchers behind")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--gui", required=True, type=Path)
    parser.add_argument("--gui-mode", choices=("startup", "binary"), default="startup")
    parser.add_argument("--expected-version", required=True, help="release version without the v prefix")
    parser.add_argument("--evidence-json", type=Path, default=Path("artifacts/visual-semantics-evidence.json"))
    args = parser.parse_args()
    cli = args.cli.resolve()
    gui = args.gui.resolve()
    if not cli.is_file() or not gui.is_file():
        raise SystemExit("extracted GUI and CLI executables are required")

    repository_root = Path(__file__).resolve().parents[1]
    direct_cli_root = (repository_root / "artifacts" / "cli").resolve()
    direct_gui_root = (repository_root / "artifacts" / "gui").resolve()
    direct_publish = (
        cli.parent.parent == direct_cli_root
        and gui.parent.parent == direct_gui_root
        and cli.parent.name == gui.parent.name
    )
    if direct_publish:
        inspect_distribution(cli.parent, require_checksum=False)
        inspect_distribution(gui.parent, require_checksum=False)
        package_checksum = None
        distribution_kind = "direct-publish"
    else:
        package_checksum = inspect_distribution(cli.parent)
        distribution_kind = "extracted-package"
    version = invoke(cli, ["--version"])
    if not version.stdout.strip().startswith(f"DocRedock {args.expected_version}"):
        raise RuntimeError(f"unexpected CLI version: {version.stdout.strip()}")
    verify_cli_launcher(cli, args.expected_version)
    verify_local_markdown_links(cli.parent)
    exercise_linux_install(cli.parent, args.expected_version)
    blocked = invoke(cli, ["restore", "missing.md"], allowed=(4,))
    if "DOCREDOCK_ENABLE_EXPERIMENTAL=1" not in blocked.stdout:
        raise RuntimeError("experimental command did not explain the opt-in environment variable")

    with tempfile.TemporaryDirectory(prefix="docredock-release-smoke-") as temporary:
        root = Path(temporary)
        docx_projection = exercise_format(root, cli, "docx", "word/document.xml", create_docx)
        exercise_format(root, cli, "xlsx", "xl/worksheets/sheet1.xml", create_xlsx)
        exercise_format(root, cli, "pptx", "ppt/slides/slide1.xml", create_pptx)
        visual_evidence = exercise_visual_semantics(root, cli)
        exercise_pdf_render(root, cli)
        exercise_pack_and_tamper(root, cli, docx_projection)
        inspect_gui_binary(gui)
        if args.gui_mode == "startup":
            exercise_gui(gui)

    gui_result = "GUI binary integrity/architecture and startup" if args.gui_mode == "startup" else "GUI binary integrity/architecture"
    qa_evidence = build_evidence(
        tag=f"v{args.expected_version}",
        version=args.expected_version,
        cases=visual_evidence["metric_cases"],
        corpus=generate_perturbation_corpus(),
    )
    failed_tiers = [tier for tier, item in qa_evidence["tiers"].items() if not item["gate"]["passed"]]
    determinism_failures = visual_evidence["determinism_failures"]
    qa_status = "failed" if failed_tiers or determinism_failures else "passed"
    evidence = {
        "tag": qa_evidence["tag"],
        "version": qa_evidence["version"],
        "schema_version": qa_evidence["schema_version"],
        "metrics": qa_evidence["metrics"],
        "tiers": qa_evidence["tiers"],
        "formats": qa_evidence["formats"],
        "execution": qa_evidence["execution"],
        "perturbation_corpus": qa_evidence["perturbation_corpus"],
        "target_version": args.expected_version,
        "commit": os.environ.get("GITHUB_SHA", "local"),
        "product_source_commit": os.environ.get("PRODUCT_SOURCE_COMMIT", os.environ.get("GITHUB_SHA", "local")),
        "release_workflow_commit": os.environ.get("RELEASE_WORKFLOW_COMMIT", os.environ.get("GITHUB_SHA", "local")),
        "rid": os.environ.get("RUNNER_ARCH", "unknown"),
        "distribution_kind": distribution_kind,
        "package_checksum_sha256": package_checksum,
        "visual_semantics": {
            "fixture_families": ["docx", "xlsx", "pptx", "pdf"],
            "assertion": "structured Mermaid graph smoke plus fallback diagnostics",
            "inference_modes": list(INFERENCE_MODES),
            "relation_assertions": visual_evidence["relation_assertions"],
            "mode_checks": visual_evidence["tier_metrics"],
            "determinism_failures": determinism_failures,
            "metrics": qa_evidence["metrics"],
            "tiers": qa_evidence["tiers"],
            "formats": qa_evidence["formats"],
            "execution": qa_evidence["execution"],
            "status": qa_status,
        },
        "status": qa_status,
    }
    args.evidence_json.parent.mkdir(parents=True, exist_ok=True)
    args.evidence_json.write_text(json.dumps(evidence, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if failed_tiers or determinism_failures:
        print(f"Visual semantics evidence: {args.evidence_json}")
        reasons = []
        if failed_tiers:
            reasons.append("metric gates failed: " + ", ".join(failed_tiers))
        if determinism_failures:
            reasons.append("non-deterministic cases: " + ", ".join(determinism_failures))
        raise RuntimeError("visual semantics " + "; ".join(reasons)
                           + f"; failed evidence written to {args.evidence_json}")
    distribution_result = "package checksums/font exclusion" if package_checksum is not None else "direct-publish font exclusion"
    print(f"Release smoke test passed for v{args.expected_version} versioning, experimental gating, hidden-content policies, DOCX/XLSX/PPTX readable export, structured visual-semantics smoke, F0/F1 restore, Japanese PDF rendering, {distribution_result}, pack/unpack, tamper rejection, and {gui_result}.")
    if package_checksum is not None:
        print(f"Verified package checksum manifest SHA-256: {package_checksum}")
    else:
        print("Direct publish outputs checked; checksum verification remains mandatory for extracted-package smoke.")
    print(f"Visual semantics evidence: {args.evidence_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

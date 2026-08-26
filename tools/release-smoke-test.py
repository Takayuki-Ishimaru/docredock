#!/usr/bin/env python3
"""Smoke-test an extracted DocRedock distribution without exercising PDF paths."""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path


HIDDEN_SENTINELS = {
    "docx": "DOCREDOCK_RELEASE_HIDDEN_DOCX",
    "xlsx": "DOCREDOCK_RELEASE_HIDDEN_XLSX",
    "pptx": "DOCREDOCK_RELEASE_HIDDEN_PPTX",
}


def write_zip(path: Path, parts: dict[str, str]) -> None:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, value in parts.items():
            archive.writestr(name, value.encode("utf-8"))


def create_docx(path: Path) -> None:
    write_zip(
        path,
        {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "word/document.xml": f'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Before</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>{HIDDEN_SENTINELS["docx"]}</w:t></w:r></w:p></w:body></w:document>',
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

    invoke(cli, ["export", str(source), "--output", str(readable), "--profile", "readable", "--ocr", "off"])
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

    invoke(cli, ["export", str(source), "--output", str(projection), "--ocr", "off"], experimental=True)
    invoke(cli, ["verify", str(projection)])
    invoke(cli, ["diff", str(projection)], experimental=True)
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
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--gui", required=True, type=Path)
    args = parser.parse_args()
    cli = args.cli.resolve()
    gui = args.gui.resolve()
    if not cli.is_file() or not gui.is_file():
        raise SystemExit("extracted GUI and CLI executables are required")

    version = invoke(cli, ["--version"])
    if not version.stdout.strip().startswith("DocRedock 0.1.3"):
        raise RuntimeError(f"unexpected CLI version: {version.stdout.strip()}")
    blocked = invoke(cli, ["restore", "missing.md"], allowed=(4,))
    if "DOCREDOCK_ENABLE_EXPERIMENTAL=1" not in blocked.stdout:
        raise RuntimeError("experimental command did not explain the opt-in environment variable")

    with tempfile.TemporaryDirectory(prefix="docredock-release-smoke-") as temporary:
        root = Path(temporary)
        docx_projection = exercise_format(root, cli, "docx", "word/document.xml", create_docx)
        exercise_format(root, cli, "xlsx", "xl/worksheets/sheet1.xml", create_xlsx)
        exercise_format(root, cli, "pptx", "ppt/slides/slide1.xml", create_pptx)
        exercise_pack_and_tamper(root, cli, docx_projection)
        exercise_gui(gui)

    print("Release smoke test passed for v0.1.3 versioning, experimental gating, hidden-content policies, DOCX/XLSX/PPTX readable export, F0/F1 restore, pack/unpack, tamper rejection, and GUI startup.")
    print("PDF paths intentionally skipped: current policy is do not use pending validation.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

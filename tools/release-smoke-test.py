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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--gui", required=True, type=Path)
    parser.add_argument("--gui-mode", choices=("startup", "binary"), default="startup")
    args = parser.parse_args()
    cli = args.cli.resolve()
    gui = args.gui.resolve()
    if not cli.is_file() or not gui.is_file():
        raise SystemExit("extracted GUI and CLI executables are required")

    version = invoke(cli, ["--version"])
    if not version.stdout.strip().startswith("DocRedock 0.1.4"):
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
        inspect_gui_binary(gui)
        if args.gui_mode == "startup":
            exercise_gui(gui)

    gui_result = "GUI binary integrity/architecture and startup" if args.gui_mode == "startup" else "GUI binary integrity/architecture"
    print(f"Release smoke test passed for v0.1.4 versioning, experimental gating, hidden-content policies, DOCX/XLSX/PPTX readable export, F0/F1 restore, pack/unpack, tamper rejection, and {gui_result}.")
    print("PDF paths intentionally skipped: current policy is do not use pending validation.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

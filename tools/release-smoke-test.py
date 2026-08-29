#!/usr/bin/env python3
"""Smoke-test an extracted DocRedock v0.1.6 distribution, including visual-semantics preservation paths."""

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


def exercise_visual_semantics(root: Path, cli: Path) -> None:
    fixtures = {
        "visual.docx": {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "word/document.xml": '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><w:body><w:p><mc:AlternateContent><mc:Choice Requires="w14" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"><w:r><w:t>ChoiceNode</w:t></w:r></mc:Choice><mc:Fallback><w:r><w:t>FallbackNode</w:t></w:r></mc:Fallback></mc:AlternateContent><w:r><w:drawing><wps:wsp><a:cNvPr id="start"/><a:xfrm><a:off x="0" y="0"/><a:ext cx="20" cy="20"/></a:xfrm><w:txbxContent><w:p><w:r><w:t>Start</w:t></w:r></w:p></w:txbxContent></wps:wsp><wps:wsp><a:cNvPr id="end"/><a:xfrm><a:off x="100" y="0"/><a:ext cx="20" cy="20"/></a:xfrm><w:txbxContent><w:p><w:r><w:t>End</w:t></w:r></w:p></w:txbxContent></wps:wsp><wps:wsp><a:cNvPr id="connector"/><a:prstGeom prst="line"/><a:stCxn id="start"/><a:endCxn id="end"/><a:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="0"/></a:xfrm></wps:wsp></w:drawing></w:r></w:p></w:body></w:document>',
        },
        "visual.pptx": {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
            "ppt/presentation.xml": '<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst></p:presentation>',
            "ppt/_rels/presentation.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/></Relationships>',
            "ppt/slides/slide1.xml": '<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/><p:sp><p:nvSpPr><p:cNvPr id="2" name="Start"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></a:xfrm><a:prstGeom prst="roundRect"/></p:spPr><p:txBody><a:bodyPr/><a:p><a:r><a:t>Start</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:cNvPr id="3" name="End"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="300" y="0"/><a:ext cx="100" cy="100"/></a:xfrm><a:prstGeom prst="rect"/></p:spPr><p:txBody><a:bodyPr/><a:p><a:r><a:t>End</a:t></a:r></a:p></p:txBody></p:sp><p:cxnSp><p:nvCxnSpPr><p:cNvPr id="4" name="edge"/><p:cNvCxnSpPr><a:stCxn id="2" idx="0"/><a:endCxn id="3" idx="0"/></p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr><p:spPr><a:prstGeom prst="line"/></p:spPr></p:cxnSp></p:spTree></p:cSld></p:sld>',
        },
        "visual.xlsx": {
            "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/></Types>',
            "_rels/.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdWorkbook" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
            "xl/workbook.xml": '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Flow" sheetId="1" r:id="rId1"/></sheets></workbook>',
            "xl/_rels/workbook.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
            "xl/worksheets/sheet1.xml": '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheetData/><drawing r:id="rDrawing"/></worksheet>',
            "xl/worksheets/_rels/sheet1.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDrawing" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="/xl/drawings/drawing1.xml"/></Relationships>',
            "xl/drawings/drawing1.xml": '<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:row>0</xdr:row></xdr:from><xdr:ext cx="900000" cy="500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="2" name="ProcessNode"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="flowChartProcess"/></xdr:spPr><xdr:txBody><a:bodyPr/><a:p><a:r><a:t>ProcessNode</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:oneCellAnchor><xdr:oneCellAnchor><xdr:from><xdr:col>4</xdr:col><xdr:row>0</xdr:row></xdr:from><xdr:ext cx="900000" cy="500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="3" name="DecisionNode"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="flowChartDecision"/></xdr:spPr><xdr:txBody><a:bodyPr/><a:p><a:r><a:t>DecisionNode</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:oneCellAnchor><xdr:oneCellAnchor><xdr:from><xdr:col>4</xdr:col><xdr:row>1</xdr:row></xdr:from><xdr:ext cx="400000" cy="10000"/><xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id="4" name="edge"/><xdr:cNvCxnSpPr><a:stCxn id="2" idx="0"/><a:endCxn id="3" idx="0"/></xdr:cNvCxnSpPr></xdr:nvCxnSpPr><xdr:spPr><a:prstGeom prst="line"/></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>',        },
    }
    mermaid = chr(96) * 3 + "mermaid"
    for filename, parts in fixtures.items():
        source = root / filename
        write_zip(source, parts)
        output = root / (filename + ".visual.md")
        invoke(cli, ["export", str(source), "--profile", "readable", "--output", str(output), "--ocr", "off"], allowed=(0, 1), experimental=True)
        markdown = output.read_text(encoding="utf-8")
        if filename.endswith(".docx"):
            if markdown.count("ChoiceNode") != 1 or "FallbackNode" in markdown or "-->" not in markdown or "Start" not in markdown or "End" not in markdown:
                raise RuntimeError("DOCX AlternateContent Choice/Fallback projection is incorrect")
        elif filename.endswith(".pptx"):
            if mermaid not in markdown or "v_2 --> v_3" not in markdown:
                raise RuntimeError("PPTX fixture did not preserve native connector Mermaid topology")
        elif filename.endswith(".xlsx"):
            if mermaid not in markdown or "ProcessNode" not in markdown or "DecisionNode" not in markdown or "N_S_2 --> N_S_3" not in markdown:
                raise RuntimeError("XLSX DrawingML flowChart fixture did not preserve Mermaid nodes/labels")


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


def inspect_distribution(root: Path) -> str:
    font_files = [
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and path.suffix.lower() in FONT_SUFFIXES
    ]
    if font_files:
        raise RuntimeError(f"release package contains font files: {', '.join(sorted(font_files))}")

    checksum_file = root / "BINARY-SHA256SUMS"
    if not checksum_file.is_file():
        raise RuntimeError("release package is missing BINARY-SHA256SUMS")
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

    package_checksum = inspect_distribution(cli.parent)
    version = invoke(cli, ["--version"])
    if not version.stdout.strip().startswith("DocRedock 0.1.6"):
        raise RuntimeError(f"unexpected CLI version: {version.stdout.strip()}")
    blocked = invoke(cli, ["restore", "missing.md"], allowed=(4,))
    if "DOCREDOCK_ENABLE_EXPERIMENTAL=1" not in blocked.stdout:
        raise RuntimeError("experimental command did not explain the opt-in environment variable")

    with tempfile.TemporaryDirectory(prefix="docredock-release-smoke-") as temporary:
        root = Path(temporary)
        docx_projection = exercise_format(root, cli, "docx", "word/document.xml", create_docx)
        exercise_format(root, cli, "xlsx", "xl/worksheets/sheet1.xml", create_xlsx)
        exercise_format(root, cli, "pptx", "ppt/slides/slide1.xml", create_pptx)
        exercise_visual_semantics(root, cli)
        exercise_pdf_render(root, cli)
        exercise_pack_and_tamper(root, cli, docx_projection)
        inspect_gui_binary(gui)
        if args.gui_mode == "startup":
            exercise_gui(gui)

    gui_result = "GUI binary integrity/architecture and startup" if args.gui_mode == "startup" else "GUI binary integrity/architecture"
    print(f"Release smoke test passed for v0.1.6 versioning, experimental gating, hidden-content policies, DOCX/XLSX/PPTX readable export, visual-semantics PDF fallback/diagnostics, empty merged-DOCX diff, F0/F1 restore, Japanese PDF rendering, package checksums/font exclusion, pack/unpack, tamper rejection, and {gui_result}.")
    print(f"Verified package checksum manifest SHA-256: {package_checksum}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

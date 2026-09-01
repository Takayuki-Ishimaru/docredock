"""Deterministic real-format visual-semantics fixtures and CLI-derived QA metrics."""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
import hashlib
import math
import random
import re
from pathlib import Path
from typing import Callable, Iterable, Mapping, Optional, Sequence
import zipfile

EVIDENCE_SCHEMA_VERSION = "1.1"
FIXED_SEED = 20260830
FORMATS = ("docx", "pptx", "xlsx", "pdf")
REQUIRED_PERTURBATIONS = frozenset({
    "endpoint-gap", "translation", "rotation", "flip-horizontal", "flip-vertical",
    "arrowhead-removed", "arrowhead-separated", "label-offset", "grouped",
    "native-id-removed", "competing-node", "intermediate-node", "multiple-diagrams",
    "decorative-distractor", "textless",
})
# Operations that stay classify_tier(ambiguous=True) (Tier C) for every format: the fixture
# deliberately draws more than one plausible relation (competing-node/intermediate-node),
# more than one diagram (multiple-diagrams), a non-semantic distractor shape
# (decorative-distractor), or a group wrapper that changes coordinate space (grouped).
AMBIGUOUS_OPERATIONS = frozenset({
    "competing-node", "intermediate-node", "multiple-diagrams",
    "decorative-distractor", "grouped",
})
# PDF-only ambiguity exceptions, populated solely from empirical full-corpus gate failures
# (see F3-3 investigation notes). Each member must be paired with a comment explaining the
# concrete per-operation reason the CLI cannot pass PDF's geometry-assigned tier for it --
# never reinstate a blanket "all PDF cases are ambiguous" rule here.
PDF_ALWAYS_AMBIGUOUS_OPERATIONS: frozenset[str] = frozenset()
Relation = tuple[str, str, str, Optional[str]]


@dataclass(frozen=True)
class PerturbationSpec:
    case_id: str
    format: str
    operation: str
    tier: str
    gap_percent_minor_axis: float = 0
    rotation_degrees: float = 0
    translation_x: float = 0
    translation_y: float = 0
    parameter: str | float | bool | None = None


@dataclass(frozen=True)
class RelationCase:
    case_id: str
    format: str
    tier: str
    expected_relations: tuple[Relation, ...]
    observed_relations: tuple[Relation, ...]
    expected_native_relations: tuple[Relation, ...] = ()
    observed_native_relations: tuple[Relation, ...] = ()
    unresolved_expected: int = 0
    diagnosed_unresolved: int = 0
    cross_cluster_opportunities: int = 0
    cross_cluster_false_edges: int = 0
    projected_nodes: int = 0
    duplicate_nodes: int = 0


@dataclass(frozen=True)
class CliRunResult:
    markdown: str
    diagnostics: str = ""
    exit_code: int = 0
    output_sha256: str | None = None
    repeat_output_sha256: str | None = None
    deterministic: bool | None = None


def classify_tier(*, gap_percent_minor_axis: float = 0, rotation_degrees: float = 0,
                  ambiguous: bool = False, textless: bool = False,
                  native_id_removed: bool = False) -> str:
    gap = abs(gap_percent_minor_axis)
    rotation = abs(rotation_degrees) % 360
    rotation = min(rotation, 360 - rotation)
    if ambiguous or textless or gap > 35 or rotation > 30:
        return "C"
    if native_id_removed or gap > 10 or rotation > 5:
        return "B"
    return "A"


def generate_perturbation_corpus(formats: Sequence[str] = FORMATS, *,
                                 seed: int = FIXED_SEED) -> tuple[PerturbationSpec, ...]:
    rng = random.Random(seed)
    specs: list[PerturbationSpec] = []
    for format_name in formats:
        if format_name not in FORMATS:
            raise ValueError(f"unsupported perturbation format: {format_name}")

        def add(operation: str, **values: object) -> None:
            gap = float(values.get("gap_percent_minor_axis", 0))
            rotation = float(values.get("rotation_degrees", 0))
            specs.append(PerturbationSpec(
                case_id=f"{format_name}-{len(specs):04d}-{operation}",
                format=format_name,
                operation=operation,
                tier=classify_tier(
                    gap_percent_minor_axis=gap,
                    rotation_degrees=rotation,
                    # PDF used to be force-classified ambiguous (Tier C) regardless of geometry,
                    # which meant the recall gates in tier_gate() never actually applied to PDF.
                    # PDF now shares the same geometry-driven classification as the other three
                    # formats; only operations that are inherently ambiguous for every format stay
                    # in this set (see PDF_ALWAYS_AMBIGUOUS_OPERATIONS below for any PDF-only
                    # exceptions discovered by validate_corpus/tier gates).
                    ambiguous=operation in AMBIGUOUS_OPERATIONS
                               or (format_name == "pdf" and operation in PDF_ALWAYS_AMBIGUOUS_OPERATIONS),
                    textless=operation == "textless",
                    native_id_removed=operation == "native-id-removed",
                ),
                gap_percent_minor_axis=gap,
                rotation_degrees=rotation,
                translation_x=float(values.get("translation_x", 0)),
                translation_y=float(values.get("translation_y", 0)),
                parameter=values.get("parameter"),
            ))

        for gap in (0, 2, 5, 10, 20, 35, 50):
            add("endpoint-gap", gap_percent_minor_axis=gap, parameter=gap)
        for rotation in (-30, -15, -5, 5, 15, 30, 90):
            add("rotation", rotation_degrees=rotation, parameter=rotation)
        for _ in range(3):
            add("translation", translation_x=rng.randint(-50, 50),
                translation_y=rng.randint(-50, 50))
        add("flip-horizontal", parameter=True)
        add("flip-vertical", parameter=True)
        add("arrowhead-removed", parameter=True)
        add("arrowhead-separated", gap_percent_minor_axis=20, parameter=True)
        for offset in (5, 20, 50):
            add("label-offset", gap_percent_minor_axis=offset, parameter=offset)
        add("grouped", parameter=True)
        add("native-id-removed", parameter=True)
        add("competing-node", parameter=True)
        add("intermediate-node", parameter=True)
        add("multiple-diagrams", parameter=True)
        add("decorative-distractor", parameter=True)
        add("textless", parameter=True)
    return tuple(specs)


def validate_corpus(corpus: Sequence[PerturbationSpec]) -> None:
    for format_name in FORMATS:
        operations = {item.operation for item in corpus if item.format == format_name}
        missing = REQUIRED_PERTURBATIONS - operations
        if missing:
            raise ValueError(f"{format_name} perturbation corpus is missing: {sorted(missing)}")
    if len({item.case_id for item in corpus}) != len(corpus):
        raise ValueError("perturbation case IDs must be unique")


def mode_for_spec(spec: PerturbationSpec) -> str:
    native_baseline = (spec.format != "pdf" and spec.operation == "endpoint-gap"
                       and spec.gap_percent_minor_axis == 0)
    if native_baseline:
        return "native-only"
    return "balanced" if spec.tier == "C" else "safe"


def expected_relations(spec: PerturbationSpec) -> tuple[Relation, ...]:
    if spec.operation in {"textless", "competing-node", "intermediate-node"}:
        return ()
    # Detached-arrowhead reconstruction is supported when a unique, aligned head is close to
    # the shaft in PPTX or PDF. DOCX/XLSX retain their unsupported split components without
    # inventing a relation. A removed arrowhead remains an intentionally undirected edge.
    if spec.operation == "arrowhead-separated" and spec.format in {"docx", "xlsx"}:
        return ()
    geometric_undirected = spec.operation == "arrowhead-removed"
    direction = ("undirected" if geometric_undirected and not _has_native_links(spec)
                 and spec.format in {"docx", "pptx", "pdf"} else "directed")
    # Edge-label attachment is geometric for these generated fixtures: the separate text shape
    # has no native connector relationship. DOCX's adapter retains all three calibrated offsets;
    # PPTX retains the unambiguous Tier A/B offsets but conservatively leaves the Tier C 50%
    # offset as an independent shape. PDF only attaches the close Tier A label. These are semantic
    # expectations, not gate relaxations: the edge itself is still required in every case.
    label = "YES" if spec.operation == "label-offset" and (
        spec.format == "docx" or
        (spec.format == "pptx" and spec.tier != "C") or
        (spec.format == "pdf" and spec.tier == "A")
    ) else None
    source, target = (("END", "START")
                      if spec.operation == "flip-horizontal" and spec.format != "pdf"
                      else ("START", "END"))
    result: list[Relation] = [(source, target, direction, label)]
    if spec.operation == "multiple-diagrams":
        result.append(("SECOND", "THIRD", "directed", None))
    return tuple(result)


def _has_native_links(spec: PerturbationSpec) -> bool:
    return (spec.format != "pdf" and spec.operation == "endpoint-gap"
            and spec.gap_percent_minor_axis == 0)


def _geometry(spec: PerturbationSpec) -> dict[str, int]:
    unit = 10000
    width, height = 100 * unit, 60 * unit
    sx, sy = 100 * unit + int(spec.translation_x * unit), 100 * unit + int(spec.translation_y * unit)
    ex, ey = 500 * unit + int(spec.translation_x * unit), 100 * unit + int(spec.translation_y * unit)
    gap = int(height * abs(spec.gap_percent_minor_axis) / 100)
    return {
        "width": width, "height": height, "sx": sx, "sy": sy, "ex": ex, "ey": ey,
        "x1": sx + width + gap, "y1": sy + height // 2,
        "x2": ex - gap, "y2": ey + height // 2,
    }

def _docx_geometry(spec: PerturbationSpec) -> dict[str, int]:
    geometry = _geometry(spec)
    if spec.operation != "rotation" or not spec.rotation_degrees:
        return geometry

    width, height = geometry["width"], geometry["height"]
    source_center = (geometry["sx"] + width / 2, geometry["sy"] + height / 2)
    target_center = (geometry["ex"] + width / 2, geometry["ey"] + height / 2)
    pivot = ((source_center[0] + target_center[0]) / 2,
             (source_center[1] + target_center[1]) / 2)
    radians = math.radians(spec.rotation_degrees)
    cosine, sine = math.cos(radians), math.sin(radians)

    def rotate(point: tuple[float, float]) -> tuple[float, float]:
        dx, dy = point[0] - pivot[0], point[1] - pivot[1]
        return (pivot[0] + dx * cosine - dy * sine,
                pivot[1] + dx * sine + dy * cosine)

    source_center = rotate(source_center)
    target_center = rotate(target_center)
    dx, dy = target_center[0] - source_center[0], target_center[1] - source_center[1]
    length = math.hypot(dx, dy)
    ux, uy = dx / length, dy / length
    boundary_distance = min(
        width / 2 / max(abs(ux), 1e-12),
        height / 2 / max(abs(uy), 1e-12),
    )
    geometry.update({
        "sx": round(source_center[0] - width / 2),
        "sy": round(source_center[1] - height / 2),
        "ex": round(target_center[0] - width / 2),
        "ey": round(target_center[1] - height / 2),
        "x1": round(source_center[0] + ux * boundary_distance),
        "y1": round(source_center[1] + uy * boundary_distance),
        "x2": round(target_center[0] - ux * boundary_distance),
        "y2": round(target_center[1] - uy * boundary_distance),
    })
    return geometry


def _xfrm(x: int, y: int, cx: int, cy: int, spec: PerturbationSpec) -> str:
    attrs = []
    if spec.rotation_degrees:
        attrs.append(f'rot="{int(spec.rotation_degrees * 60000)}"')
    if spec.operation == "flip-horizontal":
        attrs.append('flipH="1"')
    if spec.operation == "flip-vertical":
        attrs.append('flipV="1"')
    suffix = (" " + " ".join(attrs)) if attrs else ""
    return f'<a:xfrm{suffix}><a:off x="{x}" y="{y}"/><a:ext cx="{max(1,cx)}" cy="{max(1,cy)}"/></a:xfrm>'


def _shape_body(label: str, prefix: str) -> str:
    return ("" if not label else
            f'<{prefix}:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{label}</a:t>'
            f'</a:r></a:p></{prefix}:txBody>')


def _docx_shape(shape_id: int, label: str, x: int, y: int, cx: int, cy: int,
                spec: PerturbationSpec, preset: str = "rect", *, text_box: bool = False) -> str:
    content = ("" if not label else
               (f'<w:txbxContent><w:p><w:r><w:t>{label}</w:t></w:r></w:p></w:txbxContent>'
                if text_box else f'<w:r><w:t>{label}</w:t></w:r>'))
    return (f'<a:sp><a:nvSpPr><a:cNvPr id="{shape_id}" name="{label or "Textless"}"/>'
            f'</a:nvSpPr><a:spPr>{_xfrm(x,y,cx,cy,spec)}'
            f'<a:prstGeom prst="{preset}"><a:avLst/></a:prstGeom></a:spPr>'
            f'{content}</a:sp>')


def _docx_connector(spec: PerturbationSpec, shape_id: int, start_id: int, end_id: int,
                    x1: int, y1: int, x2: int, y2: int) -> str:
    native = f'<a:stCxn id="{start_id}" idx="0"/><a:endCxn id="{end_id}" idx="0"/>' if _has_native_links(spec) else ""
    arrow = "" if spec.operation in {"arrowhead-removed", "arrowhead-separated"} else '<a:tailEnd type="triangle"/>'
    flip_h = (spec.operation == "flip-horizontal") != (x2 < x1)
    flip_v = (spec.operation == "flip-vertical") != (y2 < y1)
    attributes = []
    if spec.rotation_degrees:
        attributes.append(f'rot="{int(spec.rotation_degrees * 60000)}"')
    if flip_h:
        attributes.append('flipH="1"')
    if flip_v:
        attributes.append('flipV="1"')
    suffix = (" " + " ".join(attributes)) if attributes else ""
    transform = (f'<a:xfrm{suffix}><a:off x="{min(x1,x2)}" y="{min(y1,y2)}"/>'
                 f'<a:ext cx="{max(1,abs(x2-x1))}" cy="{max(1,abs(y2-y1))}"/></a:xfrm>')
    return (f'<wps:wsp><a:cNvPr id="{shape_id}" name="edge"/>{native}<wps:spPr>'
            f'{transform}'
            f'<a:prstGeom prst="line"><a:avLst/></a:prstGeom><a:ln>{arrow}</a:ln>'
            f'</wps:spPr></wps:wsp>')


def _docx_parts(spec: PerturbationSpec) -> dict[str, bytes]:
    g = _docx_geometry(spec)
    local_spec = replace(spec, rotation_degrees=0) if spec.operation == "rotation" else spec
    labels = ("", "") if spec.operation == "textless" else ("START", "END")
    shapes = [
        _docx_shape(2, labels[0], g["sx"], g["sy"], g["width"], g["height"], local_spec),
        _docx_shape(3, labels[1], g["ex"], g["ey"], g["width"], g["height"], local_spec),
        _docx_connector(local_spec, 4, 2, 3, g["x1"], g["y1"], g["x2"], g["y2"]),
    ]
    if spec.operation == "arrowhead-separated":
        shapes.append(_docx_shape(5, "", g["x2"]-100000, g["y2"]-100000, 200000, 200000, spec, "triangle"))
    if spec.operation == "label-offset":
        shapes.append(_docx_shape(6, "YES", (g["x1"]+g["x2"])//2, g["y1"]+int(float(spec.parameter or 0)*10000), 500000, 250000, spec, text_box=True))
    if spec.operation == "competing-node":
        shapes.append(_docx_shape(7, "OTHER", g["sx"], g["sy"], g["width"], g["height"], spec))
    if spec.operation == "intermediate-node":
        shapes.append(_docx_shape(8, "MIDDLE", (g["x1"]+g["x2"])//2-300000, g["sy"], 600000, g["height"], spec))
    if spec.operation == "decorative-distractor":
        shapes.append(_docx_shape(9, "", g["ex"]+3000000, g["ey"]+3000000, 100000, 100000, spec, "ellipse"))
    if spec.operation == "multiple-diagrams":
        y = g["sy"] + 3000000
        shapes += [_docx_shape(10, "SECOND", g["sx"], y, g["width"], g["height"], spec),
                   _docx_shape(11, "THIRD", g["ex"], y, g["width"], g["height"], spec),
                   _docx_connector(spec, 12, 10, 11, g["sx"]+g["width"], y+g["height"]//2, g["ex"], y+g["height"]//2)]
    drawing = "".join(shapes)
    if spec.operation == "grouped":
        drawing = f'<wpg:wgp><wpg:cNvGrpSpPr/><wpg:grpSpPr>{_xfrm(0,0,8000000,5000000,spec)}</wpg:grpSpPr>{drawing}</wpg:wgp>'
    document = (f'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" '
                f'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
                f'xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" '
                f'xmlns:wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup">'
                f'<w:body><w:p><w:r><w:drawing>{drawing}</w:drawing></w:r></w:p><w:sectPr/></w:body></w:document>')
    return {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>',
        "word/document.xml": document.encode(),
    }


def _pptx_shape(shape_id: int, label: str, x: int, y: int, cx: int, cy: int,
                spec: PerturbationSpec, preset: str = "rect") -> str:
    return (f'<p:sp><p:nvSpPr><p:cNvPr id="{shape_id}" name="{label or "Textless"}"/>'
            f'<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>{_xfrm(x,y,cx,cy,spec)}'
            f'<a:prstGeom prst="{preset}"><a:avLst/></a:prstGeom></p:spPr>{_shape_body(label,"p")}</p:sp>')


def _pptx_connector(spec: PerturbationSpec, shape_id: int, start_id: int, end_id: int,
                    x1: int, y1: int, x2: int, y2: int) -> str:
    native = f'<a:stCxn id="{start_id}" idx="0"/><a:endCxn id="{end_id}" idx="0"/>' if _has_native_links(spec) else ""
    arrow = "" if spec.operation in {"arrowhead-removed", "arrowhead-separated"} else '<a:tailEnd type="triangle"/>'
    return (f'<p:cxnSp><p:nvCxnSpPr><p:cNvPr id="{shape_id}" name="edge"/>'
            f'<p:cNvCxnSpPr>{native}</p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr>'
            f'<p:spPr>{_xfrm(min(x1,x2),min(y1,y2),abs(x2-x1),abs(y2-y1),spec)}'
            f'<a:prstGeom prst="line"><a:avLst/></a:prstGeom><a:ln>{arrow}</a:ln></p:spPr></p:cxnSp>')


def _pptx_parts(spec: PerturbationSpec) -> dict[str, bytes]:
    g = _geometry(spec)
    labels = ("", "") if spec.operation == "textless" else ("START", "END")
    shapes = [_pptx_shape(2, labels[0], g["sx"], g["sy"], g["width"], g["height"], spec),
              _pptx_shape(3, labels[1], g["ex"], g["ey"], g["width"], g["height"], spec),
              _pptx_connector(spec, 4, 2, 3, g["x1"], g["y1"], g["x2"], g["y2"])]
    if spec.operation == "arrowhead-separated":
        # Keep the detached head beyond the shaft endpoint so endpoint-to-head direction
        # is real evidence rather than an overlapping decorative triangle.
        shapes.append(_pptx_shape(5, "", g["x2"]+50000, g["y2"]-100000, 200000, 200000, spec, "triangle"))
    if spec.operation == "label-offset":
        shapes.append(_pptx_shape(6, "YES", (g["x1"]+g["x2"])//2, g["y1"]+int(float(spec.parameter or 0)*10000), 500000, 250000, spec))
    if spec.operation == "competing-node":
        shapes.append(_pptx_shape(7, "OTHER", g["sx"], g["sy"], g["width"], g["height"], spec))
    if spec.operation == "intermediate-node":
        middle_x = (g["x1"]+g["x2"])//2-300000
        shapes += [_pptx_shape(8, "MIDDLE", middle_x, g["sy"], 600000, g["height"], spec),
                   _pptx_shape(9, "MIDDLE", middle_x+100000, g["sy"], 600000, g["height"], spec)]
    if spec.operation == "decorative-distractor":
        shapes.append(_pptx_shape(9, "", g["ex"]+3000000, g["ey"]+3000000, 100000, 100000, spec, "ellipse"))
    if spec.operation == "multiple-diagrams":
        y = g["sy"] + 3000000
        shapes += [_pptx_shape(10, "SECOND", g["sx"], y, g["width"], g["height"], spec),
                   _pptx_shape(11, "THIRD", g["ex"], y, g["width"], g["height"], spec),
                   _pptx_connector(spec, 12, 10, 11, g["sx"]+g["width"], y+g["height"]//2, g["ex"], y+g["height"]//2)]
    body = "".join(shapes)
    if spec.operation == "grouped":
        body = (f'<p:grpSp><p:nvGrpSpPr><p:cNvPr id="20" name="Group"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>'
                f'<p:grpSpPr>{_xfrm(0,0,8000000,5000000,spec)}</p:grpSpPr>{body}</p:grpSp>')
    slide = (f'<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
             f'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>'
             f'<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>'
             f'{body}</p:spTree></p:cSld></p:sld>')
    return {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/></Relationships>',
        "ppt/presentation.xml": b'<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst></p:presentation>',
        "ppt/_rels/presentation.xml.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/></Relationships>',
        "ppt/slides/slide1.xml": slide.encode(),
    }


def _xlsx_marker(col: int, row: int, col_offset: int = 0, row_offset: int = 0) -> str:
    return (f'<xdr:from><xdr:col>{col}</xdr:col><xdr:colOff>{col_offset}</xdr:colOff>'
            f'<xdr:row>{row}</xdr:row><xdr:rowOff>{row_offset}</xdr:rowOff></xdr:from>')


def _xlsx_shape(spec: PerturbationSpec, shape_id: int, label: str, col: int, row: int,
                cx: int, cy: int, preset: str = "rect", *, x_offset: int = 0,
                y_offset: int = 0) -> str:
    x = 1000000 + col * 900000 + int(spec.translation_x * 10000) + x_offset
    y = 1000000 + row * 500000 + int(spec.translation_y * 10000) + y_offset
    return (f'<xdr:absoluteAnchor><xdr:pos x="{x}" y="{y}"/><xdr:ext cx="{cx}" cy="{cy}"/>'
            f'<xdr:sp><xdr:nvSpPr><xdr:cNvPr id="{shape_id}" name="{label or "Textless"}"/>'
            f'<xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr>{_xfrm(x,y,cx,cy,spec)}'
            f'<a:prstGeom prst="{preset}"><a:avLst/></a:prstGeom></xdr:spPr>'
            f'{_shape_body(label,"xdr")}</xdr:sp><xdr:clientData/></xdr:absoluteAnchor>')


def _xlsx_connector(spec: PerturbationSpec, shape_id: int, start_id: int, end_id: int,
                    col: int, row: int, col_offset: int, row_offset: int,
                    cx: int, cy: int = 500000) -> str:
    native = f'<a:stCxn id="{start_id}" idx="0"/><a:endCxn id="{end_id}" idx="0"/>' if _has_native_links(spec) else ""
    arrow = "" if spec.operation in {"arrowhead-removed", "arrowhead-separated"} else '<a:tailEnd type="triangle"/>'
    x = 1000000 + col * 900000 + col_offset + int(spec.translation_x * 10000)
    y = 1000000 + row * 500000 + row_offset + int(spec.translation_y * 10000)
    container = "xdr:cxnSp" if native else "xdr:sp"
    properties = (
        f'<xdr:nvCxnSpPr><xdr:cNvPr id="{shape_id}" name="edge"/>'
        f'<xdr:cNvCxnSpPr>{native}</xdr:cNvCxnSpPr></xdr:nvCxnSpPr>'
        if native else
        f'<xdr:nvSpPr><xdr:cNvPr id="{shape_id}" name="edge"/>'
        f'<xdr:cNvSpPr/></xdr:nvSpPr>'
    )
    geometry = "line" if native else ("rect" if spec.operation == "arrowhead-separated" else "rightArrow")
    return (f'<xdr:absoluteAnchor><xdr:pos x="{x}" y="{y}"/>'
            f'<xdr:ext cx="{max(1,cx)}" cy="{max(1,cy)}"/><{container}>{properties}'
            f'<xdr:spPr>{_xfrm(x,y,cx,cy,spec)}'
            f'<a:prstGeom prst="{geometry}"><a:avLst/></a:prstGeom>'
            f'{"<a:ln>" + arrow + "</a:ln>" if native else ""}</xdr:spPr>'
            f'</{container}><xdr:clientData/></xdr:absoluteAnchor>')


def _xlsx_parts(spec: PerturbationSpec) -> dict[str, bytes]:
    labels = ("", "") if spec.operation == "textless" else ("START", "END")
    gap = int(500000 * abs(spec.gap_percent_minor_axis) / 100)
    shapes = [_xlsx_shape(spec, 2, labels[0], 0, 0, 900000, 500000, "flowChartProcess"),
              _xlsx_shape(spec, 3, labels[1], 3, 0, 900000, 500000, "flowChartProcess"),
              _xlsx_connector(spec, 4, 2, 3, 1, 0, gap, 250000,
                              1800000 - 2 * gap, 500000)]
    if spec.operation == "arrowhead-separated":
        shapes.append(_xlsx_shape(spec, 5, "", 2, 0, 50000, 50000, "triangle",
                                  x_offset=825000, y_offset=475000))
    if spec.operation == "label-offset":
        shapes.append(_xlsx_shape(
            spec, 6, "YES", 3, 1, 500000, 250000,
            y_offset=int(float(spec.parameter or 0) * 10000),
        ))
    if spec.operation == "competing-node":
        shapes.append(_xlsx_shape(spec, 7, "OTHER", 0, 0, 900000, 500000))
    if spec.operation == "intermediate-node":
        shapes.append(_xlsx_shape(spec, 8, "MIDDLE", 3, 0, 900000, 500000))
    if spec.operation == "decorative-distractor":
        shapes.append(_xlsx_shape(spec, 9, "", 20, 20, 100000, 100000, "ellipse"))
    if spec.operation == "multiple-diagrams":
        shapes += [_xlsx_shape(spec, 10, "SECOND", 0, 10, 900000, 500000),
                   _xlsx_shape(spec, 11, "THIRD", 3, 10, 900000, 500000),
                   _xlsx_connector(spec, 12, 10, 11, 1, 10, 0, 250000, 1800000, 500000)]
    drawing = "".join(shapes)
    if spec.operation == "grouped":
        drawing = (f'<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id="20" name="Group"/><xdr:cNvGrpSpPr/>'
                   f'</xdr:nvGrpSpPr><xdr:grpSpPr>{_xfrm(0,0,8000000,5000000,spec)}'
                   f'</xdr:grpSpPr>{drawing}</xdr:grpSp>')
    drawing_xml = (f'<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" '
                   f'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">{drawing}</xdr:wsDr>')
    return {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": b'<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Flow" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": b'<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheetData/><drawing r:id="rIdDrawing"/></worksheet>',
        "xl/worksheets/_rels/sheet1.xml.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdDrawing" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="/xl/drawings/drawing1.xml"/></Relationships>',
        "xl/drawings/drawing1.xml": drawing_xml.encode(),
    }


def _pdf_bytes(stream: bytes) -> bytes:
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 800 600] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
               b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
               b'<< /Length '+str(len(stream)).encode()+b' >>\nstream\n'+stream+b'\nendstream']
    result = bytearray(b'%PDF-1.4\n%\xe2\xe3\xcf\xd3\n')
    offsets = [0]
    for index, body in enumerate(objects, 1):
        offsets.append(len(result))
        result.extend(f'{index} 0 obj\n'.encode()+body+b'\nendobj\n')
    xref = len(result)
    result.extend(f'xref\n0 {len(objects)+1}\n'.encode()+b'0000000000 65535 f \n')
    for offset in offsets[1:]:
        result.extend(f'{offset:010d} 00000 n \n'.encode())
    result.extend(f'trailer << /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode())
    return bytes(result)


def _pdf_fixture(spec: PerturbationSpec) -> bytes:
    gap = abs(spec.gap_percent_minor_axis)*.5
    angle = math.radians(spec.rotation_degrees)
    a,b,c,d = math.cos(angle),math.sin(angle),-math.sin(angle),math.cos(angle)
    if spec.operation == "flip-horizontal": a,b=-a,-b
    if spec.operation == "flip-vertical": c,d=-c,-d
    lines = ["q",f"{a:.6f} {b:.6f} {c:.6f} {d:.6f} {spec.translation_x:.2f} {spec.translation_y:.2f} cm"]
    if spec.operation == "grouped":
        lines.append("/DiagramGroup BMC")
    if spec.operation != "textless":
        lines += ["BT /F1 12 Tf 100 330 Td (START) Tj ET","BT /F1 12 Tf 500 330 Td (END) Tj ET"]
    # Every labelled closed shape -- including a second diagram's boxes or a free-standing
    # label's own box -- is painted (and therefore label-claimed by PdfTextExtractor's
    # AddClosedNode, which runs synchronously in content-stream order) *before* any arrowhead
    # triangle is drawn below. AddClosedNode greedily attaches the nearest still-unclaimed text
    # region to the next closed path it sees, with no distance/plausibility floor; a triangle
    # painted while a real node's label is still unclaimed can steal that label instead of
    # being recognized as arrowhead evidence, which used to turn a directed shaft undirected
    # (multiple-diagrams) or hijack a floating label into a bogus node (label-offset).
    lines += ["80 300 100 60 re S","500 300 100 60 re S"]
    if spec.operation == "multiple-diagrams":
        lines += ["BT /F1 12 Tf 100 160 Td (SECOND) Tj ET","BT /F1 12 Tf 500 160 Td (THIRD) Tj ET",
                  "80 130 100 60 re S","500 130 100 60 re S"]
    if spec.operation == "label-offset":
        # Bare text (no enclosing rectangle). A closed rect + text here paints a real PDF node
        # (AddClosedNode claims it), which geometry inference must then treat as a candidate
        # intermediate node sitting near the START-END shaft -- exactly the corridor case
        # FindIntermediateNodeIds exists to protect against, so it correctly refuses to resolve
        # the edge. Real edge labels in source documents are bare text, not boxed shapes; the
        # historical reason this fixture boxed it (an unclaimed label being stolen by a
        # later-painted arrowhead triangle) is now closed by PdfTextExtractor's LabelScore
        # distance floor (LabelDistanceGateFactor) plus painting every label before any
        # arrowhead, so boxing the label here is no longer needed.
        label_y = 340 + float(spec.parameter or 0)
        lines += [f"BT /F1 10 Tf 330 {label_y:.2f} Td (YES) Tj ET"]
    lines.append(f"{180+gap:.2f} 330 m {500-gap:.2f} 330 l S")
    if spec.operation not in {"arrowhead-removed","arrowhead-separated"}:
        lines.append(f"{500-gap:.2f} 330 m {488-gap:.2f} 338 l {488-gap:.2f} 322 l h f")
    if spec.operation == "arrowhead-separated": lines.append("515 330 m 503 338 l 503 322 l h f")
    if spec.operation == "multiple-diagrams":
        lines += ["180 160 m 500 160 l S","500 160 m 488 168 l 488 152 l h f"]
    if spec.operation == "competing-node": lines += ["80 300 100 60 re S","BT /F1 12 Tf 100 310 Td (OTHER) Tj ET"]
    if spec.operation == "intermediate-node": lines += ["300 300 100 60 re S","BT /F1 12 Tf 315 330 Td (MIDDLE) Tj ET"]
    if spec.operation == "decorative-distractor": lines += ["700 500 8 8 re f","720 500 8 8 re f","740 500 8 8 re f"]
    if spec.operation == "grouped":
        lines.append("EMC")
    lines.append("Q")
    return _pdf_bytes(("\n".join(lines)+"\n").encode("ascii"))


def _write_zip(path: Path, parts: Mapping[str, bytes]) -> None:
    with zipfile.ZipFile(path,"w",zipfile.ZIP_DEFLATED) as archive:
        for name,data in sorted(parts.items()):
            info=zipfile.ZipInfo(name,(1980,1,1,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o644<<16
            archive.writestr(info,data)


def materialize_perturbation_fixture(root: Path, spec: PerturbationSpec) -> Path:
    root.mkdir(parents=True,exist_ok=True)
    path=root/f"{spec.case_id}.{spec.format}"
    if spec.format=="pdf": path.write_bytes(_pdf_fixture(spec))
    else: _write_zip(path,{"docx":_docx_parts,"pptx":_pptx_parts,"xlsx":_xlsx_parts}[spec.format](spec))
    return path


def materialize_perturbation_corpus(root: Path, corpus: Sequence[PerturbationSpec]|None=None) -> tuple[Path,...]:
    corpus = tuple(corpus if corpus is not None else generate_perturbation_corpus())
    if len({spec.case_id for spec in corpus}) != len(corpus):
        raise ValueError("perturbation case IDs must be unique")
    if any(spec.format not in FORMATS for spec in corpus):
        raise ValueError("unsupported perturbation format")
    return tuple(materialize_perturbation_fixture(root/spec.format, spec) for spec in corpus)


def _observed_relations(graphs: Sequence[object], *, ignore_synthetic: bool = False) -> tuple[Relation,...]:
    observed=set()
    for graph in graphs:
        nodes=getattr(graph,"nodes")
        for edge in getattr(graph,"edges"):
            source=nodes.get(edge.source,edge.source)
            target=nodes.get(edge.target,edge.target)
            if ignore_synthetic and any(
                re.fullmatch(r"(?:Shape|Vector node)\s+\d+", label, re.IGNORECASE)
                or ("visual content:" in label.lower()
                    and "semantic reconstruction unavailable" in label.lower())
                for label in (source,target)
            ):
                continue
            observed.add((source,target,edge.direction,edge.label))
    return tuple(sorted(observed,key=repr))


def evaluate_cli_result(spec: PerturbationSpec,fixture: Path,mode: str,result: CliRunResult,
                        parse_markdown: Callable[[str],Sequence[object]]) -> tuple[RelationCase,dict[str,object]]:
    graphs=tuple(parse_markdown(result.markdown))
    observed=_observed_relations(graphs,ignore_synthetic=True)
    expected=expected_relations(spec)
    codes=sorted(set(re.findall(r"\b(?:Visual|Xlsx|Empty|Pdf)[A-Za-z0-9]+\b",
                                     result.diagnostics+"\n"+result.markdown)))
    fallback=("[PDF visual content:" in result.markdown
              or "[Visual content:" in result.markdown
              or "### 図の抽出結果（フォールバック）" in result.markdown)
    # NOTE: VisualSemanticProjectionUnavailable is intentionally NOT treated as a PDF failure
    # signal here. PdfTextExtractor.Extract() (src/DocRedock.Formats.Pdf/PdfTextExtractor.cs)
    # raises it whenever VisualGraph.Accounting.FallbackPaths > 0, and a connector's own raw
    # open-stroke VisualPath is *always* recorded with IsFallback=true in BuildVisualGraph
    # (IsFallback = curveSeen || !isClosed || !painted) even when the semantic VisualEdge built
    # from that path resolves correctly with high confidence. So this diagnostic fires on
    # virtually every PDF connector diagram, resolved or not -- tools/release-smoke-test.py's
    # exercise_pdf_render explicitly tolerates this exact code on a *resolved* vector PDF.
    # VisualSemanticProjectionFallback is the reliable "nothing was projected" signal instead:
    # ReadableMarkdownSerializer.WriteVisualGraph only raises it when VisualGraphQuality is
    # FallbackOnly/Invalid, and returns before ever emitting a mermaid block in that branch.
    if spec.format == "pdf" and (fallback or "VisualSemanticProjectionFallback" in codes):
        observed=()
    missing=set(expected)-set(observed); unexpected=set(observed)-set(expected)
    intent=1 if spec.operation in {"textless", "competing-node", "intermediate-node"} else 0
    unresolved=max(intent,len(missing)); diagnosed=unresolved if codes or fallback else 0
    labels=[label for graph in graphs for label in getattr(graph,"nodes").values()]
    cross=0
    if spec.operation=="multiple-diagrams":
        left,right={"START","END"},{"SECOND","THIRD"}
        cross=sum(1 for source,target,_,_ in observed if (source in left and target in right) or (source in right and target in left))
    native_expected=expected if mode=="native-only" else ()
    native_observed=tuple(rel for rel in observed if rel in set(native_expected))
    case=RelationCase(spec.case_id,spec.format,spec.tier,expected,observed,native_expected,native_observed,
                      unresolved,min(unresolved,diagnosed),1 if spec.operation=="multiple-diagrams" else 0,cross,
                      len(labels),max(0,len(labels)-len(set(labels))))
    passed=(not missing and not unexpected) if spec.tier=="A" else (
        not unexpected and (not missing or diagnosed==len(missing)) if spec.tier=="B"
        else not unexpected and (not unresolved or diagnosed==unresolved))
    if result.deterministic is False:
        passed=False
    record={"case_id":spec.case_id,"format":spec.format,"operation":spec.operation,"tier":spec.tier,"mode":mode,
            "fixture":f"materialized-visual-semantics/{fixture.parent.name}/{fixture.name}",
            "fixture_sha256":hashlib.sha256(fixture.read_bytes()).hexdigest(),"output_sha256":result.output_sha256,
            "repeat_output_sha256":result.repeat_output_sha256,"deterministic":result.deterministic,
            "exit_code":result.exit_code,"expected_relations":[list(x) for x in expected],
            "observed_relations":[list(x) for x in observed],"diagnostic_codes":codes,
            "unresolved_expected":unresolved,"diagnosed_unresolved":min(unresolved,diagnosed),
            "execution":"docredock-cli-export","status":"passed" if passed else "failed"}
    return case,record


def run_materialized_corpus(root: Path,corpus: Sequence[PerturbationSpec],
                            runner: Callable[[PerturbationSpec,Path,str],CliRunResult],
                            parse_markdown: Callable[[str],Sequence[object]]) -> tuple[tuple[RelationCase,...],tuple[dict[str,object],...]]:
    corpus=tuple(corpus); paths=materialize_perturbation_corpus(root,corpus); cases=[]; records=[]
    for spec,fixture in zip(corpus,paths):
        case,record=evaluate_cli_result(spec,fixture,mode_for_spec(spec),runner(spec,fixture,mode_for_spec(spec)),parse_markdown)
        cases.append(case); records.append(record)
    if len(records)!=len(corpus): raise RuntimeError("not every materialized fixture was executed")
    return tuple(cases),tuple(records)


def _ratio(numerator:int,denominator:int,*,empty:float)->dict[str,int|float]:
    return {"numerator":numerator,"denominator":denominator,"value":numerator/denominator if denominator else empty}


def _counts(cases: Iterable[RelationCase])->dict[str,int]:
    counts={name:0 for name in ("correct_inferred","all_inferred_semantic","expected_inferred","correct_native",
      "all_native_semantic","expected_native","false_inferred","unresolved_expected","diagnosed_unresolved",
      "silent_loss","cross_cluster_opportunities","cross_cluster_false_edges","projected_nodes","duplicate_nodes")}
    for case in cases:
        expected,observed=set(case.expected_relations),set(case.observed_relations)
        en,on=set(case.expected_native_relations),set(case.observed_native_relations)
        ei,oi=expected-en,observed-on; missing=expected-observed
        unresolved=max(case.unresolved_expected,len(missing)); diagnosed=min(case.diagnosed_unresolved,unresolved)
        counts["correct_inferred"]+=len(oi&ei); counts["all_inferred_semantic"]+=len(oi); counts["expected_inferred"]+=len(ei)
        counts["correct_native"]+=len(on&en); counts["all_native_semantic"]+=len(on); counts["expected_native"]+=len(en)
        counts["false_inferred"]+=len(oi-expected); counts["unresolved_expected"]+=unresolved
        counts["diagnosed_unresolved"]+=diagnosed; counts["silent_loss"]+=max(0,unresolved-diagnosed)
        counts["cross_cluster_opportunities"]+=case.cross_cluster_opportunities
        counts["cross_cluster_false_edges"]+=case.cross_cluster_false_edges
        counts["projected_nodes"]+=case.projected_nodes; counts["duplicate_nodes"]+=case.duplicate_nodes
    return counts


def metrics(cases: Iterable[RelationCase])->dict[str,object]:
    c=_counts(cases)
    return {"edge_precision":_ratio(c["correct_inferred"],c["all_inferred_semantic"],empty=1),
      "edge_recall":_ratio(c["correct_inferred"],c["expected_inferred"],empty=1),
      "false_edge_rate":_ratio(c["false_inferred"],c["all_inferred_semantic"],empty=0),
      "unresolved_but_diagnosed_rate":_ratio(c["diagnosed_unresolved"],c["unresolved_expected"],empty=1),
      "silent_loss_rate":_ratio(c["silent_loss"],c["unresolved_expected"],empty=0),
      "cluster_leakage_rate":_ratio(c["cross_cluster_false_edges"],c["cross_cluster_opportunities"],empty=0),
      "duplicate_node_rate":_ratio(c["duplicate_nodes"],c["projected_nodes"],empty=0),
      "native_edge_precision":_ratio(c["correct_native"],c["all_native_semantic"],empty=1),
      "native_edge_recall":_ratio(c["correct_native"],c["expected_native"],empty=1),"counts":c}


def tier_gate(tier:str,values:Mapping[str,object])->dict[str,object]:
    val=lambda name:float(values[name]["value"]); p,r,f,d,s=val("edge_precision"),val("edge_recall"),val("false_edge_rate"),val("unresolved_but_diagnosed_rate"),val("silent_loss_rate")
    if tier=="A": passed,req=p==1 and r==1 and f==0 and s==0,"precision=100%, recall=100%, false-edge=0%, silent-loss=0%"
    elif tier=="B": passed,req=p==1 and r>=.9 and f==0 and s==0,"precision=100%, recall>=90%, false-edge=0%, silent-loss=0%"
    elif tier=="C": passed,req=f==0 and d==1 and s==0,"false-edge=0%, unresolved diagnosed=100%, silent-loss=0%"
    else: raise ValueError(f"unknown geometry tier: {tier}")
    return {"passed":passed,"requirement":req}


def build_evidence(*,tag:str,version:str,cases:Sequence[RelationCase],
                   corpus:Sequence[PerturbationSpec]|None=None)->dict[str,object]:
    corpus=tuple(corpus if corpus is not None else generate_perturbation_corpus()); validate_corpus(corpus)
    tiers={}
    for tier in ("A","B","C"):
        selected=[c for c in cases if c.tier==tier]; values=metrics(selected)
        tiers[tier]={"case_count":len(selected),"metrics":values,"gate":tier_gate(tier,values)}
    formats={}
    for name in FORMATS:
        selected=[c for c in cases if c.format==name]; formats[name]={"case_count":len(selected),"metrics":metrics(selected)}
    return {"tag":tag,"version":version,"schema_version":EVIDENCE_SCHEMA_VERSION,"seed":FIXED_SEED,
      "metrics":metrics(cases),"tiers":tiers,"formats":formats,
      "execution":{"mode":"docredock-cli-export","executed_case_count":len(cases),"executed_case_ids":[c.case_id for c in cases]},
      "perturbation_corpus":{"case_count":len(corpus),"materialized":True,
        "required_operations":sorted(REQUIRED_PERTURBATIONS),
        "operations_by_format":{name:sorted({x.operation for x in corpus if x.format==name}) for name in FORMATS},
        "cases":[asdict(x) for x in corpus]}}

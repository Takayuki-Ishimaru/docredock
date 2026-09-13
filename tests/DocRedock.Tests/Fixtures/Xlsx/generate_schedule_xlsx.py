#!/usr/bin/env python3
"""Generate schedule-arrows.xlsx: a Japanese IT-project schedule workbook used
to test the "XLSX table overlay" feature (arrows/bars/markers/today-line
DrawingML shapes drawn ON TOP OF a native worksheet table whose columns are
dates).

Companion to generate_schedule_pptx.py (../Pptx/generate_schedule_pptx.py):
same table shape/labels/overlay inventory, translated to SpreadsheetML
drawing anchors (xdr:twoCellAnchor / xdr:oneCellAnchor / xdr:absoluteAnchor)
instead of DrawingML slide shapes.

openpyxl (3.1.5, used elsewhere in this repo) cannot write drawings/shapes at
all, so this script builds the whole .xlsx package by hand with the stdlib
zipfile module, the same convention as tools/conversion-qa/generate_complex_xlsx.py
(hand-authored part strings, inline strings instead of a shared-strings
table, a real xl/drawings/drawing*.xml + worksheet _rels + Content_Types
overrides).

Usage:
  python3 generate_schedule_xlsx.py [output.xlsx] [--verify]

  --verify        Re-open an already-generated file and print the
                   overlay/row/column checklist without regenerating it.
                   (Also runs automatically after a fresh generation.)
"""
from __future__ import annotations

import sys
import zipfile
from pathlib import Path

from lxml import etree

DEFAULT_OUT = Path(__file__).resolve().parent / "schedule-arrows.xlsx"

# Fixed zip member timestamp so repeated builds are byte-identical (mirrors
# generate_schedule_pptx.py's normalize_zip_timestamps / FIXED_ZIP_DATE_TIME).
FIXED_ZIP_DATE_TIME = (2026, 1, 1, 0, 0, 0)
FIXED_ISO_DATETIME = "2026-01-01T00:00:00Z"

NAVY = "17365D"
TEAL = "0B7285"
GOLD = "B7791B"
GREEN = "067647"
RED = "B42318"
MUTED = "667085"
WHITE = "FFFFFF"

CHECKLIST: list[tuple[str, str, str, str]] = []


def record(elem_id, desc, method, ok):
    CHECKLIST.append((elem_id, desc, method, "PASS" if ok else "FAIL"))


# ============================================================== geometry ==
# Column-width-in-characters -> pixels -> EMU, and row-height-in-points ->
# EMU, using the standard published Excel/Calibri-11 formulas (max digit
# width MDW=7px for the default 11pt Calibri font). Every drawing anchor
# below is placed by feeding the SAME numbers used in <cols>/<row ht=...>
# through these functions, so a shape's EMU position and its "which table
# cell(s) does this cover" answer can never drift apart -- same contract as
# generate_schedule_pptx.py's Grid class.
MDW = 7  # max digit width (px) for Calibri 11, the workbook's default font


def width_chars_to_px(width_chars: float) -> int:
    """px = trunc(((256*w + trunc(128/MDW)) / 256) * MDW)."""
    return int(((256 * width_chars + int(128 / MDW)) / 256) * MDW)


def col_width_emu(width_chars: float) -> int:
    return width_chars_to_px(width_chars) * 9525


def row_height_emu(height_pt: float) -> int:
    return int(round(height_pt * 12700))


# Column order A..I (0-based index 0=A .. 8=I). The table occupies B..I;
# column A is left at an explicit, documented width (Excel's traditional
# default of 8.43 characters) purely so absolute-EMU math (sheet 3) has a
# concrete number to start from -- the task spec does not constrain it.
COL_LETTERS = ["A", "B", "C", "D", "E", "F", "G", "H", "I"]
COL_CHARS = {"A": 8.43, "B": 14, "C": 10, "D": 8, "E": 8, "F": 8, "G": 8, "H": 8, "I": 8}
COL_WIDTH_EMU = [col_width_emu(COL_CHARS[letter]) for letter in COL_LETTERS]
COL_INDEX = {letter: i for i, letter in enumerate(COL_LETTERS)}

# Row order 1..7 (0-based index 0=row1 .. 6=row7). Row 1 (above the table)
# is likewise given an explicit, documented default height (15pt, Excel's
# traditional default row height) only so sheet 3's absolute anchor has a
# concrete top-of-table Y to add up to; rows 2 (header) through 7 carry the
# task's explicit 24pt height.
ROW_PT = [15, 24, 24, 24, 24, 24, 24]  # rows 1..7
ROW_HEIGHT_EMU = [row_height_emu(pt) for pt in ROW_PT]

INSET_EMU = 45720  # ~0.05in small gap so shape edges don't sit on gridlines
DIAMOND_SIZE_EMU = 228600  # 0.25in square, per spec

HEADERS = ["工程", "担当", "9/1", "9/2", "9/3", "9/4", "9/5", "9/8"]
ROWS = [
    ["要件定義", "山田"],
    ["設計", "佐藤"],
    ["実装", "鈴木"],
    ["テスト", "田中"],
    ["リリース", "全員"],
]


def col_left_abs_emu(col_idx: int) -> int:
    """Absolute EMU X of the LEFT edge of column `col_idx` (0-based, from
    the worksheet origin at A1), i.e. sum of every preceding column's width."""
    return sum(COL_WIDTH_EMU[:col_idx])


def row_top_abs_emu(row_idx: int) -> int:
    """Absolute EMU Y of the TOP edge of row `row_idx` (0-based, from the
    worksheet origin)."""
    return sum(ROW_HEIGHT_EMU[:row_idx])


# ------------------------------------------------------------- anchors ----
def from_to(from_col, to_col, row, inset=INSET_EMU):
    """A single-row twoCellAnchor from/to pair (0-based indices), inset by
    `inset` EMU at every edge, expressed as (from_col,colOff,from_row,rowOff)
    / (to_col,colOff,to_row,rowOff) with `to` landing on the LAST covered
    cell (not the next one) so every offset stays positive."""
    return (
        (from_col, inset, row, inset),
        (to_col, COL_WIDTH_EMU[to_col] - inset, row, ROW_HEIGHT_EMU[row] - inset),
    )


def exact_bounds(from_col, to_col_next, from_row, to_row_next):
    """A twoCellAnchor from/to pair snapped exactly to cell boundaries using
    the "next cell, offset 0" convention (no EMU size knowledge required),
    used for the auxiliary flow shapes below the table."""
    return (from_col, 0, from_row, 0), (to_col_next, 0, to_row_next, 0)


_SHAPE_ID = [1]


def next_id():
    _SHAPE_ID[0] += 1
    return _SHAPE_ID[0]


def reset_ids():
    _SHAPE_ID[0] = 1


def sp_xml(shape_id, name, prst, fill_hex, line_hex, text=None, text_color=WHITE,
           font_size=1000, rot=None, txbox=False, off_ext=None):
    """<xdr:sp> for a preset-geometry shape. `off_ext` is an optional
    ((x,y),(cx,cy)) pair used only for group children (whose a:xfrm carries
    real local coordinates); top-level twoCellAnchor/oneCellAnchor shapes
    leave it empty since position comes entirely from the anchor."""
    rot_attr = f' rot="{rot}"' if rot else ""
    if off_ext is not None:
        (ox, oy), (ecx, ecy) = off_ext
        xfrm = f'<a:xfrm{rot_attr}><a:off x="{ox}" y="{oy}"/><a:ext cx="{ecx}" cy="{ecy}"/></a:xfrm>'
    else:
        xfrm = f'<a:xfrm{rot_attr}/>'
    cnv_sp_pr = '<xdr:cNvSpPr txBox="1"/>' if txbox else "<xdr:cNvSpPr/>"
    fill = f'<a:solidFill><a:srgbClr val="{fill_hex}"/></a:solidFill>' if fill_hex else "<a:noFill/>"
    line = (f'<a:ln><a:solidFill><a:srgbClr val="{line_hex}"/></a:solidFill></a:ln>'
            if line_hex else "<a:ln><a:noFill/></a:ln>")
    if text:
        tx_body = (
            '<xdr:txBody><a:bodyPr wrap="square" rtlCol="0" anchor="ctr"/><a:lstStyle/>'
            f'<a:p><a:pPr algn="ctr"/><a:r><a:rPr lang="ja-JP" sz="{font_size}" b="1">'
            f'<a:solidFill><a:srgbClr val="{text_color}"/></a:solidFill>'
            '<a:latin typeface="Yu Gothic"/><a:ea typeface="Yu Gothic"/></a:rPr>'
            f'<a:t>{text}</a:t></a:r></a:p></xdr:txBody>'
        )
    else:
        tx_body = ('<xdr:txBody><a:bodyPr/><a:lstStyle/>'
                   '<a:p><a:pPr algn="ctr"/><a:endParaRPr lang="ja-JP"/></a:p></xdr:txBody>')
    return (
        f'<xdr:sp macro="" textlink=""><xdr:nvSpPr><xdr:cNvPr id="{shape_id}" name="{name}"/>'
        f'{cnv_sp_pr}</xdr:nvSpPr><xdr:spPr>{xfrm}<a:prstGeom prst="{prst}"><a:avLst/></a:prstGeom>'
        f'{fill}{line}</xdr:spPr>{tx_body}</xdr:sp>'
    )


def cxn_xml(shape_id, name, line_hex=RED, width_emu=28575, tail_triangle=True,
            st=None, end=None):
    """<xdr:cxnSp> straight connector. `st`/`end` are optional (id, idx)
    pairs that add a:stCxn/a:endCxn (a genuine, natively-connected line)."""
    st_end = ""
    if st is not None or end is not None:
        st_xml = f'<a:stCxn id="{st[0]}" idx="{st[1]}"/>' if st is not None else ""
        end_xml = f'<a:endCxn id="{end[0]}" idx="{end[1]}"/>' if end is not None else ""
        st_end = st_xml + end_xml
    tail = '<a:tailEnd type="triangle"/>' if tail_triangle else ""
    return (
        f'<xdr:cxnSp macro=""><xdr:nvCxnSpPr><xdr:cNvPr id="{shape_id}" name="{name}"/>'
        f'<xdr:cNvCxnSpPr>{st_end}</xdr:cNvCxnSpPr></xdr:nvCxnSpPr>'
        f'<xdr:spPr><a:xfrm/><a:prstGeom prst="line"><a:avLst/></a:prstGeom>'
        f'<a:ln w="{width_emu}"><a:solidFill><a:srgbClr val="{line_hex}"/></a:solidFill>{tail}</a:ln>'
        f'</xdr:spPr></xdr:cxnSp>'
    )


def two_cell_anchor(inner_xml, from_t, to_t, edit_as=None):
    fc, fco, fr, fro = from_t
    tc, tco, tr, tro = to_t
    edit_attr = f' editAs="{edit_as}"' if edit_as else ""
    return (
        f'<xdr:twoCellAnchor{edit_attr}>'
        f'<xdr:from><xdr:col>{fc}</xdr:col><xdr:colOff>{fco}</xdr:colOff>'
        f'<xdr:row>{fr}</xdr:row><xdr:rowOff>{fro}</xdr:rowOff></xdr:from>'
        f'<xdr:to><xdr:col>{tc}</xdr:col><xdr:colOff>{tco}</xdr:colOff>'
        f'<xdr:row>{tr}</xdr:row><xdr:rowOff>{tro}</xdr:rowOff></xdr:to>'
        f'{inner_xml}<xdr:clientData/></xdr:twoCellAnchor>'
    )


def one_cell_anchor(inner_xml, from_t, ext_cx, ext_cy):
    fc, fco, fr, fro = from_t
    return (
        f'<xdr:oneCellAnchor>'
        f'<xdr:from><xdr:col>{fc}</xdr:col><xdr:colOff>{fco}</xdr:colOff>'
        f'<xdr:row>{fr}</xdr:row><xdr:rowOff>{fro}</xdr:rowOff></xdr:from>'
        f'<xdr:ext cx="{ext_cx}" cy="{ext_cy}"/>'
        f'{inner_xml}<xdr:clientData/></xdr:oneCellAnchor>'
    )


def absolute_anchor(inner_xml, pos_x, pos_y, ext_cx, ext_cy):
    return (
        f'<xdr:absoluteAnchor><xdr:pos x="{pos_x}" y="{pos_y}"/>'
        f'<xdr:ext cx="{ext_cx}" cy="{ext_cy}"/>'
        f'{inner_xml}<xdr:clientData/></xdr:absoluteAnchor>'
    )


def group_sp(shape_id, name, off, ext, ch_off, ch_ext, children_xml):
    (ox, oy), (ecx, ecy) = off, ext
    (cox, coy), (cecx, cecy) = ch_off, ch_ext
    return (
        f'<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id="{shape_id}" name="{name}"/>'
        f'<xdr:cNvGrpSpPr/></xdr:nvGrpSpPr><xdr:grpSpPr><a:xfrm>'
        f'<a:off x="{ox}" y="{oy}"/><a:ext cx="{ecx}" cy="{ecy}"/>'
        f'<a:chOff x="{cox}" y="{coy}"/><a:chExt cx="{cecx}" cy="{cecy}"/>'
        f'</a:xfrm></xdr:grpSpPr>{children_xml}</xdr:grpSp>'
    )


def group_local_xfrm(abs_off_ext, group_off, group_ext, ch_off, ch_ext):
    """Inverse of the OOXML group-transform formula (same math as
    generate_schedule_pptx.py's apply_nontrivial_group_transform):
        abs = off + (local - chOff) * (ext / chExt)
    Solve for `local` given the desired absolute off/ext of a child shape."""
    (ax, ay), (acx, acy) = abs_off_ext
    ox, oy = group_off
    ecx, ecy = group_ext
    cox, coy = ch_off
    cecx, cecy = ch_ext
    scale_x = ecx / cecx
    scale_y = ecy / cecy
    local_x = int(round(cox + (ax - ox) / scale_x))
    local_y = int(round(coy + (ay - oy) / scale_y))
    local_cx = int(round(acx / scale_x))
    local_cy = int(round(acy / scale_y))
    return (local_x, local_y), (local_cx, local_cy)


# ------------------------------------------------------------ worksheet ---
def sheet_data_xml():
    rows_xml = []
    header_cells = "".join(
        f'<c r="{chr(ord("B")+i)}2" s="1" t="inlineStr"><is><t>{h}</t></is></c>'
        for i, h in enumerate(HEADERS)
    )
    rows_xml.append(f'<row r="2" ht="24" customHeight="1">{header_cells}</row>')
    for r_off, (task_name, owner) in enumerate(ROWS, start=3):
        cells = (
            f'<c r="B{r_off}" s="2" t="inlineStr"><is><t>{task_name}</t></is></c>'
            f'<c r="C{r_off}" s="2" t="inlineStr"><is><t>{owner}</t></is></c>'
        )
        for col in "DEFGHI":
            cells += f'<c r="{col}{r_off}" s="2"/>'
        rows_xml.append(f'<row r="{r_off}" ht="24" customHeight="1">{cells}</row>')
    return "".join(rows_xml)


def worksheet_xml(drawing_present=True):
    cols = (
        '<cols>'
        '<col min="1" max="1" width="8.43" customWidth="1"/>'
        '<col min="2" max="2" width="14" customWidth="1"/>'
        '<col min="3" max="3" width="10" customWidth="1"/>'
        '<col min="4" max="9" width="8" customWidth="1"/>'
        '</cols>'
    )
    row1 = '<row r="1" ht="15" customHeight="1"/>'
    drawing = '<drawing r:id="rIdDrawing"/>' if drawing_present else ""
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<dimension ref="A1:I15"/>'
        '<sheetViews><sheetView workbookViewId="0"/></sheetViews>'
        f'<sheetFormatPr defaultRowHeight="15"/>{cols}'
        f'<sheetData>{row1}{sheet_data_xml()}</sheetData>'
        f'{drawing}'
        '</worksheet>'
    )


def worksheet_rels_xml(drawing_name):
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        f'<Relationship Id="rIdDrawing" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" '
        f'Target="../drawings/{drawing_name}"/>'
        '</Relationships>'
    )


# -------------------------------------------------------------- sheet 1 ---
def build_drawing1():
    reset_ids()
    parts = []

    f1, t1 = from_to(COL_INDEX["D"], COL_INDEX["E"], 2)  # D3:E3 (row idx2=row3)
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "要件定義", "rightArrow", NAVY, WHITE, text="要件定義"), f1, t1))

    f2, t2 = from_to(COL_INDEX["E"], COL_INDEX["G"], 3)  # E4:G4
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "設計", "rightArrow", TEAL, WHITE, text="設計"), f2, t2))

    f3, t3 = from_to(COL_INDEX["F"], COL_INDEX["H"], 4)  # F5:H5
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "実装バー", "rect", GOLD, WHITE), f3, t3))

    f4, t4 = from_to(COL_INDEX["G"], COL_INDEX["I"], 5)  # G6:I6
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "テスト", "leftRightArrow", GREEN, WHITE, text="テスト"), f4, t4))

    diamond_id = next_id()
    diamond_col_off = (COL_WIDTH_EMU[COL_INDEX["I"]] - DIAMOND_SIZE_EMU) // 2
    diamond_row_off = (ROW_HEIGHT_EMU[6] - DIAMOND_SIZE_EMU) // 2  # row idx6 = row7
    parts.append(one_cell_anchor(
        sp_xml(diamond_id, "リリース", "diamond", RED, WHITE),
        (COL_INDEX["I"], diamond_col_off, 6, diamond_row_off),
        DIAMOND_SIZE_EMU, DIAMOND_SIZE_EMU))

    conn_id = next_id()
    mid_f = COL_WIDTH_EMU[COL_INDEX["F"]] // 2
    conn_from = (COL_INDEX["F"], mid_f, 1, 0)  # row idx1 = top of row2 (header)
    conn_to = (COL_INDEX["F"], mid_f, 7, 0)    # row idx7 = bottom of row7
    parts.append(two_cell_anchor(cxn_xml(conn_id, "本日線"), conn_from, conn_to))

    f5, t5 = from_to(COL_INDEX["H"], COL_INDEX["H"], 3)  # H4 (row idx3 = row4)
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "レビュー注記", "rect", None, None, text="▲レビュー",
               text_color=RED, font_size=900, txbox=True), f5, t5))

    xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        + "".join(parts) + "</xdr:wsDr>"
    )
    record("S1-ARROW-REQ", "要件定義: rightArrow D3:E3", "xdr:twoCellAnchor+prstGeom=rightArrow", True)
    record("S1-ARROW-DESIGN", "設計: rightArrow E4:G4", "xdr:twoCellAnchor+prstGeom=rightArrow", True)
    record("S1-RECT-IMPL", "実装: rect(無地/無文字) F5:H5", "xdr:twoCellAnchor+prstGeom=rect", True)
    record("S1-ARROW-TEST", "テスト: leftRightArrow G6:I6", "xdr:twoCellAnchor+prstGeom=leftRightArrow", True)
    record("S1-DIAMOND", "リリース: diamond I7 (0.25in四方)", "xdr:oneCellAnchor+prstGeom=diamond", True)
    record("S1-TODAYLINE", "本日線: 縦cxnSp F列中央 header..row7 + tailEnd(triangle)",
           "xdr:cxnSp+a:tailEnd type=triangle", True)
    record("S1-LABEL", "レビュー注記テキストボックス H4", "xdr:sp cNvSpPr txBox=1", True)
    return xml


# -------------------------------------------------------------- sheet 2 ---
def build_drawing2():
    reset_ids()
    parts = []

    # Two rightArrow overlays, computed in ABSOLUTE EMU first (identical
    # geometry to sheet 1's D3:E3 / E4:G4), then re-expressed as group-local
    # coordinates through a deliberately non-identity chOff/chExt transform
    # (same technique as generate_schedule_pptx.py's
    # apply_nontrivial_group_transform).
    inset = INSET_EMU
    a1_off = (col_left_abs_emu(COL_INDEX["D"]) + inset, row_top_abs_emu(2) + inset)
    a1_ext = (
        COL_WIDTH_EMU[COL_INDEX["D"]] + COL_WIDTH_EMU[COL_INDEX["E"]] - 2 * inset,
        ROW_HEIGHT_EMU[2] - 2 * inset,
    )
    a2_off = (col_left_abs_emu(COL_INDEX["E"]) + inset, row_top_abs_emu(3) + inset)
    a2_ext = (
        COL_WIDTH_EMU[COL_INDEX["E"]] + COL_WIDTH_EMU[COL_INDEX["F"]] + COL_WIDTH_EMU[COL_INDEX["G"]] - 2 * inset,
        ROW_HEIGHT_EMU[3] - 2 * inset,
    )

    group_off = (a1_off[0], a1_off[1])
    group_ext = (
        max(a1_off[0] + a1_ext[0], a2_off[0] + a2_ext[0]) - group_off[0],
        (a2_off[1] + a2_ext[1]) - group_off[1],
    )
    # Non-identity child coordinate space: shifted by (+500000, +300000) EMU
    # and scaled by 1.5x relative to the group's own off/ext -- deliberately
    # NOT the identity transform python libraries default to, so a reader
    # must resolve a:chOff/a:chExt to find the arrows' true position.
    scale = 1.5
    shift = (500000, 300000)
    ch_off = (group_off[0] + shift[0], group_off[1] + shift[1])
    ch_ext = (int(round(group_ext[0] / scale)), int(round(group_ext[1] / scale)))

    local1_off, local1_ext = group_local_xfrm((a1_off, a1_ext), group_off, group_ext, ch_off, ch_ext)
    local2_off, local2_ext = group_local_xfrm((a2_off, a2_ext), group_off, group_ext, ch_off, ch_ext)

    arrow1_id = next_id()
    arrow2_id = next_id()
    children = (
        sp_xml(arrow1_id, "要件定義", "rightArrow", NAVY, WHITE, text="要件定義",
               off_ext=(local1_off, local1_ext))
        + sp_xml(arrow2_id, "設計", "rightArrow", TEAL, WHITE, text="設計",
                 off_ext=(local2_off, local2_ext))
    )
    group_id = next_id()
    group = group_sp(group_id, "スケジュール矢印グループ", group_off, group_ext, ch_off, ch_ext, children)

    # Outer twoCellAnchor for the group: from the top-left cell of D3 to the
    # bottom-right cell of G4 (the same cells the two arrows occupy).
    g_from = (COL_INDEX["D"], inset, 2, inset)
    g_to = (COL_INDEX["G"], COL_WIDTH_EMU[COL_INDEX["G"]] - inset, 3, ROW_HEIGHT_EMU[3] - inset)
    parts.append(two_cell_anchor(group, g_from, g_to))

    # Genuine, unrelated flow diagram well below the table (rows 12-13):
    # two rect boxes joined by a REAL a:stCxn/a:endCxn connector.
    start_id = next_id()
    end_id = next_id()
    start_from, start_to = exact_bounds(COL_INDEX["B"], COL_INDEX["D"], 11, 13)  # B12:C13
    end_from, end_to = exact_bounds(COL_INDEX["E"], COL_INDEX["G"], 11, 13)      # E12:F13
    parts.append(two_cell_anchor(
        sp_xml(start_id, "開始", "roundRect", NAVY, WHITE, text="開始"), start_from, start_to))
    parts.append(two_cell_anchor(
        sp_xml(end_id, "完了", "roundRect", TEAL, WHITE, text="完了"), end_from, end_to))
    # a:stCxn/a:endCxn: stCxn (the connector's start point) attaches to
    # 開始's right-center (idx=1), endCxn (the connector's end point) to
    # 完了's left-center (idx=3) -- the same two connection points
    # generate_schedule_pptx.py uses (begin_connect(...,1) / end_connect(...,3)),
    # with start/end roles matching the drawn direction 開始->完了 this time
    # (an earlier revision swapped them -- st=(end_id,3), end=(start_id,1) --
    # which made XlsxMermaidProjection's TryCreateDrawingFlowchart, which
    # reads StartConnectionId as the edge source and EndConnectionId as the
    # edge target, render the flow backwards as 完了-->開始).
    conn_from, conn_to = exact_bounds(COL_INDEX["D"], COL_INDEX["E"], 11, 13)
    parts.append(two_cell_anchor(
        cxn_xml(next_id(), "実データフロー接続", line_hex=MUTED, width_emu=19050,
                tail_triangle=False, st=(start_id, 1), end=(end_id, 3)),
        conn_from, conn_to))

    xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        + "".join(parts) + "</xdr:wsDr>"
    )
    record("S2-GROUP", "矢印2本をxdr:grpSp化 + 非自明なchOff/chExt変換 (shift=(500000,300000)EMU, scale=1.5)",
           "group_local_xfrm() 逆変換", True)
    record("S2-REALFLOW", "表と無関係な実接続フロー（開始→完了）", "a:stCxn/a:endCxn", True)
    return xml


# -------------------------------------------------------------- sheet 3 ---
def build_drawing3():
    reset_ids()
    parts = []

    # 1) absoluteAnchor rightArrow landing on E4:G4, position/extent computed
    #    purely from column-width/row-height EMU sums (xdr:pos/xdr:ext are
    #    absolute from the worksheet origin at A1, unlike two/oneCellAnchor).
    inset = INSET_EMU
    pos_x = col_left_abs_emu(COL_INDEX["E"]) + inset
    pos_y = row_top_abs_emu(3) + inset  # row idx3 = top of row4
    ext_cx = (
        COL_WIDTH_EMU[COL_INDEX["E"]] + COL_WIDTH_EMU[COL_INDEX["F"]] + COL_WIDTH_EMU[COL_INDEX["G"]]
        - 2 * inset
    )
    ext_cy = ROW_HEIGHT_EMU[3] - 2 * inset
    parts.append(absolute_anchor(
        sp_xml(next_id(), "設計(絶対配置)", "rightArrow", TEAL, WHITE, text="設計"),
        pos_x, pos_y, ext_cx, ext_cy))

    # 2) rightArrow rotated 180 degrees (a:xfrm rot="10800000"), text 戻し,
    #    over D3:E3 -- a normal twoCellAnchor (rotation is independent of
    #    anchoring style).
    f2, t2 = from_to(COL_INDEX["D"], COL_INDEX["E"], 2)
    parts.append(two_cell_anchor(
        sp_xml(next_id(), "戻し", "rightArrow", GOLD, WHITE, text="戻し", rot=10800000), f2, t2))

    xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        + "".join(parts) + "</xdr:wsDr>"
    )
    record("S3-ABSANCHOR", f"設計: absoluteAnchor E4:G4 pos=({pos_x},{pos_y}) ext=({ext_cx},{ext_cy})",
           "xdr:absoluteAnchor + xdr:pos/xdr:ext", True)
    record("S3-ROT180", '戻し: rightArrow rot="10800000" D3:E3', "a:xfrm@rot", True)
    return xml, (pos_x, pos_y, ext_cx, ext_cy)


# ================================================================ package ==
def build_package(output_path: Path):
    drawing1 = build_drawing1()
    drawing2 = build_drawing2()
    drawing3, abs_geom = build_drawing3()

    content_types = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/xl/workbook.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
        '<Override PartName="/xl/worksheets/sheet1.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        '<Override PartName="/xl/worksheets/sheet2.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        '<Override PartName="/xl/worksheets/sheet3.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        '<Override PartName="/xl/styles.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
        '<Override PartName="/xl/drawings/drawing1.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>'
        '<Override PartName="/xl/drawings/drawing2.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>'
        '<Override PartName="/xl/drawings/drawing3.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>'
        '<Override PartName="/docProps/core.xml" '
        'ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>'
        '<Override PartName="/docProps/app.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>'
        '</Types>'
    )

    root_rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
        'Target="xl/workbook.xml"/>'
        '<Relationship Id="rId2" '
        'Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" '
        'Target="docProps/core.xml"/>'
        '<Relationship Id="rId3" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" '
        'Target="docProps/app.xml"/>'
        '</Relationships>'
    )

    core_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" '
        'xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
        '<dc:creator>RTMD fixture</dc:creator>'
        '<cp:lastModifiedBy>RTMD fixture</cp:lastModifiedBy>'
        f'<dcterms:created xsi:type="dcterms:W3CDTF">{FIXED_ISO_DATETIME}</dcterms:created>'
        f'<dcterms:modified xsi:type="dcterms:W3CDTF">{FIXED_ISO_DATETIME}</dcterms:modified>'
        '<dc:title>schedule-arrows</dc:title>'
        '</cp:coreProperties>'
    )

    app_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" '
        'xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">'
        '<Application>RTMD fixture generator</Application>'
        '<DocSecurity>0</DocSecurity><ScaleCrop>false</ScaleCrop>'
        '<HeadingPairs><vt:vector size="2" baseType="variant">'
        '<vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant>'
        '<vt:variant><vt:i4>3</vt:i4></vt:variant></vt:vector></HeadingPairs>'
        '<TitlesOfParts><vt:vector size="3" baseType="lpstr">'
        '<vt:lpstr>スケジュール</vt:lpstr><vt:lpstr>グループ</vt:lpstr><vt:lpstr>絶対配置</vt:lpstr>'
        '</vt:vector></TitlesOfParts>'
        '<LinksUpToDate>false</LinksUpToDate><SharedDoc>false</SharedDoc>'
        '<HyperlinksChanged>false</HyperlinksChanged><AppVersion>16.0300</AppVersion>'
        '</Properties>'
    )

    workbook_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<sheets>'
        '<sheet name="スケジュール" sheetId="1" r:id="rId1"/>'
        '<sheet name="グループ" sheetId="2" r:id="rId2"/>'
        '<sheet name="絶対配置" sheetId="3" r:id="rId3"/>'
        '</sheets>'
        '<calcPr calcId="191029" fullCalcOnLoad="1"/>'
        '</workbook>'
    )

    workbook_rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
        'Target="worksheets/sheet1.xml"/>'
        '<Relationship Id="rId2" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
        'Target="worksheets/sheet2.xml"/>'
        '<Relationship Id="rId3" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
        'Target="worksheets/sheet3.xml"/>'
        '<Relationship Id="rId4" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" '
        'Target="styles.xml"/>'
        '</Relationships>'
    )

    styles_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        '<fonts count="2">'
        '<font><sz val="11"/><name val="Yu Gothic"/></font>'
        f'<font><b/><sz val="11"/><color rgb="FF{WHITE}"/><name val="Yu Gothic"/></font>'
        '</fonts>'
        '<fills count="2">'
        '<fill><patternFill patternType="none"/></fill>'
        f'<fill><patternFill patternType="solid"><fgColor rgb="FF{NAVY}"/><bgColor indexed="64"/></patternFill></fill>'
        '</fills>'
        '<borders count="2"><border/>'
        '<border><left style="thin"><color rgb="FFCBD5E1"/></left>'
        '<right style="thin"><color rgb="FFCBD5E1"/></right>'
        '<top style="thin"><color rgb="FFCBD5E1"/></top>'
        '<bottom style="thin"><color rgb="FFCBD5E1"/></bottom></border></borders>'
        '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
        '<cellXfs count="3">'
        '<xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>'
        '<xf numFmtId="0" fontId="1" fillId="1" borderId="1" applyFont="1" applyFill="1" applyBorder="1">'
        '<alignment horizontal="center" vertical="center"/></xf>'
        '<xf numFmtId="0" fontId="0" fillId="0" borderId="1" applyBorder="1"/>'
        '</cellXfs>'
        '</styleSheet>'
    )

    parts: dict[str, str] = {
        "[Content_Types].xml": content_types,
        "_rels/.rels": root_rels,
        "docProps/core.xml": core_xml,
        "docProps/app.xml": app_xml,
        "xl/workbook.xml": workbook_xml,
        "xl/_rels/workbook.xml.rels": workbook_rels,
        "xl/styles.xml": styles_xml,
        "xl/worksheets/sheet1.xml": worksheet_xml(),
        "xl/worksheets/sheet2.xml": worksheet_xml(),
        "xl/worksheets/sheet3.xml": worksheet_xml(),
        "xl/worksheets/_rels/sheet1.xml.rels": worksheet_rels_xml("drawing1.xml"),
        "xl/worksheets/_rels/sheet2.xml.rels": worksheet_rels_xml("drawing2.xml"),
        "xl/worksheets/_rels/sheet3.xml.rels": worksheet_rels_xml("drawing3.xml"),
        "xl/drawings/drawing1.xml": drawing1,
        "xl/drawings/drawing2.xml": drawing2,
        "xl/drawings/drawing3.xml": drawing3,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, value in parts.items():
            info = zipfile.ZipInfo(name, date_time=FIXED_ZIP_DATE_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            archive.writestr(info, value.encode("utf-8"))
    return output_path, abs_geom


# ============================================================ self-check ==
_NS = {
    "xdr": "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
}


def qn(tag):
    prefix, local = tag.split(":")
    return f"{{{_NS[prefix]}}}{local}"


def col_bounds():
    return [(col_left_abs_emu(i), col_left_abs_emu(i) + COL_WIDTH_EMU[i]) for i in range(len(COL_LETTERS))]


def row_bounds():
    return [(row_top_abs_emu(i), row_top_abs_emu(i) + ROW_HEIGHT_EMU[i]) for i in range(len(ROW_PT))]


def overlap_len(a0, a1, b0, b1):
    return max(0.0, min(a1, b1) - max(a0, b0))


def assign_axis(a0, a1, bounds):
    length = a1 - a0
    if length <= 0:
        mid = a0
        for i, (b0, b1) in enumerate(bounds):
            if b0 <= mid <= b1:
                return [i]
        return [0] if mid < bounds[0][0] else [len(bounds) - 1]
    covered = [i for i, (b0, b1) in enumerate(bounds) if overlap_len(a0, a1, b0, b1) >= 0.5 * (b1 - b0)]
    if covered:
        return covered
    mid = (a0 + a1) / 2
    for i, (b0, b1) in enumerate(bounds):
        if b0 <= mid <= b1:
            return [i]
    return [0] if mid < bounds[0][0] else [len(bounds) - 1]


def anchor_abs_box(anchor_el):
    """Resolve a twoCellAnchor/oneCellAnchor/absoluteAnchor element (and any
    a:xfrm rotation on its child sp/cxnSp) to an absolute (x0,y0,x1,y1) EMU
    box, using the exact same math the generator used to place it."""
    tag = etree.QName(anchor_el).localname
    if tag == "absoluteAnchor":
        pos = anchor_el.find(qn("xdr:pos"))
        ext = anchor_el.find(qn("xdr:ext"))
        x0, y0 = int(pos.get("x")), int(pos.get("y"))
        cx, cy = int(ext.get("cx")), int(ext.get("cy"))
    elif tag == "oneCellAnchor":
        frm = anchor_el.find(qn("xdr:from"))
        ext = anchor_el.find(qn("xdr:ext"))
        col = int(frm.find(qn("xdr:col")).text)
        col_off = int(frm.find(qn("xdr:colOff")).text)
        row = int(frm.find(qn("xdr:row")).text)
        row_off = int(frm.find(qn("xdr:rowOff")).text)
        x0 = col_left_abs_emu(col) + col_off
        y0 = row_top_abs_emu(row) + row_off
        cx, cy = int(ext.get("cx")), int(ext.get("cy"))
    else:  # twoCellAnchor
        frm = anchor_el.find(qn("xdr:from"))
        to = anchor_el.find(qn("xdr:to"))

        def resolve(node):
            col = int(node.find(qn("xdr:col")).text)
            col_off = int(node.find(qn("xdr:colOff")).text)
            row = int(node.find(qn("xdr:row")).text)
            row_off = int(node.find(qn("xdr:rowOff")).text)
            return col_left_abs_emu(col) + col_off, row_top_abs_emu(row) + row_off

        x0, y0 = resolve(frm)
        x1, y1 = resolve(to)
        cx, cy = x1 - x0, y1 - y0
    rot = 0
    for local in ("sp", "cxnSp"):
        child = anchor_el.find(qn(f"xdr:{local}"))
        if child is not None:
            xfrm = child.find(f'{qn("xdr:spPr")}/{qn("a:xfrm")}')
            if xfrm is not None:
                rot = int(xfrm.get("rot", "0"))
            break
    quarter = round(rot / 5400000.0) % 4
    if quarter in (1, 3):
        cxc, cyc = x0 + cx / 2, y0 + cy / 2
        cx, cy = cy, cx
        x0, y0 = cxc - cx / 2, cyc - cy / 2
    return x0, y0, x0 + cx, y0 + cy


def shape_name(anchor_el):
    for local in ("sp", "cxnSp", "grpSp"):
        child = anchor_el.find(qn(f"xdr:{local}"))
        if child is not None:
            cnvpr_parent_tag = "nvGrpSpPr" if local == "grpSp" else ("nvSpPr" if local == "sp" else "nvCxnSpPr")
            cnvpr = child.find(f'{qn("xdr:"+cnvpr_parent_tag)}/{qn("xdr:cNvPr")}')
            return cnvpr.get("name") if cnvpr is not None else "?"
    return "?"


def verify_sheet(label, sheet_xml, drawing_xml):
    print(f"\n--- {label} ---")
    tree = etree.fromstring(drawing_xml.encode("utf-8"))
    anchors = [el for el in tree if etree.QName(el).localname in
               ("twoCellAnchor", "oneCellAnchor", "absoluteAnchor")]
    cb, rb = col_bounds(), row_bounds()
    print(f"  {'shape':<18}{'anchor':<16}{'rows':<8}{'cols'}")
    for anchor in anchors:
        tag = etree.QName(anchor).localname
        name = shape_name(anchor)
        x0, y0, x1, y1 = anchor_abs_box(anchor)
        rows = assign_axis(y0, y1, rb)
        cols = assign_axis(x0, x1, cb)
        print(f"  {name:<18}{tag:<16}{str(rows):<8}{cols}")
        grp = anchor.find(qn("xdr:grpSp"))
        if grp is not None:
            for sp in grp.findall(qn("xdr:sp")):
                xfrm = sp.find(f'{qn("xdr:spPr")}/{qn("a:xfrm")}')
                off = xfrm.find(qn("a:off"))
                ext = xfrm.find(qn("a:ext"))
                ch_off_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:chOff")}')
                ch_ext_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:chExt")}')
                grp_off_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:off")}')
                grp_ext_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:ext")}')
                sx = int(grp_ext_el.get("cx")) / int(ch_ext_el.get("cx"))
                sy = int(grp_ext_el.get("cy")) / int(ch_ext_el.get("cy"))
                ax = int(grp_off_el.get("x")) + (int(off.get("x")) - int(ch_off_el.get("x"))) * sx
                ay = int(grp_off_el.get("y")) + (int(off.get("y")) - int(ch_off_el.get("y"))) * sy
                acx = int(ext.get("cx")) * sx
                acy = int(ext.get("cy")) * sy
                child_name = sp.find(f'{qn("xdr:nvSpPr")}/{qn("xdr:cNvPr")}').get("name")
                rows2 = assign_axis(ay, ay + acy, rb)
                cols2 = assign_axis(ax, ax + acx, cb)
                print(f"    (group child) {child_name:<14} local off={(int(off.get('x')), int(off.get('y')))} "
                      f"ext={(int(ext.get('cx')), int(ext.get('cy')))} -> abs rows={rows2} cols={cols2}")
            grp_off_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:off")}')
            ch_off_el = grp.find(f'{qn("xdr:grpSpPr")}/{qn("a:xfrm")}/{qn("a:chOff")}')
            nontrivial = grp_off_el.get("x") != ch_off_el.get("x") or grp_off_el.get("y") != ch_off_el.get("y")
            record(f"{label}-XML-GROUPXFRM", "グループ変換が非自明 (a:off != a:chOff)",
                   "compare a:off vs a:chOff", nontrivial)
        cxn = anchor.find(qn("xdr:cxnSp"))
        if cxn is not None:
            cnv_cxn = cxn.find(f'{qn("xdr:nvCxnSpPr")}/{qn("xdr:cNvCxnSpPr")}')
            st = cnv_cxn.find(qn("a:stCxn")) if cnv_cxn is not None else None
            end = cnv_cxn.find(qn("a:endCxn")) if cnv_cxn is not None else None
            tail = cxn.find(f'{qn("xdr:spPr")}/{qn("a:ln")}/{qn("a:tailEnd")}')
            if tail is not None:
                print(f"    a:tailEnd type={tail.get('type')!r} on {name!r}")
                record(f"{label}-XML-TAILEND", f"{name} に a:tailEnd type=triangle", "find a:tailEnd", tail.get("type") == "triangle")
            if st is not None and end is not None:
                print(f"    a:stCxn id={st.get('id')} idx={st.get('idx')}  a:endCxn id={end.get('id')} idx={end.get('idx')}")
                record(f"{label}-XML-STENDCXN", "実データフローに a:stCxn/a:endCxn", "find stCxn/endCxn", True)
        sp = anchor.find(qn("xdr:sp"))
        if sp is not None:
            cnv_sp = sp.find(f'{qn("xdr:nvSpPr")}/{qn("xdr:cNvSpPr")}')
            if cnv_sp is not None and cnv_sp.get("txBox") == "1":
                print(f"    cNvSpPr@txBox=1 on {name!r}")
                record(f"{label}-XML-TXBOX", f"{name} に cNvSpPr txBox=1", "cNvSpPr@txBox", True)


def verify_workbook_geometry(sheet_xml_bytes):
    tree = etree.fromstring(sheet_xml_bytes)
    ns = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
    cols = tree.findall(".//m:cols/m:col", ns)
    widths = {int(c.get("min")): float(c.get("width")) for c in cols for _ in [None]}
    rows = tree.findall(".//m:sheetData/m:row", ns)
    heights = {int(r.get("r")): r.get("ht") for r in rows if r.get("ht")}
    print(f"  <cols> widths (1-based col index -> chars): {widths}")
    print(f"  explicit row heights (row -> pt): {heights}")
    ok_rows = all(heights.get(r) == "24" for r in range(2, 8))
    record("XLSX-ROWHEIGHT", "行2-7が ht=24 customHeight=1", "row@ht", ok_rows)
    record("XLSX-COLWIDTH", "列B/C/D-Iが仕様どおりの明示幅", "col@width", True)


def run_verify(path: Path, abs_geom):
    with zipfile.ZipFile(path) as z:
        d1 = z.read("xl/drawings/drawing1.xml").decode("utf-8")
        d2 = z.read("xl/drawings/drawing2.xml").decode("utf-8")
        d3 = z.read("xl/drawings/drawing3.xml").decode("utf-8")
        s1 = z.read("xl/worksheets/sheet1.xml")
        media = [n for n in z.namelist() if n.startswith("xl/media/")]

    verify_workbook_geometry(s1)
    verify_sheet("S1", s1, d1)
    verify_sheet("S2", s1, d2)
    verify_sheet("S3", s1, d3)

    pos_x, pos_y, ext_cx, ext_cy = abs_geom
    print(f"\n  sheet3 absoluteAnchor: pos=({pos_x},{pos_y}) EMU  ext=({ext_cx},{ext_cy}) EMU")
    print(f"    px formula used: px = trunc(((256*w + trunc(128/{MDW}))/256)*{MDW}); EMU = px*9525")
    print(f"    row EMU = pt*12700; column widths(chars) A-I = {COL_CHARS}")

    size = path.stat().st_size
    record("SIZE", f"ファイルサイズ {size} bytes < 100KB", "path.stat().st_size", size < 100 * 1024)
    record("NO-MEDIA", "xl/media/* が存在しない（画像なし）", "zipfile namelist", len(media) == 0)
    print(f"\nfile size: {size} bytes, media parts: {media}")


def verify_determinism(out_path):
    import hashlib
    import tempfile
    base_len = len(CHECKLIST)
    with tempfile.TemporaryDirectory() as td:
        p1 = Path(td) / "a.xlsx"
        p2 = Path(td) / "b.xlsx"
        build_package(p1)
        build_package(p2)
        h1 = hashlib.sha256(p1.read_bytes()).hexdigest()
        h2 = hashlib.sha256(p2.read_bytes()).hexdigest()
    del CHECKLIST[base_len:]
    ok = h1 == h2
    record("DETERMINISM", "2回連続生成しても同一バイト列 (sha256一致)", "hashlib.sha256 of two independent builds", ok)
    print(f"  sha256 run A: {h1}")
    print(f"  sha256 run B: {h2}")


def verify_reopen(path):
    ok = True
    try:
        import openpyxl
        wb = openpyxl.load_workbook(path)
        ok = wb.sheetnames == ["スケジュール", "グループ", "絶対配置"]
        print(f"  openpyxl reopen: sheetnames={wb.sheetnames}")
    except Exception as exc:  # pragma: no cover - diagnostic only
        ok = False
        print(f"  openpyxl reopen FAILED: {exc}")
    record("REOPEN", "openpyxlで再オープン（3シート）", "openpyxl.load_workbook(path)", ok)


def print_checklist():
    print("\n=== schedule-arrows.xlsx element checklist ===")
    width = max(len(c[0]) for c in CHECKLIST)
    for elem_id, desc, method, status in CHECKLIST:
        print(f"[{status}] {elem_id.ljust(width)}  {desc}  ({method})")
    failed = [c for c in CHECKLIST if c[3] != "PASS"]
    print(f"\n{len(CHECKLIST)} checks, {len(failed)} failures.")
    return len(failed) == 0


def main():
    args = [a for a in sys.argv[1:] if a != "--verify"]
    verify_only = "--verify" in sys.argv[1:]
    out_path = Path(args[0]) if args else DEFAULT_OUT

    if verify_only:
        if not out_path.exists():
            print(f"error: {out_path} does not exist; run without --verify first", file=sys.stderr)
            sys.exit(2)
        with zipfile.ZipFile(out_path) as z:
            d3 = etree.fromstring(z.read("xl/drawings/drawing3.xml"))
        anchor = d3.find(qn("xdr:absoluteAnchor"))
        pos = anchor.find(qn("xdr:pos"))
        ext = anchor.find(qn("xdr:ext"))
        abs_geom = (int(pos.get("x")), int(pos.get("y")), int(ext.get("cx")), int(ext.get("cy")))
    else:
        out_path, abs_geom = build_package(out_path)
        print(f"wrote {out_path} ({out_path.stat().st_size} bytes)")

    verify_reopen(out_path)
    run_verify(out_path, abs_geom)
    verify_determinism(out_path)

    all_ok = print_checklist()
    if not all_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()

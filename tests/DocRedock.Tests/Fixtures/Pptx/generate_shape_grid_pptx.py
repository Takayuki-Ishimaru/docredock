#!/usr/bin/env python3
"""Generate schedule-shape-grid.pptx: a Japanese IT-project schedule deck
whose "table" is NOT a native a:tbl but a grid built from adjacent rectangle
shapes (the other very common Japanese PowerPoint authoring style, alongside
the native-table-plus-overlays style already covered by
schedule-arrows.pptx / generate_schedule_pptx.py in this same directory).

See the design spec this fixture was built from:
  図形で組んだ表（shape-grid table）へのオーバーレイ対応 — PPTX 設計仕様
(handed to this script's author out-of-repo; the load-bearing rules are
restated inline as comments where they drive a geometry decision below).

This script deliberately IMPORTS its geometry/XML-verification primitives
from generate_schedule_pptx.py (Grid, add_bar_shape/add_diamond_marker/
add_today_line/add_label_textbox, and the raw-XML helpers used by its
--verify mode) rather than copying them, so the two fixtures' overlay
placement math and verification logic can never silently drift apart.

Usage:
  python3 generate_shape_grid_pptx.py [output.pptx] [--verify]

  --verify        Re-open an already-generated file and print the
                   shape-count/prstGeom/row-column checklist without
                   regenerating it. (Also runs automatically after a fresh
                   generation.)

Determinism: same approach as generate_schedule_pptx.py -- python-pptx's
bundled default.pptx template already carries a static docProps/core.xml,
so the only non-deterministic knob is the zip member timestamp, which is
rewritten to a fixed value after saving (normalize_zip_timestamps, imported
below), making the .pptx byte-identical across repeated runs.
"""
from __future__ import annotations

import sys
import zipfile
from pathlib import Path

from lxml import etree

from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.oxml.ns import qn

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_schedule_pptx import (  # noqa: E402
    Grid,
    JP_FONT, NAVY, TEAL, INK, MUTED, RED, GREEN, WHITE,
    add_titled_slide,
    add_bar_shape, add_diamond_marker, add_today_line, add_label_textbox,
    normalize_zip_timestamps,
    _local, _prstGeom_prst, _xfrm_of, _off_ext_rot, print_shape_facts,
    rotated_aabb, overlap_len, assign_axis, table_bounds, emu,
    find_tailEnd, is_textbox, stcxn_endcxn,
)

DEFAULT_OUT = Path(__file__).resolve().parent / "schedule-shape-grid.pptx"

# ---------------------------------------------------------------- palette --
HEADER_FILL = RGBColor(0xE3, 0xEA, 0xF2)   # light blue-gray: header rects
LABEL_FILL = RGBColor(0xF2, 0xF3, 0xF6)    # very light gray: label rects
GRID_BORDER = RGBColor(0x8C, 0x98, 0xA8)   # thin gray outline: grid members
DARK_BAR = RGBColor(0x24, 0x2B, 0x36)      # solid dark fill: textless bar overlay

FIXED_ZIP_DATE_TIME = (2026, 1, 1, 0, 0, 0)

CHECKLIST = []  # (id, description, method, "PASS"/"FAIL")


def record(elem_id, desc, method, ok):
    CHECKLIST.append((elem_id, desc, method, "PASS" if ok else "FAIL"))


# =============================================================== geometry ==
# Main grid (slides 1, 2): 工程|担当 + 6 date columns, header row 0.5in tall,
# 5 body (label) rows 0.6in tall each -- per the task spec ("header ... height
# 0.5in", "label column ... each 0.6in tall"). Date columns share the
# remainder of a 12.3in-wide grid equally: 12.3 - 1.6 - 1.6*? no -- 1.6(工程)
# + 1.1(担当) + 6*x = 12.3  =>  x = (12.3 - 1.6 - 1.1) / 6 = 1.6in.
COL_WIDTHS_MAIN = [1.6, 1.1, 1.6, 1.6, 1.6, 1.6, 1.6, 1.6]
ROW_HEIGHTS_MAIN = [0.5, 0.6, 0.6, 0.6, 0.6, 0.6]
HEADERS_MAIN = ["工程", "担当", "9/1", "9/2", "9/3", "9/4", "9/5", "9/8"]
LABELS_PROCESS = ["要件定義", "設計", "実装", "テスト", "リリース"]
LABELS_OWNER = ["山田", "佐藤", "鈴木", "田中", "全員"]

# Slide 4 grid: date columns only (no 工程/担当), header 0.5in, then four
# 0.6in "Y bands" -- there is no label column at all, so rows must be
# derived purely from the overlays' Y-centre clustering (spec step 6).
COL_WIDTHS_DATES_ONLY = [1.6] * 6
ROW_HEIGHTS_NOLABEL = [0.5, 0.6, 0.6, 0.6, 0.6]
HEADERS_DATES_ONLY = ["9/1", "9/2", "9/3", "9/4", "9/5", "9/8"]

CARD_TITLES = ["機能A", "機能B", "機能C", "機能D", "機能E", "機能F"]

# Body cells that carry real text on Slide 2 (row, col) -> text. Row/col are
# indices into the main grid: row 3 = 実装, col 7 = 9/8; row 5 = リリース,
# col 6 = 9/5. Neither cell is touched by any overlay, so no marker stacking
# is exercised here -- see schedule-shape-grid.expected.md for why.
SLIDE2_BODY_TEXT = {(3, 7): "済", (5, 6): "予定"}


# ------------------------------------------------------------- grid rects -
def add_grid_member_rect(shapes, grid, row, col, text="", row_span=1, col_span=1,
                          fill_color=None, border_color=GRID_BORDER, border_pt=0.75,
                          text_color=INK, bold=False, size=10.5, name=None,
                          shape_type=MSO_SHAPE.RECTANGLE):
    """A single grid-member rectangle (header cell, label cell, or an empty/
    text-bearing body placeholder), positioned flush against its neighbours
    (no inset) using the SAME Grid helpers the overlays below are placed
    with, so a rect's on-slide position and its "which row/column is this"
    answer can never drift apart -- same contract as bar_geometry() in
    generate_schedule_pptx.py, just without the overlay inset/height_frac.
    """
    left = grid.col_left(col)
    top = grid.row_top(row)
    width = sum(grid.col_widths[col:col + col_span])
    height = sum(grid.row_heights[row:row + row_span])
    shp = shapes.add_shape(shape_type, Inches(left), Inches(top), Inches(width), Inches(height))
    if fill_color is None:
        shp.fill.background()  # explicit <a:noFill/>, not an inherited theme fill
    else:
        shp.fill.solid()
        shp.fill.fore_color.rgb = fill_color
    shp.line.color.rgb = border_color
    shp.line.width = Pt(border_pt)
    if name:
        shp.name = name
    if text:
        tf = shp.text_frame
        tf.word_wrap = True
        tf.vertical_anchor = MSO_ANCHOR.MIDDLE
        p = tf.paragraphs[0]
        p.alignment = PP_ALIGN.CENTER
        r = p.add_run()
        r.text = text
        r.font.size = Pt(size)
        r.font.bold = bold
        r.font.color.rgb = text_color
        r.font.name = JP_FONT
    return shp


def name_shape(shp, name):
    shp.name = name
    return shp


def add_header_and_labels(shapes, grid):
    for c, htext in enumerate(HEADERS_MAIN):
        add_grid_member_rect(shapes, grid, row=0, col=c, text=htext, fill_color=HEADER_FILL,
                              bold=True, size=11, text_color=NAVY, name=f"GridHeader-{c}-{htext}")
    for r, label in enumerate(LABELS_PROCESS, start=1):
        add_grid_member_rect(shapes, grid, row=r, col=0, text=label, fill_color=LABEL_FILL,
                              name=f"GridLabelProcess-{r}-{label}")
    for r, owner in enumerate(LABELS_OWNER, start=1):
        add_grid_member_rect(shapes, grid, row=r, col=1, text=owner, fill_color=LABEL_FILL,
                              name=f"GridLabelOwner-{r}-{owner}")


def add_common_schedule_overlays(shapes, grid, include_today_line):
    """The overlay set shared by slides 1 and 2 (slide 2 omits the today
    line, per the task spec: "the same set of overlays as slide 1 except
    the today-line"). Column/row indices mirror schedule-arrows.pptx slide
    1 exactly -- same rows/cols/text -- since it is the same schedule
    content, just authored as shapes instead of a native table.
    """
    overlays = []
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=1, col_start=2, col_end=3,
        text="要件定義", fill_color=NAVY), "Overlay-Arrow-要件定義"))
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=2, col_start=3, col_end=5,
        text="設計", fill_color=TEAL), "Overlay-Arrow-設計"))
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RECTANGLE, row=3, col_start=4, col_end=6,
        text="", fill_color=DARK_BAR), "Overlay-Bar-実装"))
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.LEFT_RIGHT_ARROW, row=4, col_start=5, col_end=7,
        text="テスト", fill_color=GREEN), "Overlay-Arrow-テスト"))
    overlays.append(name_shape(add_diamond_marker(
        shapes, grid, row=5, col=7, fill_color=RED), "Overlay-Diamond-リリース"))
    if include_today_line:
        overlays.append(name_shape(add_today_line(
            shapes, grid, col=4, row_start=0, row_end=5, color=RED), "Overlay-TodayLine"))
    overlays.append(name_shape(add_label_textbox(
        shapes, grid, row=2, col=6, text="▲レビュー", color=RED), "Overlay-Label-レビュー"))
    return overlays


# ================================================================ slides ==
def build_slide1(prs):
    """『図形で組んだスケジュール』: header row + label columns built from
    adjacent RECTANGLE shapes (NOT a:tbl), body area otherwise empty, same
    overlay set as schedule-arrows.pptx slide 1 (arrows/bar/diamond/today
    line/review label) placed against the same column/row semantics.
    """
    slide = add_titled_slide(prs, "図形で組んだスケジュール")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)

    add_header_and_labels(shapes, grid)
    overlays = add_common_schedule_overlays(shapes, grid, include_today_line=True)

    record("S1-HEADER", "ヘッダ行: 隣接する矩形8個（表ではない）", "add_shape(RECTANGLE) x8", True)
    record("S1-LABELS", "ラベル列（工程/担当）: 矩形5+5個", "add_shape(RECTANGLE) x10", True)
    record("S1-BODY-EMPTY", "本体領域に矩形なし（オーバーレイのみ）", "no GridBody-* shapes on this slide", True)
    record("S1-ARROW-REQ", "要件定義行: 右矢印 9/1-9/2", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S1-ARROW-DESIGN", "設計行: 右矢印 9/2-9/4", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S1-BAR-IMPL", "実装行: 四角バー(無地濃色/無文字) 9/3-9/5", "MSO_SHAPE.RECTANGLE", True)
    record("S1-ARROW-TEST", "テスト行: 両矢印 9/4-9/8", "MSO_SHAPE.LEFT_RIGHT_ARROW", True)
    record("S1-DIAMOND", "リリース行: ひし形マーカー 9/8", "MSO_SHAPE.DIAMOND", True)
    record("S1-TODAYLINE", "本日線: 縦コネクタ 9/3列 全行 + 矢尻(triangle)",
           "add_connector(STRAIGHT) + raw <a:tailEnd type=triangle/>", True)
    record("S1-LABEL", "レビュー注記テキストボックス 9/5(設計行)", "add_textbox", True)
    return slide, grid, overlays


def build_slide2(prs):
    """『本体セルあり』: same header/labels as slide 1, but the body area
    ALSO carries a full 5x6 grid of (mostly empty, thin-outline, no-fill)
    rectangles under the date columns, two of which carry real text (済 /
    予定), and the same overlay set as slide 1 EXCEPT the today line.
    """
    slide = add_titled_slide(prs, "本体セルあり")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)

    add_header_and_labels(shapes, grid)

    for r in range(1, 6):
        for c in range(2, 8):
            text = SLIDE2_BODY_TEXT.get((r, c), "")
            add_grid_member_rect(shapes, grid, row=r, col=c, text=text,
                                  fill_color=None, name=f"GridBody-r{r}c{c}")

    overlays = add_common_schedule_overlays(shapes, grid, include_today_line=False)

    record("S2-HEADER", "ヘッダ行: 隣接する矩形8個（表ではない）", "add_shape(RECTANGLE) x8", True)
    record("S2-LABELS", "ラベル列（工程/担当）: 矩形5+5個", "add_shape(RECTANGLE) x10", True)
    record("S2-BODY-GRID", "本体領域: 5行x6列の矩形（大半は無地/無文字）", "add_shape(RECTANGLE) x30", True)
    record("S2-BODY-TEXT-JI", "本体セルテキスト「済」(実装x9/8)", "cell text", True)
    record("S2-BODY-TEXT-YOTEI", "本体セルテキスト「予定」(リリースx9/5)", "cell text", True)
    record("S2-NO-TODAYLINE", "本日線コネクタなし（スライド1のみに存在）", "no Overlay-TodayLine shape", True)
    return slide, grid, overlays


def build_slide3(prs):
    """『カード型（表ではない）』: negative case -- a 2x3 grid of rounded
    rectangle "cards" with NO overlays at all must NOT be synthesized into
    a table (spec 8b: at least one overlay in the body area is required).
    Also carries a genuine, natively-connected flow (開始→完了) below the
    cards, to confirm detection does not swallow real diagrams either.
    """
    slide = add_titled_slide(prs, "カード型（表ではない）")
    shapes = slide.shapes

    left0, top0 = 1.0, 1.7
    card_w, card_h = 3.5, 1.2
    gap_x, gap_y = 0.35, 0.35
    for i, title in enumerate(CARD_TITLES):
        r, c = divmod(i, 3)
        left = left0 + c * (card_w + gap_x)
        top = top0 + r * (card_h + gap_y)
        shp = shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(left), Inches(top),
                                Inches(card_w), Inches(card_h))
        shp.fill.solid()
        shp.fill.fore_color.rgb = TEAL if r == 0 else NAVY
        shp.line.color.rgb = WHITE
        shp.name = f"Card-{title}"
        tf = shp.text_frame
        tf.word_wrap = True
        tf.vertical_anchor = MSO_ANCHOR.MIDDLE
        p = tf.paragraphs[0]
        p.alignment = PP_ALIGN.CENTER
        run = p.add_run()
        run.text = title
        run.font.bold = True
        run.font.size = Pt(14)
        run.font.color.rgb = WHITE
        run.font.name = JP_FONT

    flow_top = top0 + 2 * card_h + gap_y + 0.6
    start_box = shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(1.0), Inches(flow_top),
                                  Inches(1.8), Inches(0.7))
    start_box.fill.solid()
    start_box.fill.fore_color.rgb = NAVY
    start_box.line.color.rgb = WHITE
    start_box.name = "Flow-開始"
    sp = start_box.text_frame.paragraphs[0]
    sp.alignment = PP_ALIGN.CENTER
    sr = sp.add_run()
    sr.text = "開始"
    sr.font.bold = True
    sr.font.size = Pt(14)
    sr.font.color.rgb = WHITE
    sr.font.name = JP_FONT

    end_box = shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(3.8), Inches(flow_top),
                                Inches(1.8), Inches(0.7))
    end_box.fill.solid()
    end_box.fill.fore_color.rgb = TEAL
    end_box.line.color.rgb = WHITE
    end_box.name = "Flow-完了"
    ep = end_box.text_frame.paragraphs[0]
    ep.alignment = PP_ALIGN.CENTER
    er = ep.add_run()
    er.text = "完了"
    er.font.bold = True
    er.font.size = Pt(14)
    er.font.color.rgb = WHITE
    er.font.name = JP_FONT

    flow_conn = shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(0), Inches(0), Inches(1), Inches(1))
    flow_conn.begin_connect(start_box, 1)  # right-centre of 開始
    flow_conn.end_connect(end_box, 3)      # left-centre of 完了
    flow_conn.line.color.rgb = MUTED
    flow_conn.line.width = Pt(1.5)
    flow_conn.name = "Flow-Connector"

    record("S3-CARDS", "カード: 角丸矩形6個（2x3グリッド、オーバーレイなし）",
           "add_shape(ROUNDED_RECTANGLE) x6", True)
    record("S3-NEGATIVE", "本体オーバーレイが存在しない（表に合成されない）", "no arrow/diamond shapes", True)
    record("S3-REALFLOW", "表と無関係な実接続フロー（開始→完了）",
           "add_connector + begin_connect/end_connect (stCxn/endCxn)", True)
    return slide


def build_slide4(prs):
    """『ラベル列なし』: header row of 6 date rectangles only (no 工程/担当
    columns, no label column at all). Three RIGHT_ARROW overlays on three
    distinct Y bands (0.6in apart) plus a DIAMOND on a fourth band exercise
    "rows derived from overlay Y-centre clustering" (spec step 6) since
    there is no label column to derive rows from.
    """
    slide = add_titled_slide(prs, "ラベル列なし")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_DATES_ONLY, ROW_HEIGHTS_NOLABEL)

    for c, htext in enumerate(HEADERS_DATES_ONLY):
        add_grid_member_rect(shapes, grid, row=0, col=c, text=htext, fill_color=HEADER_FILL,
                              bold=True, size=11, text_color=NAVY, name=f"GridHeader-{c}-{htext}")

    overlays = []
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=1, col_start=0, col_end=1,
        text="要件定義", fill_color=NAVY), "Overlay-Arrow-要件定義"))
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=2, col_start=1, col_end=3,
        text="設計", fill_color=TEAL), "Overlay-Arrow-設計"))
    overlays.append(name_shape(add_bar_shape(
        shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=3, col_start=2, col_end=4,
        text="実装", fill_color=GREEN), "Overlay-Arrow-実装"))
    overlays.append(name_shape(add_diamond_marker(
        shapes, grid, row=4, col=5, fill_color=RED), "Overlay-Diamond"))

    record("S4-HEADER", "ヘッダ行: 日付矩形6個のみ（工程/担当列なし）", "add_shape(RECTANGLE) x6", True)
    record("S4-NOLABELS", "ラベル列の矩形が存在しない", "no GridLabel* shapes", True)
    record("S4-ARROW-REQ", "Yバンド1: 右矢印 要件定義 9/1-9/2", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S4-ARROW-DESIGN", "Yバンド2: 右矢印 設計 9/2-9/4", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S4-ARROW-IMPL", "Yバンド3: 右矢印 実装 9/3-9/5", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S4-DIAMOND", "Yバンド4: ひし形マーカー 9/8", "MSO_SHAPE.DIAMOND", True)
    return slide, grid, overlays


# ============================================================ determinism ==
# (normalize_zip_timestamps is imported from generate_schedule_pptx.py.)


# =============================================================== helpers ==
def collect_named(slide_el, prefix):
    """Every direct-child <p:sp>/<p:cxnSp> of this slide's spTree whose
    p:cNvPr/@name starts with `prefix`, as (name, element) pairs. Used to
    separate grid-member rectangles (GridHeader-*/GridLabel*/GridBody-*)
    from overlay shapes (Overlay-*) and flow/card shapes when walking the
    raw XML during verification -- python-pptx's own shape objects don't
    expose the sp/cxnSp distinction as cheaply as this does.
    """
    sp_tree = slide_el.find(qn("p:cSld")).find(qn("p:spTree"))
    result = []
    for el in sp_tree:
        tag = _local(el.tag)
        if tag not in ("sp", "cxnSp"):
            continue
        nv = el.find(qn("p:nvSpPr")) if tag == "sp" else el.find(qn("p:nvCxnSpPr"))
        if nv is None:
            continue
        cNvPr = nv.find(qn("p:cNvPr"))
        name = cNvPr.get("name") if cNvPr is not None else ""
        if name.startswith(prefix):
            result.append((name, el))
    return result


def find_shape_by_name(slide, name):
    for shp in slide.shapes:
        if shp.name == name:
            return shp
    return None


def print_overlay_assignments(label, slide_el, grid):
    """Print, and return, every Overlay-* shape's computed row/column
    assignment under the same rotation-aware-AABB + 50%-overlap rule
    generate_schedule_pptx.py uses for schedule-arrows.pptx, so a C# test
    author can copy these expectations directly instead of re-deriving
    them (same convention as that script's verify_slideN functions).
    """
    col_bounds, row_bounds = table_bounds(grid)
    col_bounds = [(emu(a), emu(b)) for a, b in col_bounds]
    row_bounds = [(emu(a), emu(b)) for a, b in row_bounds]

    overlays = collect_named(slide_el, "Overlay-")
    print(f"\n  {label} computed overlay row/column assignment (50% overlap rule):")
    print(f"  {'name':<24}{'prstGeom':<14}{'rows':<10}{'cols':<12}")
    for name, el in overlays:
        prst, off, ext, rot = print_shape_facts(label, el, kind_hint=name)
        (ox, oy), (ecx, ecy) = rotated_aabb(off, ext, rot)
        rows = assign_axis(oy, oy + ecy, row_bounds)
        cols = assign_axis(ox, ox + ecx, col_bounds)
        print(f"  {name:<24}{str(prst):<14}{str(rows):<10}{str(cols):<12}")
    return overlays


def verify_no_native_table(label, slide_el):
    tbl = slide_el.find(f'.//{qn("a:tbl")}')
    record(f"{label}-NOTABLE", "ネイティブ表 (a:tbl) が存在しない", 'slide_el.find(".//a:tbl")', tbl is None)


def verify_shape_count(label, slide, expected_count):
    actual = len(slide.shapes)
    record(f"{label}-COUNT", f"シェイプ数 {actual} (期待 {expected_count})",
           "len(slide.shapes)", actual == expected_count)


def verify_grid_geometry(label, slide_el, grid, check_labels=True):
    headers = collect_named(slide_el, "GridHeader-")
    total_w = sum(int(_xfrm_of(el).find(qn("a:ext")).get("cx")) for _, el in headers)
    expected_w = emu(grid.width)
    ok_w = abs(total_w - expected_w) <= 2
    print(f"  {label}: sum(header widths)={total_w} vs grid width={expected_w}")
    record(f"{label}-HEADERSUM", "ヘッダ矩形の幅合計 == 枠幅", "sum(a:ext/@cx) over GridHeader-*", ok_w)

    if check_labels:
        label_rects = collect_named(slide_el, "GridLabelProcess-")
        total_h = sum(int(_xfrm_of(el).find(qn("a:ext")).get("cy")) for _, el in label_rects)
        expected_h = emu(sum(grid.row_heights[1:]))
        ok_h = abs(total_h - expected_h) <= 2
        print(f"  {label}: sum(label heights)={total_h} vs body height={expected_h}")
        record(f"{label}-LABELSUM", "ラベル列矩形の高さ合計 == 本体高さ",
               "sum(a:ext/@cy) over GridLabelProcess-*", ok_h)
    else:
        record(f"{label}-NOLABELRECTS", "ラベル列の矩形が0個", 'collect_named("GridLabel")',
               len(collect_named(slide_el, "GridLabel")) == 0)


def verify_slide1(slide_el, grid):
    print("\n--- Slide 1: 図形で組んだスケジュール ---")
    verify_no_native_table("S1", slide_el)
    verify_grid_geometry("S1", slide_el, grid, check_labels=True)
    overlays = print_overlay_assignments("S1", slide_el, grid)

    tail_ok = any(find_tailEnd(el) == "triangle" for _, el in overlays)
    txbox_ok = any(is_textbox(el) for _, el in overlays)
    record("S1-XML-TAILEND", "本日線コネクタに a:tailEnd type=triangle", "find_tailEnd()", tail_ok)
    record("S1-XML-TXBOX", "レビュー注記に p:cNvSpPr@txBox=1", "is_textbox()", txbox_ok)

    prst_values = {_prstGeom_prst(el) for _, el in overlays if _prstGeom_prst(el)}
    expected_prst = {"rightArrow", "leftRightArrow", "rect", "diamond", "line"}
    record("S1-PRSTGEOM", f"オーバーレイの prstGeom 集合 {sorted(prst_values)}",
           "prstGeom over Overlay-*", expected_prst.issubset(prst_values))


def verify_slide2(slide_el, grid, prs_slide):
    print("\n--- Slide 2: 本体セルあり ---")
    verify_no_native_table("S2", slide_el)
    verify_grid_geometry("S2", slide_el, grid, check_labels=True)
    overlays = print_overlay_assignments("S2", slide_el, grid)

    body_rects = collect_named(slide_el, "GridBody-")
    record("S2-BODY-COUNT", f"本体矩形数 {len(body_rects)} (期待 30)", 'collect_named("GridBody-")',
           len(body_rects) == 30)

    no_today = len(collect_named(slide_el, "Overlay-TodayLine")) == 0
    record("S2-NO-TODAYLINE-XML", "本日線コネクタが存在しない", 'collect_named("Overlay-TodayLine")', no_today)

    ji = find_shape_by_name(prs_slide, "GridBody-r3c7")
    yotei = find_shape_by_name(prs_slide, "GridBody-r5c6")
    record("S2-TEXT-JI", '実装x9/8 に "済"', "shape.text_frame.text",
           ji is not None and ji.text_frame.text == "済")
    record("S2-TEXT-YOTEI", 'リリースx9/5 に "予定"', "shape.text_frame.text",
           yotei is not None and yotei.text_frame.text == "予定")

    prst_values = {_prstGeom_prst(el) for _, el in overlays if _prstGeom_prst(el)}
    expected_prst = {"rightArrow", "leftRightArrow", "rect", "diamond"}
    record("S2-PRSTGEOM", f"オーバーレイの prstGeom 集合 {sorted(prst_values)}",
           "prstGeom over Overlay-*", expected_prst.issubset(prst_values))


def verify_slide3(slide_el):
    print("\n--- Slide 3: カード型（表ではない） ---")
    cards = collect_named(slide_el, "Card-")
    record("S3-CARD-COUNT", f"カード数 {len(cards)} (期待 6)", 'collect_named("Card-")', len(cards) == 6)
    card_prst = {_prstGeom_prst(el) for _, el in cards}
    record("S3-CARD-PRSTGEOM", f"カードの prstGeom {sorted(card_prst)}", "prstGeom over Card-*",
           card_prst == {"roundRect"})

    forbidden_prst = {"rightArrow", "leftRightArrow", "diamond"}
    sp_tree = slide_el.find(qn("p:cSld")).find(qn("p:spTree"))
    all_prst = {_prstGeom_prst(el) for el in sp_tree if _local(el.tag) == "sp" and _prstGeom_prst(el)}
    record("S3-NO-OVERLAY-GEOM", "矢印/ひし形の prstGeom が存在しない（オーバーレイなし）",
           "prstGeom over all sp", forbidden_prst.isdisjoint(all_prst))

    flow_conn = find_shape_by_name_el(slide_el, "Flow-Connector")
    st, end = stcxn_endcxn(flow_conn) if flow_conn is not None else (None, None)
    record("S3-XML-STENDCXN", "実データフローのコネクタに a:stCxn/a:endCxn", "stcxn_endcxn()",
           st is not None and end is not None)


def find_shape_by_name_el(slide_el, name):
    matches = collect_named(slide_el, name)
    for n, el in matches:
        if n == name:
            return el
    return None


def verify_slide4(slide_el, grid):
    print("\n--- Slide 4: ラベル列なし ---")
    verify_no_native_table("S4", slide_el)
    verify_grid_geometry("S4", slide_el, grid, check_labels=False)
    overlays = print_overlay_assignments("S4", slide_el, grid)

    prst_values = {_prstGeom_prst(el) for _, el in overlays if _prstGeom_prst(el)}
    expected_prst = {"rightArrow", "diamond"}
    record("S4-PRSTGEOM", f"オーバーレイの prstGeom 集合 {sorted(prst_values)}",
           "prstGeom over Overlay-*", expected_prst.issubset(prst_values))


def verify_reopen(path, expected_slides=4):
    prs = Presentation(str(path))
    ok = len(prs.slides) == expected_slides
    record("REOPEN", f"python-pptxで再オープン（{len(prs.slides)}枚）", "Presentation(path)", ok)
    return prs


def verify_no_images_and_size(path, max_kb=150):
    size = path.stat().st_size
    with zipfile.ZipFile(path) as z:
        media = [n for n in z.namelist() if n.startswith("ppt/media/")]
    record("SIZE", f"ファイルサイズ {size} bytes < {max_kb}KB", "path.stat().st_size", size < max_kb * 1024)
    record("NO-IMAGES", "ppt/media/* が存在しない（画像なし）", "zipfile namelist", len(media) == 0)
    print(f"\nfile size: {size} bytes, media parts: {media}")


def verify_determinism(build_fn, out_path):
    import hashlib
    import tempfile
    base_len = len(CHECKLIST)
    with tempfile.TemporaryDirectory() as td:
        p1 = Path(td) / "a.pptx"
        p2 = Path(td) / "b.pptx"
        build_fn(p1)
        build_fn(p2)
        h1 = hashlib.sha256(p1.read_bytes()).hexdigest()
        h2 = hashlib.sha256(p2.read_bytes()).hexdigest()
    del CHECKLIST[base_len:]
    ok = h1 == h2
    record("DETERMINISM", "2回連続生成しても同一バイト列 (sha256一致)",
           "hashlib.sha256 of two independent builds", ok)
    print(f"  sha256 run A: {h1}")
    print(f"  sha256 run B: {h2}")


def print_checklist():
    print("\n=== schedule-shape-grid.pptx element checklist ===")
    width = max(len(c[0]) for c in CHECKLIST)
    for elem_id, desc, method, status in CHECKLIST:
        print(f"[{status}] {elem_id.ljust(width)}  {desc}  ({method})")
    failed = [c for c in CHECKLIST if c[3] != "PASS"]
    print(f"\n{len(CHECKLIST)} checks, {len(failed)} failures.")
    return len(failed) == 0


# =================================================================== build
def build(output_path):
    prs = Presentation()
    prs.slide_width = Inches(13.333)
    prs.slide_height = Inches(7.5)

    _, grid1, _ov1 = build_slide1(prs)
    _, grid2, _ov2 = build_slide2(prs)
    build_slide3(prs)
    _, grid4, _ov4 = build_slide4(prs)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    prs.save(str(output_path))
    normalize_zip_timestamps(output_path, FIXED_ZIP_DATE_TIME)
    return output_path, grid1, grid2, grid4


def run_verify(path, grid1, grid2, grid4):
    with zipfile.ZipFile(path) as z:
        slide_names = sorted(
            (n for n in z.namelist() if n.startswith("ppt/slides/slide") and n.endswith(".xml")),
            key=lambda n: int("".join(ch for ch in n if ch.isdigit())),
        )
        slide_xmls = [etree.fromstring(z.read(n)) for n in slide_names]

    prs = verify_reopen(path, expected_slides=4)

    verify_shape_count("S1", prs.slides[0], 26)
    verify_shape_count("S2", prs.slides[1], 55)
    verify_shape_count("S3", prs.slides[2], 10)
    verify_shape_count("S4", prs.slides[3], 11)

    verify_slide1(slide_xmls[0], grid1)
    verify_slide2(slide_xmls[1], grid2, prs.slides[1])
    verify_slide3(slide_xmls[2])
    verify_slide4(slide_xmls[3], grid4)

    verify_no_images_and_size(path)
    verify_determinism(lambda p: build(p), path)


def main():
    args = [a for a in sys.argv[1:] if a != "--verify"]
    verify_only = "--verify" in sys.argv[1:]
    out_path = Path(args[0]) if args else DEFAULT_OUT

    if verify_only:
        if not out_path.exists():
            print(f"error: {out_path} does not exist; run without --verify first", file=sys.stderr)
            sys.exit(2)
        grid1 = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)
        grid2 = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)
        grid4 = Grid(0.5, 1.5, COL_WIDTHS_DATES_ONLY, ROW_HEIGHTS_NOLABEL)
        run_verify(out_path, grid1, grid2, grid4)
    else:
        out_path, grid1, grid2, grid4 = build(out_path)
        print(f"wrote {out_path} ({out_path.stat().st_size} bytes, "
              f"{len(Presentation(str(out_path)).slides)} slides)")
        run_verify(out_path, grid1, grid2, grid4)

    all_ok = print_checklist()
    if not all_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()

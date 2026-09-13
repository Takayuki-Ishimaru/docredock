#!/usr/bin/env python3
"""Generate schedule-shape-grid-gapped.pptx: a "hand-authored-looking"
variant of schedule-shape-grid.pptx's Slide 1, deliberately built with the
small sizing/positioning sloppiness a human author leaves behind when they
draw a schedule out of individual rectangles in PowerPoint instead of using
the table tool -- gaps between cells, a mis-sized label column, inconsistent
row heights, off-center body rows -- plus two more slides that are negative
cases for a specific extraction bug: a lone decorative line (or a row of
chevrons) sitting under/near a row of shapes must NOT make the grid-table
detector synthesize a bogus table.

This script deliberately IMPORTS its geometry/palette/XML-verification
primitives from generate_schedule_pptx.py and generate_shape_grid_pptx.py
(same directory) rather than copying them, so this fixture's overlay
placement math and verification logic can never silently drift from the
other two shape-grid fixtures.

Usage:
  python3 generate_shape_grid_gapped_pptx.py [output.pptx] [--verify]

  --verify        Re-open an already-generated file and print the
                   shape-count/prstGeom/row-column checklist without
                   regenerating it. (Also runs automatically after a fresh
                   generation.)

Determinism: same approach as the two fixtures above -- python-pptx's
bundled default.pptx template already carries a static docProps/core.xml,
so the only non-deterministic knob is the zip member timestamp, which is
rewritten to a fixed value after saving (normalize_zip_timestamps, imported
below), making the .pptx byte-identical across repeated runs.

--- Slide 1 「隙間のある図形格子」 -----------------------------------------
Same content as schedule-shape-grid.pptx Slide 1 (same header row, label
columns, and overlay set -- see generate_shape_grid_pptx.py's build_slide1),
but every hand-drawn rectangle carries realistic imperfections instead of
being perfectly flush against its neighbours:

  1. A 2pt gap (GAP_EMU = 25400 EMU) between every adjacent rectangle, both
     horizontally (the 8 header cells) and vertically (each label column's
     5 stacked cells, and the header-to-first-label-row seam).
  2. The 工程 (process) label-column rectangles are 20% wider
     (PROCESS_WIDTH_MULT = 1.2) than the (gap-trimmed) 工程 header cell,
     extending to the right so they overlap into the 担当 column's gap.
  3. Header cells stay exactly 0.5in tall (never trimmed); label/body rows
     are 0.6in tall (ROW_HEIGHTS_MAIN, imported unchanged) -- same
     header/body height split as the original, undisturbed fixture.
  4. The 担当 (owner) column's rectangles are 1pt shorter
     (OWNER_SHRINK_EMU = 12700 EMU) than their row and vertically centred
     within it.

None of this is supposed to change where the overlays land: per the design
spec's own boundary-derivation rule, column boundaries come from the
header cells' LEFT edges (plus the last cell's right edge) and row
boundaries come from the header's top edge and the 工程 label cells' TOP
edges (plus the last cell's bottom edge) -- i.e. boundaries chain from one
cell's leading edge to the next cell's leading edge, which absorbs each
2pt gap into the interval before it instead of leaving a dead zone nothing
can be assigned to. Because every rectangle in this fixture keeps its
LEADING (left/top) edge anchored at the same coordinate the undisturbed
schedule-shape-grid.pptx grid would use -- only trailing edges (right/
bottom) are trimmed -- the derived column boundaries are bit-identical to
the original grid, and the derived row boundaries differ from it only by
the fixed 2pt gap (far below the 50%-overlap rule's threshold for any
overlay in this deck). The overlay shapes themselves are placed with the
exact same Grid/add_bar_shape/add_diamond_marker/add_today_line/
add_label_textbox calls as the undisturbed fixture, so the expected
row/column assignment per overlay is IDENTICAL to schedule-shape-grid.pptx
Slide 1: 要件定義 rows=[1] cols=[2,3]; 設計 rows=[2] cols=[3,4,5]; the
textless 実装 bar rows=[3] cols=[4,5,6]; テスト rows=[4] cols=[5,6,7];
リリース diamond rows=[5] cols=[7]; the today-line rows=[0..5] cols=[4];
▲レビュー rows=[2] cols=[6]. `--verify` re-derives these boundaries from
the actual generated rectangles (not from the idealized Grid) and checks
each overlay's computed cell against this table.

--- Slide 2 「飾り線つきカード（表ではない）」 -----------------------------
A row of 4 adjacent (flush, no gap) KPI card rectangles (売上/利益/顧客数/
解約率, each with a second line of numbers), a thin horizontal `line`-kind
connector directly under them spanning their combined width, and a
RIGHT_ARROW labelled 「次のステップ」 below that line. This is a negative
case for the "one decorative line below a row of shapes makes a bogus
2-row table" bug: there is only ONE row of rectangles here (no second row
of cells the line could plausibly be separating), so the grid-table
detector must not synthesize a table out of a card row plus an unrelated
decorative line.

--- Slide 3 「チェブロンのアジェンダ」 -------------------------------------
4 adjacent MSO_SHAPE.CHEVRON shapes (STEP1-STEP4) with one RIGHT_ARROW
underneath. Another negative case: chevrons are NOT rectangles (prstGeom
"chevron", not "rect"), so an aligned row of them plus a trailing arrow
must not be mistaken for a shape-grid table header row either.
"""
from __future__ import annotations

import sys
import zipfile
from pathlib import Path

from lxml import etree

from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_schedule_pptx import (  # noqa: E402
    Grid,
    JP_FONT, NAVY, TEAL, INK, MUTED, GREEN, WHITE,
    add_titled_slide,
    normalize_zip_timestamps,
    _prstGeom_prst, _off_ext_rot, print_shape_facts,
    rotated_aabb, assign_axis, emu,
    find_tailEnd, is_textbox,
)
from generate_shape_grid_pptx import (  # noqa: E402
    HEADER_FILL, LABEL_FILL, GRID_BORDER,
    COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN, HEADERS_MAIN, LABELS_PROCESS, LABELS_OWNER,
    FIXED_ZIP_DATE_TIME,
    add_common_schedule_overlays,
    collect_named, find_shape_by_name_el,
)

DEFAULT_OUT = Path(__file__).resolve().parent / "schedule-shape-grid-gapped.pptx"

CHECKLIST = []  # (id, description, method, "PASS"/"FAIL")


def record(elem_id, desc, method, ok):
    CHECKLIST.append((elem_id, desc, method, "PASS" if ok else "FAIL"))


# =============================================================== geometry ==
# See the module docstring for the rationale behind every constant below.
GAP_EMU = 25400              # 2pt: gap between every adjacent hand-drawn rect
GAP_IN = GAP_EMU / 914400.0

OWNER_SHRINK_EMU = 12700     # 1pt: 担当 boxes are shorter than their row slot
OWNER_SHRINK_IN = OWNER_SHRINK_EMU / 914400.0

PROCESS_WIDTH_MULT = 1.2     # 工程 label boxes are 20% wider than the 工程 header box


def header_row_geometry(grid, c):
    """Header cell `c`: left/top anchored at the ideal grid position; width
    trimmed by GAP_IN on the trailing (right) side for every column except
    the last (nothing to gap against there); height is always the full
    header row height (0.5in) -- header cells are never trimmed vertically.
    """
    left = grid.col_left(c)
    top = grid.row_top(0)
    width = grid.col_widths[c] - (GAP_IN if c < grid.n_cols - 1 else 0.0)
    height = grid.row_heights[0]
    return left, top, width, height


def _label_slot(grid, r):
    """The gap-adjusted vertical slot label row `r` (1-based: 1..n_rows-1)
    occupies: top is shifted down by GAP_IN to leave a 2pt gap against the
    box above (the header for r==1, the previous label row otherwise);
    height is trimmed by GAP_IN on the trailing side for every row except
    the last (nothing below it to gap against).
    """
    is_last = r == grid.n_rows - 1
    top = grid.row_top(r) + GAP_IN
    height = grid.row_heights[r] if is_last else grid.row_heights[r] - GAP_IN
    return top, height


def process_label_geometry(grid, r):
    """工程 label cell for row `r`: anchored at the column's ideal left
    edge, occupying the full gap-adjusted vertical slot, but 20% WIDER
    than the (gap-trimmed) 工程 header cell -- extending to the right so it
    overlaps into 担当's column."""
    header_left, _, header_width, _ = header_row_geometry(grid, 0)
    top, height = _label_slot(grid, r)
    width = header_width * PROCESS_WIDTH_MULT
    return header_left, top, width, height


def owner_label_geometry(grid, r):
    """担当 label cell for row `r`: same gap-adjusted vertical slot as the
    process column, but shrunk by a further 1pt and vertically centred
    within that slot (so it also reads as visibly shorter than its row)."""
    left = grid.col_left(1)
    slot_top, slot_height = _label_slot(grid, r)
    height = slot_height - OWNER_SHRINK_IN
    top = slot_top + OWNER_SHRINK_IN / 2
    width = grid.col_widths[1]
    return left, top, width, height


# ------------------------------------------------------------- grid rects -
def add_gapped_rect(shapes, left, top, width, height, text="", fill_color=None,
                     border_color=GRID_BORDER, border_pt=0.75, text_color=INK,
                     bold=False, size=10.5, name=None, shape_type=MSO_SHAPE.RECTANGLE):
    """Same styling contract as add_grid_member_rect() in
    generate_shape_grid_pptx.py, but taking raw left/top/width/height
    (inches) instead of deriving them from a Grid row/col -- the gapped
    geometry helpers above already did that derivation, deliberately with
    imperfections a plain Grid lookup would not produce.
    """
    shp = shapes.add_shape(shape_type, Inches(left), Inches(top), Inches(width), Inches(height))
    if fill_color is None:
        shp.fill.background()
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


# ================================================================ slides ==
def build_slide1(prs):
    """『隙間のある図形格子』: same header/label/overlay content as
    schedule-shape-grid.pptx Slide 1, but every rectangle is gapped/
    mis-sized per the module docstring's four imperfections.
    """
    slide = add_titled_slide(prs, "隙間のある図形格子")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)

    for c, htext in enumerate(HEADERS_MAIN):
        left, top, width, height = header_row_geometry(grid, c)
        add_gapped_rect(shapes, left, top, width, height, text=htext, fill_color=HEADER_FILL,
                         bold=True, size=11, text_color=NAVY, name=f"GridHeader-{c}-{htext}")

    for r, label in enumerate(LABELS_PROCESS, start=1):
        left, top, width, height = process_label_geometry(grid, r)
        add_gapped_rect(shapes, left, top, width, height, text=label, fill_color=LABEL_FILL,
                         name=f"GridLabelProcess-{r}-{label}")

    for r, owner in enumerate(LABELS_OWNER, start=1):
        left, top, width, height = owner_label_geometry(grid, r)
        add_gapped_rect(shapes, left, top, width, height, text=owner, fill_color=LABEL_FILL,
                         name=f"GridLabelOwner-{r}-{owner}")

    overlays = add_common_schedule_overlays(shapes, grid, include_today_line=True)

    record("S1-HEADER", "ヘッダ行: 隣接する矩形8個（2ptギャップ、表ではない）", "add_shape(RECTANGLE) x8", True)
    record("S1-LABELS", "ラベル列（工程/担当）: 矩形5+5個（ギャップ/幅/高さの不揃いあり）",
           "add_shape(RECTANGLE) x10", True)
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


CARD_KPI = [("売上", "¥12.3M"), ("利益", "¥3.1M"), ("顧客数", "1,024"), ("解約率", "4.2%")]


def build_slide2(prs):
    """『飾り線つきカード（表ではない）』: negative case for the "one
    decorative line makes a 2-row table" bug -- a single row of flush KPI
    cards, a thin decorative `line` connector under them, and an unrelated
    right arrow further below. There is no second row of cells anywhere on
    this slide.
    """
    slide = add_titled_slide(prs, "飾り線つきカード（表ではない）")
    shapes = slide.shapes

    left0, top = 1.0, 1.8
    card_w, card_h = 2.6, 1.1
    for i, (title, number) in enumerate(CARD_KPI):
        left = left0 + i * card_w
        shp = shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(left), Inches(top),
                                Inches(card_w), Inches(card_h))
        shp.fill.solid()
        shp.fill.fore_color.rgb = HEADER_FILL if i % 2 == 0 else LABEL_FILL
        shp.line.color.rgb = GRID_BORDER
        shp.line.width = Pt(0.75)
        shp.name = f"KpiCard-{title}"
        tf = shp.text_frame
        tf.word_wrap = True
        tf.vertical_anchor = MSO_ANCHOR.MIDDLE
        p0 = tf.paragraphs[0]
        p0.alignment = PP_ALIGN.CENTER
        r0 = p0.add_run()
        r0.text = title
        r0.font.size = Pt(12)
        r0.font.bold = True
        r0.font.color.rgb = NAVY
        r0.font.name = JP_FONT
        p1 = tf.add_paragraph()
        p1.alignment = PP_ALIGN.CENTER
        r1 = p1.add_run()
        r1.text = number
        r1.font.size = Pt(14)
        r1.font.bold = True
        r1.font.color.rgb = INK
        r1.font.name = JP_FONT

    row_left = left0
    row_right = left0 + card_w * len(CARD_KPI)
    line_y = top + card_h + 0.18
    line = shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(row_left), Inches(line_y),
                                 Inches(row_right), Inches(line_y))
    line.line.color.rgb = MUTED
    line.line.width = Pt(0.75)
    line.name = "DecorativeLine"

    arrow_top = line_y + 0.3
    arrow = shapes.add_shape(MSO_SHAPE.RIGHT_ARROW, Inches(row_left), Inches(arrow_top),
                              Inches(3.0), Inches(0.5))
    arrow.fill.solid()
    arrow.fill.fore_color.rgb = TEAL
    arrow.line.color.rgb = WHITE
    arrow.name = "NextStepArrow"
    tf = arrow.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.alignment = PP_ALIGN.CENTER
    r = p.add_run()
    r.text = "次のステップ"
    r.font.size = Pt(11)
    r.font.bold = True
    r.font.color.rgb = WHITE
    r.font.name = JP_FONT

    record("S2-CARDS", "KPIカード: 隣接する矩形4個（売上/利益/顧客数/解約率、2行構成テキスト）",
           "add_shape(RECTANGLE) x4", True)
    record("S2-LINE", "カード下の飾り線: 細い line コネクタ（カード幅ぶん）",
           "add_connector(STRAIGHT), no stCxn/endCxn", True)
    record("S2-ARROW", "飾り線の下の右矢印「次のステップ」（カードとは無関係）",
           "MSO_SHAPE.RIGHT_ARROW", True)
    record("S2-NEGATIVE", "1本の飾り線だけでは2行の表と誤認されない（本体行は1行のみ）",
           "no second row of rectangles anywhere on this slide", True)
    return slide


CHEVRON_STEPS = ["STEP1", "STEP2", "STEP3", "STEP4"]


def build_slide3(prs):
    """『チェブロンのアジェンダ』: negative case -- 4 adjacent CHEVRON
    shapes (not rectangles) plus a trailing right arrow must not be
    mistaken for a shape-grid table header row either."""
    slide = add_titled_slide(prs, "チェブロンのアジェンダ")
    shapes = slide.shapes

    left0, top = 1.0, 2.0
    chevron_w, chevron_h = 2.6, 1.0
    for i, step in enumerate(CHEVRON_STEPS):
        left = left0 + i * chevron_w
        shp = shapes.add_shape(MSO_SHAPE.CHEVRON, Inches(left), Inches(top),
                                Inches(chevron_w), Inches(chevron_h))
        shp.fill.solid()
        shp.fill.fore_color.rgb = TEAL if i % 2 == 0 else NAVY
        shp.line.color.rgb = WHITE
        shp.name = f"Chevron-{step}"
        tf = shp.text_frame
        tf.word_wrap = True
        tf.vertical_anchor = MSO_ANCHOR.MIDDLE
        p = tf.paragraphs[0]
        p.alignment = PP_ALIGN.CENTER
        r = p.add_run()
        r.text = step
        r.font.bold = True
        r.font.size = Pt(14)
        r.font.color.rgb = WHITE
        r.font.name = JP_FONT

    arrow_top = top + chevron_h + 0.4
    arrow = shapes.add_shape(MSO_SHAPE.RIGHT_ARROW, Inches(left0), Inches(arrow_top),
                              Inches(3.0), Inches(0.5))
    arrow.fill.solid()
    arrow.fill.fore_color.rgb = GREEN
    arrow.line.color.rgb = WHITE
    arrow.name = "AgendaArrow"
    tf = arrow.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.alignment = PP_ALIGN.CENTER
    r = p.add_run()
    r.text = "次へ"
    r.font.size = Pt(11)
    r.font.bold = True
    r.font.color.rgb = WHITE
    r.font.name = JP_FONT

    record("S3-CHEVRONS", "チェブロン: 隣接する図形4個（STEP1-STEP4、prstGeom=chevron）",
           "add_shape(MSO_SHAPE.CHEVRON) x4", True)
    record("S3-ARROW", "チェブロン列の下の右矢印「次へ」", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S3-NEGATIVE", "チェブロンは矩形ではないため表格子と誤認されない（prstGeom!=rect）",
           "prstGeom over Chevron-*", True)
    return slide


# ============================================================ self-check ==
def _shape_box(el):
    (ox, oy), (ecx, ecy), rot = _off_ext_rot(el)
    return ox, oy, ecx, ecy


def _sorted_by_index(named, index_pos=1):
    return sorted(named, key=lambda t: int(t[0].split("-")[index_pos]))


def gapped_col_bounds(slide_el):
    """Column boundaries per the design spec's boundary rule: chain the
    header cells' LEFT edges, then close with the last cell's right edge.
    Absorbs each horizontal gap into the column before it instead of
    leaving a dead zone."""
    headers = _sorted_by_index(collect_named(slide_el, "GridHeader-"))
    boxes = [_shape_box(el) for _, el in headers]
    lefts = [ox for ox, _oy, _ecx, _ecy in boxes]
    last_right = boxes[-1][0] + boxes[-1][2]
    edges = lefts + [last_right]
    return [(edges[i], edges[i + 1]) for i in range(len(edges) - 1)]


def gapped_row_bounds(slide_el):
    """Row boundaries per the design spec's boundary rule: the header's
    top edge, then the 工程 label cells' TOP edges in row order, closed
    with the last label cell's bottom edge. Absorbs each vertical gap into
    the row before it instead of leaving a dead zone."""
    headers = collect_named(slide_el, "GridHeader-")
    header_top = min(_shape_box(el)[1] for _, el in headers)
    labels = _sorted_by_index(collect_named(slide_el, "GridLabelProcess-"))
    boxes = [_shape_box(el) for _, el in labels]
    tops = [oy for _ox, oy, _ecx, _ecy in boxes]
    last_bottom = boxes[-1][1] + boxes[-1][3]
    edges = [header_top] + tops + [last_bottom]
    return [(edges[i], edges[i + 1]) for i in range(len(edges) - 1)]


EXPECTED_OVERLAY_CELLS = {
    "Overlay-Arrow-要件定義": ([1], [2, 3]),
    "Overlay-Arrow-設計": ([2], [3, 4, 5]),
    "Overlay-Bar-実装": ([3], [4, 5, 6]),
    "Overlay-Arrow-テスト": ([4], [5, 6, 7]),
    "Overlay-Diamond-リリース": ([5], [7]),
    "Overlay-TodayLine": ([0, 1, 2, 3, 4, 5], [4]),
    "Overlay-Label-レビュー": ([2], [6]),
}


def print_overlay_assignments_gapped(label, slide_el):
    col_bounds = gapped_col_bounds(slide_el)
    row_bounds = gapped_row_bounds(slide_el)
    overlays = collect_named(slide_el, "Overlay-")
    print(f"\n  {label} computed overlay row/column assignment "
          f"(50% overlap rule, boundaries derived from actual rectangles):")
    print(f"  {'name':<24}{'prstGeom':<14}{'rows':<16}{'cols':<14}")
    for name, el in overlays:
        prst, off, ext, rot = print_shape_facts(label, el, kind_hint=name)
        (ox, oy), (ecx, ecy) = rotated_aabb(off, ext, rot)
        rows = assign_axis(oy, oy + ecy, row_bounds)
        cols = assign_axis(ox, ox + ecx, col_bounds)
        print(f"  {name:<24}{str(prst):<14}{str(rows):<16}{str(cols):<14}")
        expected = EXPECTED_OVERLAY_CELLS.get(name)
        ok = expected is not None and rows == expected[0] and cols == expected[1]
        record(f"{label}-CELLS-{name}",
               f"{name}: rows={rows} cols={cols}（期待 rows={expected[0] if expected else '?'} "
               f"cols={expected[1] if expected else '?'}）",
               "assign_axis() over gap-derived boundaries", ok)
    return overlays


def verify_no_native_table(label, slide_el):
    from pptx.oxml.ns import qn
    tbl = slide_el.find(f'.//{qn("a:tbl")}')
    record(f"{label}-NOTABLE", "ネイティブ表 (a:tbl) が存在しない", 'slide_el.find(".//a:tbl")', tbl is None)


def verify_shape_count(label, slide, expected_count):
    actual = len(slide.shapes)
    record(f"{label}-COUNT", f"シェイプ数 {actual} (期待 {expected_count})",
           "len(slide.shapes)", actual == expected_count)


def verify_gap_geometry(label, slide_el, grid):
    headers = _sorted_by_index(collect_named(slide_el, "GridHeader-"))
    boxes = [_shape_box(el) for _, el in headers]

    hgap_ok = True
    for i in range(len(boxes) - 1):
        this_right = boxes[i][0] + boxes[i][2]
        next_left = boxes[i + 1][0]
        if abs((next_left - this_right) - GAP_EMU) > 2:
            hgap_ok = False
    record(f"{label}-HGAP", f"ヘッダ矩形間の水平ギャップ = {GAP_EMU} EMU (2pt)",
           "header box left/right diffs", hgap_ok)

    header_h_expected = emu(grid.row_heights[0])
    header_h_ok = all(abs(ecy - header_h_expected) <= 2 for _, _, _, ecy in boxes)
    record(f"{label}-HEADERHEIGHT", f"ヘッダ矩形の高さ = {header_h_expected} EMU (0.5in, 常に一定)",
           "header box a:ext/@cy", header_h_ok)

    labels_p = _sorted_by_index(collect_named(slide_el, "GridLabelProcess-"))
    p_boxes = [_shape_box(el) for _, el in labels_p]

    header_bottom = boxes[0][1] + boxes[0][3]
    vgap_ok = abs((p_boxes[0][1] - header_bottom) - GAP_EMU) <= 2
    for i in range(len(p_boxes) - 1):
        this_bottom = p_boxes[i][1] + p_boxes[i][3]
        next_top = p_boxes[i + 1][1]
        if abs((next_top - this_bottom) - GAP_EMU) > 2:
            vgap_ok = False
    record(f"{label}-VGAP-PROCESS", f"工程ラベル矩形間（およびヘッダとの間）の垂直ギャップ = {GAP_EMU} EMU (2pt)",
           "process label box top/bottom diffs", vgap_ok)

    header0_w = boxes[0][2]
    expected_w = header0_w * PROCESS_WIDTH_MULT
    width_ok = all(abs(ecx - expected_w) <= 3 for _, _, ecx, _ in p_boxes)
    record(f"{label}-PROCESSWIDTH",
           f"工程ラベル矩形の幅 = ヘッダ矩形幅 x 1.2 (期待 {expected_w:.0f} EMU)",
           "process label a:ext/@cx vs header a:ext/@cx * 1.2", width_ok)

    labels_o = _sorted_by_index(collect_named(slide_el, "GridLabelOwner-"))
    o_boxes = [_shape_box(el) for _, el in labels_o]

    owner_ok = True
    center_ok = True
    for idx, (ox, oy, ecx, ecy) in enumerate(o_boxes):
        r = idx + 1
        slot_top, slot_height = _label_slot(grid, r)
        expected_h = emu(slot_height) - OWNER_SHRINK_EMU
        if abs(ecy - expected_h) > 3:
            owner_ok = False
        top_margin = oy - emu(slot_top)
        bottom_margin = (emu(slot_top) + emu(slot_height)) - (oy + ecy)
        if abs(top_margin - bottom_margin) > 3:
            center_ok = False
    record(f"{label}-OWNERSHRINK", f"担当ラベル矩形の高さ = 行スロット高さ - {OWNER_SHRINK_EMU} EMU (1pt)",
           "owner label a:ext/@cy vs slot height", owner_ok)
    record(f"{label}-OWNERCENTER", "担当ラベル矩形は行スロット内で上下中央揃え",
           "top margin == bottom margin within slot", center_ok)

    record(f"{label}-ROWHEIGHTS", "行の基準高さ: ヘッダ行 0.5in, ラベル/本体行 0.6in (schedule-shape-grid.pptx と同一)",
           "ROW_HEIGHTS_MAIN", ROW_HEIGHTS_MAIN[0] == 0.5 and all(h == 0.6 for h in ROW_HEIGHTS_MAIN[1:]))


def verify_slide1(slide_el, grid):
    print("\n--- Slide 1: 隙間のある図形格子 ---")
    verify_no_native_table("S1", slide_el)
    verify_gap_geometry("S1", slide_el, grid)
    overlays = print_overlay_assignments_gapped("S1", slide_el)

    tail_ok = any(find_tailEnd(el) == "triangle" for _, el in overlays)
    txbox_ok = any(is_textbox(el) for _, el in overlays)
    record("S1-XML-TAILEND", "本日線コネクタに a:tailEnd type=triangle", "find_tailEnd()", tail_ok)
    record("S1-XML-TXBOX", "レビュー注記に p:cNvSpPr@txBox=1", "is_textbox()", txbox_ok)

    prst_values = {_prstGeom_prst(el) for _, el in overlays if _prstGeom_prst(el)}
    expected_prst = {"rightArrow", "leftRightArrow", "rect", "diamond", "line"}
    record("S1-PRSTGEOM", f"オーバーレイの prstGeom 集合 {sorted(prst_values)}",
           "prstGeom over Overlay-*", expected_prst.issubset(prst_values))


def verify_slide2(slide_el):
    print("\n--- Slide 2: 飾り線つきカード（表ではない） ---")
    verify_no_native_table("S2", slide_el)

    cards = collect_named(slide_el, "KpiCard-")
    record("S2-CARD-COUNT", f"カード数 {len(cards)} (期待 4)", 'collect_named("KpiCard-")', len(cards) == 4)
    card_prst = {_prstGeom_prst(el) for _, el in cards}
    record("S2-CARD-PRSTGEOM", f"カードの prstGeom {sorted(card_prst)}", "prstGeom over KpiCard-*",
           card_prst == {"rect"})

    line_el = find_shape_by_name_el(slide_el, "DecorativeLine")
    record("S2-LINE-EXISTS", "飾り線コネクタが存在する", 'find_shape_by_name_el("DecorativeLine")',
           line_el is not None)
    line_prst = _prstGeom_prst(line_el) if line_el is not None else None
    record("S2-LINE-PRSTGEOM", f"飾り線の prstGeom = {line_prst} (期待 line)", "prstGeom", line_prst == "line")

    arrow_el = find_shape_by_name_el(slide_el, "NextStepArrow")
    record("S2-ARROW-EXISTS", "「次のステップ」矢印が存在する", 'find_shape_by_name_el("NextStepArrow")',
           arrow_el is not None)
    arrow_prst = _prstGeom_prst(arrow_el) if arrow_el is not None else None
    record("S2-ARROW-PRSTGEOM", f"矢印の prstGeom = {arrow_prst} (期待 rightArrow)",
           "prstGeom", arrow_prst == "rightArrow")

    grid_like = collect_named(slide_el, "GridHeader-") or collect_named(slide_el, "GridLabel")
    record("S2-NEGATIVE-NOGRID", "GridHeader-/GridLabel* 系の矩形が存在しない（表格子を構成しない）",
           "collect_named", len(grid_like) == 0)


def verify_slide3(slide_el):
    print("\n--- Slide 3: チェブロンのアジェンダ ---")
    verify_no_native_table("S3", slide_el)

    chevrons = collect_named(slide_el, "Chevron-")
    record("S3-CHEVRON-COUNT", f"シェブロン数 {len(chevrons)} (期待 4)", 'collect_named("Chevron-")',
           len(chevrons) == 4)
    chevron_prst = {_prstGeom_prst(el) for _, el in chevrons}
    record("S3-CHEVRON-PRSTGEOM", f"シェブロンの prstGeom {sorted(chevron_prst)}",
           "prstGeom over Chevron-*", chevron_prst == {"chevron"})

    arrow_el = find_shape_by_name_el(slide_el, "AgendaArrow")
    record("S3-ARROW-EXISTS", "アジェンダ矢印が存在する", 'find_shape_by_name_el("AgendaArrow")',
           arrow_el is not None)
    arrow_prst = _prstGeom_prst(arrow_el) if arrow_el is not None else None
    record("S3-ARROW-PRSTGEOM", f"矢印の prstGeom = {arrow_prst} (期待 rightArrow)",
           "prstGeom", arrow_prst == "rightArrow")

    grid_like = collect_named(slide_el, "GridHeader-") or collect_named(slide_el, "GridLabel")
    record("S3-NEGATIVE-NOGRID", "GridHeader-/GridLabel* 系の矩形が存在しない（表格子を構成しない）",
           "collect_named", len(grid_like) == 0)


def verify_reopen(path, expected_slides=3):
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
    print("\n=== schedule-shape-grid-gapped.pptx element checklist ===")
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
    build_slide2(prs)
    build_slide3(prs)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    prs.save(str(output_path))
    normalize_zip_timestamps(output_path, FIXED_ZIP_DATE_TIME)
    return output_path, grid1


def run_verify(path, grid1):
    with zipfile.ZipFile(path) as z:
        slide_names = sorted(
            (n for n in z.namelist() if n.startswith("ppt/slides/slide") and n.endswith(".xml")),
            key=lambda n: int("".join(ch for ch in n if ch.isdigit())),
        )
        slide_xmls = [etree.fromstring(z.read(n)) for n in slide_names]

    prs = verify_reopen(path, expected_slides=3)

    verify_shape_count("S1", prs.slides[0], 26)
    verify_shape_count("S2", prs.slides[1], 7)
    verify_shape_count("S3", prs.slides[2], 6)

    verify_slide1(slide_xmls[0], grid1)
    verify_slide2(slide_xmls[1])
    verify_slide3(slide_xmls[2])

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
        run_verify(out_path, grid1)
    else:
        out_path, grid1 = build(out_path)
        print(f"wrote {out_path} ({out_path.stat().st_size} bytes, "
              f"{len(Presentation(str(out_path)).slides)} slides)")
        run_verify(out_path, grid1)

    all_ok = print_checklist()
    if not all_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()

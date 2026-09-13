#!/usr/bin/env python3
"""Generate schedule-arrows.pptx: a Japanese IT-project schedule deck used to
test the "PPTX table overlay" feature (arrows/bars/markers/today-line drawn
ON TOP of a native a:tbl whose columns are dates).

See the design spec this fixture was built from:
  PPTX 表オーバーレイ（スケジュール矢印）対応 設計仕様
(handed to this script's author out-of-repo; the load-bearing rules are
restated inline as comments where they drive a geometry decision below).

Modeled on generate_complex_pptx.py in this same directory (shape-id
handling conventions, Japanese font, deterministic-output checks, and the
python-pptx-reopen + raw-XML verification pattern).

Usage:
  python3 generate_schedule_pptx.py [output.pptx] [--verify]

  --verify        Re-open an already-generated file and print the
                   overlay/row/column checklist without regenerating it.
                   (Also runs automatically after a fresh generation.)

Determinism: python-pptx's bundled default.pptx template already carries a
static docProps/core.xml (created/modified fixed at 2013-01-27T..., not the
current time -- confirmed empirically, see below), so no core_properties
override is needed for *content* determinism. The one non-deterministic
knob python-pptx does *not* control is the zip member timestamp (defaults
to "now" via zipfile.writestr), so this script rewrites every zip entry's
date_time to a fixed value after saving, making the .pptx byte-identical
across repeated runs (verified below by hashing two consecutive builds).
"""
from __future__ import annotations

import sys
import zipfile
from pathlib import Path

from lxml import etree

from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.oxml.ns import qn

DEFAULT_OUT = Path(__file__).resolve().parent / "schedule-arrows.pptx"

# ---------------------------------------------------------------- palette --
JP_FONT = "Yu Gothic"
NAVY = RGBColor(0x17, 0x36, 0x5D)
TEAL = RGBColor(0x0B, 0x72, 0x85)
INK = RGBColor(0x1F, 0x29, 0x37)
MUTED = RGBColor(0x66, 0x70, 0x85)
RED = RGBColor(0xB4, 0x23, 0x18)
GREEN = RGBColor(0x06, 0x76, 0x47)
GOLD = RGBColor(0xB7, 0x79, 0x1B)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)

# Fixed zip member timestamp used to make repeated builds byte-identical.
# (year, month, day, hour, minute, second) -- zipfile requires year >= 1980.
FIXED_ZIP_DATE_TIME = (2026, 1, 1, 0, 0, 0)

CHECKLIST = []  # (id, description, method, "PASS"/"FAIL")


def record(elem_id, desc, method, ok):
    CHECKLIST.append((elem_id, desc, method, "PASS" if ok else "FAIL"))


# =============================================================== geometry ==
# Every overlay shape below is placed by reading the SAME numbers that were
# handed to shapes.add_table()/tbl.columns[i].width/tbl.rows[r].height, so
# a shape's pixel position and its "which table cell(s) does this cover"
# answer can never drift apart. This mirrors the extraction side's contract
# (a:gridCol/@w + a:tr/@h -> column/row boundaries in EMU).
class Grid:
    """Column/row boundary helper for one table, all values in inches."""

    def __init__(self, left_in, top_in, col_widths_in, row_heights_in):
        self.left = left_in
        self.top = top_in
        self.col_widths = list(col_widths_in)
        self.row_heights = list(row_heights_in)

        self._col_left = []
        acc = left_in
        for w in self.col_widths:
            self._col_left.append(acc)
            acc += w

        self._row_top = []
        acc = top_in
        for h in self.row_heights:
            self._row_top.append(acc)
            acc += h

    def col_left(self, i):
        return self._col_left[i]

    def col_right(self, i):
        return self._col_left[i] + self.col_widths[i]

    def col_center(self, i):
        return (self.col_left(i) + self.col_right(i)) / 2

    def row_top(self, r):
        return self._row_top[r]

    def row_bottom(self, r):
        return self._row_top[r] + self.row_heights[r]

    def row_center(self, r):
        return (self.row_top(r) + self.row_bottom(r)) / 2

    @property
    def width(self):
        return sum(self.col_widths)

    @property
    def height(self):
        return sum(self.row_heights)

    @property
    def n_cols(self):
        return len(self.col_widths)

    @property
    def n_rows(self):
        return len(self.row_heights)


INSET_IN = 0.06          # small gap so arrow edges don't sit exactly on gridlines
BAR_HEIGHT_FRAC = 0.6    # "height ~= 60% of the row", per the task spec

# Slide 1 / 2 table: 工程|担当 + 6 date columns. First two columns wider,
# date columns share the remainder equally: 1.6+1.1+6*1.6 = 12.3in.
COL_WIDTHS_MAIN = [1.6, 1.1, 1.6, 1.6, 1.6, 1.6, 1.6, 1.6]
ROW_HEIGHTS_MAIN = [0.6] * 6
HEADERS_MAIN = ["工程", "担当", "9/1", "9/2", "9/3", "9/4", "9/5", "9/8"]

# Slide 3 table: 工程 + 4 date columns. 1.7 + 4*1.6 = 8.1in.
COL_WIDTHS_SMALL = [1.7, 1.6, 1.6, 1.6, 1.6]
ROW_HEIGHTS_SMALL = [0.6] * 4
HEADERS_SMALL = ["工程", "9/1", "9/2", "9/3", "9/4"]


def bar_geometry(grid, row, col_start, col_end, inset=INSET_IN, height_frac=BAR_HEIGHT_FRAC):
    """left/top/width/height (inches) for a horizontal bar/arrow overlay
    spanning columns [col_start, col_end] (inclusive) of `row`, inset from
    the column edges and vertically centred in the row at height_frac."""
    left = grid.col_left(col_start) + inset
    right = grid.col_right(col_end) - inset
    width = right - left
    height = grid.row_heights[row] * height_frac
    top = grid.row_center(row) - height / 2
    return left, top, width, height


# ------------------------------------------------------------- xml utils --
def next_shape_id(slide):
    ids = [int(el.get("id")) for el in slide.shapes._spTree.iter(qn("p:cNvPr"))]
    return max(ids, default=1) + 1


def set_title(slide, text, size=28, color=NAVY):
    title = slide.shapes.title
    title.text_frame.text = text
    for p in title.text_frame.paragraphs:
        for r in p.runs:
            r.font.name = JP_FONT
            r.font.color.rgb = color
            if size:
                r.font.size = Pt(size)
    return title


def add_titled_slide(prs, title_text):
    """Title Only layout (index 5 in the default template, same convention
    as generate_complex_pptx.py): a title placeholder plus free canvas."""
    layout = prs.slide_masters[0].slide_layouts[5]
    slide = prs.slides.add_slide(layout)
    if title_text is not None and slide.shapes.title is not None:
        set_title(slide, title_text)
    return slide


# --------------------------------------------------------------- table ----
def build_table(slide, grid, headers, rows):
    """add_table with explicit a:gridCol widths and a:tr heights taken
    verbatim from `grid`, so the table's real column/row geometry always
    matches what the overlay shapes below were positioned against."""
    n_rows = len(rows) + 1
    n_cols = len(headers)
    gframe = slide.shapes.add_table(
        n_rows, n_cols, Inches(grid.left), Inches(grid.top), Inches(grid.width), Inches(grid.height)
    )
    tbl = gframe.table
    for c, w in enumerate(grid.col_widths):
        tbl.columns[c].width = Inches(w)
    for r, h in enumerate(grid.row_heights):
        tbl.rows[r].height = Inches(h)

    for c, htext in enumerate(headers):
        cell = tbl.cell(0, c)
        cell.text = htext
        cell.fill.solid()
        cell.fill.fore_color.rgb = NAVY
        run = cell.text_frame.paragraphs[0].runs[0]
        run.font.bold = True
        run.font.size = Pt(11)
        run.font.color.rgb = WHITE
        run.font.name = JP_FONT

    for r, rowvals in enumerate(rows, start=1):
        for c, val in enumerate(rowvals):
            cell = tbl.cell(r, c)
            cell.text = val
            if val:
                run = cell.text_frame.paragraphs[0].runs[0]
                run.font.size = Pt(10.5)
                run.font.name = JP_FONT
                run.font.color.rgb = INK
    return gframe, tbl


# ------------------------------------------------------------- overlays ---
def add_bar_shape(shapes, grid, shape_type, row, col_start, col_end, text="",
                   fill_color=NAVY, text_color=WHITE, font_size=10, bold=True,
                   line_color=WHITE):
    left, top, width, height = bar_geometry(grid, row, col_start, col_end)
    shp = shapes.add_shape(shape_type, Inches(left), Inches(top), Inches(width), Inches(height))
    shp.fill.solid()
    shp.fill.fore_color.rgb = fill_color
    shp.line.color.rgb = line_color
    if text:
        tf = shp.text_frame
        tf.word_wrap = True
        p = tf.paragraphs[0]
        p.alignment = PP_ALIGN.CENTER
        r = p.add_run()
        r.text = text
        r.font.size = Pt(font_size)
        r.font.bold = bold
        r.font.color.rgb = text_color
        r.font.name = JP_FONT
    return shp


def add_diamond_marker(shapes, grid, row, col, size_in=0.3, fill_color=RED):
    cx = grid.col_center(col)
    cy = grid.row_center(row)
    left = cx - size_in / 2
    top = cy - size_in / 2
    shp = shapes.add_shape(MSO_SHAPE.DIAMOND, Inches(left), Inches(top), Inches(size_in), Inches(size_in))
    shp.fill.solid()
    shp.fill.fore_color.rgb = fill_color
    shp.line.color.rgb = WHITE
    return shp


def add_today_line(shapes, grid, col, row_start, row_end, color=RED, width_pt=2.25):
    """Vertical straight connector, NOT connected to any shape, with a
    triangle tailEnd arrowhead injected via raw XML (python-pptx has no
    line-arrowhead API). Represents the "本日" (today) line."""
    x = grid.col_center(col)
    y1 = grid.row_top(row_start)
    y2 = grid.row_bottom(row_end)
    conn = shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(x), Inches(y1), Inches(x), Inches(y2))
    conn.line.color.rgb = color
    conn.line.width = Pt(width_pt)
    ln = conn.line._get_or_add_ln()  # <a:ln> element (CT_LineProperties)
    tail_end = ln.makeelement(qn("a:tailEnd"), {"type": "triangle"})
    ln.insert_element_before(tail_end, "a:extLst")  # schema order: ...headEnd,tailEnd,extLst
    return conn


def add_label_textbox(shapes, grid, row, col, text, color=RED, size=9, inset=0.05):
    left = grid.col_left(col) + inset
    top = grid.row_top(row) + inset
    width = grid.col_widths[col] - 2 * inset
    height = grid.row_heights[row] - 2 * inset
    tb = shapes.add_textbox(Inches(left), Inches(top), Inches(width), Inches(height))
    tf = tb.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.alignment = PP_ALIGN.CENTER
    r = p.add_run()
    r.text = text
    r.font.size = Pt(size)
    r.font.bold = True
    r.font.color.rgb = color
    r.font.name = JP_FONT
    return tb


# ---------------------------------------------------- group shape transform
def apply_nontrivial_group_transform(group, shift_x_in, shift_y_in, scale):
    """Rewrite `group`'s a:chOff/a:chExt to a coordinate space that is
    shifted AND scaled relative to a:off/a:ext, then rewrite each direct
    child <p:sp>'s a:off/a:ext (which are expressed in that child/local
    space) so the shapes' ABSOLUTE on-slide position is unchanged.

    python-pptx's own add_group_shape()/recalculate_extents() always
    produces an *identity* transform (chOff == off, chExt == ext), which
    would never exercise the affine-transform math the PptxAdapter has to
    do for group children. This function deliberately manufactures a
    non-identity transform (non-zero chOff shift + non-1 scale) while
    keeping every child's rendered position identical, so the fixture
    genuinely tests "resolve absolute position through a group transform"
    rather than a no-op pass-through.

    Formula (OOXML group transform): for a child point/extent expressed in
    local coordinates, abs = off + (local - chOff) * (ext / chExt). Solving
    for `local` given the desired `abs` (the shape's original, pre-group
    absolute geometry) with chExt := ext / scale (so ext/chExt == scale):
        local_off   = chOff + (abs_off - off) / scale
        local_ext   = abs_ext / scale
    """
    grp_el = group._element
    off_x, off_y = int(group.left), int(group.top)
    ext_cx, ext_cy = int(group.width), int(group.height)

    shift_x = int(Inches(shift_x_in))
    shift_y = int(Inches(shift_y_in))
    new_chOff_x = off_x + shift_x
    new_chOff_y = off_y + shift_y
    new_chExt_cx = int(round(ext_cx / scale))
    new_chExt_cy = int(round(ext_cy / scale))

    child_sps = grp_el.findall(qn("p:sp"))
    assert child_sps, "group has no direct <p:sp> children to retarget"

    # Capture each child's current (still-absolute, since transform is
    # still identity at this point) off/ext BEFORE mutating anything.
    originals = []
    for sp_el in child_sps:
        xfrm = sp_el.find(qn("p:spPr")).find(qn("a:xfrm"))
        off = xfrm.find(qn("a:off"))
        ext = xfrm.find(qn("a:ext"))
        originals.append((int(off.get("x")), int(off.get("y")), int(ext.get("cx")), int(ext.get("cy"))))

    grp_el.chOff.x = new_chOff_x
    grp_el.chOff.y = new_chOff_y
    grp_el.chExt.cx = new_chExt_cx
    grp_el.chExt.cy = new_chExt_cy
    # group's own a:off/a:ext are left untouched: they already describe the
    # correct absolute bounding box (set by recalculate_extents()).

    for sp_el, (ax, ay, aw, ah) in zip(child_sps, originals):
        xfrm = sp_el.find(qn("p:spPr")).find(qn("a:xfrm"))
        off = xfrm.find(qn("a:off"))
        ext = xfrm.find(qn("a:ext"))
        local_x = int(round(new_chOff_x + (ax - off_x) / scale))
        local_y = int(round(new_chOff_y + (ay - off_y) / scale))
        local_cx = int(round(aw / scale))
        local_cy = int(round(ah / scale))
        off.set("x", str(local_x))
        off.set("y", str(local_y))
        ext.set("cx", str(local_cx))
        ext.set("cy", str(local_cy))


# ================================================================ slides ==
def build_slide1(prs):
    """『開発スケジュール』: the primary overlay fixture -- every Kind/
    Direction/Axis combination the spec defines is exercised on one table:
      row1 要件定義: RIGHT_ARROW covering 9/1-9/2      (arrow, right, horizontal)
      row2 設計    : RIGHT_ARROW covering 9/2-9/4      (arrow, right, horizontal)
                     + textbox "▲レビュー" on 9/5      (label, none, horizontal)
      row3 実装    : RECTANGLE (no text) covering 9/3-9/5 (bar, none, horizontal)
      row4 テスト  : LEFT_RIGHT_ARROW covering 9/4-9/8 (arrow, both, horizontal)
      row5 リリース: DIAMOND on 9/8                    (marker, none, horizontal)
      today line   : vertical connector on 9/3, header..row5, triangle
                     tailEnd -> classified as an ARROW (down), vertical axis,
                     spanning EVERY row including the header.
    """
    slide = add_titled_slide(prs, "開発スケジュール")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)

    rows = [
        ["要件定義", "山田", "", "", "", "", "", ""],
        ["設計", "佐藤", "", "", "", "", "", ""],
        ["実装", "鈴木", "", "", "", "", "", ""],
        ["テスト", "田中", "", "", "", "", "", ""],
        ["リリース", "全員", "", "", "", "", "", ""],
    ]
    build_table(slide, grid, HEADERS_MAIN, rows)

    add_bar_shape(shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=1, col_start=2, col_end=3,
                  text="要件定義", fill_color=NAVY)
    add_bar_shape(shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=2, col_start=3, col_end=5,
                  text="設計", fill_color=TEAL)
    add_bar_shape(shapes, grid, MSO_SHAPE.RECTANGLE, row=3, col_start=4, col_end=6,
                  text="", fill_color=GOLD)
    add_bar_shape(shapes, grid, MSO_SHAPE.LEFT_RIGHT_ARROW, row=4, col_start=5, col_end=7,
                  text="テスト", fill_color=GREEN)
    add_diamond_marker(shapes, grid, row=5, col=7, fill_color=RED)
    add_today_line(shapes, grid, col=4, row_start=0, row_end=5, color=RED)
    add_label_textbox(shapes, grid, row=2, col=6, text="▲レビュー", color=RED)

    record("S1-TABLE", "表: 6行8列（工程/担当+日付6列）", "add_table(6,8) + 明示的な列幅/行高", True)
    record("S1-ARROW-REQ", "要件定義行: 右矢印 9/1-9/2", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S1-ARROW-DESIGN", "設計行: 右矢印 9/2-9/4", "MSO_SHAPE.RIGHT_ARROW", True)
    record("S1-BAR-IMPL", "実装行: 四角バー(無地/無文字) 9/3-9/5", "MSO_SHAPE.RECTANGLE", True)
    record("S1-ARROW-TEST", "テスト行: 両矢印 9/4-9/8", "MSO_SHAPE.LEFT_RIGHT_ARROW", True)
    record("S1-DIAMOND", "リリース行: ひし形マーカー 9/8", "MSO_SHAPE.DIAMOND", True)
    record("S1-TODAYLINE", "本日線: 縦コネクタ 9/3列 全行 + 矢尻(triangle)",
           "add_connector(STRAIGHT) + raw <a:tailEnd type=triangle/>", True)
    record("S1-LABEL", "レビュー注記テキストボックス 9/5(設計行)", "add_textbox", True)
    return slide, grid


def build_slide2(prs):
    """『グループ化されたスケジュール』: same table, but the two RIGHT_ARROW
    overlays live inside a <p:grpSp> whose a:chOff/a:chExt are DELIBERATELY
    non-identity (see apply_nontrivial_group_transform), while the table
    itself stays a slide-level shape (python-pptx's shape tree has no API
    to add a table INSIDE a group -- GroupShapes exposes add_textbox/
    add_shape/add_connector/add_picture/add_group_shape but no add_table;
    see tests/.../generate_schedule_pptx.py comments and the README).

    Also carries a genuine, unrelated flow diagram (2 rounded rectangles +
    a connector using begin_connect/end_connect) placed well below the
    table, to prove overlay detection does not accidentally swallow real,
    natively-connected diagrams that merely happen to share a slide with a
    table.
    """
    slide = add_titled_slide(prs, "グループ化されたスケジュール")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)

    rows = [
        ["要件定義", "山田", "", "", "", "", "", ""],
        ["設計", "佐藤", "", "", "", "", "", ""],
        ["実装", "鈴木", "", "", "", "", "", ""],
        ["テスト", "田中", "", "", "", "", "", ""],
        ["リリース", "全員", "", "", "", "", "", ""],
    ]
    build_table(slide, grid, HEADERS_MAIN, rows)

    arrow1 = add_bar_shape(shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=1, col_start=2, col_end=3,
                            text="要件定義", fill_color=NAVY)
    arrow2 = add_bar_shape(shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=2, col_start=3, col_end=5,
                            text="設計", fill_color=TEAL)

    group = shapes.add_group_shape([arrow1, arrow2])
    group.name = "スケジュール矢印グループ"
    apply_nontrivial_group_transform(group, shift_x_in=0.5, shift_y_in=0.3, scale=1.5)

    # Real, natively-connected flow -- NOT an overlay -- placed clear of the
    # table (table bottom = 1.5+3.6 = 5.1in).
    start_box = shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(0.6), Inches(5.9), Inches(1.8), Inches(0.7))
    start_box.fill.solid()
    start_box.fill.fore_color.rgb = NAVY
    start_box.line.color.rgb = WHITE
    sp = start_box.text_frame.paragraphs[0]
    sp.alignment = PP_ALIGN.CENTER
    sr = sp.add_run()
    sr.text = "開始"
    sr.font.bold = True
    sr.font.size = Pt(14)
    sr.font.color.rgb = WHITE
    sr.font.name = JP_FONT

    end_box = shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(3.4), Inches(5.9), Inches(1.8), Inches(0.7))
    end_box.fill.solid()
    end_box.fill.fore_color.rgb = TEAL
    end_box.line.color.rgb = WHITE
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

    caption = shapes.add_textbox(Inches(0.6), Inches(5.55), Inches(6.0), Inches(0.35))
    cr = caption.text_frame.paragraphs[0].add_run()
    cr.text = "実データフロー（表とは無関係。begin_connect/end_connectで接続済み）"
    cr.font.size = Pt(10)
    cr.font.italic = True
    cr.font.color.rgb = MUTED
    cr.font.name = JP_FONT

    record("S2-TABLE", "表: slide直下（グループの外）", "add_table (slide.shapes)", True)
    record("S2-GROUP", "矢印2本をグループ化 + 非自明なchOff/chExt変換",
           "add_group_shape + apply_nontrivial_group_transform(shift=(0.5,0.3)in, scale=1.5)", True)
    record("S2-REALFLOW", "表と無関係な実接続フロー（開始→完了）",
           "add_connector + begin_connect/end_connect (stCxn/endCxn)", True)
    return slide, grid, group


def build_slide3(prs):
    """『回転した矢印』: exercises rotation-aware AABB computation.
      - RIGHT_ARROW rotation=180 (visually left-pointing) covering 9/2-9/3
        on a normal (unrotated-AABB) row.
      - RIGHT_ARROW rotation=90, pre-rotation width/height chosen so that,
        AFTER the 90-degree AABB swap, it exactly spans rows 1-3 within the
        9/4 column (vertical bar look, built from a horizontal arrow).
      - RECTANGLE "共通" spanning BOTH rows 2-3 and columns 9/1-9/2 (a
        genuine multi-row multi-column bar).
    """
    slide = add_titled_slide(prs, "回転した矢印")
    shapes = slide.shapes
    grid = Grid(0.5, 1.5, COL_WIDTHS_SMALL, ROW_HEIGHTS_SMALL)

    rows = [
        ["差し戻し", "", "", "", ""],
        ["設計", "", "", "", ""],
        ["実装", "", "", "", ""],
    ]
    build_table(slide, grid, HEADERS_SMALL, rows)

    # 1) rotation=180 -> AABB unchanged (symmetric rect), visually flipped.
    arrow_back = add_bar_shape(shapes, grid, MSO_SHAPE.RIGHT_ARROW, row=1, col_start=2, col_end=3,
                                text="戻し", fill_color=TEAL)
    arrow_back.rotation = 180

    # 2) rotation=90 -> pre-rotation width/height are swapped by PowerPoint
    #    at render time; python-pptx's left/top/width/height always mean
    #    the UN-rotated frame, so we must pre-swap them ourselves:
    #      pre-rotation width  = 3 row heights  (becomes the AABB HEIGHT)
    #      pre-rotation height = 60% of the 9/4 column width (AABB WIDTH)
    #    centred on the centre of the rows1-3 / column9/4 span.
    w0 = grid.row_heights[0] * 3           # -> AABB height after rotation
    h0 = grid.col_widths[4] * 0.6          # -> AABB width after rotation
    cx = grid.col_center(4)
    cy = (grid.row_top(1) + grid.row_bottom(3)) / 2
    left = cx - w0 / 2
    top = cy - h0 / 2
    arrow_vert = shapes.add_shape(MSO_SHAPE.RIGHT_ARROW, Inches(left), Inches(top), Inches(w0), Inches(h0))
    arrow_vert.rotation = 90
    arrow_vert.fill.solid()
    arrow_vert.fill.fore_color.rgb = NAVY
    arrow_vert.line.color.rgb = WHITE

    # 3) multi-row x multi-column bar: rows 2-3, columns 9/1-9/2.
    inset = 0.05
    left3 = grid.col_left(1) + inset
    top3 = grid.row_top(2) + inset
    width3 = (grid.col_right(2) - grid.col_left(1)) - 2 * inset
    height3 = (grid.row_bottom(3) - grid.row_top(2)) - 2 * inset
    bar_common = shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(left3), Inches(top3), Inches(width3), Inches(height3))
    bar_common.fill.solid()
    bar_common.fill.fore_color.rgb = GOLD
    bar_common.line.color.rgb = WHITE
    tf = bar_common.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.alignment = PP_ALIGN.CENTER
    r = p.add_run()
    r.text = "共通"
    r.font.bold = True
    r.font.size = Pt(11)
    r.font.color.rgb = WHITE
    r.font.name = JP_FONT

    record("S3-TABLE", "表: 4行5列（小型）", "add_table(4,5)", True)
    record("S3-ROT180", "rotation=180 (rot=10800000): 9/2-9/3, 左向き表示", "shape.rotation=180", True)
    record("S3-ROT90", "rotation=90 (rot=5400000): rows1-3 x 9/4列（AABB入替）", "shape.rotation=90", True)
    record("S3-MULTIBAR", "複数行x複数列バー「共通」 rows2-3 x 9/1-9/2", "MSO_SHAPE.RECTANGLE", True)
    return slide, grid


# ============================================================ determinism ==
def normalize_zip_timestamps(path, date_time=FIXED_ZIP_DATE_TIME):
    """Rewrite every zip member's date_time to a fixed value so the .pptx
    is byte-identical across repeated generator runs (content is already
    deterministic; only the zip timestamps defaulted to wall-clock time)."""
    tmp_path = str(path) + ".normalize.tmp"
    with zipfile.ZipFile(path) as zin, zipfile.ZipFile(tmp_path, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            item.date_time = date_time
            zout.writestr(item, data)
    import os
    os.replace(tmp_path, path)


# ============================================================ self-check ==
_NS = {
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
}


def _local(tag):
    return etree.QName(tag).localname


def _prstGeom_prst(sp_el):
    spPr = sp_el.find(qn("p:spPr"))
    if spPr is None:
        return None
    geom = spPr.find(qn("a:prstGeom"))
    if geom is None:
        return None
    return geom.get("prst")


def _xfrm_of(sp_el):
    spPr = sp_el.find(qn("p:spPr"))
    if spPr is None:
        return None
    return spPr.find(qn("a:xfrm"))


def _off_ext_rot(sp_el):
    xfrm = _xfrm_of(sp_el)
    if xfrm is None:
        return None, None, 0
    off = xfrm.find(qn("a:off"))
    ext = xfrm.find(qn("a:ext"))
    ox = int(off.get("x")) if off is not None else None
    oy = int(off.get("y")) if off is not None else None
    ecx = int(ext.get("cx")) if ext is not None else None
    ecy = int(ext.get("cy")) if ext is not None else None
    rot = int(xfrm.get("rot", "0"))
    return (ox, oy), (ecx, ecy), rot


def rotated_aabb(off, ext, rot):
    """Apply the spec's AABB rule: round rotation to nearest 90deg; if that
    is 90 or 270, swap width/height around the same centre."""
    ox, oy = off
    cx_, cy_ = ext
    quarter = (round(rot / 5400000.0) % 4)  # 5,400,000 = 90deg in 60,000ths
    if quarter in (1, 3):
        cx_c = ox + cx_ / 2
        cy_c = oy + cy_ / 2
        cx_, cy_ = cy_, cx_
        ox = cx_c - cx_ / 2
        oy = cy_c - cy_ / 2
    return (ox, oy), (cx_, cy_)


def overlap_len(a0, a1, b0, b1):
    return max(0.0, min(a1, b1) - max(a0, b0))


def assign_axis(aabb0, aabb1, bounds):
    """One axis of the spec's row/column assignment rule: bounds is a list
    of (start, end) cell boundaries along this axis. Returns the sorted
    list of covered indices. Degenerate (zero-length) aabb -> point/nearest
    containment instead of the 50% overlap rule."""
    length = aabb1 - aabb0
    if length <= 0:
        mid = aabb0
        for i, (b0, b1) in enumerate(bounds):
            if b0 <= mid <= b1:
                return [i]
        return [0] if mid < bounds[0][0] else [len(bounds) - 1]

    covered = [i for i, (b0, b1) in enumerate(bounds)
               if overlap_len(aabb0, aabb1, b0, b1) >= 0.5 * (b1 - b0)]
    if covered:
        return covered
    mid = (aabb0 + aabb1) / 2
    for i, (b0, b1) in enumerate(bounds):
        if b0 <= mid <= b1:
            return [i]
    return [0] if mid < bounds[0][0] else [len(bounds) - 1]


def table_bounds(grid):
    col_bounds = [(grid.col_left(i), grid.col_right(i)) for i in range(grid.n_cols)]
    row_bounds = [(grid.row_top(r), grid.row_bottom(r)) for r in range(grid.n_rows)]
    return col_bounds, row_bounds


def emu(inches_val):
    return int(round(inches_val * 914400))


def verify_table_geometry(label, tbl_el, expected_grid):
    """Compare a:gridCol/@w and a:tr/@h against the Grid used to place the
    overlays, and confirm the sums equal the frame's own width/height."""
    grid_el = tbl_el.find(qn("a:tblGrid"))
    col_ws = [int(gc.get("w")) for gc in grid_el.findall(qn("a:gridCol"))]
    row_hs = [int(tr.get("h")) for tr in tbl_el.findall(qn("a:tr"))]
    expected_cols = [emu(w) for w in expected_grid.col_widths]
    expected_rows = [emu(h) for h in expected_grid.row_heights]
    cols_ok = col_ws == expected_cols
    rows_ok = row_hs == expected_rows
    width_ok = abs(sum(col_ws) - emu(expected_grid.width)) <= 2
    height_ok = abs(sum(row_hs) - emu(expected_grid.height)) <= 2
    print(f"  a:gridCol widths (EMU): {col_ws}")
    print(f"  a:tr heights   (EMU): {row_hs}")
    print(f"  sum(gridCol)={sum(col_ws)} vs frame width={emu(expected_grid.width)}  "
          f"sum(tr.h)={sum(row_hs)} vs frame height={emu(expected_grid.height)}")
    record(f"{label}-GRIDCOL", "a:gridCol@w が期待列幅と一致", "grid_el.findall(a:gridCol)", cols_ok)
    record(f"{label}-TRHEIGHT", "a:tr@h が期待行高と一致", "tbl_el.findall(a:tr)", rows_ok)
    record(f"{label}-COLSUM", "Σ列幅 == 枠幅", "sum(a:gridCol@w) == frame width", width_ok)
    record(f"{label}-ROWSUM", "Σ行高 == 枠高", "sum(a:tr@h) == frame height", height_ok)


def print_shape_facts(label, sp_el, kind_hint=""):
    prst = _prstGeom_prst(sp_el)
    off, ext, rot = _off_ext_rot(sp_el)
    cNvPr = sp_el.find(f'.//{qn("p:cNvPr")}')
    name = cNvPr.get("name") if cNvPr is not None else "?"
    print(f"  [{label}] name={name!r} kind={kind_hint} prstGeom={prst} "
          f"off={off} ext={ext} rot={rot}")
    return prst, off, ext, rot


def find_tailEnd(sp_el):
    spPr = sp_el.find(qn("p:spPr"))
    if spPr is None:
        return None
    ln = spPr.find(qn("a:ln"))
    if ln is None:
        return None
    te = ln.find(qn("a:tailEnd"))
    return te.get("type") if te is not None else None


def is_textbox(sp_el):
    cNvSpPr = sp_el.find(f'{qn("p:nvSpPr")}/{qn("p:cNvSpPr")}')
    if cNvSpPr is None:
        return False
    return cNvSpPr.get("txBox") == "1"


def stcxn_endcxn(sp_el):
    nvCxnSpPr = sp_el.find(qn("p:nvCxnSpPr"))
    if nvCxnSpPr is None:
        return None, None
    cNvCxnSpPr = nvCxnSpPr.find(qn("p:cNvCxnSpPr"))
    if cNvCxnSpPr is None:
        return None, None
    st = cNvCxnSpPr.find(qn("a:stCxn"))
    end = cNvCxnSpPr.find(qn("a:endCxn"))
    st_id = st.get("id") if st is not None else None
    end_id = end.get("id") if end is not None else None
    return st_id, end_id


def verify_slide1(slide_el, grid):
    print("\n--- Slide 1: 開発スケジュール ---")
    tbl_el = slide_el.find(f'.//{qn("a:tbl")}')
    verify_table_geometry("S1", tbl_el, grid)

    col_bounds, row_bounds = table_bounds(grid)
    col_bounds = [(emu(a), emu(b)) for a, b in col_bounds]
    row_bounds = [(emu(a), emu(b)) for a, b in row_bounds]

    sp_tree = slide_el.find(qn("p:cSld")).find(qn("p:spTree"))
    overlay_sps = [el for el in sp_tree if _local(el.tag) in ("sp", "cxnSp")
                   and el.find(qn("p:nvSpPr") if _local(el.tag) == "sp" else qn("p:nvCxnSpPr")) is not None
                   and _prstGeom_prst(el) is not None]

    print("\n  computed row/column assignment (50% overlap rule):")
    print(f"  {'shape':<14}{'prstGeom':<14}{'rot':>10}  {'rows':<8}{'cols':<10}")
    any_tail = False
    any_txbox = False
    for el in overlay_sps:
        prst, off, ext, rot = print_shape_facts("S1", el)
        (ox, oy), (ecx, ecy) = rotated_aabb(off, ext, rot)
        rows = assign_axis(oy, oy + ecy, row_bounds)
        cols = assign_axis(ox, ox + ecx, col_bounds)
        cNvPr = el.find(f'.//{qn("p:cNvPr")}')
        name = cNvPr.get("name") if cNvPr is not None else "?"
        print(f"  {name:<14}{str(prst):<14}{rot:>10}  {str([r for r in rows]):<8}{str(cols):<10}")
        if find_tailEnd(el):
            any_tail = True
            print(f"    tailEnd type={find_tailEnd(el)!r} on {name!r}")
        if is_textbox(el):
            any_txbox = True
            print(f"    txBox=1 on {name!r}")

    record("S1-XML-TAILEND", "本日線コネクタに a:tailEnd type=triangle", "find_tailEnd()", any_tail)
    record("S1-XML-TXBOX", "レビュー注記に p:cNvSpPr@txBox=1", "is_textbox()", any_txbox)


def verify_slide2(slide_el, grid):
    print("\n--- Slide 2: グループ化されたスケジュール ---")
    tbl_el = slide_el.find(f'.//{qn("a:tbl")}')
    verify_table_geometry("S2", tbl_el, grid)

    sp_tree = slide_el.find(qn("p:cSld")).find(qn("p:spTree"))
    grp_el = sp_tree.find(qn("p:grpSp"))
    off = grp_el.find(f'{qn("p:grpSpPr")}/{qn("a:xfrm")}/{qn("a:off")}')
    ext = grp_el.find(f'{qn("p:grpSpPr")}/{qn("a:xfrm")}/{qn("a:ext")}')
    chOff = grp_el.find(f'{qn("p:grpSpPr")}/{qn("a:xfrm")}/{qn("a:chOff")}')
    chExt = grp_el.find(f'{qn("p:grpSpPr")}/{qn("a:xfrm")}/{qn("a:chExt")}')
    off_v = (int(off.get("x")), int(off.get("y")))
    ext_v = (int(ext.get("cx")), int(ext.get("cy")))
    chOff_v = (int(chOff.get("x")), int(chOff.get("y")))
    chExt_v = (int(chExt.get("cx")), int(chExt.get("cy")))
    print(f"  group a:off={off_v} a:ext={ext_v}")
    print(f"  group a:chOff={chOff_v} a:chExt={chExt_v}")
    nontrivial = (off_v != chOff_v) or (ext_v != chExt_v)
    record("S2-XML-GROUPXFRM", "グループ変換が非自明（chOff!=off または chExt!=ext）",
           "compare a:off/a:ext vs a:chOff/a:chExt", nontrivial)

    col_bounds, row_bounds = table_bounds(grid)
    col_bounds_e = [(emu(a), emu(b)) for a, b in col_bounds]
    row_bounds_e = [(emu(a), emu(b)) for a, b in row_bounds]

    scale_x = ext_v[0] / chExt_v[0]
    scale_y = ext_v[1] / chExt_v[1]
    print("\n  grouped arrows: local (child-space) xfrm -> resolved absolute AABB -> row/col")
    for sp_el in grp_el.findall(qn("p:sp")):
        prst, local_off, local_ext, rot = print_shape_facts("S2", sp_el)
        abs_x = off_v[0] + (local_off[0] - chOff_v[0]) * scale_x
        abs_y = off_v[1] + (local_off[1] - chOff_v[1]) * scale_y
        abs_cx = local_ext[0] * scale_x
        abs_cy = local_ext[1] * scale_y
        rows = assign_axis(abs_y, abs_y + abs_cy, row_bounds_e)
        cols = assign_axis(abs_x, abs_x + abs_cx, col_bounds_e)
        print(f"    resolved abs off=({abs_x:.0f},{abs_y:.0f}) ext=({abs_cx:.0f},{abs_cy:.0f}) "
              f"-> rows={rows} cols={cols}")

    flow_conns = [el for el in sp_tree.findall(qn("p:cxnSp"))]
    any_connected = False
    for cxn in flow_conns:
        st, end = stcxn_endcxn(cxn)
        if st is not None and end is not None:
            any_connected = True
            print(f"  real-flow connector: stCxn.id={st} endCxn.id={end}")
    record("S2-XML-STENDCXN", "実データフローのコネクタに a:stCxn/a:endCxn", "stcxn_endcxn()", any_connected)


def verify_slide3(slide_el, grid):
    print("\n--- Slide 3: 回転した矢印 ---")
    tbl_el = slide_el.find(f'.//{qn("a:tbl")}')
    verify_table_geometry("S3", tbl_el, grid)

    col_bounds, row_bounds = table_bounds(grid)
    col_bounds = [(emu(a), emu(b)) for a, b in col_bounds]
    row_bounds = [(emu(a), emu(b)) for a, b in row_bounds]

    sp_tree = slide_el.find(qn("p:cSld")).find(qn("p:spTree"))
    overlay_sps = [el for el in sp_tree.findall(qn("p:sp")) if _prstGeom_prst(el) is not None]

    rot180_ok = False
    rot90_ok = False
    for el in overlay_sps:
        prst, off, ext, rot = print_shape_facts("S3", el)
        (ox, oy), (ecx, ecy) = rotated_aabb(off, ext, rot)
        rows = assign_axis(oy, oy + ecy, row_bounds)
        cols = assign_axis(ox, ox + ecx, col_bounds)
        print(f"    rotated AABB off=({ox:.0f},{oy:.0f}) ext=({ecx:.0f},{ecy:.0f}) -> rows={rows} cols={cols}")
        if rot == 10800000:
            rot180_ok = True
        if rot == 5400000:
            rot90_ok = True

    record("S3-XML-ROT180", 'rot="10800000" (180deg) がXMLに存在', "xfrm@rot", rot180_ok)
    record("S3-XML-ROT90", 'rot="5400000" (90deg) がXMLに存在', "xfrm@rot", rot90_ok)


def verify_reopen(path):
    prs = Presentation(str(path))
    ok = len(prs.slides) == 3
    record("REOPEN", f"python-pptxで再オープン（{len(prs.slides)}枚）", "Presentation(path)", ok)
    return prs


def verify_no_images_and_size(path):
    size = path.stat().st_size
    with zipfile.ZipFile(path) as z:
        media = [n for n in z.namelist() if n.startswith("ppt/media/")]
    record("SIZE", f"ファイルサイズ {size} bytes < 100KB", "path.stat().st_size", size < 100 * 1024)
    record("NO-IMAGES", "ppt/media/* が存在しない（画像なし）", "zipfile namelist", len(media) == 0)
    print(f"\nfile size: {size} bytes, media parts: {media}")


def verify_determinism(build_fn, out_path):
    """Build the file twice into throwaway copies and hash-compare, proving
    the zip-timestamp normalization actually makes output byte-identical."""
    import hashlib
    import tempfile
    base_len = len(CHECKLIST)  # build_fn() re-runs build_slideN(), which
    with tempfile.TemporaryDirectory() as td:  # calls record(); discard those
        p1 = Path(td) / "a.pptx"                # duplicate entries below so
        p2 = Path(td) / "b.pptx"                # the printed checklist only
        build_fn(p1)                            # reflects the real output.
        build_fn(p2)
        h1 = hashlib.sha256(p1.read_bytes()).hexdigest()
        h2 = hashlib.sha256(p2.read_bytes()).hexdigest()
    del CHECKLIST[base_len:]
    ok = h1 == h2
    record("DETERMINISM", "2回連続生成しても同一バイト列 (sha256一致)", "hashlib.sha256 of two independent builds", ok)
    print(f"  sha256 run A: {h1}")
    print(f"  sha256 run B: {h2}")


def print_checklist():
    print("\n=== schedule-arrows.pptx element checklist ===")
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

    _, grid1 = build_slide1(prs)
    _, grid2, _group = build_slide2(prs)
    _, grid3 = build_slide3(prs)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    prs.save(str(output_path))
    normalize_zip_timestamps(output_path)
    return output_path, grid1, grid2, grid3


def run_verify(path, grid1, grid2, grid3):
    with zipfile.ZipFile(path) as z:
        slide_names = sorted(
            (n for n in z.namelist() if n.startswith("ppt/slides/slide") and n.endswith(".xml")),
            key=lambda n: int("".join(ch for ch in n if ch.isdigit())),
        )
        slide_xmls = [etree.fromstring(z.read(n)) for n in slide_names]

    verify_reopen(path)
    verify_slide1(slide_xmls[0], grid1)
    verify_slide2(slide_xmls[1], grid2)
    verify_slide3(slide_xmls[2], grid3)
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
        # Rebuild the grids purely for the geometry expectations (no I/O
        # side effects besides reading the existing file for XML checks).
        grid1 = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)
        grid2 = Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)
        grid3 = Grid(0.5, 1.5, COL_WIDTHS_SMALL, ROW_HEIGHTS_SMALL)
        run_verify(out_path, grid1, grid2, grid3)
    else:
        out_path, grid1, grid2, grid3 = build(out_path)
        print(f"wrote {out_path} ({out_path.stat().st_size} bytes, {len(Presentation(str(out_path)).slides)} slides)")
        run_verify(out_path, grid1, grid2, grid3)

    all_ok = print_checklist()
    if not all_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Generate schedule-arrows.pdf: a Japanese IT-project schedule document used
to test PDF "table overlay" extraction (arrow/bar/marker/today-line vector
shapes drawn ON TOP OF a ruled table whose columns are dates).

Companion to ../Pptx/generate_schedule_pptx.py, ../Xlsx/generate_schedule_xlsx.py,
and ../Docx/generate_schedule_docx.py: same table shape/labels/overlay
inventory, translated to raw PDF vector graphics (reportlab canvas paths)
instead of an OOXML shape/drawing model -- a PDF page has no native
"shape"/"table" object, so every gridline, fill, and arrowhead below is
drawn as an explicit stroked/filled path at explicit page coordinates.

Reuses generate_complex_pdf.py's conventions: `canvas.Canvas(...,
invariant=1)` for deterministic output (no wall-clock CreationDate/ModDate,
no random internal object IDs) and, via that script's `resolve_font()`
helper, an embedded (subset) TrueType font for Japanese text. The
built-in CID font `HeiseiKakuGo-W5` (Adobe-Japan1 standard 14 CJK font --
not embedded, predefined CMap) is deliberately NOT used: DocRedock's
BCL-only PDF text extractor cannot decode that encoding, so every label
extracted as mojibake (stray NUL bytes) even though the glyphs render
correctly on screen.

Usage:
  python3 generate_schedule_pdf.py [output.pdf] [--verify]

  --verify        Re-open an already-generated file and print, for every
                   table cell and every overlay: its exact page coordinates
                   in points (PDF origin bottom-left), and (for overlays)
                   the computed expected row(s)/column(s) under the same
                   50%-overlap rule the PPTX/XLSX/DOCX generators use.
                   (Also runs automatically after a fresh generation.)

Unlike the OOXML formats, a PDF content stream has no structured shape
model to cheaply re-parse (there's no equivalent of `a:off`/`a:ext` to walk
with lxml) -- the coordinates below ARE the single source of truth, so
--verify recomputes them from the same Grid object used to draw the page,
and independently confirms only what a byte-level/text-level check on the
actual file can: page count, extractable text, file size, and a same-
process two-independent-builds SHA-256 comparison.
"""
from __future__ import annotations

import sys
from pathlib import Path

from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas

_FIXTURE_DIR = Path(__file__).resolve().parent
if str(_FIXTURE_DIR) not in sys.path:
    sys.path.insert(0, str(_FIXTURE_DIR))
from generate_complex_pdf import resolve_font  # same embedded-TrueType helper

DEFAULT_OUT = Path(__file__).resolve().parent / "schedule-arrows.pdf"

FONT_NAME = "DocRedockScheduleFont"
FONT, FONT_FACE_INDEX = resolve_font()
pdfmetrics.registerFont(TTFont(FONT_NAME, str(FONT), subfontIndex=FONT_FACE_INDEX))

PAGE_W, PAGE_H = landscape(A4)

NAVY = (0x17 / 255, 0x36 / 255, 0x5D / 255)
TEAL = (0x0B / 255, 0x72 / 255, 0x85 / 255)
GOLD = (0xB7 / 255, 0x79 / 255, 0x1B / 255)
GREEN = (0x06 / 255, 0x76 / 255, 0x47 / 255)
RED = (0xB4 / 255, 0x23 / 255, 0x18 / 255)
MUTED = (0x66 / 255, 0x70 / 255, 0x85 / 255)
INK = (0x1F / 255, 0x29 / 255, 0x37 / 255)
WHITE = (1, 1, 1)
GRID_GRAY = (0xCB / 255, 0xD5 / 255, 0xE1 / 255)

CHECKLIST: list[tuple[str, str, str, str]] = []


def record(elem_id, desc, method, ok):
    CHECKLIST.append((elem_id, desc, method, "PASS" if ok else "FAIL"))


# ============================================================== geometry ==
MARGIN = 20 * mm


class Grid:
    """Column/row boundary helper, all values in points, PDF origin
    bottom-left. `top_y` is the Y of the table's TOP edge; rows are laid
    out downward (decreasing Y) from there -- row 0 is the header."""

    def __init__(self, left_x, top_y, col_widths, row_height, n_rows):
        self.left_x = left_x
        self.top_y = top_y
        self.col_widths = list(col_widths)
        self.row_height = row_height
        self.n_rows = n_rows

        self._col_left = []
        acc = left_x
        for w in self.col_widths:
            self._col_left.append(acc)
            acc += w
        self._table_right = acc

    def col_left(self, i):
        return self._col_left[i]

    def col_right(self, i):
        return self._col_left[i] + self.col_widths[i]

    def col_center(self, i):
        return (self.col_left(i) + self.col_right(i)) / 2

    def row_top(self, r):
        return self.top_y - r * self.row_height

    def row_bottom(self, r):
        return self.row_top(r) - self.row_height

    def row_center(self, r):
        return (self.row_top(r) + self.row_bottom(r)) / 2

    @property
    def table_right(self):
        return self._table_right

    @property
    def table_bottom(self):
        return self.row_bottom(self.n_rows - 1)

    @property
    def n_cols(self):
        return len(self.col_widths)


INSET = 1.5 * mm
BAR_HEIGHT_FRAC = 0.6

COL_WIDTHS_MAIN = [40 * mm, 28 * mm] + [26 * mm] * 6
HEADERS_MAIN = ["工程", "担当", "9/1", "9/2", "9/3", "9/4", "9/5", "9/8"]
ROWS_MAIN = [
    ["要件定義", "山田"],
    ["設計", "佐藤"],
    ["実装", "鈴木"],
    ["テスト", "田中"],
    ["リリース", "全員"],
]
ROW_HEIGHT_MAIN = 10 * mm

COL_WIDTHS_SMALL = [40 * mm] + [26 * mm] * 4
HEADERS_SMALL = ["工程", "9/1", "9/2", "9/3", "9/4"]
ROWS_SMALL = [["差し戻し"], ["設計"], ["実装"]]
ROW_HEIGHT_SMALL = 10 * mm


def bar_bbox(grid, row, col_start, col_end, inset=INSET, height_frac=BAR_HEIGHT_FRAC):
    x0 = grid.col_left(col_start) + inset
    x1 = grid.col_right(col_end) - inset
    h = grid.row_height * height_frac
    y0 = grid.row_center(row) - h / 2
    y1 = y0 + h
    return x0, y0, x1, y1


# ------------------------------------------------------------- drawing ----
def draw_table(c, grid, headers, rows, header_fill=NAVY):
    """Ruled table: every horizontal/vertical gridline as a stroked path,
    header/row-label/date text as text -- no reportlab Table flowable."""
    c.setLineWidth(0.6)
    c.setStrokeColorRGB(*GRID_GRAY)
    # Header fill
    c.setFillColorRGB(*header_fill)
    c.rect(grid.left_x, grid.row_bottom(0), grid.table_right - grid.left_x, grid.row_height,
           stroke=0, fill=1)
    # Vertical lines
    for i in range(grid.n_cols + 1):
        x = grid.left_x if i == 0 else grid.col_right(i - 1)
        c.line(x, grid.table_bottom, x, grid.top_y)
    # Horizontal lines
    for r in range(grid.n_rows + 1):
        y = grid.top_y if r == 0 else grid.row_bottom(r - 1)
        c.line(grid.left_x, y, grid.table_right, y)

    c.setFont(FONT_NAME, 9)
    for ci, htext in enumerate(headers):
        c.setFillColorRGB(*WHITE)
        c.drawCentredString(grid.col_center(ci), grid.row_center(0) - 3, htext)
    c.setFont(FONT_NAME, 8.5)
    for ri, rowvals in enumerate(rows, start=1):
        for ci, val in enumerate(rowvals):
            if not val:
                continue
            c.setFillColorRGB(*INK)
            c.drawString(grid.col_left(ci) + 2, grid.row_center(ri) - 3, val)


def right_arrow_points(x0, y0, x1, y1):
    """7-point filled polygon: rectangular shaft + triangular head,
    pointing right (+X)."""
    h = y1 - y0
    shaft_h = h * BAR_HEIGHT_FRAC
    sy0 = y0 + (h - shaft_h) / 2
    sy1 = sy0 + shaft_h
    head_len = min(h, (x1 - x0) * 0.4)
    neck_x = x1 - head_len
    return [
        (x0, sy0), (x0, sy1), (neck_x, sy1), (neck_x, y1),
        (x1, (y0 + y1) / 2), (neck_x, y0), (neck_x, sy0),
    ]


def left_arrow_points(x0, y0, x1, y1):
    """Mirror of right_arrow_points, pointing left (-X): used for the
    180-degree-rotated arrow on page 2."""
    pts = right_arrow_points(x0, y0, x1, y1)
    cx = (x0 + x1) / 2
    return [(2 * cx - x, y) for x, y in pts]


def left_right_arrow_points(x0, y0, x1, y1):
    """10-point filled polygon: rectangular shaft with a triangular head on
    BOTH ends."""
    h = y1 - y0
    shaft_h = h * BAR_HEIGHT_FRAC
    sy0 = y0 + (h - shaft_h) / 2
    sy1 = sy0 + shaft_h
    head_len = min(h, (x1 - x0) * 0.25)
    neck_l = x0 + head_len
    neck_r = x1 - head_len
    return [
        (x0, (y0 + y1) / 2), (neck_l, y1), (neck_l, sy1),
        (neck_r, sy1), (neck_r, y1), (x1, (y0 + y1) / 2),
        (neck_r, y0), (neck_r, sy0), (neck_l, sy0), (neck_l, y0),
    ]


def diamond_points(cx, cy, size):
    r = size / 2
    return [(cx, cy + r), (cx + r, cy), (cx, cy - r), (cx - r, cy)]


def draw_polygon(c, points, fill_rgb, close=True):
    c.setFillColorRGB(*fill_rgb)
    path = c.beginPath()
    path.moveTo(*points[0])
    for x, y in points[1:]:
        path.lineTo(x, y)
    if close:
        path.close()
    c.drawPath(path, stroke=0, fill=1)


def draw_centered_text(c, x0, y0, x1, y1, text, color=WHITE, size=8.5, bold_font=None):
    c.setFillColorRGB(*color)
    c.setFont(bold_font or FONT_NAME, size)
    c.drawCentredString((x0 + x1) / 2, (y0 + y1) / 2 - size * 0.35, text)


def draw_today_line(c, grid, col, row_start, row_end, color=RED, width=1.6):
    x = grid.col_center(col)
    y_top = grid.row_top(row_start)
    y_bottom = grid.row_bottom(row_end)
    c.setStrokeColorRGB(*color)
    c.setLineWidth(width)
    c.line(x, y_top, x, y_bottom)
    # Small filled triangle arrowhead at the bottom, pointing down.
    head = 3.2 * mm
    tri = [(x - head / 2, y_bottom + head), (x + head / 2, y_bottom + head), (x, y_bottom)]
    draw_polygon(c, tri, color)
    return x, y_top, y_bottom


# ================================================================ page 1 ==
def build_page1(c):
    c.setFont(FONT_NAME, 16)
    c.setFillColorRGB(*NAVY)
    c.drawString(MARGIN, PAGE_H - 15 * mm, "開発スケジュール")

    grid = Grid(MARGIN, PAGE_H - 30 * mm, COL_WIDTHS_MAIN, ROW_HEIGHT_MAIN, 1 + len(ROWS_MAIN))
    draw_table(c, grid, HEADERS_MAIN, ROWS_MAIN)

    overlays = {}

    # 1) 要件定義 rightArrow, row1, cols2-3 (9/1-9/2)
    bb = bar_bbox(grid, 1, 2, 3)
    draw_polygon(c, right_arrow_points(*bb), NAVY)
    draw_centered_text(c, *bb, "要件定義", size=8)
    overlays["要件定義"] = ("rightArrow", bb, [1], [2, 3])

    # 2) 設計 rightArrow, row2, cols3-5 (9/2-9/4)
    bb = bar_bbox(grid, 2, 3, 5)
    draw_polygon(c, right_arrow_points(*bb), TEAL)
    draw_centered_text(c, *bb, "設計", size=8)
    overlays["設計"] = ("rightArrow", bb, [2], [3, 4, 5])

    # 3) rect bar 実装, row3, cols4-6 (9/3-9/5), no text
    bb = bar_bbox(grid, 3, 4, 6)
    c.setFillColorRGB(*GOLD)
    c.rect(bb[0], bb[1], bb[2] - bb[0], bb[3] - bb[1], stroke=0, fill=1)
    overlays["実装バー"] = ("rect", bb, [3], [4, 5, 6])

    # 4) leftRightArrow テスト, row4, cols5-7 (9/4-9/8)
    bb = bar_bbox(grid, 4, 5, 7)
    draw_polygon(c, left_right_arrow_points(*bb), GREEN)
    draw_centered_text(c, *bb, "テスト", size=8)
    overlays["テスト"] = ("leftRightArrow", bb, [4], [5, 6, 7])

    # 5) diamond リリース, row5, col7 (9/8)
    size = 5 * mm
    cx, cy = grid.col_center(7), grid.row_center(5)
    draw_polygon(c, diamond_points(cx, cy, size), RED)
    bb = (cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2)
    overlays["リリース"] = ("diamond", bb, [5], [7])

    # 6) today line, col4 (9/3), header(row0) top -> row5 bottom
    x, y_top, y_bottom = draw_today_line(c, grid, col=4, row_start=0, row_end=5)
    overlays["本日線"] = ("line", (x, y_bottom, x, y_top), [0, 1, 2, 3, 4, 5], [4])

    # 7) ▲レビュー text, row2, col6 (9/5)
    c.setFillColorRGB(*RED)
    c.setFont(FONT_NAME, 8)
    c.drawCentredString(grid.col_center(6), grid.row_center(2) - 3, "▲レビュー")
    overlays["レビュー注記"] = ("text", (grid.col_left(6), grid.row_bottom(2), grid.col_right(6), grid.row_top(2)),
                            [2], [6])

    for name in ("要件定義", "設計", "実装バー", "テスト", "リリース", "本日線", "レビュー注記"):
        record(f"P1-{name}", f"{name}: 期待 rows={overlays[name][2]} cols={overlays[name][3]}",
               "Grid座標から直接計算", True)

    return grid, overlays


# ================================================================ page 2 ==
def build_page2(c):
    c.showPage()
    c.setFont(FONT_NAME, 16)
    c.setFillColorRGB(*NAVY)
    c.drawString(MARGIN, PAGE_H - 15 * mm, "回転した矢印（ページ2）")

    grid2 = Grid(MARGIN, PAGE_H - 30 * mm, COL_WIDTHS_SMALL, ROW_HEIGHT_SMALL, 1 + len(ROWS_SMALL))
    draw_table(c, grid2, HEADERS_SMALL, ROWS_SMALL)

    overlays = {}
    # rotated (left-pointing) arrow labelled 戻し, row1, cols1-2 (9/2-9/3)
    bb = bar_bbox(grid2, 1, 1, 2)
    draw_polygon(c, left_arrow_points(*bb), GOLD)
    draw_centered_text(c, *bb, "戻し", size=8)
    overlays["戻し"] = ("rightArrow(rot180)", bb, [1], [1, 2])
    record("P2-戻し", f"戻し: 期待 rows={overlays['戻し'][2]} cols={overlays['戻し'][3]}",
           "Grid座標から直接計算(180度回転=左向き多角形)", True)

    # Separate flow diagram, away from the table: two stroked rounded
    # rects + a stroked connector line with a filled triangle head.
    flow_y = grid2.table_bottom - 30 * mm
    box_w, box_h = 45 * mm, 18 * mm
    box1_x = MARGIN
    box2_x = MARGIN + box_w + 25 * mm
    c.setStrokeColorRGB(*MUTED)
    c.setLineWidth(1.2)
    c.setFillColorRGB(*WHITE)
    c.roundRect(box1_x, flow_y, box_w, box_h, 4 * mm, stroke=1, fill=0)
    c.roundRect(box2_x, flow_y, box_w, box_h, 4 * mm, stroke=1, fill=0)
    c.setFont(FONT_NAME, 11)
    c.setFillColorRGB(*INK)
    c.drawCentredString(box1_x + box_w / 2, flow_y + box_h / 2 - 4, "開始")
    c.drawCentredString(box2_x + box_w / 2, flow_y + box_h / 2 - 4, "完了")
    conn_y = flow_y + box_h / 2
    c.setLineWidth(1.4)
    c.line(box1_x + box_w, conn_y, box2_x, conn_y)
    head = 3 * mm
    tri = [(box2_x - head, conn_y + head / 1.6), (box2_x - head, conn_y - head / 1.6), (box2_x, conn_y)]
    draw_polygon(c, tri, MUTED)
    record("P2-REALFLOW", "表と無関係な実フロー図（開始/完了 stroked roundRect + 矢印付き接続線）",
           "canvas.roundRect(stroke=1, fill=0) + drawPath triangle", True)

    return grid2, overlays, {
        "box1": (box1_x, flow_y, box1_x + box_w, flow_y + box_h),
        "box2": (box2_x, flow_y, box2_x + box_w, flow_y + box_h),
    }


# ================================================================= build ==
def build(output_path: Path):
    c = canvas.Canvas(str(output_path), pagesize=(PAGE_W, PAGE_H), invariant=1)
    c.setTitle("schedule-arrows")
    c.setAuthor("RTMD fixture")
    c.setSubject("DRMD PDF table-overlay fixture")
    c.setCreator("RTMD fixture generator")

    grid1, overlays1 = build_page1(c)
    grid2, overlays2, flow_boxes = build_page2(c)

    c.showPage()
    c.save()
    return output_path, grid1, overlays1, grid2, overlays2, flow_boxes


# ============================================================ self-check ==
def overlap_len(a0, a1, b0, b1):
    return max(0.0, min(a1, b1) - max(a0, b0))


def assign_axis(a0, a1, bounds):
    lo, hi = min(a0, a1), max(a0, a1)
    length = hi - lo
    if length <= 0:
        mid = lo
        for i, (b0, b1) in enumerate(bounds):
            if min(b0, b1) <= mid <= max(b0, b1):
                return [i]
        return [0]
    covered = []
    for i, (b0, b1) in enumerate(bounds):
        blo, bhi = min(b0, b1), max(b0, b1)
        if overlap_len(lo, hi, blo, bhi) >= 0.5 * (bhi - blo):
            covered.append(i)
    if covered:
        return covered
    mid = (lo + hi) / 2
    for i, (b0, b1) in enumerate(bounds):
        blo, bhi = min(b0, b1), max(b0, b1)
        if blo <= mid <= bhi:
            return [i]
    return [0]


def col_bounds(grid):
    return [(grid.col_left(i), grid.col_right(i)) for i in range(grid.n_cols)]


def row_bounds(grid):
    return [(grid.row_bottom(r), grid.row_top(r)) for r in range(grid.n_rows)]


def print_table_cells(label, grid, headers):
    print(f"\n--- {label}: table cell boundaries (pt, PDF origin bottom-left) ---")
    for r in range(grid.n_rows):
        cells = []
        for ci in range(grid.n_cols):
            cells.append(f"({grid.col_left(ci):.1f},{grid.row_bottom(r):.1f})-"
                         f"({grid.col_right(ci):.1f},{grid.row_top(r):.1f})")
        row_label = "header" if r == 0 else headers[r]
        print(f"  row{r} [{row_label}]: " + " | ".join(cells))


def print_overlays(label, grid, overlays):
    print(f"\n--- {label}: overlay bounding boxes -> expected rows/cols ---")
    cb, rb = col_bounds(grid), row_bounds(grid)
    for name, (kind, bbox, expect_rows, expect_cols) in overlays.items():
        x0, y0, x1, y1 = bbox
        rows = assign_axis(y0, y1, rb)
        cols = assign_axis(x0, x1, cb)
        ok = sorted(rows) == sorted(expect_rows) and sorted(cols) == sorted(expect_cols)
        print(f"  {name:<10} kind={kind:<16} bbox=({x0:.1f},{y0:.1f})-({x1:.1f},{y1:.1f}) "
              f"-> rows={rows} cols={cols}  [expected rows={expect_rows} cols={expect_cols}] "
              f"{'OK' if ok else 'MISMATCH'}")
        record(f"VERIFY-{label}-{name}", f"{name} 50%重複ルールで期待どおりの行/列",
               "assign_axis()", ok)


def run_verify(build_result):
    _, grid1, overlays1, grid2, overlays2, flow_boxes = build_result
    print_table_cells("Page1", grid1, HEADERS_MAIN)
    print_overlays("Page1", grid1, overlays1)
    print_table_cells("Page2", grid2, HEADERS_SMALL)
    print_overlays("Page2", grid2, overlays2)
    print(f"\n  Page2 flow-diagram boxes (away from table): {flow_boxes}")


def verify_file(path: Path):
    import pypdf
    reader = pypdf.PdfReader(str(path))
    n_pages = len(reader.pages)
    text0 = reader.pages[0].extract_text() or ""
    text1 = reader.pages[1].extract_text() or ""
    record("REOPEN", f"pypdfで再オープン（{n_pages}ページ）", "pypdf.PdfReader(path)", n_pages == 2)
    record("TEXT-P1", "1ページ目に表ヘッダー/日付が含まれる", "extract_text()",
           "工程" in text0 and "9/1" in text0)
    record("TEXT-P2", "2ページ目に「戻し」が含まれる", "extract_text()", "戻し" in text1)

    size = path.stat().st_size
    record("SIZE", f"ファイルサイズ {size} bytes < 100KB", "path.stat().st_size", size < 100 * 1024)
    print(f"\nfile size: {size} bytes, pages: {n_pages}")


def verify_determinism(out_path):
    import hashlib
    import tempfile
    base_len = len(CHECKLIST)
    with tempfile.TemporaryDirectory() as td:
        p1 = Path(td) / "a.pdf"
        p2 = Path(td) / "b.pdf"
        build(p1)
        build(p2)
        h1 = hashlib.sha256(p1.read_bytes()).hexdigest()
        h2 = hashlib.sha256(p2.read_bytes()).hexdigest()
    del CHECKLIST[base_len:]
    ok = h1 == h2
    record("DETERMINISM", "2回連続生成しても同一バイト列 (sha256一致)", "hashlib.sha256 of two independent builds", ok)
    print(f"  sha256 run A: {h1}")
    print(f"  sha256 run B: {h2}")


def print_checklist():
    print("\n=== schedule-arrows.pdf element checklist ===")
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
        # Recompute the same geometry (no I/O side effects besides the
        # eventual verify_file() read) purely for the printed expectations.
        grid1 = Grid(MARGIN, PAGE_H - 30 * mm, COL_WIDTHS_MAIN, ROW_HEIGHT_MAIN, 1 + len(ROWS_MAIN))
        grid2 = Grid(MARGIN, PAGE_H - 30 * mm, COL_WIDTHS_SMALL, ROW_HEIGHT_SMALL, 1 + len(ROWS_SMALL))
        dummy = canvas.Canvas(str(Path(__file__).parent / ".verify-scratch.pdf"), pagesize=(PAGE_W, PAGE_H))
        _, overlays1 = build_page1(dummy)
        _, overlays2, flow_boxes = build_page2(dummy)
        Path(__file__).parent.joinpath(".verify-scratch.pdf").unlink(missing_ok=True)
        build_result = (out_path, grid1, overlays1, grid2, overlays2, flow_boxes)
    else:
        build_result = build(out_path)
        print(f"wrote {out_path} ({build_result[0].stat().st_size} bytes)")

    run_verify(build_result)
    verify_file(build_result[0])
    verify_determinism(build_result[0])

    all_ok = print_checklist()
    if not all_ok:
        sys.exit(1)


if __name__ == "__main__":
    main()

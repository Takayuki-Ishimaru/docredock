# schedule-shape-grid.pptx — expected readable(.md) tables

This is a **human-authored target**, not a golden file captured from a real
run. It shows what the GFM table for Slides 1, 2, and 4 of
`schedule-shape-grid.pptx` is expected to look like once `PptxAdapter`
implements shape-grid table synthesis per
「図形で組んだ表（shape-grid table）へのオーバーレイ対応」design spec. Use it
to sanity-check an implementation or a C# test's expected string; do not
diff a real run against this file byte-for-byte without re-checking
whitespace/escaping first.

Unlike `schedule-arrows.pptx` (native `a:tbl` + overlay shapes),
`schedule-shape-grid.pptx` has **no native table anywhere**. Its "tables"
are header rows and label columns built from adjacent `RECTANGLE` shapes;
the spec detects this grid from shape adjacency (row/column clustering,
50%-gap-tolerant), then applies the SAME overlay marker rules as the
native-table feature (`table-overlay-spec.md`) against the grid's derived
column/row boundaries.

Marker legend (identical to `schedule-arrows.expected.md`):
- `━━` / `━━▶` / `◀━━` : horizontal arrow body / right head / left head
- `━━` (bar) : plain bar, no direction, no start/end distinction — every
  covered column gets the same body glyph
- `│` / `▼` : vertical line body / down head (not used past Slide 1 here —
  Slide 2 has no today-line, Slide 4 has no vertical connector at all)
- `◆` : marker (diamond)
- A leading `テキスト ` before the first symbol is the overlay's label,
  placed on the first covered column (horizontal arrows) — bars carry no
  label prefix (`実装` bar is textless in this fixture, same as in
  `schedule-arrows.pptx`).
- `<br>` stands in for an embedded `\n` in a cell's `Text`.

Column/row indices below are the ones `generate_shape_grid_pptx.py --verify`
prints under "computed overlay row/column assignment" — copy them directly
into a C# test rather than re-deriving them by hand.

## Slide 1 「図形で組んだスケジュール」

Grid: header row (8 `RECTANGLE`s: 工程|担当|9/1|9/2|9/3|9/4|9/5|9/8) + label
column (5 `RECTANGLE`s under 工程) + a second label column (5 `RECTANGLE`s
under 担当). Body area under the date columns carries **no rectangles at
all** — only the overlays below. This is the same schedule content as
`schedule-arrows.pptx` Slide 1 (同一の行/列/テキスト), so — once the grid's
column/row boundaries are resolved from the header/label rectangles instead
of `a:gridCol`/`a:tr` — the resulting table is byte-identical to
`schedule-arrows.expected.md`'s Slide 1 table, including the today-line's
`│`/`▼` markers and the `9/3<br>│` header-cell marker.

Overlay inventory (rows/cols confirmed by `--verify`):

| # | Shape | Kind | Direction | Rows | Columns (0-based) | Text |
|---|-------|------|-----------|------|--------------------|------|
| 1 | Right Arrow | arrow | right | 1 | 2–3 (9/1–9/2) | 要件定義 |
| 2 | Right Arrow | arrow | right | 2 | 3–5 (9/2–9/4) | 設計 |
| 3 | Rectangle (dark, no text) | bar | none | 3 | 4–6 (9/3–9/5) | (none) |
| 4 | Left-Right Arrow | arrow | both | 4 | 5–7 (9/4–9/8) | テスト |
| 5 | Diamond | marker | none | 5 | 7 (9/8) | (none) |
| 6 | Straight connector (今日線), triangle tailEnd | arrow* | down | 0–5 (incl. header) | 4 (9/3) | (none) |
| 7 | TextBox | label | none | 2 | 6 (9/5) | ▲レビュー |

\* Same edge case as `schedule-arrows.pptx`: a connector with a `tailEnd`
arrowhead and no `headEnd` is promoted from `line` to `arrow`/`down`.

```
| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |
| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |
| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |
| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |
| リリース | 全員 |  |  | ▼ |  |  | ◆ |
```

### Per-cell derivation notes

- **header, col4 (9/3)** = `9/3<br>│`: the today-line's row range starts at
  `row_top(0)` (the header rectangle's own top edge), so it covers the
  header row too; its `│` body marker is appended after the header
  rectangle's own text `9/3`.
- **(row1, col2=9/1)** = `要件定義 ━━`: arrow #1 covers exactly 2 columns
  (2–3), so its first column gets label+body combined.
- **(row1, col3=9/2)** = `━━▶`: arrow #1's last column → head.
- **(row1, col4=9/3)** = `│`: only the today-line reaches this cell in row 1.
- **(row2, col3=9/2)** = `設計 ━━`: arrow #2's first column → label+body.
- **(row2, col4=9/3)** = `│<br>━━`: today-line body (`│`) first, then arrow
  #2's middle column (`━━`, col4 is strictly between c1=3 and c2=5).
- **(row2, col5=9/4)** = `━━▶`: arrow #2's last column → head.
- **(row2, col6=9/5)** = `▲レビュー`: label-kind textbox #7, plain text only.
- **(row3, col4=9/3)** = `│<br>━━`: today-line body, then bar #3's first
  covered column (bar rule: every covered column gets the same `━━`, no
  start/end distinction, no label since the bar is textless).
- **(row3, col5=9/4)**, **(row3, col6=9/5)** = `━━`: bar #3's remaining
  columns.
- **(row4, col4=9/3)** = `│`: arrow #4 covers columns 5–7, not 4; only the
  today-line reaches this cell.
- **(row4, col5=9/4)** = `テスト ◀━━`: arrow #4 (`both`), first column →
  label + left head.
- **(row4, col6=9/5)** = `━━`: arrow #4's middle column.
- **(row4, col7=9/8)** = `━━▶`: arrow #4's last column → right head.
- **(row5, col4=9/3)** = `▼`: today-line's last covered row → down head.
- **(row5, col7=9/8)** = `◆`: marker #5 (diamond).

## Slide 2 「本体セルあり」

Same header/label rectangles as Slide 1, plus a full 5×6 grid of body
`RECTANGLE`s under the date columns (thin outline, no fill, no text) except
two cells that carry real text — `済` at 実装×9/8 and `予定` at
リリース×9/5 — and the SAME overlay set as Slide 1 **except the today-line
connector** (`--verify` confirms `Overlay-TodayLine` is absent and every
other overlay's row/column assignment is unchanged from Slide 1).

Removing the today-line clears every cell whose only marker was `│`/`▼`
(the header's `9/3` cell, and column 9/3 in rows 要件定義/テスト/リリース),
and removes the `│<br>` prefix from the two cells where it had stacked with
another overlay (設計/実装 rows, column 9/3 — those keep only the
surviving overlay's own glyph). Neither `済` nor `予定` is covered by any
overlay, so they stand alone with no marker stacking.

```
| 工程 | 担当 | 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ |  |  |  |  |
| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ | ▲レビュー |  |
| 実装 | 鈴木 |  |  | ━━ | ━━ | ━━ | 済 |
| テスト | 田中 |  |  |  | テスト ◀━━ | ━━ | ━━▶ |
| リリース | 全員 |  |  |  |  | 予定 | ◆ |
```

### Per-cell derivation notes (deltas from Slide 1 only)

- **header, col4 (9/3)** = `9/3`: no today-line, so no `│` suffix.
- **(row1, col4=9/3)**, **(row4, col4=9/3)**, **(row5, col4=9/3)**: empty —
  these cells received ONLY the today-line's marker on Slide 1; with the
  connector gone there is nothing left to render (the 30 empty body
  rectangles contribute no text of their own).
- **(row2, col4=9/3)** = `━━` (was `│<br>━━`): arrow #2's middle-column body
  survives; the today-line's `│<br>` prefix is gone.
- **(row3, col4=9/3)** = `━━` (was `│<br>━━`): bar #3's first-column body
  survives likewise.
- **(row3, col7=9/8)** = `済`: body rectangle text, no overlay covers this
  cell (bar #3 only reaches columns 4–6).
- **(row5, col6=9/5)** = `予定`: body rectangle text, no overlay covers this
  cell (diamond #5 only covers column 7).
- **(row5, col7=9/8)** = `◆`: unchanged from Slide 1 (diamond is unaffected
  by the today-line's removal).

## Slide 3 「カード型（表ではない）」 — negative case

A 2×3 grid of 6 `ROUNDED_RECTANGLE` "cards" (機能A…機能F) with **no
overlays** in the body area. Per the design spec's grid-adoption condition
8(b) ("少なくとも1つのオーバーレイが本体領域に存在する"), an aligned
rectangle group with zero overlays must NOT be synthesized into a table —
this slide stays as ordinary shape nodes (a visual/card layout), same as
the existing "aligned rectangles without overlays" rule already exercised
informally by other fixtures. `generate_shape_grid_pptx.py --verify`
confirms no `rightArrow`/`leftRightArrow`/`diamond` `prstGeom` exists
anywhere on this slide. Below the cards, a genuine flow (開始→完了,
`stCxn`/`endCxn`-connected) confirms grid detection does not interfere with
real diagrams sharing the slide.

No expected table for this slide — it must not produce one.

## Slide 4 「ラベル列なし」

Header row of 6 `RECTANGLE`s (9/1|9/2|9/3|9/4|9/5|9/8) with **no 工程/担当
columns and no label column at all**. Three `RIGHT_ARROW` overlays sit on
three distinct Y bands (0.6in apart, directly below the header) and a
`DIAMOND` sits on a fourth band — since there is no label column, rows must
be derived purely from clustering the overlays' Y centres (design spec step
6), each row band's height taken as the row height. Per the spec's header
rule ("ラベル列があり、ヘッダ行に対応図形が無ければ先頭に空セル"), with NO
label column present at all there is no leading label cell either — each
row is just its glyph cells for the 6 date columns.

Overlay inventory (rows/cols confirmed by `--verify`):

| # | Shape | Rows | Columns (0-based) | Text |
|---|-------|------|--------------------|------|
| 1 | Right Arrow | 1 | 0–1 (9/1–9/2) | 要件定義 |
| 2 | Right Arrow | 2 | 1–3 (9/2–9/4) | 設計 |
| 3 | Right Arrow | 3 | 2–4 (9/3–9/5) | 実装 |
| 4 | Diamond | 4 | 5 (9/8) | (none) |

```
| 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |
| --- | --- | --- | --- | --- | --- |
| 要件定義 ━━ | ━━▶ |  |  |  |  |
|  | 設計 ━━ | ━━ | ━━▶ |  |  |
|  |  | 実装 ━━ | ━━ | ━━▶ |  |
|  |  |  |  |  | ◆ |
```

### Per-cell derivation notes

- **(row1, col0=9/1)** = `要件定義 ━━`, **(row1, col1=9/2)** = `━━▶`: arrow
  #1 covers exactly 2 columns (0–1) → label+body, then head.
- **(row2, col1=9/2)** = `設計 ━━`, **(row2, col2=9/3)** = `━━`,
  **(row2, col3=9/4)** = `━━▶`: arrow #2 covers 3 columns (1–3) →
  label+body, middle body, head.
- **(row3, col2=9/3)** = `実装 ━━`, **(row3, col3=9/4)** = `━━`,
  **(row3, col4=9/5)** = `━━▶`: arrow #3 covers 3 columns (2–4), same
  pattern as arrow #2, shifted right by one column and one row band.
- **(row4, col5=9/8)** = `◆`: diamond #4, alone on the fourth Y band.

## Cross-check against generate_shape_grid_pptx.py

Run `python3 generate_shape_grid_pptx.py schedule-shape-grid.pptx` (or
`--verify` against an already-generated file) — its printed "computed
overlay row/column assignment" section for Slides 1, 2, and 4 reproduces
the Rows/Columns columns of the overlay inventory tables above from the
raw `a:off`/`a:ext`/`rot` XML using the same 50%-overlap rule, so the two
should always agree.

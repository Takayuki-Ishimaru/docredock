# schedule-arrows.pptx — Slide 1 expected readable(.md) table

This is a **human-authored target**, not a golden file captured from a real
run. It shows what the GFM table for Slide 1 (`開発スケジュール`) is
expected to look like once `ReadableMarkdownSerializer` implements
`ApplyTableOverlays` per the table-overlay design spec's marker rules. Use
it to sanity-check a serializer implementation or a C# test's expected
string; do not diff a real run against this file byte-for-byte without
re-checking whitespace/escaping first.

Marker legend used below (see the spec's マーカー規則 section):
- `━━` / `━━▶` / `◀━━` : horizontal arrow body / right head / left head
  (single-column arrows would collapse to `━▶` / `◀━`, not used on this slide)
- `━━` (bar), `━` (single-column bar) : plain bar, no direction
- `│` / `▼` / `▲` : vertical line body / down head / up head
- `◆` : marker (diamond, and anything not `ellipse`/`triangle`)
- A leading `テキスト ` before the first symbol is the overlay's label,
  placed on the first covered column (horizontal) or first covered row
  (vertical).
- `<br>` stands in for an embedded `\n` in a cell's `Text` (GFM table cells
  cannot contain literal newlines; `TableText` renders `\n` as `<br>`).

## Overlay inventory (Slide 1)

| # | Shape | Kind | Direction | Axis | Rows | Columns (0-based) | Text |
|---|-------|------|-----------|------|------|--------------------|------|
| 1 | Right Arrow (要件定義) | arrow | right | horizontal | 1 | 2–3 (9/1–9/2) | 要件定義 |
| 2 | Right Arrow (設計) | arrow | right | horizontal | 2 | 3–5 (9/2–9/4) | 設計 |
| 3 | Rectangle (実装 bar) | bar | none | horizontal | 3 | 4–6 (9/3–9/5) | (none) |
| 4 | Left-Right Arrow (テスト) | arrow | both | horizontal | 4 | 5–7 (9/4–9/8) | テスト |
| 5 | Diamond (リリース) | marker | none | horizontal | 5 | 7 (9/8) | (none) |
| 6 | Connector (本日線) | arrow* | down | vertical | 0–5 (**incl. header**) | 4 (9/3) | (none) |
| 7 | TextBox (▲レビュー) | label | none | horizontal | 2 | 6 (9/5) | ▲レビュー |

\* The connector carries a `<a:tailEnd type="triangle"/>` arrowhead with no
`headEnd`. Per the spec's connector rule ("矢尻が無ければ Kind "line"、
Direction "none""), an arrowhead present at exactly one end promotes it out
of the plain-`line` Kind into an `arrow` with a direction taken from the
start→end vector — here the connector runs top→bottom, so Direction=`down`.
This is a deliberate edge case: it both (a) crosses the header row, because
its geometry runs `row_top(0)` → `row_bottom(5)`, and (b) shares column 4
(9/3) with the 設計 arrow (#2, only over its single middle column) and the
実装 bar (#3, only over its first column), so those two cells accumulate
**two** stacked markers.

Processing order (sorted by `(StartRow, StartColumn, ShapeId)`, ascending —
this is also the order in which stacked markers are appended within a
cell): `#6 connector(0,4)` → `#1 arrow(1,2)` → `#2 arrow(2,3)` →
`#7 label(2,6)` → `#3 bar(3,4)` → `#4 arrow(4,5)` → `#5 marker(5,7)`.

## Expected table

Note the header row's own 9/3 cell: the today-line connector's row range
starts at `row_top(0)` (the top of the header row), so its `│` body marker
lands on the header cell too, appended after the existing header text
`9/3` — rendered as `9/3<br>│`. This is the ONE physical header row of the
emitted table (there is no second, unmodified header row in real output).

```
| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |
| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |
| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |
| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |
| リリース | 全員 |  |  | ▼ |  |  | ◆ |
```

## Per-cell derivation notes

- **(row1, col2=9/1)** = `要件定義 ━━`: arrow #1, c1=2 (first covered column),
  Direction=right → label + body marker (`c1..c2-1` rule collapses to a
  single body cell since the arrow covers exactly 2 columns: c1=2 gets the
  body `━━`, c2=3 gets the head).
- **(row1, col3=9/2)** = `━━▶`: arrow #1, c2=3 (last covered column) → head.
- **(row1, col4=9/3)** = `│`: only the connector (#6) reaches this cell for
  row 1; row 1 is in `r1..r2-1` (r1=0, r2=5) of the vertical arrow-down
  rule → body glyph `│`.
- **(row2, col3=9/2)** = `設計 ━━`: arrow #2, c1=3 → label + body.
- **(row2, col4=9/3)** = `│<br>━━`: connector (#6) writes first (`│`, this
  row is in `r1..r2-1`), then arrow #2 writes its middle-column body (`━━`)
  since col4 is strictly between c1=3 and c2=5 for that arrow. Appended
  with `\n` → rendered as `<br>`.
- **(row2, col5=9/4)** = `━━▶`: arrow #2, c2=5 → head.
- **(row2, col6=9/5)** = `▲レビュー`: label-kind textbox #7, no arrow/marker
  glyph, just the text.
- **(row3, col4=9/3)** = `│<br>━━`: connector (#6) body (`│`) then bar #3's
  first column (bar rule has no start/end distinction, every covered
  column just gets `━━`).
- **(row3, col5=9/4)**, **(row3, col6=9/5)** = `━━`: bar #3, remaining
  columns.
- **(row4, col4=9/3)**: NOT covered by arrow #4 (its columns are 5–7), so
  this cell only ever receives the connector's `│` — unlike rows 2 and 3 it
  has no second overlay to stack.
- **(row4, col5=9/4)** = `テスト ◀━━`: arrow #4 (`both`), c1=5 → label +
  left head (`◀━━`).
- **(row4, col6=9/5)** = `━━`: arrow #4, middle column.
- **(row4, col7=9/8)** = `━━▶`: arrow #4, c2=7 → right head.
- **(row5, col4=9/3)** = `▼`: connector (#6), row 5 is r2 (last covered
  row) of the vertical arrow-down rule → down head.
- **(row5, col7=9/8)** = `◆`: marker #5 (diamond → `◆` per "それ以外は◆").
- **header row, col4=9/3** = `9/3<br>│`: the connector's row range starts
  at `row_top(0)`, i.e. row 0 = the header row itself, so its `│` body
  marker lands on the header cell too, appended after the existing header
  text `9/3`.

## Cross-check against generate_schedule_pptx.py

Run `python3 generate_schedule_pptx.py schedule-arrows.pptx` (or
`--verify` against an already-generated file) — its printed "computed row/
column assignment" section for Slide 1 reproduces the Rows/Columns column
of the Overlay inventory table above from the raw `a:off`/`a:ext`/`rot` XML
using the same 50%-overlap rule, so the two should always agree.

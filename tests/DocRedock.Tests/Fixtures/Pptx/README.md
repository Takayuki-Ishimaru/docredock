# PPTX visual-review corpus

All Office documents in this directory are synthetic fixtures created for
DocRedock. They contain no customer documents, personal data, credentials, or
production identifiers.

The primary round-trip corpus is the checked-in
`real-office-roundtrip.original.pptx` beside this file. It is a four-slide
fictional Project Atlas release review. The corpus covers an image and rounded
crop, speaker notes, a five-stage flow with native connectors, a native chart,
a four-column native table, footer furniture, a slide master, a slide layout,
and theme parts. `PptxRealCorpusTests` checks F0 byte identity and an F1 title
change while asserting that chart, notes, media, master, layout, and theme parts
are unmodified.

For visual QA, render the fixture with a local PPTX renderer and compare every
slide before accepting a corpus or layout change. The checked-in fixture is the
authoritative test input; local render output is intentionally not distributed.

## complex-design-doc corpus (P01-P15 conversion-gap fixture)

`complex-design-doc.pptx` / `complex-design-doc.expectations.json` are the
PPTX leg of the synthetic complex design-doc corpus described in
`../COMPLEX_DESIGN_DOC_SPEC.md`. It targets `--profile readable` conversion
quality (P01–P15), not the visual-review round-trip tests above.

The checked-in presentation is deterministic and is the public regression-test
authority. Its development-only generator and source corpus are intentionally
excluded from the release because users do not need them to build, test, or use
DocRedock. The fixture was verified by reopening it with python-pptx and
checking the raw slide/chart/diagram XML for the OOXML constructs each element
(P01-P15) is supposed to exercise (`buAutoNum`,
`stCxn`/`endCxn`, `<p:grpSp>`, chart/diagram `graphicData` URIs,
`strike="sngStrike"`, `rot="2700000"`, footer/date/slideNum placeholder
types, absence of the forbidden OCR token), printing a PASS/FAIL checklist
and exiting non-zero on any unexpected failure. Images (IMG-01/IMG-02) are
synthetic project assets embedded directly in the checked-in fixture.

### Element coverage (P01-P15)

All 15 elements are implemented; none are skipped. 13 use python-pptx's
documented API (`add_shape`/`add_connector`/`begin_connect`/`end_connect`,
`add_group_shape`, `add_table` + `cell.merge`, `add_chart`, `notes_slide`,
`shape.rotation`). Four needed raw OOXML injection because python-pptx has no
high-level surface for them:

- **buChar / buNone / buAutoNum** (P02-P04): python-pptx exposes paragraph
  `level` but not bullet character/scheme, so `<a:buFont>`, `<a:buChar>`,
  `<a:buNone>`, `<a:buAutoNum>` are built by hand under `pPr` (see
  `set_bullet_char` / `set_bullet_none` / `set_bullet_autonum`).
- **strikethrough** (P14): python-pptx has `font.bold/italic/underline` but
  no `strike`; set via `run._r.get_or_add_rPr().set("strike", "sngStrike")`.
- **footer / date / slide-number placeholders** (P13): `Slides.add_slide()`
  only clones the title/body placeholders from the layout, not the
  date/footer/sldNum ones (even though every stock layout declares them at
  idx 10/11/12). `add_furniture()` deep-copies those `<p:sp>` blocks from the
  slide's own layout and re-numbers their shape id.
- **SmartArt** (P07): python-pptx cannot create
  `<a:graphicData uri=".../diagram">` content at all. `inject_smartart()`
  rewrites the saved .pptx zip after `prs.save()`, adding a minimal-but-valid
  `ppt/diagrams/{data1,layout1,quickStyle1,colors1}.xml` part set (a real
  `dgm:dataModel` with 5 `doc`/`node`/`parTrans`/`sibTrans` points and
  `parOf` connections carrying the actual node text, plus deliberately
  thin/boilerplate layout/quickStyle/colors parts — SmartArt's algorithmic
  layout definitions run to hundreds of lines in real PowerPoint output and
  were not worth reproducing since nothing here renders the diagram
  visually) and a `<p:graphicFrame>` + relationships + `[Content_Types].xml`
  overrides referencing it. Verified in isolation before wiring into the
  full deck: python-pptx re-opens the result cleanly and the DRMD CLI exits
  0 on it (it simply never reads `ppt/diagrams/*`, which is exactly the P07
  gap under test — SmartArt text is silently dropped, not that the package
  becomes unreadable). Because the layout/quickStyle/colors parts are
  intentionally minimal, this file may prompt a repair dialog if opened in
  real PowerPoint; it was validated only via python-pptx and the DRMD CLI,
  never opened in PowerPoint itself. Slide 13 also carries the same 5 step
  names as ordinary CHEVRON autoshape text (not part of the SmartArt data
  model) so the slide still contributes real content regardless of whether
  `dgm:` extraction ever gets implemented; `expectations.json` P07-2 uses
  this duplication (`count`/`min: 2`) to detect the day dgm: text starts
  being read.

### expectations.json

59 items: 2-3 per P01-P15 element plus the 12 QA facts from
`COMPLEX_DESIGN_DOC_SPEC.md` section 1 (each as one or more `guard`
`contains` items) and the mandatory `OCR-JP-20260823-017` `not_contains`
guard (QA12). Severities were assigned empirically — every `guard` item was
checked against a real `--profile readable` conversion of this exact file
before being committed, not just against the spec's a-priori gap
descriptions; a couple of elements turned out to already convert better than
the spec assumed, so they became guards instead of goals (see below).

Run `tools/conversion-qa/run.py` against the current implementation for the authoritative score. Native charts are now extracted as readable summaries, so the original P06 "chart vanishes" expectation is historical rather than a current gap. Remaining observations below describe the fixture and unresolved areas:

- The only failing guard is **QA12** (`OCR-JP-20260823-017` not_contains),
  and only because this machine has an OCR engine wired up and
  `--profile readable`'s default `--ocr auto` resolves to on: the CLI
  legitimately OCRs IMG-02 and appends the receipt text (including the
  forbidden token) inside a `<details class="ocr-extraction">` block.
  Re-running with `--ocr off` makes QA12 pass cleanly (confirmed). This is
  an environment/CLI-flag concern, not a fixture defect —
  `tools/conversion-qa` should pin `--ocr off` when it evaluates this guard,
  for determinism across machines that may or may not have an OCR engine
  installed.
- **P12-2** ("読み順": left column fully before right column) already passes
  today even though the spec lists P12 under goals — this fixture's Two
  Content layout happens to already read left-then-right correctly.
- **P09** (merged-cell table) also converts better than the spec's "列がず
  れる" assumption suggested: columns stay correctly aligned around the
  blank continuation cells (P09-1, guard). Only the finer "repeat the merged
  label on every row" enhancement is still a goal (P09-2).
- The remaining historical gap areas include P03, P05, P07-2, P08-3, P10-2, P14-3, and P15-2: buAutoNum and inherited bullets can degrade, SmartArt-only text can be dropped, connector/group relationships are not fully reconstructed, notes flatten to a plain readable form, and strikethrough/45° rotation may leave no trace. P06 native charts are now covered by extraction regressions.

Smoke test: `dotnet run --project src/DocRedock.Cli -c Release -- export
complex-design-doc.pptx --profile readable --output <out>.md --force --quiet`
exits **1** (not 0) with `WARNING EmbeddedObjectPresent: Embedded or ActiveX
content is preserved as passthrough and never executed.` — this is expected:
every native chart (P06) embeds its backing workbook at
`ppt/embeddings/Microsoft_Excel_SheetN.xlsx` per the OOXML chart spec, and
the converter's package inspector conservatively (and correctly) flags any
`/embeddings/` part. Not a fixture bug.

## schedule-arrows corpus (PPTX table-overlay fixture)

`schedule-arrows.pptx` is a synthetic Japanese IT-project schedule deck used
by the "table overlay" feature: PowerPoint schedule slides commonly place
arrow shapes, rectangle bars, diamond milestones, a vertical "today" line,
and small text boxes ON TOP OF a native table (`a:tbl`) whose columns are
dates, to express a Gantt-like progress view. The feature detects which
table row(s)/column(s) each overlay shape covers and folds that into the
table cells as markers (e.g. `設計 ━━ | ━━ | ━━▶`) instead of emitting the
shapes as unrelated paragraphs or feeding them into "Visual flow" inference.
`schedule-arrows.expected.md` (next to the .pptx) is the human-authored
target GFM table for Slide 1, derived by hand from the design spec's marker
rules — a study aid and cross-check, not a captured golden file.

Its generator, `generate_schedule_pptx.py`, is **dev-only**: it is not part
of the build, is not required to run the test suite (the checked-in
`.pptx` is the regression-test authority, same convention as
`generate_complex_pptx.py`), and exists so the fixture's geometry can be
regenerated or extended without hand-editing OOXML. Regenerate with:

```
python3 tests/DocRedock.Tests/Fixtures/Pptx/generate_schedule_pptx.py \
    tests/DocRedock.Tests/Fixtures/Pptx/schedule-arrows.pptx
```

`schedule-arrows.powerpoint-saved.pptx` is the same deck after being opened
and re-saved (Save As → .pptx) by Microsoft PowerPoint for Mac 16.x, then
normalized (zip member timestamps fixed to 1980-01-01, `cp:lastModifiedBy`
replaced with "RTMD fixture"; no other content edited). It is NOT produced
by the generator. It exists because real PowerPoint output decorates every
table `a:gridCol`/`a:tr` with an `<a:extLst><a:ext uri="…"><a16:colId|rowId/>`
block placed after the frame's `p:xfrm`, and every `p:cNvPr` with an
`a16:creationId` extension — XML that python-pptx never writes and that once
made the extractor treat a uri-only `a:ext` as a size element (zeroing the
table frame and silently disabling overlay detection on PowerPoint-saved
decks). `PptxScheduleOverlayFixtureTests` asserts this variant renders
exactly the same readable Markdown as the generated deck.

Add `--verify` to re-check an already-generated file without rebuilding it.
The script also runs its own checklist at the end of every generation
(re-open with python-pptx, inspect raw slide XML for the exact OOXML
constructs each overlay is supposed to produce, and hash-compare two
independent builds to prove the output is byte-identical across runs) and
exits non-zero if anything fails.

### Slide-by-slide element list

Slide dimensions: 13.333in × 7.5in (16:9). No footer/date/slide-number
placeholders. Column/row indices below are 0-based, matching `a:gridCol`/
`a:tr` order; "9/1" etc. are date-column labels, not literal calendar
computation.

**Slide 1 「開発スケジュール」** — table: 6 rows × 8 columns
(`工程|担当|9/1|9/2|9/3|9/4|9/5|9/8`), left 0.5in/top 1.5in/width 12.3in/
height 3.6in, column widths `[1.6, 1.1, 1.6, 1.6, 1.6, 1.6, 1.6, 1.6]`in,
row heights `0.6`in each (all explicit, via `tbl.columns[i].width` /
`tbl.rows[r].height`, so the overlay geometry below is computed from the
exact same numbers via `col_left(i)`/`col_right(i)`/`row_top(r)`/
`row_bottom(r)` helpers in the generator):

| Row (label) | Shape | Columns covered | Text |
|---|---|---|---|
| 1 (要件定義) | `RIGHT_ARROW` | 2–3 (9/1–9/2) | 要件定義 |
| 2 (設計) | `RIGHT_ARROW` | 3–5 (9/2–9/4) | 設計 |
| 2 (設計) | `TEXT_BOX` | 6 (9/5) | ▲レビュー |
| 3 (実装) | `RECTANGLE` (solid fill, no text) | 4–6 (9/3–9/5) | (none) |
| 4 (テスト) | `LEFT_RIGHT_ARROW` | 5–7 (9/4–9/8) | テスト |
| 5 (リリース) | `DIAMOND` (~0.3in square) | 7 (9/8) | (none) |
| 0–5 (all rows, incl. header) | straight connector, unconnected | 4 (9/3) | (none) |

The last row is the "本日" (today) line: a vertical
`shapes.add_connector(MSO_CONNECTOR.STRAIGHT, ...)`, deliberately NOT
connected to any shape (`begin_connect`/`end_connect` are never called on
it), running through the horizontal centre of the 9/3 column from
`row_top(0)` to `row_bottom(5)` — i.e. it spans the header row too. Red
line, and a triangle arrowhead injected as raw XML because **python-pptx
has no line-arrowhead API**: `ln = connector.line._get_or_add_ln()` (the
private accessor for the `<a:ln>` element) then
`ln.insert_element_before(ln.makeelement(qn("a:tailEnd"), {"type":
"triangle"}), "a:extLst")` to keep correct OOXML child ordering
(`...headEnd, tailEnd, extLst`). Per the design spec's own connector rule,
a connector with an arrowhead at exactly one end is no longer a plain
`line` overlay — it becomes an `arrow` with `Direction` taken from the
start→end vector (here: `down`, since the line runs top→bottom), which is
an intentional edge case this fixture exercises (see
`schedule-arrows.expected.md` for the resulting per-cell marker stacking,
including two cells that receive two overlays' markers on top of each
other).

**Slide 2 「グループ化されたスケジュール」** — same table shape/labels as
slide 1, but only rows 1–2's `RIGHT_ARROW` overlays (要件定義 9/1–9/2,
設計 9/2–9/4) are present, and they live inside a `<p:grpSp>` (via
`shapes.add_group_shape([arrow1, arrow2])`).

*python-pptx limitation worked around*: `GroupShapes` has no `add_table` —
a table cannot be created inside a group through the API, so the table
stays a slide-level shape and only the two arrows are grouped, per the
task's own fallback instruction. Separately, `add_group_shape()`/
`recalculate_extents()` always produce an **identity** transform
(`a:chOff == a:off`, `a:chExt == a:ext`), which would never exercise the
affine-transform math a real extractor needs for group children. So after
grouping, `apply_nontrivial_group_transform()` rewrites the group's
`a:chOff`/`a:chExt` to a coordinate space that is both shifted (by 0.5in/
0.3in) and scaled (factor 1.5) relative to `a:off`/`a:ext`, and rewrites
each child `<p:sp>`'s own `a:off`/`a:ext` (which are expressed in that
local/child space) using the inverse of the standard OOXML group-transform
formula (`abs = off + (local - chOff) * (ext / chExt)`) so that the
shapes' **absolute on-slide position is unchanged** — i.e. the arrows still
sit exactly on 9/1–9/2 and 9/2–9/4 of row 1/2, but only *after* resolving
a non-trivial group transform, not by reading their raw XML off/ext
directly. `generate_schedule_pptx.py --verify` prints both the raw
child-local xfrm and the resolved absolute AABB for these two shapes so a
C# test can confirm its own transform math independently.

Also on slide 2, away from the table (bottom-left, below `y=5.1in`): two
`ROUNDED_RECTANGLE`s "開始"→"完了" joined by a `STRAIGHT` connector using
real `begin_connect`/`end_connect` (i.e. `a:stCxn`/`a:endCxn` are present)
— this is a genuine, natively-connected diagram, not an overlay, and is
included to confirm overlay detection does not swallow real diagrams that
merely share a slide with a table.

**Slide 3 「回転した矢印」** — table: 4 rows × 5 columns
(`工程|9/1|9/2|9/3|9/4`), left 0.5in/top 1.5in, column widths
`[1.7, 1.6, 1.6, 1.6, 1.6]`in, row heights `0.6`in each:

| Row (label) | Shape | Columns covered | Notes |
|---|---|---|---|
| 1 (差し戻し) | `RIGHT_ARROW`, `rotation=180` | 2–3 (9/2–9/3) | Text 「戻し」; symmetric rect AABB is unchanged by a 180° rotation, only the visual direction flips (arrowhead now points left). XML: `rot="10800000"`. |
| 1–3 (差し戻し/設計/実装) | `RIGHT_ARROW`, `rotation=90` | 4 (9/4) | No text. Pre-rotation width/height are deliberately swapped from the intended final AABB (`width = 3 × row height`, `height = 60% of the 9/4 column width`) because python-pptx's `left/top/width/height` always describe the shape's UN-rotated frame; after PowerPoint applies the 90° rotation around the frame's centre, width/height swap, landing the arrow's real AABB exactly on rows 1–3 of column 9/4. XML: `rot="5400000"`. |
| 2–3 (設計/実装) | `RECTANGLE` "共通" | 1–2 (9/1–9/2) | Genuine multi-row **and** multi-column bar. |

### Verification (no dotnet)

`generate_schedule_pptx.py` verifies itself without invoking `dotnet` (a
concurrent build/test run must not be disturbed): it re-opens the saved
file with python-pptx, walks the raw slide XML with `lxml`, and prints:

- every table's `a:gridCol/@w` list and `a:tr/@h` list, and that their sums
  equal the table frame's own width/height (from the `a:xfrm` on the
  `p:graphicFrame`);
- for every overlay shape: its `a:prstGeom/@prst` (`rightArrow`, `rect`,
  `leftRightArrow`, `diamond`, `line`), its `a:off`/`a:ext`, and its
  `a:xfrm/@rot` (checking specifically for `rot="10800000"` and
  `rot="5400000"` on slide 3);
- the `<a:tailEnd type="triangle"/>` on the today-line connector;
- `p:cNvSpPr/@txBox="1"` on the review textbox;
- `a:stCxn`/`a:endCxn` on slide 2's real-flow connector;
- slide 2's group `a:off`/`a:ext` vs `a:chOff`/`a:chExt` (and asserts they
  are **not** equal, proving the transform is non-trivial), then resolves
  each grouped arrow's absolute AABB through that transform;
- for **every** overlay shape on all three slides, the table row(s)/
  column(s) it would be assigned via the design spec's 50%-overlap rule
  (rotation-aware AABB: width/height are swapped when rotation rounds to
  90°/270°), so a C# test author can copy these expectations directly
  instead of re-deriving them;
- file size (< 100KB) and absence of any `ppt/media/*` (no images);
- a same-process, two-independent-builds SHA-256 comparison proving the
  output is byte-identical across runs.

All of the above prints as a `[PASS]`/`[FAIL]` checklist, and the script
exits non-zero if anything fails.

### Determinism

python-pptx's bundled default template already carries a **static**
`docProps/core.xml` (`created`/`modified` fixed at `2013-01-27T...`, not
the time of generation — confirmed by generating two presentations several
seconds apart and diffing their `core.xml`), so no `core_properties`
override was needed for content determinism. The one non-deterministic
knob python-pptx does not control is the **zip member timestamp**, which
defaults to wall-clock time via `zipfile.writestr`; `generate_schedule_pptx.py`
rewrites every zip entry's `date_time` to a fixed value after `prs.save()`
(`normalize_zip_timestamps`), which is what actually makes two separate
runs byte-identical (confirmed by the SHA-256 check above).

## schedule-shape-grid corpus (PPTX shape-grid-table fixture)

`schedule-shape-grid.pptx` is the companion fixture to `schedule-arrows.pptx`
for the OTHER very common Japanese PowerPoint "table" authoring style: a
grid of adjacent rectangle shapes standing in for a native table, instead
of an actual `a:tbl`. This is what "台形/矩形を並べて表にする" schedule
slides look like when authored without PowerPoint's own table tool — a
header row of rectangles with date text, a label column of rectangles with
process names, and arrows/bars/markers drawn on top of the (otherwise
empty) body area. The feature under test detects this rectangle grid
(header-row + label-column adjacency clustering, 50%-gap-tolerant, same
50%-overlap overlay rule as the native-table feature) and synthesizes a
`NodeKind.Table` from it, so it renders and round-trips the same way a
native-table-plus-overlays deck does. `schedule-shape-grid.expected.md`
(next to the .pptx) is the human-authored target GFM table for Slides 1, 2,
and 4 — a study aid and cross-check, not a captured golden file.

Its generator, `generate_shape_grid_pptx.py`, is **dev-only** (same
convention as `generate_schedule_pptx.py`/`generate_complex_pptx.py`): not
part of the build, not required to run the test suite, and exists so the
fixture's geometry can be regenerated or extended without hand-editing
OOXML. It imports its `Grid`/overlay/XML-verification helpers directly from
`generate_schedule_pptx.py` (same directory) rather than copying them, so
the two fixtures' geometry and verification logic cannot silently drift
apart. Regenerate with:

```
python3 tests/DocRedock.Tests/Fixtures/Pptx/generate_shape_grid_pptx.py \
    tests/DocRedock.Tests/Fixtures/Pptx/schedule-shape-grid.pptx
```

`schedule-shape-grid.powerpoint-saved.pptx` (when present) is the same deck
after being opened and re-saved (Save As → Open XML Presentation) by
Microsoft PowerPoint for Mac, then normalized (zip member timestamps fixed
to 1980-01-01, `cp:lastModifiedBy` replaced with "RTMD fixture"; no other
content edited) — same rationale and process as
`schedule-arrows.powerpoint-saved.pptx` above. It is NOT produced by the
generator.

Add `--verify` to re-check an already-generated file without rebuilding it.

### Slide-by-slide element list

Slide dimensions: 13.333in × 7.5in (16:9). Column/row indices below are
0-based; "9/1" etc. are date-column labels, not literal calendar
computation. All grid geometry (header/label rectangle positions AND
overlay positions) is derived from the same `Grid(left, top, col_widths,
row_heights)` helper imported from `generate_schedule_pptx.py`, so an
overlay's pixel position and its "which grid row/column does this cover"
answer can never drift apart.

**Main grid (Slides 1–2)**: left 0.5in / top 1.5in, column widths
`[1.6, 1.1, 1.6, 1.6, 1.6, 1.6, 1.6, 1.6]`in (工程, 担当, then 6 equal-width
date columns — `(12.3 - 1.6 - 1.1) / 6 = 1.6`in each), row heights
`[0.5, 0.6, 0.6, 0.6, 0.6, 0.6]`in (header row 0.5in, 5 label/body rows
0.6in each). In EMU (914400/in): column left edges
`[457200, 1920240, 2926080, 4389120, 5852160, 7315200, 8778240, 10241280]`,
right edge `11704320`; row top edges
`[1371600, 1828800, 2377440, 2926080, 3474720, 4023360]`, bottom edge
`4572000`.

**Slide 1 「図形で組んだスケジュール」** — 26 shapes (title + 8 header
rects + 5+5 label rects + 7 overlays), **no `a:tbl` anywhere**:

| Row (label) | Shape | Columns covered | Text |
|---|---|---|---|
| 1 (要件定義) | `RIGHT_ARROW` | 2–3 (9/1–9/2) | 要件定義 |
| 2 (設計) | `RIGHT_ARROW` | 3–5 (9/2–9/4) | 設計 |
| 2 (設計) | `TEXT_BOX` | 6 (9/5) | ▲レビュー |
| 3 (実装) | `RECTANGLE` (solid dark fill, no text) | 4–6 (9/3–9/5) | (none) |
| 4 (テスト) | `LEFT_RIGHT_ARROW` | 5–7 (9/4–9/8) | テスト |
| 5 (リリース) | `DIAMOND` (~0.3in square) | 7 (9/8) | (none) |
| 0–5 (all rows, incl. header) | straight connector, unconnected, triangle `tailEnd` | 4 (9/3) | (none) |

The header row (8 `RECTANGLE`s: 工程|担当|9/1|9/2|9/3|9/4|9/5|9/8) and the
two label columns (工程: 要件定義/設計/実装/テスト/リリース; 担当: 山田/
佐藤/鈴木/田中/全員, 5 `RECTANGLE`s each) sit flush against their
neighbours (no gap), light fill (header) / very light fill (labels), thin
gray outline. The body area under the date columns carries **no
rectangles** — the table content there comes entirely from the overlays,
same content/geometry as `schedule-arrows.pptx` Slide 1, so once the grid's
boundaries are resolved from these rectangles instead of `a:gridCol`/
`a:tr`, the resulting readable-Markdown table is expected to be identical
to `schedule-arrows.expected.md`'s Slide 1 table (see
`schedule-shape-grid.expected.md`).

**Slide 2 「本体セルあり」** — 55 shapes (title + 8 header + 5+5 label +
30 body + 6 overlays), same header/label rectangles as Slide 1, but the
body area ALSO carries a full 5×6 grid of `RECTANGLE`s under the date
columns (thin outline, no fill (`<a:noFill/>`), no text) except two cells
with real text — `済` at 実装×9/8, `予定` at リリース×9/5 — exercising
"body-cell text becomes the table cell's text" mapping. Same overlay set
as Slide 1 **except the today-line connector** (dropped, so its `│`/`▼`
markers don't appear — see `schedule-shape-grid.expected.md` for the
resulting table).

**Slide 3 「カード型（表ではない）」** — 10 shapes (title + 6
`ROUNDED_RECTANGLE` cards in a 2×3 grid + 2 more `ROUNDED_RECTANGLE`s +
1 connector), the **negative case**: the 6 cards (機能A…機能F) carry NO
overlays anywhere in their area, so per the design spec's grid-adoption
condition ("少なくとも1つのオーバーレイが本体領域に存在する") this aligned
rectangle group must NOT be synthesized into a table — it stays as
ordinary shape nodes. Below the cards, a genuine flow (開始→完了 via
`begin_connect`/`end_connect`, i.e. real `a:stCxn`/`a:endCxn`) confirms
grid detection does not swallow real diagrams sharing the slide.

**Slide 4 「ラベル列なし」** — 11 shapes (title + 6 header rects + 4
overlays): a header row of 6 date `RECTANGLE`s only (9/1|9/2|9/3|9/4|9/5|
9/8, left 0.5in/top 1.5in, `1.6`in each, header height 0.5in) with **no
工程/担当 columns and no label column at all**. Three `RIGHT_ARROW`
overlays sit on three distinct Y bands (0.6in apart, directly below the
header) plus a `DIAMOND` on a fourth band:

| Y band | Shape | Columns covered | Text |
|---|---|---|---|
| 1 | `RIGHT_ARROW` | 0–1 (9/1–9/2) | 要件定義 |
| 2 | `RIGHT_ARROW` | 1–3 (9/2–9/4) | 設計 |
| 3 | `RIGHT_ARROW` | 2–4 (9/3–9/5) | 実装 |
| 4 | `DIAMOND` | 5 (9/8) | (none) |

Since there is no label column, this exercises "rows derived from
clustering the overlays' own Y centres" (design spec step 6) rather than
from a label column's rectangles.

### Verification (no dotnet)

`generate_shape_grid_pptx.py` verifies itself without invoking `dotnet`: it
re-opens the saved file with python-pptx, walks the raw slide XML with
`lxml`, and prints/checks:

- shape count per slide (26 / 55 / 10 / 11) against the counts above;
- absence of any `a:tbl` on every slide;
- every header rectangle's width sum vs. the grid's total width, and every
  label rectangle's height sum vs. the grid's body height (both in EMU);
- for every `Overlay-*`-named shape: its `a:prstGeom/@prst`, `a:off`/
  `a:ext`, `a:xfrm/@rot`, and its computed row(s)/column(s) under the same
  rotation-aware-AABB + 50%-overlap rule `generate_schedule_pptx.py` uses,
  printed for Slides 1, 2, and 4 so a C# test author can copy the
  expectations directly;
- the `<a:tailEnd type="triangle"/>` on Slide 1's today-line connector, and
  its absence (together with the `│`/`▼` markers it would otherwise
  produce) on Slide 2;
- `p:cNvSpPr/@txBox="1"` on the review textbox;
- the two Slide-2 body cells' text (`済`, `予定`);
- Slide 3's card count/`prstGeom` (`roundRect` × 6), absence of any arrow/
  diamond `prstGeom` anywhere on that slide (the negative-case check), and
  `a:stCxn`/`a:endCxn` on its real-flow connector;
- Slide 4's absence of any `GridLabel*`-named shape;
- file size (< 150KB) and absence of any `ppt/media/*` (no images);
- a same-process, two-independent-builds SHA-256 comparison proving the
  output is byte-identical across runs.

All of the above prints as a `[PASS]`/`[FAIL]` checklist, and the script
exits non-zero if anything fails.

## schedule-shape-grid-gapped corpus (hand-authored-looking imperfections)

`schedule-shape-grid-gapped.pptx` is a "hand-authored-looking" variant of
`schedule-shape-grid.pptx` Slide 1: the same header row, label columns, and
overlay set, but every hand-drawn rectangle carries the small sizing/
positioning sloppiness a real author leaves behind — gaps between cells, a
mis-sized label column, and an off-centre body row — instead of sitting
perfectly flush. It also adds two more slides that are negative cases for a
specific extraction bug: a lone decorative line (or a row of chevrons) near
a row of shapes must not make the grid-table detector synthesize a bogus
table.

Its generator, `generate_shape_grid_gapped_pptx.py`, is **dev-only** (same
convention as the other generators in this directory): not part of the
build, not required to run the test suite, and exists so the fixture's
geometry can be regenerated or extended without hand-editing OOXML. It
imports `Grid`/palette/overlay/XML-verification helpers directly from both
`generate_schedule_pptx.py` and `generate_shape_grid_pptx.py` (same
directory) rather than copying them, so this fixture's overlay placement
math and verification logic cannot silently drift from its two siblings.
Regenerate with:

```
python3 tests/DocRedock.Tests/Fixtures/Pptx/generate_shape_grid_gapped_pptx.py \
    tests/DocRedock.Tests/Fixtures/Pptx/schedule-shape-grid-gapped.pptx
```

`schedule-shape-grid-gapped.powerpoint-saved.pptx` is the same deck after
being opened and re-saved (Save As → Open XML Presentation) by Microsoft
PowerPoint for Mac, then normalized (zip member timestamps fixed to
1980-01-01, `cp:lastModifiedBy` replaced with "RTMD fixture"; `dc:creator`
and `Company` were already empty; no other content edited) — same
rationale and process as `schedule-shape-grid.powerpoint-saved.pptx` above.
It is NOT produced by the generator. python-pptx confirms it carries the
same shape count (and the same shape names) on all 3 slides as the
generator's own output (26 / 7 / 6).

Add `--verify` to re-check an already-generated file without rebuilding it.

### Slide-by-slide element list

**Slide 1 「隙間のある図形格子」** — 26 shapes (title + 8 header rects +
5+5 label rects + 7 overlays), **no `a:tbl` anywhere**. Uses the exact same
`Grid(0.5, 1.5, COL_WIDTHS_MAIN, ROW_HEIGHTS_MAIN)` and the same overlay
calls (`add_bar_shape`/`add_diamond_marker`/`add_today_line`/
`add_label_textbox`) as `schedule-shape-grid.pptx` Slide 1 — the overlays'
own coordinates are untouched. Only the header/label rectangles differ,
via four imperfections:

1. **A 2pt gap (`GAP_EMU = 25400` EMU) between every adjacent rectangle**,
   both horizontally (the 8 header cells) and vertically (each label
   column's 5 stacked cells, and the header-to-first-label-row seam). Each
   rectangle keeps its LEADING (left/top) edge anchored at the coordinate
   the undisturbed grid would use; only the TRAILING edge (right for
   header cells, bottom for label cells) is trimmed by `GAP_EMU`, so the
   gap appears between a cell and its successor without moving anything
   before it.
2. **The 工程 (process) label-column rectangles are 20% wider**
   (`PROCESS_WIDTH_MULT = 1.2`) than the (gap-trimmed) 工程 header cell,
   extending to the right so they overlap into the 担当 column's gap (and
   into the leading edge of 担当's own label rectangles).
3. **Header cells stay exactly 0.5in tall** (never trimmed vertically);
   label/body rows are 0.6in tall — the same header/body height split
   `ROW_HEIGHTS_MAIN = [0.5, 0.6, 0.6, 0.6, 0.6, 0.6]` already used by
   `schedule-shape-grid.pptx`.
4. **The 担当 (owner) column's rectangles are 1pt shorter**
   (`OWNER_SHRINK_EMU = 12700` EMU) than the gap-adjusted row slot they sit
   in, and vertically centred within it (so each is inset by 6350 EMU —
   half a point — top and bottom).

None of this moves where the overlays land. Per the design spec's own
boundary-derivation rule, column boundaries come from the header cells'
LEFT edges (plus the last cell's right edge) and row boundaries come from
the header's top edge and the 工程 label cells' TOP edges (plus the last
cell's bottom edge) — boundaries chain from one cell's leading edge to the
next cell's leading edge, absorbing each 2pt gap into the interval before
it instead of leaving a dead zone nothing can be assigned to. Because
every rectangle here keeps its leading edge anchored at the position the
undisturbed grid would use, the derived column boundaries are bit-
identical to `schedule-shape-grid.pptx`'s, and the derived row boundaries
differ only by the fixed 2pt gap — far below the 50%-overlap rule's
threshold for any overlay in this deck. The expected row/column assignment
per overlay is therefore IDENTICAL to `schedule-shape-grid.pptx` Slide 1
(see `schedule-shape-grid.expected.md`):

| Overlay | Rows | Columns |
|---|---|---|
| 要件定義 (`RIGHT_ARROW`) | `[1]` | `[2, 3]` |
| 設計 (`RIGHT_ARROW`) | `[2]` | `[3, 4, 5]` |
| 実装 (`RECTANGLE`, textless bar) | `[3]` | `[4, 5, 6]` |
| テスト (`LEFT_RIGHT_ARROW`) | `[4]` | `[5, 6, 7]` |
| リリース (`DIAMOND`) | `[5]` | `[7]` |
| today-line (connector, `tailEnd=triangle`) | `[0, 1, 2, 3, 4, 5]` | `[4]` |
| ▲レビュー (`TEXT_BOX`) | `[2]` | `[6]` |

`--verify` re-derives the column/row boundaries from the actual generated
rectangles (not from the idealized `Grid`) and checks each overlay's
computed cell against this table.

**Slide 2 「飾り線つきカード（表ではない）」** — 7 shapes (title + 4 KPI
card `RECTANGLE`s + 1 decorative `line` connector + 1 arrow), the
**negative case** for the "one decorative line below a row of shapes makes
a bogus 2-row table" bug: a row of 4 adjacent (flush) KPI cards (売上/利益/
顧客数/解約率, each with a title line and a numbers line), a thin
horizontal `line`-kind connector (unconnected, no `a:stCxn`/`a:endCxn`,
same "bare straight connector" shape as the today-line but horizontal and
without a `tailEnd`) spanning the cards' combined width directly under
them, and a `RIGHT_ARROW` labelled 「次のステップ」 further below. There is
only ONE row of rectangles anywhere on this slide — no second row of cells
the line could plausibly be separating — so grid-table synthesis must not
fire here.

**Slide 3 「チェブロンのアジェンダ」** — 6 shapes (title + 4
`MSO_SHAPE.CHEVRON`s + 1 arrow), another **negative case**: 4 adjacent
chevrons (STEP1–STEP4, `prstGeom="chevron"`, not `"rect"`) with one
`RIGHT_ARROW` underneath. An aligned row of non-rectangular shapes plus a
trailing arrow must not be mistaken for a shape-grid table header row.

In EMU (914400/in), Slide 1's grid geometry (same as
`schedule-shape-grid.pptx`'s Main grid, restated here since the gapped
rectangles are derived from it): column left edges
`[457200, 1920240, 2926080, 4389120, 5852160, 7315200, 8778240, 10241280]`,
ideal right edge `11704320`; row top edges
`[1371600, 1828800, 2377440, 2926080, 3474720, 4023360]`, ideal bottom edge
`4572000`. `GAP_EMU = 25400` (2pt), `OWNER_SHRINK_EMU = 12700` (1pt),
`PROCESS_WIDTH_MULT = 1.2`.

### Verification (no dotnet)

`generate_shape_grid_gapped_pptx.py` verifies itself the same way its two
siblings do: it re-opens the saved file with python-pptx, walks the raw
slide XML with `lxml`, and prints/checks:

- shape count per slide (26 / 7 / 6) against the counts above, and absence
  of any `a:tbl` on every slide;
- the 2pt (`25400` EMU) horizontal gap between every adjacent header cell,
  the 2pt vertical gap between the header and the first 工程 label cell and
  between consecutive 工程 label cells, the header cells' fixed 0.5in
  height, the 工程 label cells' width (header cell width × 1.2), and the
  担当 label cells' height (row slot height − 12700 EMU) and vertical
  centring within their slot;
- for every `Overlay-*`-named shape on Slide 1: its `a:prstGeom/@prst`,
  `a:off`/`a:ext`, computed row(s)/column(s) under the rotation-aware-AABB
  + 50%-overlap rule (boundaries derived from the actual header/label
  rectangles, per the table above), each checked against the expected
  cells table; the `<a:tailEnd type="triangle"/>` on the today-line
  connector; and `p:cNvSpPr/@txBox="1"` on the review textbox;
- Slide 2's card count/`prstGeom` (`rect` × 4), the decorative line's
  `prstGeom` (`line`), the 「次のステップ」 arrow's `prstGeom`
  (`rightArrow`), and the negative-case guard that no `GridHeader-`/
  `GridLabel*`-named shape exists anywhere on the slide;
- Slide 3's chevron count/`prstGeom` (`chevron` × 4), the arrow's
  `prstGeom` (`rightArrow`), and the same negative-case guard;
- file size (< 150KB) and absence of any `ppt/media/*` (no images);
- a same-process, two-independent-builds SHA-256 comparison proving the
  output is byte-identical across runs.

All of the above prints as a `[PASS]`/`[FAIL]` checklist, and the script
exits non-zero if anything fails.

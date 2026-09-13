# DOCX visual-review corpus

All Office documents in this directory are synthetic fixtures created for
DocRedock. They contain no customer documents, personal data, credentials, or
production identifiers.

The checked-in round-trip corpus is
`tests/DocRedock.Tests/Fixtures/Docx/real-office-roundtrip.original.docx`. It is a
three-page fictional Project Atlas release brief and includes:

- heading hierarchy and both bullet/numbered list styles;
- two multi-row tables with intentional column widths and status colors;
- two embedded PNG figures;
- Japanese text, headers, footers, page numbers, and a controlled page break.

`DocxRealCorpusTests` keeps this corpus in the extraction/F0 regression loop.
For visual QA, render the fixture with a local DOCX renderer and compare every
page before accepting a corpus or layout change. The checked-in fixture is the
authoritative test input; local render output is intentionally not distributed.

The test-local fixtures cover external hyperlinks, field boundaries, footnotes,
tracked revisions, and settings-level document protection. These are intentionally
extracted as evidence and rejected on unsafe mutation rather than flattened.

## complex-design-doc.docx (readable-conversion element corpus)

`complex-design-doc.docx` is a second, purpose-built corpus that exercises the
D01-D18 element list from
`../COMPLEX_DESIGN_DOC_SPEC.md` against the `readable` export profile. It is a
~150 KB Word design document (title page, TOC field, eight numbered chapters,
an appendix, header/footer, a landscape section) built to look like a real
Word-authored 設計書, not an Excel-grid translation.

- **Provenance**: every ID, value, sentence, and embedded image belongs to a
  fictional expense-approval scenario authored as synthetic test data for this
  repository. The private development corpus and its generated outputs are not
  distributed; this checked-in fixture is the authoritative public test input.
- **Reproducibility**: the checked-in document is deterministic and is the
  public regression-test authority. Its development-only generator and source
  corpus are intentionally excluded from the release because users do not need
  them to build, test, or use DocRedock.
- **complex-design-doc.expectations.json** declares machine-checkable
  `guard` (should currently pass against the `readable` .md) and `goal`
  (currently fails; tracks a real, verified conversion gap) assertions per
  the contract in `COMPLEX_DESIGN_DOC_SPEC.md` section 5. The judging logic
  lives in `tools/conversion-qa/`; this file only declares expectations. Every
  guard/goal value was calibrated against an actual
  `docredock export ... --profile readable` run of this fixture, not guessed.
- **Known limitation (environment-dependent guard)**: the Q12 guard
  (`not_contains "OCR-JP-20260823-017"`) is written exactly as the spec
  requires — the fixture's own authored text never contains that string, only
  IMG-02's pixels do. On a machine where the CLI's default `--ocr auto`
  resolves to the native macOS Vision engine, however, DOCX readable export
  still runs OCR on IMG-02 and correctly emits an `OCR抽出テキスト` block
  containing the marker string, so this specific guard fails under that
  default invocation even though nothing in the fixture "leaked" it. That is
  arguably correct OCR behavior, not a fixture defect; a harness that pins
  `--ocr off` (or runs on a machine without a native/Tesseract OCR engine)
  will see it pass.
- No D01-D18 element was omitted; every row of the spec table has at least one
  fixture location and at least one expectations.json item.

## schedule-arrows.docx (DOCX table-overlay fixture)

`schedule-arrows.docx` is the WordprocessingML twin of
`../Pptx/schedule-arrows.pptx` and `../Xlsx/schedule-arrows.xlsx`: the same
Japanese IT-project schedule table (`工程|担当|9/1|9/2|9/3|9/4|9/5|9/8`, rows
要件定義/山田, 設計/佐藤, 実装/鈴木, テスト/田中, リリース/全員) with
arrow/bar/marker/today-line floating shapes drawn ON TOP OF it, used to test
the "DOCX table overlay" feature. Column/row indices below are 0-based.

python-docx 1.2.0 has no write API for floating (anchored) DrawingML shapes
at all, so `generate_schedule_docx.py` builds the document/table with
python-docx and injects every `wp:anchor` -> `a:graphicData
uri=".../wordprocessingShape"` -> `wps:wsp` via raw XML strings parsed with
`lxml` — the same convention `generate_complex_docx.py` uses for its own
anchored picture/textbox/hyperlink/footnote injections. It is **dev-only**:
not part of the build, not required to run the test suite (the checked-in
`.docx` is the regression-test authority). Regenerate with:

```
python3 tests/DocRedock.Tests/Fixtures/Docx/generate_schedule_docx.py \
    tests/DocRedock.Tests/Fixtures/Docx/schedule-arrows.docx
```

Add `--verify` to re-check an already-generated file without rebuilding it.

### Page geometry

A4 landscape (`w:pgSz w="16838" h="11906" orient="landscape"`), 20mm margins
on every side (`w:pgMar` = 1134 twips). The main table's `w:tblGrid`
(`w:tblLayout type="fixed"`, `w:tblInd w="0"`): 工程=2400, 担当=1600, then
six date columns at 1400 twips each; every row carries an explicit
`w:trHeight w:val="500" w:hRule="exact"`. Page 2 uses a smaller table
(工程 + 4 date columns, same 1400-twip date columns).

### Page 1

| Cell(s) | Shape | Anchor style | Text |
|---|---|---|---|
| 9/1 cell (row 要件定義) | `rightArrow`, extent = 2 date columns | cell-anchored | 要件定義 |
| 9/2 cell (row 設計) | `rightArrow`, extent = 3 date columns | cell-anchored | 設計 |
| 9/3 cell (row 実装) | `rect` (no text) | cell-anchored | (none) |
| 9/4 cell (row テスト) | `leftRightArrow`, extent = 3 date columns | cell-anchored | テスト |
| 9/8 cell (row リリース) | `diamond` (0.25in square, centered) | cell-anchored | (none) |
| 9/5 cell (row 設計) | text box (`wps:cNvSpPr txBox="1"`) | cell-anchored | ▲レビュー |
| paragraph immediately before the table | `straightConnector1` + `<a:tailEnd type="triangle"/>` | page-anchored | (none, "本日線") |

**Cell-anchored style**: `wp:positionH relativeFrom="column"` /
`wp:positionV relativeFrom="paragraph"`, small `wp:posOffset` insets
(45720 EMU ≈ 0.05in), `behindDoc="0"`, `layoutInCell="1"`,
`allowOverlap="1"`, `wp:wrapNone`. Because `layoutInCell="1"` is set,
Word resolves `relativeFrom="column"`/`"paragraph"` against the
**containing table cell**, not the page's text column — so a shape's
`posOffset` is a small inset from its own cell's edges even though its
`wp:extent` deliberately spans into neighboring cells (a real Gantt bar
"belongs" to the row/cell it starts in but visually overflows into the
cells the task covers).

**Page-anchored today line**: `relativeFrom="page"` for both axes, anchored
in the ONE paragraph immediately before the table. Numbers used (documented
per the task's request, all in twips unless noted):

- Horizontal: `leftMargin(1134) + gridCol[工程..9/2](2400+1600+1400+1400=6800)
  + gridCol[9/3]/2(700) = 8634` twips → `8634*635 = 5,482,590` EMU (centers
  on the 9/3 column).
- Vertical: the preceding paragraph is forced to an exact 240-twip line
  (`w:spacing w:line="240" w:lineRule="exact"`, spacing before/after = 0,
  12pt/11pt text) precisely so its rendered height is a known constant —
  this is the "assume the table starts right after the preceding
  paragraph" simplification the task calls for. Table top =
  `topMargin(1134) + 240 = 1374` twips → `1374*635 = 872,490` EMU.
- Extent: `cx=1` EMU (as close to a true vertical line as the OOXML
  positive-size schema allows — see "library limitations" below), `cy =
  tableHeight(6 rows * 500 = 3000 twips) * 635 = 1,905,000` EMU.

### Page 2

A `rightArrow` rotated 180° (`a:xfrm rot="10800000"`, text 戻し) is
cell-anchored over 9/2-9/3 of the small table's 差し戻し row, using the
same cell-anchored convention as page 1.

A separate, genuine flow diagram (two `wps:wsp` `roundRect` shapes
「開始」/「完了」 plus a `straightConnector1` between them, with a
triangle `tailEnd`) sits well clear of the table, anchored via
`relativeFrom="column"/"paragraph"` with `layoutInCell="0"` in an ordinary
(non-table) paragraph — proving overlay detection does not have to treat
every floating shape on the page as a table overlay.

A page-anchored text box `※ 実績は毎週更新` deliberately overlaps only
**~30%** of its own height with page 2 table's bottom row band (below the
50%-overlap threshold the PPTX/XLSX fixtures use), so it must NOT be picked
up as an overlay by that rule. `--verify` prints the exact overlap fraction
(0.30) it computes for this shape against the table's assumed row band.

### Library limitations worked around

- python-docx 1.2.0 cannot write `wp:anchor`/`wps:wsp` at all -- every
  floating shape is raw XML (see `wsp_autoshape`/`wsp_line`/`anchor_run` in
  the generator), parsed with `lxml.etree.fromstring` and appended to a
  `docx.Document()`-built paragraph's `_p`, the same technique
  `generate_complex_docx.py` uses for its own DrawingML injections.
- `wordprocessingShape` (unlike DrawingML in PPTX/XLSX) has no dedicated
  connector element with `a:stCxn`/`a:endCxn` semantics that this generator
  relies on being real/verified Word output, so the page-2 flow diagram's
  connector is an **unconnected** `wps:wsp` shape (`prstGeom
  prst="straightConnector1"`) positioned between the two boxes rather than
  a schema-verified "real connection" -- the task only asked for "two
  `wps:wsp` rounded rectangles and a connector" here (unlike the PPTX/XLSX
  real-flow diagrams, which explicitly call for `a:stCxn`/`a:endCxn`).
- A perfectly vertical line needs `wp:extent cx="0"`, but `ST_PositiveSize2D`
  is conventionally treated as requiring a positive width by some
  consumers/validators; `cx="1"` (1 EMU) is used instead, which is visually
  indistinguishable from vertical (slope ≈ 1 / 1,905,000) while staying
  schema-safe.

### Verification (no dotnet)

`generate_schedule_docx.py` verifies itself without invoking `dotnet`: it
re-opens the file with python-docx, walks the raw `word/document.xml` with
`lxml`, and for every `wp:anchor` prints its anchor style (cell/page),
`relativeFrom` values, `posOffset`/`extent` in EMU, `prst`, `rot`, and the
computed expected table row(s)/column(s) using the same 50%-overlap rule
the PPTX/XLSX generators use (resolving cell-anchored shapes via their
ancestor `w:tc`/`w:tr`/`w:tbl`, and page-anchored shapes via the documented
absolute-EMU assumptions above, scoped to whichever page each shape
actually lands on). It also checks file size (<200KB), absence of
`word/media/*` (no images), and a same-process two-independent-builds
SHA-256 comparison proving byte-identical output across runs. Zip member
timestamps are fixed (2026-01-01) and `docProps/core.xml` carries a fixed
author ("RTMD fixture") and fixed `created`/`modified` dates.

`schedule-arrows.word-saved.docx`, if present, is the same document after
being opened and re-saved (Save As -> .docx) by Microsoft Word, then
normalized (zip member timestamps fixed to 1980-01-01, `cp:lastModifiedBy`/
`dc:creator` replaced with "RTMD fixture"; no other content edited). It is
NOT produced by the generator — see the repo's session notes for whether
this variant was successfully captured on the machine that ran the
generator, and what Word-added XML (if any) it revealed.

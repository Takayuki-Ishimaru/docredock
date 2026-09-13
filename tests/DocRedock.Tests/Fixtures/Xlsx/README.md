# XLSX visual-review corpus

All spreadsheets in this directory are synthetic fixtures created for
DocRedock. They contain no customer documents, personal data, credentials, or
production identifiers.

## schedule-arrows.xlsx (XLSX table-overlay fixture)

`schedule-arrows.xlsx` is the SpreadsheetML twin of
`../Pptx/schedule-arrows.pptx`: the same Japanese IT-project schedule table
(`工程|担当|9/1|9/2|9/3|9/4|9/5|9/8`, rows 要件定義/山田, 設計/佐藤,
実装/鈴木, テスト/田中, リリース/全員) with arrow/bar/marker/today-line
DrawingML shapes drawn ON TOP OF it, used to test the "XLSX table overlay"
feature. Column indices below are 0-based (0=A) to match `xdr:col`; the
table occupies B2:I7 (header row 2).

openpyxl 3.1.5 cannot write drawings/shapes at all, so
`generate_schedule_xlsx.py` builds the whole `.xlsx` package by hand with
the stdlib `zipfile` module — the same convention as
`tools/conversion-qa/generate_complex_xlsx.py` (hand-authored part strings,
inline strings instead of a shared-strings table, a real
`xl/drawings/drawing*.xml` + worksheet `_rels` + `[Content_Types].xml`
overrides). It is **dev-only**: not part of the build, not required to run
the test suite (the checked-in `.xlsx` is the regression-test authority).
Regenerate with:

```
python3 tests/DocRedock.Tests/Fixtures/Xlsx/generate_schedule_xlsx.py \
    tests/DocRedock.Tests/Fixtures/Xlsx/schedule-arrows.xlsx
```

Add `--verify` to re-check an already-generated file without rebuilding it.

### Sheet 1 「スケジュール」

Explicit `<cols>` widths (character units: A=8.43 default, B=14, C=10,
D-I=8) and explicit `<row ht="24" customHeight="1">` on rows 2-7:

| Cell(s) | Shape | Anchor | Text |
|---|---|---|---|
| D3:E3 | `rightArrow` | `xdr:twoCellAnchor` | 要件定義 |
| E4:G4 | `rightArrow` | `xdr:twoCellAnchor` | 設計 |
| F5:H5 | `rect` (solid fill, no text) | `xdr:twoCellAnchor` | (none) |
| G6:I6 | `leftRightArrow` | `xdr:twoCellAnchor` | テスト |
| I7 | `diamond` (0.25in square) | `xdr:oneCellAnchor` | (none) |
| F column, header row top -> row 7 bottom | straight `cxnSp` + `<a:tailEnd type="triangle"/>` | `xdr:twoCellAnchor` (no stCxn/endCxn) | (none, "本日線") |
| H4 | text box (`cNvSpPr txBox="1"`) | `xdr:twoCellAnchor` | ▲レビュー |

### Sheet 2 「グループ」

Same table. The 要件定義 (D3:E3) and 設計 (E4:G4) arrows live inside one
`xdr:grpSp` whose `a:chOff`/`a:chExt` are a deliberately **non-identity**
transform (shifted +500000/+300000 EMU and scaled 1.5x relative to the
group's own `a:off`/`a:ext`) — mirroring
`generate_schedule_pptx.py`'s `apply_nontrivial_group_transform`, so a
reader must resolve the affine transform rather than read the children's
raw local coordinates directly. `--verify` prints both the raw child-local
`a:xfrm` and the resolved absolute AABB for each grouped arrow.

Well below the table (rows 12-13), a genuine, unrelated flow — two
`roundRect` shapes 「開始」/「完了」 joined by a `cxnSp` carrying real
`a:stCxn`/`a:endCxn` — proves overlay detection excludes only the shapes that
diagram actually consumed (P-Overlay F-B), not the whole sheet: the grouped
要件定義/設計 arrows above still fold into the table even though this sheet
also renders the 開始→完了 flowchart. `a:stCxn` (idx=1, 開始's right-center)
attaches to 開始 and `a:endCxn` (idx=3, 完了's left-center) attaches to 完了,
matching the connector's drawn direction 開始→完了 —
`XlsxMermaidProjection.TryCreateDrawingFlowchart` reads `a:stCxn`'s shape as
the edge source and `a:endCxn`'s shape as the edge target, so an earlier
revision of this fixture that swapped those two roles (`st` wired to 完了,
`end` wired to 開始) rendered the flow backwards as 完了→開始 despite the
visual arrowhead direction; `generate_schedule_xlsx.py`'s `build_drawing2`
now wires them the other way round.

### Sheet 3 「絶対配置」

Same table. Two overlays that exercise absolute-EMU and rotation handling:

- One `rightArrow` (設計) placed via `xdr:absoluteAnchor` (`xdr:pos`/`xdr:ext`
  in EMU, not cell-relative) computed from the column widths/row heights
  using the standard Excel/Calibri-11 pixel formula: `px = trunc(((256*w +
  trunc(128/7))/256)*7)` (max digit width 7px for the default 11pt
  Calibri), `EMU = px * 9525`; row height `EMU = pt * 12700`. It lands on
  E4:G4 — run `--verify` for the exact computed `pos`/`ext` numbers.
- One `rightArrow` rotated 180° (`a:xfrm rot="10800000"`) with text 戻し
  over D3:E3.

### Verification (no dotnet)

`generate_schedule_xlsx.py` verifies itself without invoking `dotnet`: it
re-opens the file with openpyxl, walks the raw drawing XML with `lxml`, and
for every overlay shape on all three sheets prints the table row(s)/
column(s) it would be assigned via the same 50%-overlap rule used by
`generate_schedule_pptx.py` (rotation-aware AABB), plus file size (<100KB),
absence of `xl/media/*` (no images), and a same-process two-independent-
builds SHA-256 comparison proving byte-identical output across runs. Zip
member timestamps are fixed (2026-01-01) the same way
`generate_schedule_pptx.py` normalizes them, and `docProps/core.xml` carries
a fixed creator ("RTMD fixture") and fixed `created`/`modified` dates
instead of wall-clock time.

`schedule-arrows.excel-saved.xlsx`, if present, is the same workbook after
being opened and re-saved (Save As -> .xlsx) by Microsoft Excel, then
normalized (zip member timestamps fixed to 1980-01-01, `cp:lastModifiedBy`/
`dc:creator` replaced with "RTMD fixture"; no other content edited). It is
NOT produced by the generator — see the repo's session notes for whether
this variant was successfully captured on the machine that ran the
generator, and what Excel-added XML (if any) it revealed.

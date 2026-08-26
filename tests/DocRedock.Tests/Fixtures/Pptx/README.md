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

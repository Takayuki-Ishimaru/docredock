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

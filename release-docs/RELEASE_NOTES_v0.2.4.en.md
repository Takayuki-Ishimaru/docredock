# DocRedock v0.2.4 Public Beta Release Notes

Release date: 2026-09-06

v0.2.4 is a quality update that improves PDF text preservation and reading order, XLSX sheet selection, environment diagnostics, and output-summary reliability. The supported feature scope is unchanged.

## Changes

- PDF: Body text outside a reconstructed table is now preserved. Table-cell text is kept separate from body text, and any unexplained text fragment remains in the document with a `PdfNativeTextUnaccounted` warning.
- PDF: Tables whose rules are grouped into one drawing path can now be reconstructed in the same way as tables whose rules are drawn individually.
- PDF: Two-column pages are no longer interleaved line by line. When a continuing vertical gutter is detected, the whole left column is emitted before the right column. A single wide gap on one line is not treated as a column boundary.
- XLSX: `--sheets` with a sheet name that does not exist fails with exit code 2 and writes no output. The error lists the available sheets (hidden ones marked `(hidden)`). A partial mismatch is not silently ignored, and an empty selection (`--sheets ""`) also fails with exit code 2. Requesting a hidden sheet that the content policy excludes emits the `XlsxSheetExcludedByPolicy` warning and exits 1.
- doctor: Text and `--json` output always return the same exit code. Each capability has a `required` or `optional` tier; by default the exit code is 0 when every required capability is ready and 1 otherwise. `--strict` also fails on optional gaps, except gaps already covered by a ready alternative (`satisfied_by`). When Tesseract is ready, the `ocr-native` action reads "OCR is provided by tesseract" instead of "install Tesseract". The JSON report gains `tier`, `satisfied_by`, `strict`, `exit_code`, and `summary` (`schema_version` stays 1).
- Output summaries: `Visual summary`, `Export completed`, and the GUI diagram summary now display the same finalized values. `diagrams=` means reconstructed diagrams, while pages with vector content use the new `vector_pages=` field. False warnings on PDFs containing only a resolved arrow are gone, and remaining warnings identify the page and unresolved-connector count.

## Compatibility notes

- Plain `docredock doctor` no longer exits 1 when only optional tools (OCR, rasterizer, mermaid) are missing. Automation that relied on the stricter behavior should switch to `docredock doctor --strict`.
- The `Visual summary` line gains `vector_pages=`, and `diagrams=` changed meaning to "reconstructed diagrams".
- For library users: `PdfTableCell.TextRegionIndexes` is now `SourceTextIds`, `ExportSummary.Diagrams` is now `DiagramsReconstructed`, and `PdfTextRegion.ReadingOrder` is a sequential index in final reading order.

## How to update

1. Save your source documents and settings, then close any running DocRedock instance.
2. Download the package for your OS/CPU from this release, verify it with `SHA256SUMS`, and extract it into a separate folder.
3. Start it following the bundled `QUICKSTART.md`. Check external tools with `docredock doctor`.

## Scope and limitations

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md).
- Column detection is conservative. Two-column blocks whose lines do not share baselines (each column wrapping independently) may still be emitted in row order.

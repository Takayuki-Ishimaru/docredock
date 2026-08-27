# DocRedock v0.1.5 Public Beta Release Notes

Release date: 2026-08-28

DocRedock v0.1.5 is a reliability hotfix for the Public Beta. The supported user workflow remains local, one-way conversion to **Readable Markdown**; the desktop GUI accepts DOCX/XLSX/PPTX and PDF input by default. Round-trip, restore, render/new-document workflows, and CLI PDF export/render remain experimental; read-only CLI inspection remains available.

## Highlights

- CLI `export` now defaults to the one-way `readable` profile. Existing round-trip automation must pass `--profile roundtrip` explicitly.
- OCR evidence stays in the partition containing its parent image or PDF page. Unresolved evidence is isolated in `derived-assets` with a diagnostic.
- Horizontally and vertically merged tables render with blank continuation cells and reject continuation/shape edits during round-trip processing.
- Experimental PDF rendering no longer bundles or assumes a Japanese font.
- The desktop GUI accepts PDF input by default. Native text is extracted directly; textless-page OCR still requires a configured rasterizer and OCR provider.
- PPTX literal bullet glyphs are normalized even when wrapped in emphasis.

## Portable PDF fonts

ASCII-only PDFs use Base14 Helvetica without embedding a font program. Non-ASCII output resolves an embeddable TrueType face in this order:

1. `--font-path` and optional `--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH` and optional `DOCREDOCK_PDF_FONT_FACE_INDEX`
3. installed system fonts

The resolver validates SFNT/TTC structure, extracts the selected collection face, checks TrueType outlines, OS/2 embedding permission, and required glyph coverage. CFF/CFF2, invalid or oversized fonts, restricted embedding, and missing glyphs fail with actionable diagnostics. DocRedock never downloads a font; users are responsible for the selected font's license.

Font selection and coverage are informational. Omissions and truncation are warnings. CLI render returns exit code 1 when warnings exist; `--quiet` suppresses informational lines, while `--verbose` includes the selected font path.

## OCR, tables, and conversion quality

- OCR nodes inherit the parent image's partition and hidden/metadata layer.
- Rasterized PDF page assets stay with their page partition; a missing rasterizer produces `PdfRasterizerUnavailable`.
- DRMD editing rules 1.1 preserve compatibility with 1.0 while adding merged-table continuation and shape checks.
- Readable merged tables emit blank Markdown continuation cells for row/column spans.
- PPTX literal bullets, including bold bullet runs, become clean Markdown list markers.
- Empty GUI projections produce an explicit `EmptyProjection` result.

## Distribution and verification

- Conversion QA deterministically generates a complex XLSX with metadata, formulas, hidden rows/columns/sheets, chart references, images, and merged cells.
- `--all` QA must execute DOCX, XLSX, and PPTX and fails with `ConversionQaCoverageTooLow` when coverage drops.
- Release smoke tests verify `BINARY-SHA256SUMS`, reject unexpected font binaries, exercise readable/roundtrip exports, verify an immediate zero-operation diff, and render/extract a Japanese PDF sentinel.
- Linux CI/release jobs install an explicit Japanese system font for PDF coverage; packages remain font-free.

## Safety and compatibility

The safe content-policy default remains `visible`; `complete` includes hidden/metadata content with a warning, and `sanitized` applies stricter filtering. Readable Markdown remains one-way output. Keep the original Office file as the authoritative source.

Experimental CLI entry points, including CLI PDF export (conversion), restoration, and rendering, require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Read-only `docredock inspect <file.pdf>` remains available without the flag. The desktop GUI accepts PDF input by default, subject to its documented OCR/rasterizer and PDF-font constraints. Experimental `.drmd` and `.drmdpkg` artifacts may contain source-derived or restoration data and require the same confidentiality controls as the source.

## Upgrade

Replace the v0.1.4 GUI/CLI files with the v0.1.5 package for your OS and CPU. Update scripts that depend on round-trip export to add `--profile roundtrip`. Verify the published package SHA-256 and signing/notarization status.

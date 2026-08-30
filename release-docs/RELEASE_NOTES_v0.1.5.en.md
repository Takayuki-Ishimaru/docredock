# DocRedock v0.1.5 Public Beta Release Notes

Release date: 2026-08-28

DocRedock v0.1.5 is a reliability hotfix for the Public Beta. The supported user workflow remains local, one-way conversion to **Readable Markdown**; the desktop GUI accepts DOCX/XLSX/PPTX and PDF input by default. Round-trip, restore, render/new-document workflows, and CLI PDF export/render remain experimental; read-only CLI inspection remains available.

## Highlights

- CLI `export` now defaults to the one-way `readable` profile. Existing round-trip automation must pass `--profile roundtrip` explicitly.
- OCR text stays with its image or PDF page; content with no clear parent is separated with a diagnostic.
- Horizontally and vertically merged tables render with blank continuation cells and reject continuation/shape edits during round-trip processing.
- Experimental PDF rendering no longer bundles or assumes a Japanese font.
- The desktop GUI accepts PDF input by default. Native text is extracted directly; textless-page OCR still requires a configured rasterizer and OCR provider.
- PPTX literal bullet glyphs are normalized even when wrapped in emphasis.

## Portable PDF fonts

ASCII-only PDFs use Base14 Helvetica without embedding a font program. Non-ASCII output resolves an embeddable TrueType face in this order:

1. `--font-path` and optional `--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH` and optional `DOCREDOCK_PDF_FONT_FACE_INDEX`
3. installed system fonts

DocRedock checks the selected TrueType font for embedding permission and required characters. Invalid, oversized, restricted, or incomplete fonts are rejected with diagnostics. DocRedock never downloads a font; users are responsible for the selected font's license.

Font selection and coverage are informational. Omissions and truncation are warnings. CLI render returns exit code 1 when warnings exist; `--quiet` suppresses informational lines, while `--verbose` includes the selected font path.

## OCR, tables, and conversion quality

- OCR text follows the same visibility policy as its image or PDF page.
- PDF page previews stay with their page; a missing rasterizer produces a diagnostic.
- Readable merged tables use blank Markdown continuation cells for row and column spans.
- PPTX literal bullets, including emphasized bullet glyphs, become clean Markdown list markers.
- An empty conversion result is reported explicitly.

## Distribution

Published packages remain font-free and can be verified with their checksums.

## Safety and compatibility

The safe content-policy default remains `visible`; `complete` includes hidden/metadata content with a warning, and `sanitized` applies stricter filtering. Readable Markdown remains one-way output. Keep the original Office file as the authoritative source.

Experimental CLI entry points, including CLI PDF export (conversion), restoration, and rendering, require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Read-only `docredock inspect <file.pdf>` remains available without the flag. The desktop GUI accepts PDF input by default, subject to its documented OCR/rasterizer and PDF-font constraints. Experimental `.drmd` and `.drmdpkg` artifacts may contain source-derived or restoration data and require the same confidentiality controls as the source.

## Upgrade

Replace the v0.1.4 GUI/CLI files with the v0.1.5 package for your OS and CPU. Update scripts that depend on round-trip export to add `--profile roundtrip`. Verify the published package SHA-256 and signing/notarization status.

# DocRedock v0.1.4 Public Beta Release Notes

Release date: 2026-08-27

DocRedock v0.1.4 is a quality and startup-reliability update based on an independent review of v0.1.3. The supported user workflow remains local, one-way DOCX/XLSX/PPTX conversion to **Readable Markdown**.

## Highlights

- Fixed a fatal GUI startup crash caused by an early content-policy selection event during XAML initialization.
- Added two pre-release GUI gates: a dedicated Avalonia headless `MainWindow` construction test and a packaged Linux startup check that requires the actual DocRedock GUI child process to stay alive under Xvfb.
- Corrected XLSX native-chart visibility so unrelated hidden workbook content no longer hides visible charts.
- Improved DOCX, XLSX, PPTX, and experimental HTML readability for every issue reported by the independent review.

## Conversion fixes

### DOCX

- Removes leaked raw `<span style=...>` wrappers from Readable Markdown.
- Renders underscore emphasis correctly in experimental HTML.
- Avoids duplicated ordered-list markers such as `1. 1.`.
- Preserves nested list depth inferred from Word list styles.

### XLSX

- Resolves chart formulas and referenced cell ranges before deciding whether cached chart data is visible.
- Keeps charts visible when hidden sheets, rows, or columns are unrelated to the chart source.
- Keeps cached chart data hidden when a reference cannot be parsed and the workbook contains potentially hidden sources; clean workbooks may use cached data with a diagnostic.
- Separates update-date and status metadata rows from neighboring data/risk tables.

### PPTX

- Infers top-positioned, all-bold single-paragraph title shapes when a title placeholder is missing.
- Converts literal bullet glyphs to Markdown list items.
- Suppresses repeated bottom-of-slide footers.
- Omits empty slide headings when a hidden slide is excluded.

### Experimental HTML preview

- Renders underscore emphasis and eight-digit color spans.
- Rebases image links against the final requested HTML path, including sibling Markdown/HTML output directories.

## Safety and compatibility

- The safe default remains `visible`; `complete` includes hidden and metadata content with a warning, and `sanitized` applies stricter filtering.
- Ambiguous XLSX cached chart data is not exposed when hidden workbook sources may exist.
- No supported CLI command or content-policy name changed in this release.
- Readable Markdown remains one-way output. Keep the original Office file as the authoritative source.

## Experimental workflows

PDF conversion, round-trip editing, restoration, rendering/new-document generation, and package workflows remain experimental. Distributed GUI/CLI entry points require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Experimental artifacts such as `.drmd` and `.drmdpkg` may contain source-derived or restoration data.

## Distribution and verification

The release workflow builds self-contained packages for:

- Windows x64 and ARM64
- macOS x64 and ARM64
- Linux x64 and ARM64

Before publication, locked restore, Release build, the regular regression suite, the dedicated GUI construction test, conversion QA, license audit, package extraction, CLI smoke tests, binary architecture checks, and Linux packaged-GUI startup must pass. Each release includes checksums, SBOM/provenance, signing status, and release evidence.

## Upgrade

Replace the v0.1.3 GUI/CLI files with the v0.1.4 package for your OS and CPU. Keep original Office files and review generated Markdown and assets before sharing.

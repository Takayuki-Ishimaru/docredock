# DocRedock release documentation

[日本語](README.md) | English

Versioned release notes and version-independent publication procedures. The user-facing entry point is [README.md](../README.md).

## Latest release

### v0.2.12 Public Beta

- [v0.2.12 release notes](RELEASE_NOTES_v0.2.12.en.md)
- Fixed PDF text converted to the wrong characters when a page and its drawing components use one font name for different fonts
- Left text that is not visible on screen (outside the visible area, or on a hidden layer) out of the default output
- Added a highlight switch to the review window and fixed a conversion error on PDFs made by LibreOffice and others

### v0.2.11 Public Beta

- [v0.2.11 release notes](RELEASE_NOTES_v0.2.11.en.md)
- Preserved text and graphics inside PDF Form XObjects, with a warning and review image when one cannot be analyzed
- Added zoom to review areas and a rendered or source Markdown view to the review window
- Reduced memory use when converting PDFs with large images

### v0.2.10 Public Beta

- [v0.2.10 release notes](RELEASE_NOTES_v0.2.10.en.md)
- Distinguished solid, dashed, and dotted shapes over tables in Readable Markdown
- Removed an unnecessary warning for headings outside tables and double symbols for separately painted fill and outline
- Reported saving, visual conversion, and review needs separately, and added a GUI review window with the source page next to its Markdown

### v0.2.9 Public Beta

- [v0.2.9 release notes](RELEASE_NOTES_v0.2.9.en.md)
- Preserved horizontal and vertical double-headed PDF schedule arrows and endpoint cells
- Kept unsupported diagonal lines available for source review and reduced unnecessary table warnings
- Separated review-page and image counts, and improved OCR review order and detail controls

### v0.2.8 Public Beta

- [v0.2.8 release notes](RELEASE_NOTES_v0.2.8.en.md)
- Improved PDF schedule rows, columns, and arrow direction
- Added completion-with-warnings guidance, source review images, and OCR confidence/positions
- Added experimental restoration preflight and historical-source overwrite protection

### v0.2.7 Public Beta

- [v0.2.7 release notes](RELEASE_NOTES_v0.2.7.en.md)
- Improved readable Markdown tables for schedule arrows and duration bars
- Improved character references and text preservation in experimental round-trip editing
- Improved PDF table recognition, OCR text layout, and environment guidance

### v0.2.6 Public Beta

- [v0.2.6 release notes](RELEASE_NOTES_v0.2.6.en.md)
- Preserved source text that resembles links, images, or reference definitions as visible text
- Kept real links, images, formatting, and generated diagrams, and improved escaping in experimental HTML rendering
- Rejected CLI output through hard links identified as the same file as an input

### v0.2.5 Public Beta

- [v0.2.5 release notes](RELEASE_NOTES_v0.2.5.en.md)
- Protected source documents from overwrite and excluded content hidden through styles or groups
- Improved Word body text, equations and nested tables, PowerPoint master text and tables, and Excel display formats
- Preserved PDF body text, mixed-page images and column order, and removed false warnings from multiple tables
- Escaped literal source symbols and improved text preservation in experimental round-trip editing

### v0.2.4 Public Beta

- [v0.2.4 release notes](RELEASE_NOTES_v0.2.4.en.md)
- Preserved PDF body text outside tables and improved two-column reading order and multi-rule-path table reconstruction
- Made XLSX sheet selection strict and clarified doctor exit codes and strict mode
- Aligned CLI and GUI visual/fallback summaries and removed false warnings

### v0.2.3 Public Beta

- [v0.2.3 release notes](RELEASE_NOTES_v0.2.3.en.md)
- Regular PDF grids become Markdown tables without duplicated cell text
- Existing tolerance for small diagram gaps and offsets is preserved; unresolved relations remain notes
- Bounded visual and diagnostic output, actual environment probes through doctor, and local PDF OCR integration

### v0.2.2 Public Beta

- [v0.2.2 release notes](RELEASE_NOTES_v0.2.2.en.md)
- Improved PDF table-grid, arrow-direction, and branch-label recognition
- Improved XLSX formula diagnostics and selected-sheet output scope
- Clearer CLI diagnostic summaries and stronger per-format quality checks

### v0.2.1 Public Beta

- [v0.2.1 release notes](RELEASE_NOTES_v0.2.1.en.md)
- Always-visible current version, published-release checks, including Public Beta builds, at startup and on demand, and a trusted release-page action
- PDF table-grid suppression and arrow direction retention, plus more precise diagnostics and XLSX projection
- Documented `docredock` launcher and Linux install/uninstall flow

### v0.2.0 Public Beta

- [v0.2.0 release notes](RELEASE_NOTES_v0.2.0.en.md)
- [Release evidence correction](RELEASE_EVIDENCE_CORRECTION_v0.2.0.md)
- Visual inference defaults to safe mode; unresolved relations remain visible as fallback and diagnostics
- [v0.1.7 errata](ERRATA_v0.1.7.en.md)

### v0.1.7 Public Beta

- [v0.1.7 release notes](RELEASE_NOTES_v0.1.7.en.md)
- [v0.1.7 errata](ERRATA_v0.1.7.en.md)
- [v0.1.6 errata](ERRATA_v0.1.6.en.md)
- Uncertain diagram relations remain unresolved and visible through fallback and diagnostics

## For users

- [User guide](../docs/en/user-guide.md)
- [Supported features](../docs/en/supported-features.md)
- [Experimental features](../docs/en/experimental-features.md)
- [Security and privacy](../docs/en/security-and-privacy.md)

## For maintainers

- [Publication scope](PUBLICATION_SCOPE.en.md)
- [Release checklist](RELEASE_CHECKLIST.en.md)
- [Japanese publication scope](PUBLICATION_SCOPE.md)
- [Japanese release checklist](RELEASE_CHECKLIST.md)

GitHub Releases is the canonical history; older notes remain as repository references.

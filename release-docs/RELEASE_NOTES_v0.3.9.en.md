# DocRedock v0.3.9 Public Beta

[日本語](RELEASE_NOTES_v0.3.9.md)

v0.3.9 improves PDF reading order, paragraphs and tables, preserves Word code and chart values, and makes the CLI and GUI easier to use.

## More readable PDF text and tables

- Joins wrapped prose when measured positions and glyph widths support continuity. Headings, lists, tables, diagram labels and uncertain lines remain separate.
- Recognizes columns with narrow gutters when the gap persists across multiple lines. A gap between short labels alone does not establish columns.
- Improves reconstruction of clearly ruled rectangular tables with unequal column widths or merged rows and columns. Complete reproduction of ambiguous grids is not guaranteed.
- In full-document Readable Markdown, joins tables at adjacent page boundaries only when column positions and repeated headers match and no overlapping diagrams or other disqualifying content are present. This also applies to AI-package `document.md`; package parts continue to follow PDF page boundaries.

Detailed PDF vector-path inventories and diagnostics are kept in `report.json`; meaningful unresolved connector labels and line-style notes remain in the body. Fixed duplicate text caused by sending source-review page images to OCR. Embedded-image recognition remains available when OCR is enabled.

## Preserve Word code and chart values

Consecutive Word code paragraphs are grouped into one code block, preserving indentation and blank lines. Chart tables retain stored series values even when category names are missing. Values absent from the source file are not invented.

## CLI and GUI operation and display

Fixed CLI final-view Word exports (`--revisions final`) returning a warning solely because excluded revision history exists. The CLI, GUI and API continue to default to markup for `visible` / `complete` and final view for `sanitized`. Markup may include deleted content; review it before sharing.

CLI JSON output for package operations and reports displays Japanese and other characters more readably. Field names and value meanings are unchanged.

Enabled the GUI export shortcut: `Ctrl+Enter` on Windows/Linux and `⌘+Enter` on macOS. It works when export is available and does not run while editing a text or selection field. Japanese completion, review and failure messages and guidance for unresolved lines and shapes are clearer.

## AI-package token estimates

Estimates now account separately for consecutive digits and Markdown table delimiters. Newly exported packages may have larger estimates for the same content. The part-size target continues to use characters, not a token limit. Reading or searching an existing package does not recalculate its stored estimates.

Estimates are not measured with a model-specific tokenizer and do not guarantee that content fits an AI tool's token limit. The CLI `--token-estimate-multiplier` option and equivalent API setting remain available. The GUI has no multiplier control.

## Compatibility and limitations

Existing commands and package formats remain usable. The manifest remains schema `2.2`, and `report.json` remains schema `1.2`. Existing schema `1.0` / `2.0` / `2.1` packages remain readable.

This is a Public Beta. Readable Markdown and AI packages are one-way outputs; keep the original document as the authoritative copy. Review text, images, diagnostics and `review.md` before sharing. Absence of review items does not guarantee a correct conversion.

Desktop-GUI PDF input is enabled by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats and new PDF/Office generation remain experimental. OCR and PDF review images require provider or rasterizer configuration appropriate to your environment. Complete reproduction of diagrams, SmartArt or complex layouts is not guaranteed.

## Downloads and updating

Choose the archive matching your OS and CPU from GitHub Releases.

| OS | CPU | Filename |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.9-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.9-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.9-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.9-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.9-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.9-linux-arm64.tar.gz` |

Verify the download against `SHA256SUMS`, then follow `QUICKSTART.en.md` in the extracted folder. No separate .NET SDK is required. Check `SIGNING-STATUS.json` in each package for signing and notarization status; packages are not necessarily signed. Preserve existing documents and outputs, close the application and replace it with the new package.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md) and [security and privacy](../docs/en/security-and-privacy.md).

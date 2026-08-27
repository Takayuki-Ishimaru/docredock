# DocRedock User Guide

[日本語](../ja/user-guide.md) | English

This guide covers the v0.1.5 Public Beta supported workflow: desktop-GUI conversion of local DOCX, XLSX, PPTX, and PDF files to **Readable Markdown**.

## 1. Get DocRedock

Download the package for your OS/CPU from GitHub Releases and verify the published SHA-256. Self-contained packages do not require a separate .NET SDK.

## 2. Convert a file

1. Start DocRedock.
2. Select or drop a DOCX, XLSX, PPTX, or PDF file.
3. Select **Readable Markdown**.
4. Keep **Visible content only (recommended)** unless you intentionally need another policy.
5. Choose an output location, convert, then review the Markdown and diagnostics.

The desktop GUI accepts PDF by default. It extracts native PDF text; textless-page OCR still requires a configured rasterizer and OCR provider, and reports a diagnostic when either is unavailable.

CLI PDF export (conversion), restoration, and rendering remain experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`, like other experimental CLI workflows. Read-only `docredock inspect <file.pdf>` remains available without the flag.

CLI export also defaults to Readable Markdown:

```sh
docredock export input.docx --content-policy visible --output input.md
```

Use `--profile roundtrip` explicitly only for the experimental sidecar workflow.

## 3. Generated files

| Output | Contents | v0.1.5 use |
| --- | --- | --- |
| `.md` | Readable body text, headings, lists, tables, and supported visual descriptions | Use |
| `.assets/` | Images referenced by Markdown | Use when needed |
| `.drmd` | Source/restoration sidecar | Experimental; treat like the source document |
| `.drmdpkg` | Markdown and restoration data package | Experimental; treat like the source document |

## 4. Choose a content policy

- **visible** (default): filters recognized hidden text, hidden sheets/rows/columns, hidden slides/objects, notes, comments, and revisions out of the Markdown projection.
- **complete**: includes hidden/metadata content and emits a warning.
- **sanitized**: also filters metadata, derived/OCR content, and furniture such as headers and footers.

OCR text inherits its parent image's visibility. If DocRedock cannot resolve the parent partition, it places the evidence in a dedicated `derived-assets` partition and emits `OcrParentPartitionUnresolved`.

## 5. Review the result

Check heading hierarchy, list nesting, merged-table blanks, spreadsheet formula-cache warnings, slide boundaries, images, OCR, and diagnostics. Keep the source Office document as the authoritative copy. Readable output cannot be restored.

## 6. Experimental PDF rendering

DocRedock does not bundle or download a Japanese font. ASCII-only PDF output uses Base14 Helvetica. Non-ASCII output resolves an embeddable TrueType font in this order:

1. `--font-path` and optional `--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH` and optional `DOCREDOCK_PDF_FONT_FACE_INDEX`
3. installed system fonts

```sh
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock render input.md --format pdf \
  --font-path /path/to/font.ttc --font-face-index 0 --verbose
```

The resolver rejects unsupported CFF/CFF2 outlines, missing glyph coverage, invalid collections, and embedding-restricted fonts. The user must comply with the selected font's license. `--verbose` includes the selected path; `--quiet` suppresses informational lines, not warnings. A render with omissions/truncation returns exit code 1.

## 7. Privacy and updates

Conversion runs locally. The GUI may contact the public GitHub Releases API for update metadata; set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable it.

For experimental workflows, see [Experimental features](experimental-features.md). For handling guidance, see [Security and privacy](security-and-privacy.md).

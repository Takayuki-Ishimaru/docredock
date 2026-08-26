# DocRedock User Guide

[日本語](../ja/user-guide.md) | English

This guide covers the v0.1.3 Public Beta supported workflow: local DOCX, XLSX, and PPTX conversion to **Readable Markdown**.

## 1. Get DocRedock

Download the package for your OS/CPU from GitHub Releases and verify the published SHA-256. Self-contained packages do not require a separate .NET SDK.

## 2. Convert a file

1. Start DocRedock.
2. Select or drop a DOCX, XLSX, or PPTX file.
3. Select **Readable Markdown**.
4. Keep **Visible content only (recommended)** unless you intentionally need another policy.
5. Choose an output location, convert, then review the Markdown and diagnostics.

## 3. Generated files

| Output | Contents | v0.1.3 use |
| --- | --- | --- |
| `.md` | Readable body text, headings, lists, tables, and supported visual descriptions | Use |
| `.assets/` | Images referenced by Markdown | Use when needed |
| `.drmd` | Source/restoration sidecar | Experimental; treat like the source document |
| `.drmdpkg` | Markdown and restoration data package | Experimental; treat like the source document |

## 4. Choose a content policy

- **visible** (default): filters recognized hidden text, hidden sheets/rows/columns, hidden slides/objects, notes, comments, and revisions out of the Markdown projection.
- **complete**: includes hidden/metadata content. DocRedock emits a warning; inspect every output before sharing.
- **sanitized**: filters everything removed by visible plus metadata, derived/OCR content, and document furniture such as headers and footers.

For external images, DocRedock writes only assets referenced by nodes included under the selected policy. Still review both the `.md` and `.assets/` before sharing because Office producers can encode visibility in unfamiliar ways.

CLI:

```sh
docredock export input.docx --profile readable --content-policy visible --output input.md
```

## 5. Review the result

Check heading hierarchy, list nesting, spreadsheet tables and formula-cache warnings, slide boundaries, images, OCR, and diagnostics. Keep the source Office document as the authoritative copy. The readable output cannot be restored.

## 6. Privacy and updates

Conversion runs locally. The GUI may contact the public GitHub Releases API for update metadata; set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable it.

For experimental workflows, see [Experimental features](experimental-features.md). For handling guidance, see [Security and privacy](security-and-privacy.md).

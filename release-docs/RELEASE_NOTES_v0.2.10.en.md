# DocRedock v0.2.10 Public Beta Release Notes

v0.2.10 makes content that could not be fully converted quicker to review and preserves the line style (solid, dashed, dotted) of shapes drawn over tables. Export results now report separately that the Markdown was saved, whether every visual element was converted, and whether a person needs to review anything.

## What's improved

- **Line styles of shapes over tables**: In PDF, PowerPoint, Excel, and Word, dashed and dotted arrows, bars, and lines over tables are now distinguished from solid ones in Readable Markdown. Dashed strokes use `┅` and dotted strokes use `⋯`, and a note after the table describes each one, for example "破線の両矢印: 行「設計」、列「9/1」〜「9/3」" (a dashed double arrow in the 設計 row across the 9/1 to 9/3 columns). Diagram connections use Mermaid dotted links. Solid shapes produce exactly the same output as before.
- **No unnecessary warning for headings outside tables**: Fixed PDFs whose table frame is drawn as one rectangle reporting text outside the table, such as a heading, as an unassigned diagram label. A genuinely ambiguous diagram label on the same page still produces a warning.
- **No double symbols for separately painted fill and outline**: Fixed PDF bars and similar shapes whose fill and outline are painted separately appearing as two symbols in the same cells.
- **Clearer export results**: The start of the export summary separately reports that the Markdown was saved, whether every visual element was converted, and why a person should review the result (pages with figures to review, OCR items). The CLI adds one line per page to review, naming what to check and where the review image is.
- **OCR review counts separate from warnings**: The number of OCR results with confidence below 80% or no confidence is reported separately from warnings. It does not affect the exit code.
- **Shorter review workflow in the GUI**: The result panel shows the number of pages to review, attached review images, and unresolved visual elements, plus short explanations such as "1ページ目：表の上の斜めの線1件を表の記号に変換できませんでした。" (page 1: one diagonal line over a table could not be expressed as table symbols). Diagnostic codes and other details are grouped in a collapsible section. "該当ページを確認" (review page) opens the source page image, with unconverted lines and shapes highlighted in red, next to the Markdown of that page. Guidance for PDF figures now points to the review image or the source page.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.10 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Arbitrary PDF figures and schedules cannot always be fully reconstructed.
- Line styles distinguish solid, dashed, and dotted. Dash-dot patterns are treated as dashed, and color, line width, and legend meaning are not preserved. See "Preserved visual attributes" in the supported features.
- Readable Markdown is one-way output. This update does not automatically determine the meaning or endpoints of diagonal lines and arrows. Review the page image or source document when unresolved warnings remain. Attaching an image does not resolve those warnings.
- The review window shows a page image only when a review image was attached during export. PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- Keep the source document as the authoritative copy. Sidecars and source review images contain source-document content. See [Security and privacy](../docs/en/security-and-privacy.md) before sharing them.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

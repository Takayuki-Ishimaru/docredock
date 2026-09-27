# DocRedock v0.2.10 Public Beta Release Notes

v0.2.10 makes incomplete conversions easier to review and preserves line styles (solid, dashed, dotted) for supported shapes over tables. It also fixes unnecessary warnings and duplicate shape symbols in PDFs.

## What's improved

- **Line styles of shapes over tables**: For supported arrows, bars, and lines over tables in PDF, PowerPoint, Excel, and Word, Readable Markdown now distinguishes dashed and dotted strokes from solid ones. Symbols show the line style, and notes describe the shape kind and its row/column range. Mermaid diagrams use dotted connections for both dashed and dotted source lines, with notes identifying the original style.
- **No unnecessary warning for headings outside tables**: Fixed PDFs whose table frame is drawn as one rectangle reporting text outside the table, such as a heading, as an unassigned diagram label. A genuinely ambiguous diagram label on the same page still produces a warning.
- **No double symbols for separately painted fill and outline**: Fixed PDF bars and similar shapes whose fill and outline are painted separately appearing as two symbols in the same cells.
- **Clearer export results**: Export results separately report whether Markdown was saved, the conversion status of recognized visual elements, and the parts that need human review. For each page requiring review, the CLI describes what to check and gives the review image location when one is available.
- **Clearer OCR review guidance**: Counts of OCR results with low or missing confidence are shown separately from warnings. These counts alone do not change the CLI exit code.
- **Shorter review workflow in the GUI**: The result panel identifies pages and visual elements that need review, with short explanations. Detailed diagnostics are grouped in a collapsible section. "該当ページを確認" (review page) opens the PDF source page image next to that page's Markdown. Unconverted lines and shapes are marked with red lines or frames when their positions are known.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.10 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Arbitrary PDF figures and schedules cannot always be fully reconstructed.
- Line styles distinguish solid, dashed, and dotted. Dash-dot patterns are treated as dashed, and color, line width, and legend meaning are not preserved. See "Preserved visual attributes" in the supported features.
- Readable Markdown is one-way output. This update does not automatically determine the meaning or endpoints of diagonal lines and arrows. Review the page image or source document when unresolved warnings remain. Attaching an image does not resolve those warnings.
- Source page images in the review window are available for PDF and require a review image attached during export. If no image is available, or you are reviewing an Office document, follow the guidance to check the source document. PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled.
- The conversion status describes visual elements recognized by DocRedock. It does not guarantee that every visual detail or meaning in the source document was preserved.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- Keep the source document as the authoritative copy. Sidecars and source review images contain source-document content. See [Security and privacy](../docs/en/security-and-privacy.md) before sharing them.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

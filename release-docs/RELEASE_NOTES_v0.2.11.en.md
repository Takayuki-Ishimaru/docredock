# DocRedock v0.2.11 Public Beta Release Notes

v0.2.11 fixes text and graphics inside PDF drawing components (Form XObjects) disappearing without a warning when the page also carries its own text. The review window can now zoom to each area to check and show the page Markdown rendered.

## What's improved

- **Text and graphics inside PDF Form XObjects**: Text and vector drawing in the Form XObjects a page draws (reusable drawing components inside a PDF, not interactive form fields) now reach Readable Markdown exactly like content painted on the page itself. Previously they were lost without a warning whenever the page also carried its own text; this dates back to v0.2.9 and earlier. Nested forms and each form's placement (its transformation matrix) are applied.
- **Unanalyzable drawing components are never reported as complete**: A drawn component that cannot be analyzed, for example because it is nested too deeply, exceeds the size limit, or is stored in an encoding DocRedock cannot read, gets a `PdfFormXObjectUnparsed` warning and a review page, with a source page image when a PDF rasterizer is available. With OCR enabled, whole-page OCR supplies its text, leaving out results that repeat the page's native text.
- **Unanalyzed content reported separately**: Export results count content that could not be analyzed separately from the conversion status of recognized visual elements: the CLI shows an `Unanalyzed content` line, and the GUI shows "未解析の描画部品" (drawing components not analyzed).
- **Zoom to review areas**: "要確認箇所へ拡大" (zoom to review area) in the review window enlarges each marked area in turn and centers it. "＋" and "－", or Ctrl (or Command on macOS) with the mouse wheel, change the zoom, and "全体" (whole page) returns to the full page.
- **Rendered Markdown in the review window**: The page's Markdown can be shown rendered, with tables as tables and Mermaid diagrams as lists of connections such as "START → END (label)", or as source.
- **Lighter conversion of PDFs with large images**: Reduced unnecessary processing when reading character mapping tables, lowering memory use when converting PDFs with large images.
- **Experimental rendering fix**: A code block fenced with four or more backticks that contains a line of three backticks is no longer closed early at that inner line.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.11 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Arbitrary PDF figures and schedules cannot always be fully reconstructed.
- Clipping to a Form XObject's `/BBox` and the visibility of optional content (layers) are not evaluated, so text that is not visible on screen can appear in the output.
- Readable Markdown is one-way output. When unresolved warnings or unanalyzed content remain, review the page image or source document. Attaching an image does not resolve those warnings.
- Source page images in the review window are available for PDF and require a review image attached during export. If no image is available, or you are reviewing an Office document, follow the guidance to check the source document. PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled.
- The rendered view in the review window is a simplified view for checking. Mermaid diagrams are shown as lists of connections, and a diagram the view cannot read is shown as source.
- The conversion status describes content recognized and analyzed by DocRedock. It does not guarantee that every visual detail or meaning in the source document was preserved.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- Keep the source document as the authoritative copy. Sidecars and source review images contain source-document content. See [Security and privacy](../docs/en/security-and-privacy.md) before sharing them.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

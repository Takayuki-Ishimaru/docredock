# DocRedock v0.2.12 Public Beta Release Notes

v0.2.12 fixes PDF text being converted to the wrong characters without a warning when a page and its drawing components (Form XObjects) use one font name for different fonts. Text that is not visible on screen is left out of the default output, and the review window can hide its red marks so the original can be inspected.

## What's improved

- **Fonts decoded where they are used**: Each PDF font is resolved in the resource dictionary of the page or drawing component (Form XObject) that uses it. Previously, when a page and its forms used one name such as `/F1` for different fonts, all of them were decoded as one font's characters, with no warning; this also affected v0.2.11 and earlier releases. Fonts written directly in a resource dictionary are also decoded with their own character maps.
- **No guessing when a font cannot be identified**: When a font name is not defined where it is used and the document gives that name to different fonts, DocRedock does not decode the text with any of them; the page gets a `PdfFontResourceAmbiguous` warning and a review page.
- **Text not visible on screen is left out by default**: Text outside a Form XObject's visible area (`/BBox`), a clipping path, or the page, and text on optional-content layers that are off when the document opens, is treated as hidden content, like hidden Office text. The default `visible` policy and `sanitized` leave it out (the CLI reports the counts as `PdfClippedTextExcluded` / `PdfHiddenLayerTextExcluded`), and `complete` includes it with a warning. Text that may be partly visible is kept. Text on a layer whose visibility cannot be evaluated is kept with a `PdfLayerVisibilityUnknown` warning and a review page.
- **Highlight switch in the review window**: Clearing "強調表示" (highlight) hides the red marks so the details of the original can be inspected. The red marks keep their on-screen width at any zoom.
- **Excluded hidden content shown in the result panel**: The GUI result panel names each kind of hidden content the content policy left out of the Markdown (hidden Word text, PDF text outside the visible area, and so on) with its count.
- **Internal error on some PDFs fixed**: PDFs whose fonts map one glyph to several characters, such as the ligatures LibreOffice writes, no longer stop conversion with an internal error.
- **Experimental rendering fix**: `render` now accepts the documented `--font-path` and `--font-face-index` options.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.12 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Arbitrary PDF figures and schedules cannot always be fully reconstructed.
- PDF visibility is decided for text only. Clipping of shapes and images, text covered by shapes, text in the background color, text made invisible by its rendering mode (including the invisible text layer of OCRed PDFs), and the overflowing part of partly clipped text are not evaluated, so text that is not visible on screen can still appear in the output.
- In PDFs whose columns are printed across several pages, as Excel does, text drawn outside each page's print area (duplicates of text shown on the neighboring page) is no longer output.
- Readable Markdown is one-way output. When unresolved warnings or unanalyzed content remain, review the page image or source document. Attaching an image does not resolve those warnings.
- Source page images in the review window are available for PDF and require a review image attached during export. If no image is available, or you are reviewing an Office document, follow the guidance to check the source document. PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled.
- The rendered view in the review window is a simplified view for checking. Mermaid diagrams are shown as lists of connections, and a diagram the view cannot read is shown as source.
- The conversion status describes content recognized and analyzed by DocRedock. It does not guarantee that every visual detail or meaning in the source document was preserved.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- Keep the source document as the authoritative copy. Sidecars and source review images contain source-document content. See [Security and privacy](../docs/en/security-and-privacy.md) before sharing them.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

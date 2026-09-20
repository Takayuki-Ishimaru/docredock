# DocRedock v0.2.9 Public Beta Release Notes

v0.2.9 improves double-headed arrows in PDF schedules and makes figures that cannot be fully converted easier to review. OCR review details now follow the body text's reading order and offer a choice of detail levels.

## What's improved

- **Double-headed PDF schedule arrows**: Fixed missing arrowheads and directions that changed with PDF drawing order, for both horizontal and vertical arrows. An arrowhead stays in its endpoint cell even when the arrow only extends a short distance into that cell.
- **Review of diagonal lines crossing tables**: Diagonal lines and arrows that cannot be represented as table symbols are retained as figures requiring review instead of being silently removed as already converted. A page image is attached when source review images are enabled and a PDF rasterizer is available.
- **Missing source review images**: Pages that retain only line or shape path information, without a reconstructed diagram, are now included in review-image generation. A page-specific diagnostic explains when an image cannot be generated. This works with OCR turned off; use the GUI setting or CLI `--pdf-fallback-images auto|off` to control it.
- **Fewer unnecessary table warnings**: Reduced warnings caused by treating cell text as diagram labels in tables with header background fills and rectangular outer frames.
- **Clearer review counts**: Export results separately show pages requiring review, pages with an attached review image, and unresolved visual elements. An attached image is distinguished from successfully reconstructed figure content.
- **OCR review order and detail controls**: Review details follow the body's line and within-line order and include line numbers. Choose the detail level through the GUI OCR review selector or CLI `--ocr-review low-confidence|all|summary`.

## Change to the default OCR review display

Readable Markdown now defaults to detailed review rows only for confidence below 80% or missing confidence. Choose `all` to inspect every record or `summary` for counts only. Every setting retains the OCR body and original image. Experimental audit/roundtrip sidecars retain all records.

Confidence is an OCR engine estimate, not a measured accuracy rate. Identifiers, part numbers, and numbers are never automatically corrected by guessing.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.9 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Arbitrary PDF figures and schedules cannot always be fully reconstructed.
- Readable Markdown is one-way output. This update does not automatically determine the meaning or endpoints of diagonal lines and arrows. Review the page image or source document when unresolved warnings remain. Attaching an image does not resolve those warnings.
- PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled. OCR region navigation through position links depends on viewer support.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- Keep the source document as the authoritative copy. Sidecars and source review images contain source-document content. See [Security and privacy](../docs/en/security-and-privacy.md) before sharing them.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

# DocRedock v0.2.7 Public Beta Release Notes

v0.2.7 improves schedule arrows and duration bars in Readable Markdown, Japanese OCR, and text preservation.

## What's improved

- **Schedule arrows and bars stay inside the table**: Supported arrows, bars, and markers over Word, Excel, PowerPoint, and PDF tables become symbols such as `━━▶`, `━━`, and `◆` in the corresponding Markdown cells. This makes schedules easier to read when their tables and graphics would otherwise appear separately. Supported conditions differ by format.
- **PowerPoint schedules built from shapes**: Supported layouts made from heading and row-label rectangles with arrows or bars over the body become readable Markdown tables. Ordinary card layouts are not universally converted into tables.
- **Better PDF schedule recognition**: Supported regular ruled tables can now be reconstructed even when arrows or filled shapes overlap them. Continue to review fallback output and diagnostics for content that cannot be recognized reliably.
- **Japanese OCR spacing and setup guidance**: Reduced unnecessary spaces between recognized Japanese characters. The GUI can use available OCR for embedded Office images even when a PDF rasterizer is missing. Missing Windows OCR language capabilities and installation guidance are easier to identify.
- **Text preservation in experimental Markdown rendering**: The `render` command now uses consistent rules across output formats for references such as `&amp;`, `&copy;`, and numeric character references. References in code and unknown references stay as written, and text spelling HTML tags does not accidentally become markup.
- **Experimental Word round-trip editing**: Improved restoration of nested-table cell text, including edits made alongside outer-table changes. This supports text edits that retain the existing row, cell, and paragraph structure.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.7 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and other local capabilities.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md) for the supported scope.
- Readable Markdown is one-way output. Schedule symbols are a reading aid and do not fully reproduce original shapes or document layout. Keep the source document and review the converted tables, diagnostics, and images.
- Recognition depends on shape anchoring in Word, headers and row labels around populated cell ranges in Excel, table or rectangle layout in PowerPoint, and ruling lines and shape structure in PDF. Not every schedule or Gantt chart is supported.
- OCR requires an available OCR engine. OCR that rasterizes a PDF page also requires a PDF rasterizer. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, restoration, and new-document generation remain experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [Experimental features](../docs/en/experimental-features.md).
- `.drmd` and `.drmdpkg` may contain the source document or restoration data. Handle them with the same confidentiality controls as the source.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

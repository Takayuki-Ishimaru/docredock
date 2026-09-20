# DocRedock v0.2.8 Public Beta Release Notes

v0.2.8 improves PDF schedule conversion and helps you compare converted content with the source. Experimental restoration also gains a preflight check and protection for original documents.

## What's improved

- **PDF schedule rows and columns**: Improved handling of long filled bars over tables so they are not mistaken for ruling lines that split rows or columns. Supported bars continue to appear as symbols inside table cells.
- **PDF arrow direction**: Improved recognition of schedule arrows drawn as separate shafts and arrowheads, reducing extra rows and incomplete arrow representations.
- **Clear completion with warnings**: The GUI distinguishes successful completion, completion with warnings, and failure. When warnings remain, the result shows that saving finished and source comparison is needed. Unnecessary warnings for resolved tables and figures are removed while warnings for unresolved content remain.
- **PDF source review images**: Pages with unresolved tables or figures receive a review image when a PDF rasterizer is available, even with OCR turned off. Control this through the GUI setting or CLI `--pdf-fallback-images auto|off`; the default is `auto`. Diagnostics explain when an image cannot be generated.
- **OCR review details**: Word or line confidence and positions returned by the OCR engine help you compare recognition results with the original image. Identifiers, part numbers, and numbers are never automatically corrected.
- **Experimental restoration preflight**: `docredock preflight edited.md` checks integrity, edits, and restoration readiness together. It tries restoration in a disposable copy without changing input documents or existing reports.
- **Original-document protection during experimental restoration**: Ordinary `--force` no longer overwrites a historical source document. Intentional replacement requires `--force --replace-original`. After restoration succeeds, the destination's previous contents are retained in a `.bak` file before replacement. Use a new output name for normal restoration.

## How to update

1. Close DocRedock and save any source documents you are editing.
2. Download the v0.2.8 package for your OS and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check OCR and PDF rasterizer availability.

GUI and CLI packages are provided for Windows, macOS, and Linux on x64 and ARM64.

## Limitations and compatibility notes

- This is a Public Beta. See [Supported features](../docs/en/supported-features.md) and the [User guide](../docs/en/user-guide.md). Not every schedule or PDF layout can be recognized.
- Readable Markdown is one-way output. Table symbols and review images do not fully reproduce original shapes or layout. Attaching an image does not remove unresolved warnings; compare the result with the source.
- PDF page images require a PDF rasterizer, and OCR requires an available OCR engine. Tesseract, additional OCR language data, and pdftoppm/mutool are not bundled. OCR region navigation through position links depends on viewer support.
- GUI PDF input is available by default. CLI PDF conversion, round-trip editing, preflight, restoration, and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. A successful preflight does not guarantee visual identity with the source. See [Experimental features](../docs/en/experimental-features.md).
- `.drmd` and `.drmdpkg` may contain the source document or restoration data. New sidecars also retain the source's local absolute path. Handle them with the same confidentiality controls as the source and review them before sharing. With older sidecars, an original that has been moved, renamed, and modified may not be identified as the source. See [Security and privacy](../docs/en/security-and-privacy.md).
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

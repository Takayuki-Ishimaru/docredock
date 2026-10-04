# DocRedock v0.3.0 Public Beta

[日本語](RELEASE_NOTES_v0.3.0.md)

v0.3.0 adds **AI packages** for handing converted documents to an AI tool, improves Word, Excel and PowerPoint readability, and clarifies PDF review guidance.

## AI packages

Select **AI package** (「AI向けパッケージ」) in the GUI and export a folder or ZIP. In the CLI, run:

```sh
docredock export input.docx --ai-package zip
```

A package contains full Markdown, semantic parts, referenced images, review guidance (`review.md`), source locations and file hashes. Conversion runs locally; creating a package does not upload documents. API callers can use `DocumentService.ExportAiPackageAsync`.

Word splits at headings and intact semantic boundaries; PDF pages, PowerPoint slides and Excel sheets stay whole. Tables, nested tables, diagram members and list sequences stay together. Large units can exceed the soft size target and are listed in the review guidance. This is a character target, not a model token limit. Source locations use actual pages, slides, sheets and headings; Word page numbers are not guessed.

Content policies and selected Excel sheets apply throughout the package. Including hidden content with `complete` adds a sharing-review notice. The original document and its absolute path are not bundled, but the source filename, hash and converted content are included. Review the full text, images and `review.md` before sharing.

Failed or cancelled conversion leaves no partial package. CLI `--force` replaces an existing output only after successful conversion; repeated GUI exports use numbered names. Images are stored in the package's `assets/`, so CLI `--embed-images` cannot be combined with AI packages. Packages are one-way outputs, not restoration sidecars.

## Document readability and review

- Improved Excel row-label alignment across spacer columns and separated adjacent lookup lists from schedules and other tables. Multirow headers become hierarchical column names.
- Fixed missing later paragraphs in PowerPoint shape and connection labels, and suppressed decorative contents-slide dot leaders from diagrams when they have no arrows or attached endpoints.
- Exported saved Word chart categories and values as Markdown tables. External chart data and embedded workbook formulas are not evaluated; unavailable saved data retains a warning and placeholder.
- Improved recognition of custom Word heading styles and limited inference for directly formatted titles and chapters. Images retain their display dimensions using HTML `img` tags; use a Markdown viewer that supports HTML images.
- Suppressed unnecessary visual warnings and review images for clipping-only PDF paths. Source-comparison needs and hidden-content sharing review are displayed separately.

## Limitations and distributions

This is a Public Beta, not a production-stable release. Existing PDF/Office conversion and visual-inference limitations remain; do not assume the converted result preserves every aspect of the original. OCR and PDF review images can require a configured OCR provider or rasterizer, depending on the environment.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats and new PDF/Office generation also remain experimental.

Distributions target Windows (x64/Arm64), macOS (Intel/Apple Silicon) and Linux (x64/Arm64). At publication, check the GitHub Release for the package matching your OS/CPU and verify `SHA256SUMS`. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md) and [AI package format contract](../docs/reference/ai-package.md) for instructions, support boundaries and API/JSON details.

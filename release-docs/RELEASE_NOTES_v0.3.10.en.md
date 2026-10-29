# DocRedock v0.3.10 Public Beta

[日本語](RELEASE_NOTES_v0.3.10.md)

v0.3.10 improves PDF reading order and source review, Office table and chart output, and CLI conversion of multiple documents. It also clarifies review before sharing AI packages.

## PDF readability and source review

- Improved the order of text and tables in columns, headings, word spacing, and text immediately below tables. Repeated headers, footers and page numbers are identified and excluded under `sanitized`.
- Detailed unresolved diagram-path inventories stay in the report; the body directs readers to the source. Compare with the original when Markdown alone cannot convey a figure's meaning.
- Pages needing review, including column layouts, can include smaller comparison previews. Improved review guidance for textless pages and unavailable OCR, while preventing duplicate OCR of comparison images.
- PDF rasterization processes several pages concurrently, with improved cleanup on timeout, cancellation or a page failure.

## Office tables, charts and AI packages

- Improved recognition of Word lists and code paragraphs from localized style names and inherited settings.
- AI packages repeat vertically merged table-cell values in each row, in both full-document Markdown and parts. For standalone Readable Markdown, enable `--expand-merged-cells` or the GUI's advanced setting.
- Kept Excel document titles separate from worksheet headings and units. Multiple chart series are aligned by category label in one table; categories that cannot be identified as time periods no longer receive a trend description.
- Improved labeling of hidden content included under `complete` and handling of source metadata during package-body searches.

## CLI and GUI operation

The CLI can convert multiple files or documents directly inside a directory. Select the destination with `--output-dir` and the result display with `--json`, `--quiet` or `--verbose`. For one Readable Markdown input, `-o -` writes the body to stdout and the summary to stderr.

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export input.docx -o - --ocr off > input.md
docredock export report.pdf --allow-pdf --lang en --fail-on-review
```

`--allow-pdf` enables PDF conversion for that invocation. The existing `DOCREDOCK_ENABLE_EXPERIMENTAL=1` option remains available. Choose the language of generated headings and notes with `--lang ja|en`. Outputs that collide with source documents or other inputs are refused; even `--force` refuses symbolic links and special files such as devices.

The GUI has clearer Japanese guidance, OCR availability and source-review messages. Supported files passed at startup are selected for conversion. After installation on Linux, documents can be opened through the file manager's **Open with** action.

## Compatibility and review before sharing

The manifest remains schema `2.2`, and `report.json` remains schema `1.2`. Existing packages using schema `1.0`, `2.0` or `2.1` remain readable. Reading an existing package does not rewrite its body or stored token estimates.

The bundled Python reader now blocks `--part` and `--search` body output by default when a package requires review, returning exit code 11. Read `review.md` first, then pass `--allow-review` to display the body. The legacy `--fail-on-review` option retains exit code 3 and takes precedence over `--allow-review`. Account for this exit-code change in automation. CLI export with `--fail-on-review` saves the output and returns 11 when review is required.

Word revision defaults remain `markup` for `visible`/`complete` and `final` for `sanitized` across the CLI, GUI and API. Markup can include deleted text. Check the [revision defaults](../docs/reference/revision-defaults.md) and `review.md` before sharing.

This is a Public Beta. Readable Markdown and AI packages are one-way output. Keep the original and review text, images and diagnostics. Complete reproduction of diagrams, SmartArt or complex layouts is not guaranteed; an absence of review items does not establish correctness. PDF comparison images and OCR need a suitable rasterizer and OCR provider. Desktop GUI PDF input is enabled by default. Round-trip editing, restoration and new PDF/Office generation remain experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1` in the CLI. For `.doc`, `.xls` and `.ppt`, retain the original and save a separate copy in the corresponding modern Office format before converting.

## Downloads and updating

Choose the archive for your OS and CPU from GitHub Release.

| OS | CPU | Filename |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.10-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.10-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.10-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.10-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.10-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.10-linux-arm64.tar.gz` |

Verify `SHA256SUMS` and follow `QUICKSTART.en.md` in the extracted directory. No separate .NET SDK is required. Check each package's `SIGNING-STATUS.json` for signing/notarization status. Keep existing documents and outputs, close the app, then replace it with the new package.

See the [task guides](../docs/en/quick-tasks.md), [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md), and [security and privacy guide](../docs/en/security-and-privacy.md).

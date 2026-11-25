# DocRedock v0.4.1 Public Beta

[日本語](RELEASE_NOTES_v0.4.1.md)

v0.4.1 improves how Readable HTML displays Office tables, shapes and charts in their source layout. It also adds a review folder for checking Excel workbooks sheet by sheet.

## What changed

- **Preserve Office appearance.** HTML retains supported drawing positions, rotation, stacking order, picture crops, text formatting and charts with saved data. Excel column widths, cell colors, merges, links and print settings are also improved. Unsupported features remain review items.
- **Review Excel by worksheet.** The CLI option `--split-sheets` writes per-sheet Markdown, history-sheet content, images and Readable HTML into one folder. Use `--portable-markdown` for a single Markdown export without raw HTML.
- **Retain PDF text and images.** Improved lists, tables, text placement and repeated body images. Complex pages can use a source page image for comparison when the required tools are available. Under `sanitized`, these page images are omitted to keep excluded content from remaining in their pixels.
- **Inspect originals directly with AI assistants.** Updated integration instructions permit direct Office/PDF inspection with applications, SDKs and document skills. Run `docredock agents install` to update the integration and remove legacy restrictive hooks owned by DocRedock. `--enforce` and `--no-enforce` remain deprecated compatibility options and do not restrict source reading. Optional automatic approval settings remain available.

## How to use it

Choose **Readable HTML** in the desktop app, or use the CLI:

```sh
docredock export report.xlsx --format html --ocr off -o report.html
docredock export report.xlsx --split-sheets --ocr off -o report.review
docredock export report.xlsx --portable-markdown --ocr off -o report.md
docredock agents install
```

Review folders accept one XLSX at a time. They are separate from AI packages. Replacing an existing folder requires `--force`. See the [user guide](../docs/en/user-guide.md#workbook-review-folders).

## Compatibility and limitations

AI packages retain manifest schema `2.2` and report schema `1.2`. Readable HTML and Markdown are one-way outputs, not inputs for restoring Office files. Charts use saved data; external data is not fetched and formulas are not recalculated. Not every shape, effect, font or page layout can be reproduced completely. Keep the original and compare any layout or value that matters to your task.

Conversion runs locally. The `complete` policy can include hidden content, and the default Word revision settings can include deleted text. Use `--revisions final` to exclude deleted text. Review text, images and review items before sharing.

The desktop app accepts PDF directly. CLI/MCP PDF input requires `--allow-pdf` / `allow_pdf: true`. OCR and page images need suitable external tools. Round-trip editing, restoration and new-document generation remain experimental.

## Downloads and upgrades

Choose the archive for your OS and CPU from [GitHub Releases](https://github.com/Takayuki-Ishimaru/docredock/releases), and verify it with `SHA256SUMS`.

| OS | CPU | File |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.4.1-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.4.1-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.4.1-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.4.1-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.4.1-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.4.1-linux-arm64.tar.gz` |

Quit the app before replacing it with the new package. No separate .NET SDK is needed. See the bundled `QUICKSTART.en.md` for launch instructions and `SIGNING-STATUS.json` for signing and notarization status.

[Quick guides](../docs/en/quick-tasks.md) · [Supported features](../docs/en/supported-features.md) · [Security and privacy](../docs/en/security-and-privacy.md)

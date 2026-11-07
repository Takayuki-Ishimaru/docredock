# DocRedock v0.3.11 Public Beta

[日本語](RELEASE_NOTES_v0.3.11.md)

v0.3.11 improves PDF text and heading detection, images and tables in Office exports, and how AI packages split and find text. MCP clients can now convert documents and review AI packages.

## PDF and Office conversion

- PDF conversion joins paragraphs across columns and pages, recognizes more headings distinguished by typeface or weight, and reduces stray spaces between Japanese text and numbers. It groups recurring headers and footers that differ only by page number and is less likely to mistake separator rules or change bars for diagram connectors. It uses figure descriptions when available in tagged PDFs.
- Word exports show tracked deletions as `<del>` and place comments near the passages they refer to under `complete`. Heading-based splitting works in more Word AI packages that contain image-only paragraphs or page breaks.
- Excel conversion uses less memory on large sheets. Chart values use workbook number formats, and `complete` includes cell notes and comments and identifies hidden rows, columns, and sheets.
- PowerPoint diagrams retain more labels beside connectors. Excel and PowerPoint exports use a default label for images without a name or description. When an image selected for export is absent from the Markdown, `ImageNotWritten` flags it for source review.
- Readable Markdown now repeats vertically merged table values on each row by default. To leave the rows below the first one blank, use `--no-expand-merged-cells` or turn off the option in the GUI's advanced settings.

## AI packages and MCP

Package search matches phrases across whitespace and line breaks and shows the heading for each match. See `review.md` when a part exceeds the target size. Use `--table-row-blocks` to split large Excel tables into groups of rows. The GUI offers guidance for the selected format and actions to copy results or re-export the final view.

Register `docredock mcp` as a standard input/output server in an MCP client to convert local documents to Markdown, create AI packages, and review, search, or read them. If a package requires review, read the review information before passing `allow_review` to retrieve the body text. The server reads files named by the client with the permissions of the user who started it. See the [MCP setup guide](../docs/en/quick-tasks.md#use-docredock-from-an-mcp-client).

## CLI and compatibility

- `export --json` uses a `docredock.export.v2` envelope with a `results` array containing one result per input. Automation can use `needs_attention` and `attention` to identify items that need checking. In a batch, conversion errors take precedence over exit 11 from `--fail-on-review`, followed by exit 1 for warnings or empty output.
- Use `--images files|embed|omit` to choose how images are exported. With `-o -`, Readable Markdown uses image descriptions by default. Use `--images embed` to include the images in stdout. For inputs with the same stem, `--keep-source-extension` gives distinct output names such as `report.pdf.md`.
- AI packages use manifest schema `2.2` and `report.json` schema `1.2`. Existing packages with manifest schema `1.0`, `2.0`, or `2.1` remain readable. Word revision defaults are `markup` for `visible` and `complete`, and `final` for `sanitized`. Before sharing, check `review.md` and the [revision defaults](../docs/reference/revision-defaults.md).
- For experimental round-trip editing, `verify` returns exit 0 when it detects an ordinary edit. In Excel, `preflight` and `restore` identify formula cells that may be affected by an edit. Excel recalculates their values when the workbook is opened. Excel exports from v0.3.10 with overlapping table ranges can be edited when the affected cells can be assigned to a table.

## Downloads and upgrades

Select the archive for your OS and CPU from GitHub Releases and verify it against `SHA256SUMS`.

| OS | CPU | File |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.11-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.11-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.11-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.11-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.11-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.11-linux-arm64.tar.gz` |

Extract the archive and follow `QUICKSTART.en.md` to launch or install DocRedock. The archives are self-contained. See `SIGNING-STATUS.json` for signing and notarization status. When upgrading, keep your documents and exports and close the app before replacing it.

Readable Markdown and AI packages are one-way exports in the Public Beta. Keep the source and compare diagrams, complex layouts, and OCR results as needed. The GUI accepts PDFs by default. In the CLI, use `--allow-pdf` to enable PDF conversion. OCR and PDF review images require tools available on your system. Experimental round-trip editing, restoration, and generation require `DOCREDOCK_ENABLE_EXPERIMENTAL=1` in the CLI.

[Quick guides](../docs/en/quick-tasks.md) · [User guide](../docs/en/user-guide.md) · [Supported features](../docs/en/supported-features.md)

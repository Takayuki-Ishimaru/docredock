# DocRedock v0.3.12 Public Beta

[日本語](RELEASE_NOTES_v0.3.12.md)

v0.3.12 improves guidance when an OCR language is unavailable, recalculation handling in Excel round-trip editing, AI package search and reading from MCP, and per-file results from `export --json` in batches.

## PDF and Office conversion

- When a language you requested is not installed in Tesseract, OCR lists the requested, used, and missing languages and shows install advice. With `--ocr-strict`, the run writes its output and then returns exit 8.
- Round-trip editing rejects changes to a protected image's destination and reports `ProtectedNodeEdit`.

## AI packages and MCP

- Package search matches phrases across displayed line breaks. When a term matches a heading directly, the result shows that heading as its own context.
- For Excel, packages that need review are flagged in `review.md`. A note placed beside a table on a sheet stays outside that table.
- When review prevents MCP `package_read` from returning the body, `structuredContent` gives the `status`, `stop_reasons`, and `next_action`. Request sheet names and ranges with `source_detail: "compact"`, or use `with_sources: true` for full source details. See the [MCP setup guide](../docs/en/quick-tasks.md#use-docredock-from-an-mcp-client).

## CLI and compatibility

- `export --json` uses the `docredock.export.v2` envelope with a `results` array holding one result per input. When a batch has inputs that resolve to the same output, all inputs are rejected before anything is written and every input gets a result, not only the colliding ones. When the inputs cannot be identified, the error is reported at the top level instead of in `results`.
- For experimental Excel round-trip editing, an edit that may change formula results raises an `XlsxRecalculationRequired` warning that the workbook is pending recalculation. In AI packages, a pending workbook is gated as needing review in `review.md`. Open the workbook in Excel, recalculate, and save to finalize the values.
- The GUI bundles Avalonia 12.1.5. The archives are self-contained and need no separate .NET SDK.

## Downloads and upgrades

Select the archive for your OS and CPU from GitHub Releases and verify it against `SHA256SUMS`.

| OS | CPU | File |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.12-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.12-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.12-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.12-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.12-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.12-linux-arm64.tar.gz` |

Extract the archive and follow `QUICKSTART.en.md` to launch or install DocRedock. See `SIGNING-STATUS.json` for signing and notarization status. When upgrading, keep your documents and exports and close the app before replacing it.

Readable Markdown and AI packages are one-way exports in the Public Beta. Keep the source and compare diagrams, complex layouts, and OCR results as needed. The GUI accepts PDFs by default. In the CLI, use `--allow-pdf` to enable PDF conversion. OCR and PDF review images require tools available on your system. Experimental round-trip editing, restoration, and generation require `DOCREDOCK_ENABLE_EXPERIMENTAL=1` in the CLI.

[Quick guides](../docs/en/quick-tasks.md) · [User guide](../docs/en/user-guide.md) · [Supported features](../docs/en/supported-features.md)

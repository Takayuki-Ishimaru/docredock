# DocRedock v0.3.2 Public Beta

[日本語](RELEASE_NOTES_v0.3.2.md)

v0.3.2 improves Excel table boundaries and explains what to review after conversion and how reading order and table structure were obtained. AI packages gain an option to split large Excel tables between data rows.

## Excel tables and headings

- Fixed tables being split between an ID or row label and its quantities when a blank column separates them. Remarks columns also keep their row associations.
- Independent tables and one-column lookup lists are separated using their own headings, labels and layout. Values-only regions are treated as independent tables when their rows are out of step with a neighbouring table or they use their own year, month or date keys. Declared Excel table ranges are also consulted.
- Touching tables without a blank column are separated when each has its own merged title and their structures differ, such as a table beside a legend. Colours or borders alone do not split a single table.
- Fixed table titles becoming a neighbouring table's headers, short captions above a table becoming column headers, and unnecessary "文書情報" headings above ordinary two-column tables.

When layout does not establish a boundary, the separated ranges are listed for comparison with the original. This guidance alone does not change the CLI exit code. Complex tables still require source comparison.

## Conversion review guidance

GUI and CLI results identify detected review items and whether reading order and table structure came from the source, were inferred from layout, need comparison, or were not evaluated. Uncertain Excel table boundaries include the sheet and cell ranges to check.

No detected review items does not certify correct conversion. AI-package `review.md` includes the same guidance. Rendered document table counts now exclude the OCR review tables added by DocRedock.

## AI packages

Use the GUI option 「大きなExcelの表を行ブロックに分割」, CLI `--table-row-blocks`, or API `TableRowBlocks: true` to split Excel tables that exceed the part-size target between data rows. Each block repeats the header and records original cell ranges, the table ID, block order, and links to neighbouring parts. Full `document.md` remains intact.

```sh
docredock export input.xlsx --ai-package zip --table-row-blocks
```

The option is off by default. By default, worksheets remain whole; Word, PowerPoint and PDF splitting is unchanged. Part size is a soft target: a long individual row can exceed it.

**Compatibility for integration tools:** Manifest schema changes from `2.0` to `2.1`, adding `table_row_blocks`, each part's `estimated_tokens`, and `table_block` for row blocks. `estimated_tokens` is a model-independent estimate, not a guarantee of actual token count or model limits. `report.json` uses schema `1.1`, adding reading-order and table-structure basis fields and `review.table_boundaries`. `review.required` now also accounts for table-boundary comparison. Readers should check supported schemas and ignore unknown additive fields.

The API's `AiPackageManifestReader` and Python reader example support manifest schema `1.x` and `2.x` and file-hash verification. Unsupported major versions produce an explicit error. See the [format contract](../docs/reference/ai-package.md).

## Other fixes

On macOS, images less than 3 pixels wide or high are skipped for OCR, avoiding unnecessary failure warnings from tiny spacer images. Vision OCR failures are reported in a short, single-line message.

## Limitations and distributions

This is a Public Beta, not a production-stable release. Existing PDF/Office conversion and visual-inference limitations remain. OCR and PDF review images can require a configured OCR provider or rasterizer, depending on the environment.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats and new PDF/Office generation also remain experimental.

AI packages are generated locally without uploading documents. Review the full text, images and `review.md` before sharing.

Distributions target Windows (x64/Arm64), macOS (Intel/Apple Silicon) and Linux (x64/Arm64). At publication, check the GitHub Release for the package matching your OS/CPU and verify `SHA256SUMS`. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

See the [user guide](../docs/en/user-guide.md) and [supported features](../docs/en/supported-features.md) for instructions and support boundaries.

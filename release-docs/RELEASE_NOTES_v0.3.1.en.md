# DocRedock v0.3.1 Public Beta

[日本語](RELEASE_NOTES_v0.3.1.md)

v0.3.1 improves Excel table separation and PDF column reading order, and fixes false changes during experimental Word round-trip editing. AI packages use more compact source metadata and make the section context of individual parts easier to identify.

## Document conversion fixes

- **Excel:** Fixed independent tables and lookup lists merging into one table when separated by a single blank column. Tables with a spacer column between row labels and values retain their associations. A merged title spanning the whole table appears before the table instead of becoming a column header.
- **PDF:** Fixed left and right columns interleaving when lines in a two-column layout have different lengths. Where a column gutter can be identified, the left column finishes before the right column begins. Complex layouts still require comparison with the original.
- **Word round-trip editing (experimental):** Fixed an unchanged image-containing paragraph with trailing whitespace being reported as edited, which could block editing and restoring another paragraph. Original whitespace is preserved, and actual edits are detected separately.

## AI packages

Repeated source information in `manifest.json` is reduced, with detailed node and cell mappings stored in `source-index.json`. The detailed index is included in the package's file hashes. Full Markdown and individual parts remain included.

Word parts now include `section_path` metadata identifying their parent headings. A table-only or list-only part retains its section context when used separately.

**Compatibility for integration tools:** The manifest schema changes from `1.0` in v0.3.0 to `2.0`. Tools that read the manifest directly must support shared `locations`, each part's `source_ids` and `node_count`, and the detailed index referenced by `source_index`. Detailed `node_ids` and Excel `cell_addresses` are stored in that index. GUI export, CLI `--ai-package dir|zip`, and use of full Markdown follow the same workflow. See the [format contract](../docs/reference/ai-package.md).

CLI/GUI conversion results and AI-package `report.json` separately report the number of tables actually rendered in Markdown. This includes tables assembled from Excel cells, addressing cases where the result appeared to report zero tables despite showing them in Markdown. Table counts do not establish correct content or reading order; review the text as well.

## Limitations and distributions

This is a Public Beta, not a production-stable release. Existing PDF/Office conversion and visual-inference limitations remain. OCR and PDF review images can require a configured OCR provider or rasterizer, depending on the environment.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats and new PDF/Office generation also remain experimental.

AI packages are generated locally without uploading documents. Review the full text, images and `review.md` before sharing.

Distributions target Windows (x64/Arm64), macOS (Intel/Apple Silicon) and Linux (x64/Arm64). At publication, check the GitHub Release for the package matching your OS/CPU and verify `SHA256SUMS`. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

See the [user guide](../docs/en/user-guide.md) and [supported features](../docs/en/supported-features.md) for instructions and support boundaries.

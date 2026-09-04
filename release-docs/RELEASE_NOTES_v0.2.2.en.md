# DocRedock v0.2.2 Public Beta Release Notes

Release date: 2026-09-04

v0.2.2 is a quality update for PDF diagrams and tables, XLSX diagnostics and sheet selection, and CLI diagnostic output. Updating is recommended for v0.2.1 users. The supported-feature boundary is unchanged.

## Highlights

- PDF conversion now avoids treating regular table borders with text-filled cells as diagram connectors. When an untagged grid is inferred to be a table from its layout, a diagnostic is emitted. A lattice that is regular on only one axis, or lines that touch semantic shapes, remains available for review instead of being assumed to be a table.
- Arrowhead-to-shaft matching is more reliable in PDF flow diagrams, including diagrams with several arrows. Branch labels such as `YES` and `NO` remain attached to their connectors, while a triangle containing its own label remains a diagram node.
- PDF visual analysis now has time and work limits. If an unusually complex page reaches a limit, partial guesses are discarded and fallback output is retained with a diagnostic.
- XLSX now emits a Warning when a formula's saved result is absent, empty, or whitespace-only. DocRedock does not calculate formulas; reopen and save the workbook in a spreadsheet application when a current result is required.
- With `--sheets`, diagnostics, hidden-content counts, and image assets now follow the selected worksheet scope. Images from unselected sheets are not written.
- CLI diagnostics are summarized as one line per code by default. Use `--verbose` to inspect individual messages and available source locations.
- Restored the project banner and app icon in both README languages.

## Updating

1. Check the current version at the top of the app and select **Check for updates**.
2. Download the v0.2.2 package for your operating system and CPU from the trusted GitHub release page.
3. Verify the published `SHA256SUMS` and the package's `SIGNING-STATUS.json`.
4. Keep the source documents and required output, then replace the old package. For a Linux user-local installation, rerun `./install.sh` from the newly extracted package.

## Known limitations

- Readable Markdown is one-way output; complete reconstruction of Office drawing objects or PDF layout is not guaranteed.
- Even in `safe` mode, connectors, directions, and labels that cannot be resolved uniquely remain in fallback output and diagnostics instead of being asserted in Mermaid.
- Image-only PDF OCR needs an available rasterizer and OCR provider. The GUI disables OCR and explains why when the required capability is unavailable.
- Office restore, edited-PDF generation, and new-document generation remain experimental.

See [Supported features](../docs/en/supported-features.md), the [User guide](../docs/en/user-guide.md), and [Security and privacy](../docs/en/security-and-privacy.md) for the current contract.

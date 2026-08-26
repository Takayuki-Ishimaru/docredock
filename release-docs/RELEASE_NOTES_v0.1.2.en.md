# DocRedock v0.1.2 Public Beta

[日本語](RELEASE_NOTES_v0.1.2.md) | English

Released: August 26, 2026

v0.1.2 is a Public Beta update focused on complex XLSX/PPTX structure recognition, readable Markdown quality, round-trip previews, and safer output processing.

> **The currently approved scope is one-way “Markdown only” export from DOCX, XLSX, and PPTX.**
> PDF conversion/rendering and restoration to original file formats have not been validated sufficiently and may not work. Do not use them at this stage.

## Downloads

Choose the asset matching your operating system and CPU on GitHub Releases.

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.2-win-x64.zip` | `DocRedock-v0.1.2-win-arm64.zip` |
| macOS | `DocRedock-v0.1.2-osx-x64.zip` | `DocRedock-v0.1.2-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.2-linux-x64.tar.gz` | `DocRedock-v0.1.2-linux-arm64.tar.gz` |

Each package includes the GUI, CLI, Japanese and English quick starts and security guidance, licenses, artifact-file checksums, an artifact-linked SBOM, provenance, and an explicit signing-status record. A separate .NET SDK installation is not required.

## Highlights

### XLSX

- Distinguishes separated table regions, wide layouts, recurring column gaps, and nearby singleton cells, reducing accidental attachment of unrelated cells to a table.
- Detects the 1900 and 1904 date systems and formats dates, date-times, clock times, elapsed times, and percentages according to the workbook’s number formats.
- Preserves sparse chart point indexes so categories and values do not shift out of alignment.
- Improves consistency between readable table regions/display values and the editable round-trip projection.

### PPTX

- Detects full-width bands and left/right columns so two-column slides read down the left column before continuing with the right column.
- Summarizes bar and line series with direction, minima, and maxima; pie and doughnut charts report the largest and smallest components with their share of the total.
- Composes nested group `off/ext/chOff/chExt`, rotation, horizontal/vertical flips, and non-uniform scaling into absolute slide coordinates.
- Handles missing or zero-sized group transforms without producing non-finite geometry.

### Markdown and previews

- Improves ordering and presentation of headings, tables, notes, shapes, and charts while keeping readable and round-trip profiles distinct.
- Round-trip-specific previews preserve DRMD control comments as the editing contract while reducing their visual noise.
- Improves inline formatting and line-break handling for slide shape text and table content.

### Safety and reliability

- Applies per-entry, total expanded-size, and compression-ratio limits to non-media Office ZIP entries as well as media.
- Bounds relationship XML and chart point counts to prevent excessive memory use from hostile or malformed input.
- A backup-cleanup failure after a successful multi-output commit no longer rolls back valid installed outputs.
- Rollback continues all feasible recovery operations and reports multiple failures together.

## Verification

- All 259 tests pass in Release configuration.
- Readable and round-trip exports were checked with a complex XLSX, a complex PPTX, and a 15-slide synthetic PPTX.
- The tested XLSX and PPTX files restored byte-identically in unchanged F0 regression checks.
- The release workflow requires locked restore, Release build, the full test suite, conversion QA, LicenseAudit, and extracted-package smoke tests for all six RIDs.

## Known limitations

- Do not use PDF conversion or rendering.
- Do not restore DOCX, XLSX, PPTX, or PDF files to their original formats. Restore checks in this release are mechanical regression tests, not approval for user operation.
- `.drmd` and `.drmdpkg` can contain the original document and must be protected at the same confidentiality level.
- Tesseract, language models, Mermaid CLI, and a PDF rasterizer are not bundled.
- Macros, signatures, encryption, protection, and unsafe or unsupported package structures may be rejected.
- Windows signing and macOS signing/notarization are applied only when credentials are configured; every package records its status.

See the [user guide](USER_GUIDE.en.md), [format capability matrix](../docs/FORMAT_CAPABILITY_MATRIX.md), and [security and privacy guide](SECURITY_AND_PRIVACY.en.md) for details.

## License

DocRedock is licensed under the MIT License. Third-party dependencies and bundled assets remain subject to their respective licenses.

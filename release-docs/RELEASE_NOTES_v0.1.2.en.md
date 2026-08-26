# DocRedock v0.1.2 Public Beta

[日本語](RELEASE_NOTES_v0.1.2.md) | English

Released: August 26, 2026

v0.1.2 is a Public Beta update that improves readable Markdown output for using Office documents with AI search, summaries, and question answering.

## Who should update

- Users who want to pass DOCX, XLSX, or PPTX documents to AI for search, summaries, or questions.
- Users working with wide or separated tables, two-column slides, charts, or embedded images.
- Users who need PDF conversion, round-trip editing, restoration to an original format, or new-document generation should not use this release for those workflows yet.

## User-visible improvements

| Area | Before | In v0.1.2 |
| --- | --- | --- |
| XLSX tables and values | Separated regions could merge, and raw numbers could appear in output | Separated tables are distinguished more reliably, with dates, times, and percentages formatted as display values more often |
| PPTX reading order | Two-column and full-width elements could be difficult to follow | Columns are ordered more naturally, and chart highlights are easier to review |
| Output safety | Large or malformed inputs could increase processing pressure | Stronger resource limits protect processing and preserve outputs on failure |

## v0.1.2 support scope

| Operation | Treatment |
| --- | --- |
| DOCX/XLSX/PPTX → Readable Markdown (`readable`) | Supported |
| PDF → Markdown | Not supported |
| Round-trip editing (`roundtrip`) and restoration (`restore`) | Not supported; experimental engine |
| New-document generation (`render`) | Not supported; experimental engine |

Readable Markdown is a one-way output. Review the source and generated content before sharing. When images are present, the `.assets` directory beside the Markdown is also part of the shareable output.

## Downloads

Choose the asset matching your operating system and CPU on GitHub Releases.

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.2-win-x64.zip` | `DocRedock-v0.1.2-win-arm64.zip` |
| macOS | `DocRedock-v0.1.2-osx-x64.zip` | `DocRedock-v0.1.2-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.2-linux-x64.tar.gz` | `DocRedock-v0.1.2-linux-arm64.tar.gz` |

Each package includes the GUI, CLI, Japanese and English quick starts and security guidance, licenses, artifact-file checksums, an artifact-linked SBOM, provenance, and an explicit signing-status record. A separate .NET SDK installation is not required.

## Verification

- All 259 tests pass in Release configuration.
- Readable and round-trip exports were checked with a complex XLSX, a complex PPTX, and a 15-slide synthetic PPTX.
- The tested XLSX and PPTX files restored byte-identically in unchanged F0 regression checks. This is a mechanical regression check, not support for round-trip or restore operation.
- The release workflow requires locked restore, Release build, the full test suite, conversion QA, LicenseAudit, and extracted-package smoke tests for all six RIDs.

## Known limitations

- PDF conversion and rendering are not supported in this release.
- Restoration of DOCX, XLSX, PPTX, or PDF files to their original formats is not supported in this release.
- `.drmd` and `.drmdpkg` can contain the original document and must be protected at the same confidentiality level.
- Tesseract, language models, Mermaid CLI, and a PDF rasterizer are not bundled.
- Macros, signatures, encryption, protection, and unsafe or unsupported package structures may be rejected.
- Windows signing and macOS signing/notarization are applied only when credentials are configured; every package records its status.

## Experimental engine changes — not supported in v0.1.2

The following changes are part of implementation and regression testing but are outside the v0.1.2 user support scope.

- The round-trip preview, `.drmd`/`.drmdpkg`, `verify`, `diff`, and `restore` paths were maintained and improved.
- Nested PPTX groups compose `off/ext/chOff/chExt`, rotation, flips, and non-uniform scaling into coordinates while avoiding non-finite geometry for missing or zero-sized transforms.
- Relationship XML, ZIP expanded-size/compression-ratio, and sparse chart-point limits are enforced. A backup-cleanup failure after a successful multi-output commit does not roll back committed outputs; feasible rollback operations continue and are reported.
- Mermaid rendering in `render`, Office templates, PDF fallback, and restoration to original formats remain experimental engine features and are not supported for users.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), and [security and privacy guide](../docs/en/security-and-privacy.md) for details.

## License

DocRedock is licensed under the MIT License. Third-party dependencies and bundled assets remain subject to their respective licenses.

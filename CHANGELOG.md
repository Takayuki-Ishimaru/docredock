# Changelog

Notable user-facing changes to DocRedock are summarized here. GitHub Releases is the canonical source for downloadable artifacts, checksums, signing status, and complete release evidence.

## [0.1.3] - 2026-08-26

Public Beta security and readability update.

- Readable Markdown now defaults to the shared `visible` policy and excludes recognized hidden Office content.
- Added `complete` with an explicit hidden-content warning and a stronger `sanitized` policy.
- Added GUI and CLI content-policy controls.
- Improved DOCX title/heading/list rendering, XLSX metadata/table separation and missing formula-cache markers, PPTX document/slide headings, and experimental HTML rendering for lists, breaks, tables, code, emphasis, and relative images.
- Centralized experimental workflow opt-in with `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.
- Added assembly-derived `docredock --version` and `DOCREDOCK_DISABLE_UPDATE_CHECK=1`.
- Strengthened unchanged F0 restoration and release smoke coverage.

User-supported scope remains one-way DOCX/XLSX/PPTX → Readable Markdown conversion. PDF conversion, round-trip editing, restoration, and new-document generation remain experimental.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.3.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.3.md)

## [0.1.2] - 2026-08-26

Public Beta update focused on more natural readable Markdown output for complex XLSX and PPTX documents, safer archive handling, and more reliable output commits.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.2.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.2.md)

## [0.1.1]

- [English release notes](release-docs/RELEASE_NOTES_v0.1.1.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.1.md)

## [0.1.0]

- [English release notes](release-docs/RELEASE_NOTES_v0.1.0.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.0.md)

[0.1.3]: https://github.com/Takayuki-Ishimaru/docredock/releases
[0.1.2]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2
[0.1.1]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1
[0.1.0]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.0

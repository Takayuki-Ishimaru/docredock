# Changelog

Notable user-facing changes to DocRedock are summarized here. GitHub Releases is the canonical source for downloadable artifacts, checksums, signing status, and complete release evidence.

## [0.1.5] - 2026-08-28

Public Beta reliability hotfix.

- Made CLI `export` default to the one-way `readable` profile.
- Kept OCR nodes with their parent image/PDF page partition and isolated unresolved evidence with diagnostics.
- Added safe rectangular expansion and edit validation for horizontally/vertically merged tables.
- Removed the bundled Japanese font and added explicit/environment/system TrueType resolution with TTC face extraction, glyph coverage, and embedding-permission checks.
- Separated render information/coverage from omission and truncation warnings; warning-producing CLI renders return exit code 1.
- Enabled PDF input by default in the desktop GUI while keeping CLI PDF conversion behind the experimental gate.
- Fixed PPTX literal bullet normalization, including emphasized bullet glyphs.
- Added deterministic complex XLSX conversion QA, three-format coverage enforcement, package checksum verification, Japanese PDF smoke coverage, and rejection of bundled font binaries.
- Updated release packaging and documentation for v0.1.5.

The user-supported scope remains one-way DOCX/XLSX/PPTX/PDF → Readable Markdown conversion in the desktop GUI. CLI PDF conversion, round-trip editing, restoration, and new-document generation remain experimental.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.5.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.5.md)

## [0.1.4] - 2026-08-27

Public Beta quality and startup reliability update based on the independent v0.1.3 review.

- Fixed the GUI startup crash caused by an early content-policy selection event during XAML initialization.
- Added a dedicated Avalonia headless `MainWindow` construction test and strengthened the packaged Linux GUI smoke test.
- Improved DOCX, XLSX, PPTX, and experimental HTML readability.
- Added focused regression coverage for every reviewed conversion issue.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.4.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.4.md)

## [0.1.3] - 2026-08-26

Public Beta security and readability update.

- Added shared `visible`, `complete`, and `sanitized` content policies.
- Centralized experimental workflow opt-in with `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.
- Strengthened unchanged F0 restoration and release smoke coverage.

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

[0.1.5]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.5
[0.1.4]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.4
[0.1.3]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.3
[0.1.2]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2
[0.1.1]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1
[0.1.0]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.0

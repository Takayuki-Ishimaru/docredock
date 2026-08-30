# Changelog

Notable user-facing changes to DocRedock are summarized here. GitHub Releases is the canonical source for downloadable artifacts, checksums, signing status, and complete release evidence.

## [0.2.0] - 2026-08-30

- Public Beta visual inference modes: native-only, safe (default), and balanced.
- Ambiguous connections remain unresolved with fallback assets/source text and stable diagnostics.
- Improved conditional flow handling across DOCX, XLSX, PPTX, and PDF while retaining readable fallback for unresolved content.

- [English release notes](release-docs/RELEASE_NOTES_v0.2.0.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.2.0.md)
- [v0.1.7 errata](release-docs/ERRATA_v0.1.7.en.md)

## [0.1.7] - 2026-08-29

Visual-semantics correctness hotfix.

- Improved diagram checks so malformed or low-confidence relationships are not presented as confirmed Mermaid.
- Retained resolved connections as readable fallback when diagrams are disabled.
- Added clearer diagnostics for incomplete visual conversion.
- Documented the v0.1.6 errata and the conditional visual-support boundary.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.7.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.7.md)
- [v0.1.6 errata](release-docs/ERRATA_v0.1.6.en.md)

## [0.1.6] - 2026-08-29

Visual-semantics reliability update.

- Improved preservation of resolvable PPTX connector flows, including labels and explicit unresolved diagnostics.
- Made visual warnings consistent across supported formats.
- Improved conditional handling of DOCX DrawingML/WPS/VML connectors while retaining text or image fallback when endpoints cannot be resolved.
- Added XLSX `flowChart*` preset recognition while retaining labels for unknown presets.
- Kept PDF native text and supported simple vector flows while retaining unresolved paths and image-only content as explicit fallback.
- Improved stability when source identifiers are duplicated or missing.

The user-supported scope remains one-way DOCX/XLSX/PPTX/PDF → Readable Markdown conversion in the desktop GUI. CLI PDF conversion, round-trip editing, restoration, and new-document generation remain experimental.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.6.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.6.md)

## [0.1.5] - 2026-08-28

Public Beta reliability hotfix.

- Made CLI `export` default to the one-way `readable` profile.
- Kept OCR nodes with their parent image/PDF page partition and isolated unresolved evidence with diagnostics.
- Added safe rectangular expansion and edit validation for horizontally/vertically merged tables.
- Removed the bundled Japanese font and added explicit/environment/system TrueType resolution with TTC face extraction, glyph coverage, and embedding-permission checks.
- Separated render information/coverage from omission and truncation warnings; warning-producing CLI renders return exit code 1.
- Enabled PDF input by default in the desktop GUI while keeping CLI PDF conversion behind the experimental gate.
- Fixed PPTX literal bullet normalization, including emphasized bullet glyphs.
- Expanded release checks for complex XLSX output, package checksums, and Japanese PDF output.
- Updated release packaging and documentation for v0.1.5.

The user-supported scope remains one-way DOCX/XLSX/PPTX/PDF → Readable Markdown conversion in the desktop GUI. CLI PDF conversion, round-trip editing, restoration, and new-document generation remain experimental.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.5.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.5.md)

## [0.1.4] - 2026-08-27

Public Beta quality and startup reliability update based on the independent v0.1.3 review.

- Fixed a desktop application startup crash when the content policy initialized.
- Improved DOCX, XLSX, PPTX, and experimental HTML readability.

- [English release notes](release-docs/RELEASE_NOTES_v0.1.4.en.md)
- [日本語リリースノート](release-docs/RELEASE_NOTES_v0.1.4.md)

## [0.1.3] - 2026-08-26

Public Beta security and readability update.

- Added shared `visible`, `complete`, and `sanitized` content policies.
- Centralized experimental workflow opt-in with `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.
- Strengthened protection against unintended changes during restoration.

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

[0.2.0]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.2.0
[0.1.7]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.7
[0.1.6]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.6
[0.1.5]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.5
[0.1.4]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.4
[0.1.3]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.3
[0.1.2]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2
[0.1.1]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1
[0.1.0]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.0

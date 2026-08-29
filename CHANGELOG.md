# Changelog

Notable user-facing changes to DocRedock are summarized here. GitHub Releases is the canonical source for downloadable artifacts, checksums, signing status, and complete release evidence.

## [0.1.6] - 2026-08-29

Visual-semantics reliability update. Local pre-release verification completed 359 main tests, 4 GUI headless tests, and an osx-arm64 extracted-binary smoke. The GitHub release workflow builds and smoke-tests six RID-specific self-contained distributions and publishes checksums, SBOMs, provenance, attestations, signing status, and release evidence.

- Added a format-neutral visual graph and semantic Mermaid projection for resolvable PPTX connector flows, including native connections, conservative geometry inference, edge labels, and explicit unresolved diagnostics.
- Made visual warning codes stable across the public service and adapter catalog.
- Added DOCX Markup Compatibility Choice/Fallback exclusivity, stable visual anchors, and conditional DrawingML/WPS/VML connector topology with native or uniquely inferred endpoints, labels, and explicit unresolved fallback.
- Added XLSX `flowChart*` preset mapping while retaining unknown preset labels.
- Kept PDF native text while reconstructing supported vector paths into conditional node/edge topology, retaining curves, unresolved paths, and image-only content as explicit fallback diagnostics.
- Normalized duplicate or empty DocumentGraph node IDs deterministically and retained parent references.
- Updated bilingual support guidance, release notes, checklists, and extracted-distribution smoke coverage for v0.1.6.

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

[0.1.6]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.6
[0.1.5]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.5
[0.1.4]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.4
[0.1.3]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.3
[0.1.2]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2
[0.1.1]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1
[0.1.0]: https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.0

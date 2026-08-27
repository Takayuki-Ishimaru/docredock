# DocRedock release documentation

[日本語](README.md) | English

This directory contains versioned release notes and maintainer publication procedures. The user-facing entry point is the root [README.md](../README.md).

## v0.1.5 Public Beta

- Supported: local DOCX/XLSX/PPTX → Readable Markdown. The desktop GUI also accepts PDF input by default.
- Safe default: `visible`; broader `complete` output warns about hidden content; `sanitized` is stricter.
- Experimental: GUI round-trip and restore require an explicit labeled session opt-in. CLI round-trip, restore, render, package operations, and PDF export/restore/render require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.

## For users

- [User guide](../docs/en/user-guide.md)
- [v0.1.5 supported features](../docs/en/supported-features.md)
- [Experimental features](../docs/en/experimental-features.md)
- [Security and privacy](../docs/en/security-and-privacy.md)
- [v0.1.5 release notes](RELEASE_NOTES_v0.1.5.en.md)

## For maintainers

- [Publication scope](PUBLICATION_SCOPE.en.md)
- [Release checklist](RELEASE_CHECKLIST.en.md)
- [Japanese publication scope](PUBLICATION_SCOPE.md)
- [Japanese release checklist](RELEASE_CHECKLIST.md)

GitHub Releases is the canonical user-facing history. Older release notes remain as repository references.

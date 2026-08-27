# DocRedock

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
  <img alt="DocRedock" src="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
</picture>

A local-first Office-to-Markdown converter for AI workflows. Round-trip editing remains experimental.

[日本語](README.ja.md) · [Download the current Public Beta](https://github.com/Takayuki-Ishimaru/docredock/releases) · [User guide](docs/en/user-guide.md) · [Supported features](docs/en/supported-features.md)

## v0.1.5 Public Beta support

| Feature | Status |
| --- | --- |
| DOCX / XLSX / PPTX → Readable Markdown | Supported as Public Beta |
| PDF → Markdown | Supported in the desktop GUI; CLI requires explicit opt-in |
| Edited Markdown → Office restoration | Experimental; explicit opt-in required |
| New PDF / Office document generation | Experimental; explicit opt-in required |

The [release support table](docs/en/supported-features.md) is authoritative for user-facing availability. The [implementation capability matrix](docs/FORMAT_CAPABILITY_MATRIX.md) separately describes code-level capabilities.

## Use it in 30 seconds

1. Download the package for your operating system and CPU architecture from [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases).
2. Start DocRedock.
3. Drop a DOCX, XLSX, PPTX, or PDF file.
4. Select **Readable Markdown** and keep **Visible content only (recommended)**.
5. Convert, review the Markdown and diagnostics, then use the result with your AI tool.

A document containing images normally produces:

```text
input.xlsx
  ↓
input.md
input.assets/
```

The CLI now defaults to Readable Markdown, so `--profile readable` is optional:

```sh
docredock export input.xlsx --content-policy visible --output input.md
```

## v0.1.5 reliability improvements

- OCR evidence stays with its parent image or PDF page partition; unresolved evidence is isolated in `derived-assets` with a diagnostic.
- Horizontally and vertically merged tables use blank Markdown continuation cells and reject continuation/shape edits during round-trip processing.
- Experimental PDF rendering no longer ships or assumes a bundled Japanese font. ASCII-only PDFs use Base14 Helvetica; non-ASCII output resolves an embeddable TrueType face from `--font-path`, environment variables, then installed system fonts.
- PDF rendering reports selected-font and coverage information separately from actionable omission/truncation warnings. CLI render exits with code 1 when warnings exist.
- PPTX literal bullet glyphs, including emphasized bullets, are normalized to Markdown list items.
- Conversion QA now requires DOCX, XLSX, and PPTX coverage, and package smoke tests verify checksums and reject bundled font binaries.

## Content policy

Readable export has three policies in both the GUI and CLI. They filter the Markdown projection; the external `.assets/` directory contains only image assets referenced by nodes included under the selected policy. Review both outputs before sharing.

| Policy | Behavior |
| --- | --- |
| `visible` | Default. Excludes recognized Office-hidden text, sheets, rows/columns, slides/objects, notes, comments, and revisions. |
| `complete` | Includes hidden and metadata content and emits a warning. |
| `sanitized` | Applies the visible filter and additionally removes privacy-sensitive metadata, derived/OCR content, and document furniture. |

## Important limitations

- v0.1.5 is a Public Beta, not a production-stable release.
- Always review generated Markdown and images before sharing. Office visibility metadata is complex and third-party producers may encode content differently.
- Readable Markdown is one-way output. Keep the original Office file as the authoritative source.
- Experimental CLI workflows require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. This includes CLI PDF export (conversion), round-trip/audit operations, restoration, and rendering/new-document generation. Read-only `docredock inspect <file.pdf>` remains available without the flag.
- The desktop GUI accepts DOCX, XLSX, PPTX, and PDF input by default. PDF OCR still needs a configured rasterizer and OCR provider; if either is unavailable, review the emitted diagnostics.
- DocRedock does not bundle a Japanese PDF font or download one. The user is responsible for installing/selecting a font and complying with its embedding license.
- The GUI may query the public GitHub Releases API for update metadata. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable this request.
- Verify the published SHA-256 checksum and signing/notarization status for each package.

## Documentation

- [Japanese user guide](docs/ja/user-guide.md)
- [English user guide](docs/en/user-guide.md)
- [v0.1.5 supported features](docs/en/supported-features.md)
- [Security and privacy](docs/en/security-and-privacy.md)
- [v0.1.5 release notes](release-docs/RELEASE_NOTES_v0.1.5.en.md)
- [Experimental features](docs/en/experimental-features.md)
- [Contributing, build, and test](CONTRIBUTING.md)

## License

DocRedock is released under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for third-party dependencies and assets.

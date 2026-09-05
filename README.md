# DocRedock

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
    <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
    <img alt="DocRedock — local-first Office to Markdown" src="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png" width="1200">
  </picture>
</p>

<p align="center">
  <img alt="DocRedock app icon" src="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/app-icons/png/DocRedock-appicon-128x128.png" width="96" height="96">
</p>

A local-first Office-to-Markdown converter for AI workflows. Round-trip editing remains experimental.

[日本語](README.ja.md) · [Download the current Public Beta](https://github.com/Takayuki-Ishimaru/docredock/releases) · [User guide](docs/en/user-guide.md) · [Supported features](docs/en/supported-features.md)

## v0.2.3 Public Beta support

| Feature | Status |
| --- | --- |
| DOCX / XLSX / PPTX → Readable Markdown | Supported as Public Beta |
| PDF → Markdown | Supported in the desktop GUI; CLI requires explicit opt-in |
| Edited Markdown → Office restoration | Experimental; explicit opt-in required |
| New PDF / Office document generation | Experimental; explicit opt-in required |

The [supported-features table](docs/en/supported-features.md) is authoritative for public availability and visual-conversion boundaries. Version-specific changes stay in the [v0.2.3 release notes](release-docs/RELEASE_NOTES_v0.2.3.en.md).

## Use it in 30 seconds

1. Download the package for your operating system and CPU architecture from [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases).
2. Start DocRedock.
3. Drop a DOCX, XLSX, PPTX, or PDF file.
4. Select **Readable Markdown** and keep **Visible content only (recommended)**.
5. Convert, then review the Markdown, diagnostics, and any generated assets before using the result with an AI tool.

A document containing images normally produces:

```text
input.xlsx
  ↓
input.md
input.assets/
```

The CLI defaults to Readable Markdown, so `--profile readable` is optional:

```sh
docredock export input.xlsx --content-policy visible --output input.md
```

## Visual meaning and fallback

When DocRedock can determine a supported flow unambiguously, it emits Mermaid. Otherwise it keeps available text or an image/page fallback and reports what could not be resolved.

Visual inference defaults to `safe`. Choose `native-only` to accept only connections explicitly stored by the source format, or `balanced` to consider a wider set of estimated connections. `safe` accepts only unique high-confidence estimates. Ambiguous, contradictory, duplicate, or low-confidence relations remain unresolved and stay visible through fallback or diagnostics; DocRedock does not invent arrows. CLI example: `docredock export input.pptx --visual-inference safe --output input.md`.

This is not pixel-perfect reconstruction. SmartArt and DOCX/PDF vector topology can remain partial; PDF may use a page preview or placeholder. A warning means the Markdown alone may omit meaning—review the diagnostics/report, generated assets, and source document.

## Content policy

Readable export has three policies in both the GUI and CLI. They filter the Markdown output; the external `.assets/` directory contains only images referenced by the resulting Markdown.

| Policy | Behavior |
| --- | --- |
| `visible` | Default. Excludes recognized Office-hidden text, sheets, rows/columns, slides/objects, notes, comments, and revisions. |
| `complete` | Includes hidden and metadata content and emits a warning. |
| `sanitized` | Applies the visible filter and additionally removes privacy-sensitive metadata, derived/OCR content, and document furniture. |

## Important limitations

- v0.2.3 is a Public Beta, not a production-stable release.
- Readable Markdown is one-way output. Keep the original document as the authoritative source.
- Always review Markdown, diagnostics, and assets before sharing. Do not treat a partial visual projection as complete.
- Experimental CLI workflows require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. This includes CLI PDF export, round-trip/audit operations, restoration, and rendering/new-document generation. Read-only `docredock inspect <file.pdf>` remains available without the flag.
- PDF OCR and visual fallback may require a configured rasterizer and OCR provider. If unavailable, review the page placeholder and warning.
- DocRedock does not bundle or download a Japanese PDF font. The user is responsible for selecting a font and complying with its embedding license.
- The GUI always shows the running version and checks published releases, including Public Beta builds, at startup or on demand. It never auto-installs an update; set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` to disable startup checks.
- Verify the published SHA-256 checksum and signing/notarization status for each package.

## Documentation

- [Japanese user guide](docs/ja/user-guide.md)
- [English user guide](docs/en/user-guide.md)
- [v0.2.3 supported features](docs/en/supported-features.md)
- [Security and privacy](docs/en/security-and-privacy.md)
- [v0.2.3 release notes](release-docs/RELEASE_NOTES_v0.2.3.en.md)
- [Experimental features](docs/en/experimental-features.md)
- [Contributing, build, and test](CONTRIBUTING.md)

## License

DocRedock is released under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for third-party dependencies and assets.

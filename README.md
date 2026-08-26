# DocRedock

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
  <img alt="DocRedock" src="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
</picture>

A local-first Office-to-Markdown converter for AI workflows. Round-trip editing remains experimental.

[日本語](README.ja.md) · [Download the current Public Beta](https://github.com/Takayuki-Ishimaru/docredock/releases) · [User guide](docs/en/user-guide.md) · [Supported features](docs/en/supported-features.md)

## v0.1.4 Public Beta support

| Feature | Status |
| --- | --- |
| DOCX / XLSX / PPTX → Readable Markdown | Supported as Public Beta |
| PDF → Markdown | Experimental; explicit opt-in required |
| Edited Markdown → Office restoration | Experimental; explicit opt-in required |
| New PDF / Office document generation | Experimental; explicit opt-in required |

The [release support table](docs/en/supported-features.md) is authoritative for user-facing availability. The [implementation capability matrix](docs/FORMAT_CAPABILITY_MATRIX.md) separately describes code-level capabilities.

## Use it in 30 seconds

1. Download the package for your operating system and CPU architecture from [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases).
2. Start DocRedock.
3. Drop a DOCX, XLSX, or PPTX file.
4. Select **Readable Markdown** and keep **Visible content only (recommended)**.
5. Convert, review the Markdown and diagnostics, then use the result with your AI tool.

A document containing images normally produces:

```text
input.xlsx
  ↓
input.md
input.assets/
```

## Content policy

Readable export has three policies in both the GUI and CLI. They filter the Markdown projection; the external `.assets/` directory contains only image assets referenced by nodes included under the selected policy. Review both the Markdown and `.assets/` directory before sharing:

| Policy | Behavior |
| --- | --- |
| `visible` | Default. Excludes Office-hidden text, hidden/very-hidden sheets, hidden rows/columns, hidden slides/objects, notes, comments, and revisions when represented by the extractor. |
| `complete` | Includes hidden and metadata content and emits a warning. Review the output carefully before sharing it. |
| `sanitized` | Applies the visible filter and additionally removes privacy-sensitive metadata, derived/OCR content, and document furniture. |

CLI example:

```sh
docredock export input.xlsx --profile readable --content-policy visible --output input.md
```

## Why DocRedock

- **Local-first:** built-in conversion runs on your machine and does not upload document contents.
- **Structure-aware:** document titles, heading hierarchy, lists, tables, slide boundaries, native charts, and spreadsheet regions are rendered for reading.
- **Image-aware:** included Office images can be written beside Markdown or embedded in it.
- **Inspectably safe defaults:** recognized hidden Office content is omitted from the Markdown projection and its referenced image output by default, and the broader mode warns before its output is shared.
- **Efficient for AI:** one local synthetic-XLSX experiment used 74.1% fewer input tokens than direct Excel access. Results vary; see the [methodology and results](docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_2026-08-25.md).

## Important limitations

- v0.1.4 is a Public Beta, not a production-stable release.
- Always review generated Markdown and images before sharing. Office visibility metadata is complex and third-party producers may encode content differently.
- Readable Markdown is one-way output. Keep the original Office file as the authoritative source.
- Experimental workflows in the distributed GUI and CLI require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. The public library APIs are engineering surfaces and do not enforce this entry-point gate. Experimental workflows may create `.drmd` or `.drmdpkg` files containing source-derived or restoration data.
- The GUI may query the public GitHub Releases API for update metadata. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable this request.
- Verify the published SHA-256 checksum and signing/notarization status for each package.

## Documentation

- [Japanese user guide](docs/ja/user-guide.md)
- [English user guide](docs/en/user-guide.md)
- [v0.1.4 supported features](docs/en/supported-features.md)
- [Security and privacy](docs/en/security-and-privacy.md)
- [v0.1.4 release notes](release-docs/RELEASE_NOTES_v0.1.4.en.md)
- [Experimental features](docs/en/experimental-features.md)
- [Contributing, build, and test](CONTRIBUTING.md)

## License

DocRedock is released under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for third-party dependencies and bundled assets.

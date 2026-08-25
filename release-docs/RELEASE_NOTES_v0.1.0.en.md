# DocRedock v0.1.0 Public Beta

[日本語](RELEASE_NOTES_v0.1.0.md) | English

Released: August 26, 2026

This is the first public beta of DocRedock. It converts DOCX, XLSX, PPTX, and PDF files to Markdown locally and can safely apply supported edits back to the source format.

## Downloads

Choose the asset matching your operating system and CPU on GitHub Releases.

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.0-win-x64.zip` | `DocRedock-v0.1.0-win-arm64.zip` |
| macOS | `DocRedock-v0.1.0-osx-x64.zip` | `DocRedock-v0.1.0-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.0-linux-x64.tar.gz` | `DocRedock-v0.1.0-linux-arm64.tar.gz` |

Each package contains the branded GUI, CLI, Japanese and English quick starts, the MIT License, and third-party notices. A separate .NET SDK installation is not required.

After downloading, verify the file against `SHA256SUMS`. Dependency information is also available in `sbom.cdx.json`.

## Highlights

- Local conversion from Office/PDF into semantic Markdown that is easier for AI to read
- Readable Markdown for one-way search, summaries, and question answering
- Round-trip Markdown plus `.drmd` for controlled edits that retain the source
- Pre-restore review through `verify` and `diff`
- Supported DOCX, XLSX, and PPTX edits with explicit rejection of protected operations
- Conservative PDF extraction and an explicit render fallback
- On-device macOS and Windows OCR with an optional local Tesseract fallback
- Portable workspaces through `.drmdpkg`
- x64 and ARM64 distributions for Windows, macOS, and Linux

In a controlled 12-question benchmark over the same synthetic XLSX, plain Markdown used 74.1% fewer input tokens than direct Excel access and answered all 11 text questions correctly. `.md + .drmd` used 58.9% fewer input tokens while answering all 12 questions, including image-only evidence. These are fixture- and environment-specific measurements, not a universal performance guarantee.

## Launch notice

This Public Beta macOS application has not yet been Apple-notarized, and the Windows executables are not yet code-signed. Review the operating-system warning and the published checksums before launching. The Linux package contains a desktop entry and PNG icon but does not perform a system-wide installation automatically.

## Known limitations

- Readable Markdown cannot be restored. Use the round-trip workflow when restoration may be required.
- `.drmd` and `.drmdpkg` can contain the original document and must be protected at the same confidentiality level.
- XLSX structural changes to rows, columns, sheets, merges, and styles are unsupported.
- PPTX editing focuses on existing shape text. Notes, tables, images, and shape insertion or movement are unsupported.
- PDF extraction may be incomplete for some font/CMap structures. Restoring an edited PDF does not guarantee the original layout.
- Tesseract, language models, Mermaid CLI, and a PDF rasterizer are not bundled.
- Macros, signatures, encryption, protection, and unsafe or unsupported package structures may be rejected.

See the [user guide](USER_GUIDE.en.md), [format capability matrix](../docs/FORMAT_CAPABILITY_MATRIX.md), and [security and privacy guide](SECURITY_AND_PRIVACY.en.md) for details.

## License

DocRedock is licensed under the MIT License. Third-party dependencies and bundled assets remain subject to their respective licenses.

# DocRedock v0.4.0 Public Beta

[日本語](RELEASE_NOTES_v0.4.0.md)

Turn Word, Excel, PowerPoint, and PDF files into HTML that opens directly in a browser. Readable Markdown and AI packages are still available when you want to give a document to an AI assistant. Conversion runs on your computer.

## What’s new

- **Read and share in a browser.** Readable HTML puts supported images and SVG diagrams in one file you can open offline. The page includes light, dark, and print styles and adds a table of contents when there are enough sections. Select **Readable HTML** in the desktop app, run `docredock export report.xlsx --format html`, or use the MCP tool `export_html`.
- **Let an AI assistant check specific parts of the source.** Use **AI integration** in the desktop app or `docredock agents install` to set up Claude Code, GitHub Copilot, or Codex. The assistant reads the Markdown first, then uses `read_source` if it needs a worksheet range, slide, PDF page, or picture. The CLI offers the same option through `docredock source`. Reading from the source can expose hidden content or text deleted through tracked changes.
- **Read tables, diagrams, and PDFs more easily.** PDF conversion now handles more two-column pages, lists, and tables that continue across pages, and extracts supported embedded images. When DocRedock can recover the relationships in Office flowcharts and sequence diagrams drawn with shapes, it writes them as Mermaid in Markdown and SVG in HTML. When it cannot build a diagram, it retains any shape text it can extract.

## Conversion comparison and HTML research

Compare the arrows and branch labels in the source PowerPoint and the actual outputs. The timing study uses a development build with HTML export. [Flowchart comparison](https://github.com/Takayuki-Ishimaru/docredock/blob/v0.4.0/README.md#flowchart-comparison) · [Conversion comparison and HTML report](CONVERSION_REPORT_v0.4.0.en.md)

## Compatibility and limitations

- HTML is for reading and sharing. It does not reproduce exact page layout or restore an Office document, and it cannot be combined with `--ai-package` or `--profile roundtrip|audit`. Unsupported images can fall back to a text description. Check the source when exact positioning in a table or diagram matters.
- `export --json` results add an `output_format` field. The AI package manifest and report schemas remain `2.2` and `1.2`, respectively.
- The desktop app opens PDFs by default. In the CLI, use `--allow-pdf` for PDF conversion. OCR and page previews can require an installed OCR engine or PDF rasterizer. Round-trip editing, restoration, and new document generation remain experimental.
- Before sharing HTML exported from Word, check for deleted text. To exclude it, select **Final** in the Word revision settings or pass `--revisions final` in the CLI.

## Downloads and upgrades

Choose the archive for your OS and CPU from [GitHub Releases](https://github.com/Takayuki-Ishimaru/docredock/releases) and verify it against `SHA256SUMS`.

| OS | CPU | File |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.4.0-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.4.0-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.4.0-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.4.0-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.4.0-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.4.0-linux-arm64.tar.gz` |

Extract the archive and launch `DocRedock.exe` on Windows, `DocRedock.app` on macOS, or `./DocRedock` on Linux. No separate .NET SDK is needed. See the included `QUICKSTART.en.md` for CLI launch instructions and `SIGNING-STATUS.json` for signing and notarization status. Close the app before replacing it during an upgrade.

[Quick guides](../docs/en/quick-tasks.md) · [User guide](../docs/en/user-guide.md) · [Supported features](../docs/en/supported-features.md)

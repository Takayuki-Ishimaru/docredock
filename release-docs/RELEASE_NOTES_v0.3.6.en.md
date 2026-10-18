# DocRedock v0.3.6 Public Beta

[日本語](RELEASE_NOTES_v0.3.6.md)

v0.3.6 improves protection of files at output destinations and reliability when converting Word documents concurrently.

## Preserve existing Markdown and images

Fixed cases where a failed or cancelled Readable Markdown export could delete existing Markdown or an image folder with the same name. When exports target the same destination concurrently, a conflicting export reports the collision instead of replacing the output saved first.

When the GUI selects an unused output name, it now checks both Markdown and matching `.assets` files or folders. An export that fails because its image folder already exists preserves that folder's contents.

Without `--force`, the CLI also refuses to replace an output created after conversion started. `--force` continues to allow replacement of the requested existing output.

## Reject replacement of folders containing input documents

Strengthened CLI protection against replacing an output folder that contains an input document. This includes inputs nested deeper inside an image folder and inputs reached through resolved symbolic links. Even with `--force`, a folder identified as containing an input document is not replaced.

Choose another output destination and retry. If an export failed in an earlier version, check that the source and existing outputs are still present. This update cannot recover files already lost.

## Keep concurrent outputs and Word settings separate

Saving AI packages and experimental round-trip outputs, restoration, and document generation now also avoids replacing outputs created by another operation just before saving. If operations compete for one destination, use another output name or retry after the other operation finishes.

Fixed interference between Word revision settings in concurrent conversions. Each conversion uses its own requested revision settings, including when other conversions use different settings.

## Compatibility and limitations

Command syntax, content policies, and revision choices and defaults are maintained. The AI-package manifest remains schema `2.2`, and `report.json` remains schema `1.2`.

This is a Public Beta. Readable Markdown and AI packages are one-way outputs; keep the original document as the authoritative copy. Review text, images, diagnostics, and `review.md` before sharing. Absence of review items does not guarantee a correct conversion.

Desktop-GUI PDF input is enabled by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats, and new PDF/Office generation remain experimental. OCR and PDF review images require provider or rasterizer configuration appropriate to your environment. Complete reproduction of diagrams, SmartArt, or complex layouts is not guaranteed.

## Downloads and updating

Choose the archive matching your OS and CPU from GitHub Releases.

| OS | CPU | Filename |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.6-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.6-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.6-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.6-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.6-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.6-linux-arm64.tar.gz` |

Verify the download against `SHA256SUMS`, then follow `QUICKSTART.en.md` in the extracted folder. No separate .NET SDK is required. Check `SIGNING-STATUS.json` in each package for signing and notarization status; packages are not necessarily signed. Preserve existing documents and outputs, close the application, and replace it with the new package.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md), and [security and privacy](../docs/en/security-and-privacy.md).

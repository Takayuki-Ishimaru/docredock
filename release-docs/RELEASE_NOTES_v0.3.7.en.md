# DocRedock v0.3.7 Public Beta

[日本語](RELEASE_NOTES_v0.3.7.md)

v0.3.7 improves PDF shape labels, review summaries for Word revision markup, and token estimates in AI packages.

## Improve PDF shape and label matching

Fixed cases where a table marker could take another shape's label on a PDF page containing both a table and a flow diagram, causing diagram text or connections to be exported incorrectly. Labels are matched using their positions regardless of the order in which shapes are recorded in the file.

Text that cannot be assigned to one shape unambiguously is not forced into a match. For complex diagrams or ambiguous layouts, continue to review diagnostics and fallbacks and compare the output with the original PDF.

## Show revision-sharing review in export summaries

When Word revision markup requires review before sharing, the export summary now names that reason. This makes the sharing decision visible even when no source-comparison items were detected.

Revision choices and defaults, sharing-consent options, and `--fail-on-review` behavior are maintained. The default markup output under `visible`/`complete` may include deleted text. Check the content before sharing.

## Add a margin to token estimates

AI-package export in the CLI and API now accepts a multiplier to increase heuristic token estimates. For example:

```sh
docredock export input.docx --ai-package zip --token-estimate-multiplier 2 --output input.ai-package.zip
```

The multiplier must be a finite number from `1` to `100`, inclusive; the default is `1`. Use it with `--ai-package dir|zip`. Invalid or missing values and use without an AI package are rejected with exit code `2` before output is created. The GUI has no control for this multiplier.

The setting increases each part's `estimated_tokens` without changing its text or splitting. These are not measurements from a model-specific tokenizer and do not guarantee that content fits a token limit.

## Compatibility and limitations

Existing commands remain usable. Estimates without a multiplier remain unchanged. The API exposes `AiPackageExportOptions.TokenEstimateMultiplier` and `AiPackagePartOptions.TokenEstimateMultiplier`.

The AI-package manifest remains schema `2.2`, and `report.json` remains schema `1.2`. The manifest adds `token_estimate_multiplier`; the report summary adds `revision_sharing_required`. Integrations that read JSON directly should allow additional fields.

This is a Public Beta. Readable Markdown and AI packages are one-way outputs; keep the original document as the authoritative copy. Review text, images, diagnostics, and `review.md` before sharing. Absence of review items does not guarantee a correct conversion.

Desktop-GUI PDF input is enabled by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats, and new PDF/Office generation remain experimental. OCR and PDF review images require provider or rasterizer configuration appropriate to your environment. Complete reproduction of diagrams, SmartArt, or complex layouts is not guaranteed.

## Downloads and updating

Choose the archive matching your OS and CPU from GitHub Releases.

| OS | CPU | Filename |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.7-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.7-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.7-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.7-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.7-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.7-linux-arm64.tar.gz` |

Verify the download against `SHA256SUMS`, then follow `QUICKSTART.en.md` in the extracted folder. No separate .NET SDK is required. Check `SIGNING-STATUS.json` in each package for signing and notarization status; packages are not necessarily signed. Preserve existing documents and outputs, close the application, and replace it with the new package.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md), and [security and privacy](../docs/en/security-and-privacy.md).

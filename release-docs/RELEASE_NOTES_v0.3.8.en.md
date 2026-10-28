# DocRedock v0.3.8 Public Beta

[日本語](RELEASE_NOTES_v0.3.8.md)

v0.3.8 exposes stored token estimates when reading or searching AI packages and improves resource limits and timeout handling for OCR and Mermaid rendering.

## Token metadata in AI-package reads and searches

CLI `package read --json` and `package search --json` now include each part's stored `estimated_tokens` and the package's recorded `token_estimate_multiplier`. This gives integrations a size estimate alongside the content when selecting parts for an AI tool.

```sh
docredock package read input.ai-package.zip --part part-0001 --json
docredock package search input.ai-package.zip "search term" --json
```

Replace the value of `--part` with an actual part ID in your package. Packages with review items still require review; use `--allow-review` after checking their content. Packages that fail verification cannot be read or searched.

The API exposes `AiPackageManifest.TokenEstimateMultiplier` and `AiPackageSearchMatch.EstimatedTokens` / `TokenEstimateMultiplier`. Stored estimates already include the export multiplier; reading or searching never applies it again. An estimate or multiplier that was not recorded is returned as `null` independently. A missing multiplier is not used to infer a value.

Estimates are heuristic, not measurements from a model-specific tokenizer. They do not guarantee that content fits an AI tool's token limit; check against the tool you use.

## OCR input limits and external-tool timeouts

OCR now bounds input reads according to the configured processing budget. Inputs exceeding the budget continue to be skipped with a diagnostic.

Fixed cases where OCR or Mermaid rendering could keep waiting for output after the external tool had exited. Timeout and cancellation now also cover reading that output. Mermaid rendering is used by experimental document generation.

## Compatibility and limitations

Existing commands and plain-text `package read` output remain usable. CLI JSON output adds the fields above; integrations should allow additional fields and `null` values.

The manifest remains schema `2.2`, and `report.json` remains schema `1.2`. Existing schema `1.0` / `2.0` / `2.1` packages remain readable. When a multiplier is present in the manifest, it must be a finite number from `1` to `100`, inclusive; invalid values are rejected.

This is a Public Beta. Readable Markdown and AI packages are one-way outputs; keep the original document as the authoritative copy. Review text, images, diagnostics, and `review.md` before sharing. Absence of review items does not guarantee a correct conversion. Default Word revision markup under `visible` / `complete` may include deleted text.

Desktop-GUI PDF input is enabled by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration to original formats, and new PDF/Office generation remain experimental. OCR and PDF review images require provider or rasterizer configuration appropriate to your environment. Complete reproduction of diagrams, SmartArt, or complex layouts is not guaranteed.

## Downloads and updating

Choose the archive matching your OS and CPU from GitHub Releases.

| OS | CPU | Filename |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.8-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.8-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.8-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.8-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.8-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.8-linux-arm64.tar.gz` |

Verify the download against `SHA256SUMS`, then follow `QUICKSTART.en.md` in the extracted folder. No separate .NET SDK is required. Check `SIGNING-STATUS.json` in each package for signing and notarization status; packages are not necessarily signed. Preserve existing documents and outputs, close the application, and replace it with the new package.

See the [user guide](../docs/en/user-guide.md), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md), and [security and privacy](../docs/en/security-and-privacy.md).

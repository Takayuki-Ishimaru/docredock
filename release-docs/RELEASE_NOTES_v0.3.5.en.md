# DocRedock v0.3.5 Public Beta

[日本語](RELEASE_NOTES_v0.3.5.md)

v0.3.5 fixes missing table content in final Word revision output and improves review before reading AI packages.

## Preserve surviving text after Word cell deletion

Exporting a Word document with tracked cell deletions using `final` could also omit text from cells that were kept. This update preserves that surviving text, including when using the default final output under `sanitized`.

When the remaining columns cannot be resolved, text is output in row order and `DocxFinalTableGridUnresolved` requests comparison with the source. Column and header relationships are not guessed. Text from figures over the table is also preserved as notes with unresolved cell placement. Equivalent deleted columns are recognized across merged and adjacent cells, reducing unnecessary review.

Re-export affected documents from the originals and compare their tables if you used an earlier version. The original Word document and the choices and defaults for `final|markup|reject` are unchanged.

## Retrieve review reports from the CLI

`package review` verifies the entire folder or ZIP and retrieves its review report.

```sh
docredock package review input.ai-package.zip
docredock package review input.ai-package.zip --json --require-hidden-review --fail-on-review
```

Plain output is the content of `review.md`. With `--json`, the result includes conversion review, revision-sharing review, hidden content, reasons for blocking reading, and affected part IDs. Since not every review item can be mapped to a specific part, affected IDs cover the entire package when review is required.

The command normally exits `0`. With `--fail-on-review`, it prints the review report and exits `11` when review is needed. Invalid or modified packages produce no report or body.

## Review hidden content before reading

CLI `package search/read/review` now accepts `--require-hidden-review`. Recipients can require review for packages containing hidden content and older `complete` packages whose inclusion flag is unknown. This choice does not change the stored report.

```sh
docredock package read input.ai-package.zip --part part-0002 --require-hidden-review
# After checking the report, source and sharing scope, acknowledge one invocation
docredock package read input.ai-package.zip --part part-0002 --require-hidden-review --allow-review
```

Search and read exit `11` without body output until review is acknowledged. The GUI provides 「非表示内容を含むパッケージも共有範囲の確認後に読む」 for this choice. Confirmed and pending status now follow actual reading access; opening another package resets the previous search query, content and review confirmation.

The bundled Python reader example accepts the same option. It verifies the entire package and also enforces stored conversion and revision-sharing review, exiting `3` without body output when review is needed. Complete review, then omit the option to read the content.

## Large packages and compatibility

CLI review-report retrieval and single-part reading verify every file without retaining unselected parts' Markdown. Manifest/source-index metadata and selected text still need memory. Full-text search and the GUI retain the full text in memory, so this improvement does not apply to them.

The manifest remains schema `2.2` and `report.json` remains schema `1.2`. Update integrations that use the table-structure review item or added `package review --json` fields. With Python `--require-hidden-review --json`, `review.required`/`effective_required` return the effective reading state, `reported_required` preserves the stored review state, and `stop_reasons` lists the reasons. JSON without this option keeps its previous shape.

## Distributions and limitations

Choose the archive for your operating system and CPU on the GitHub Release.

| OS | CPU | File name |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.5-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.5-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.5-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.5-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.5-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.5-linux-arm64.tar.gz` |

After downloading, verify the checksum against `SHA256SUMS` and read `QUICKSTART.en.md` in the extracted folder. A separate .NET SDK is not required. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

This is a Public Beta. Readable Markdown and AI packages are one-way output; retain the original as the authoritative copy. Generation runs locally. Review the full text, images, diagnostics and `review.md` before sharing. No detected review items does not certify correct conversion.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration and new PDF/Office generation remain experimental. OCR and PDF review images may need configured providers or rasterizers. Complete reconstruction of diagrams, SmartArt and complex layouts is not guaranteed.

See the [user guide](../docs/en/user-guide.md#ai-packages), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md) and [security and privacy](../docs/en/security-and-privacy.md).

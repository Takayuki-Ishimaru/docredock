# DocRedock v0.3.4 Public Beta

[日本語](RELEASE_NOTES_v0.3.4.md)

v0.3.4 adds GUI and CLI workflows for finding and reading the parts of an AI package you need. It also lets you choose how Word tracked changes appear in exported content and improves review before sharing deleted content.

## Search and read AI packages

Use **Read package** (「パッケージを読む」) in the GUI to open a folder or ZIP. DocRedock verifies file integrity and source information. When review is required, body display and copying remain blocked until you confirm the review. Search for a part and copy only its Markdown; source cells and other source information stay in a separate details panel. Opening another package clears the previous content and review confirmation.

The CLI adds these commands:

```sh
docredock package search input.ai-package.zip "sales" --json
docredock package read input.ai-package.zip --part part-0002
docredock package read input.ai-package.zip --part part-0002 --json --with-sources
```

Search and read verify the entire package before returning content. When review is required, they return exit code `11` without printing the body. Read `review.md`, check the relevant source and sharing content, then use `--allow-review` to acknowledge that review for one invocation. Invalid or modified packages produce no body.

Search matches text across case, full-width/half-width forms, and supported Markdown decoration and escapes. It does not perform semantic or paraphrase matching. You can retrieve the Markdown to give an AI separately from source information kept for your own checks.

## Check part sizes

Excel row-block splitting and the target character count now sit beside the AI-package output settings. The GUI shows an OCR-free estimate for the first selected document. After export, it shows the actual part count, largest part, parts exceeding the target, and character counts for the full document and all parts combined.

OCR and other added content can change the result from the estimate. Intact units such as tables may exceed the target; a character target does not guarantee a model's token limit. Repeated titles and headers can make all parts combined larger than the full document. Select the parts your question needs.

## Choose how Word tracked changes are exported

For Readable Markdown and AI packages, use the GUI selector or CLI `--revisions`:

| Setting | Output |
| --- | --- |
| `final` | Final content: includes insertions and current content/properties, removes deletions, move sources and revision annotations |
| `markup` | Revision display that may include deleted text and previous information |
| `reject` | Refuses a Word document containing tracked changes before creating output |

`sanitized` now defaults to `final`. The default for `visible`/`complete` remains `markup`. Explicitly choosing sanitized markup requires `--allow-revision-sharing` in the CLI or sharing confirmation in the GUI.

```sh
docredock export input.docx --ai-package zip --content-policy sanitized --revisions final --fail-on-review
```

For revision markup, source comparison and review before sharing deleted content are reported separately. `export --fail-on-review` saves output and returns exit code `11` when either review is required. Default exit codes are unchanged when the option is absent.

These choices do not change the original Word document. Experimental round-trip `.drmd`/`.drmdpkg` files may contain the original and its tracked changes; handle them with the same confidentiality as the original.

## Compatibility for integrations

The manifest remains schema `2.2`. `report.json` changes from schema `1.1` to `1.2`, adding `sharing_review` for revision-sharing review. The existing `review.required` field also combines conversion and revision-sharing review.

The current API and Python reader example require review for historical packages with a tracked-change diagnostic even when `sharing_review` is absent. Update the reader in integrations that need sharing review, and allow unknown additive fields. Matching file hashes do not certify correct conversion or permission to share.

## Distributions and limitations

Choose the archive for your operating system and CPU on the GitHub Release.

| OS | CPU | File name |
| --- | --- | --- |
| Windows | Intel/AMD 64-bit | `DocRedock-v0.3.4-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.4-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.4-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.4-osx-arm64.zip` |
| Linux | Intel/AMD 64-bit | `DocRedock-v0.3.4-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.4-linux-arm64.tar.gz` |

After downloading, verify the checksum against `SHA256SUMS` and read `QUICKSTART.en.md` in the extracted folder. A separate .NET SDK is not required. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

This is a Public Beta. Readable Markdown and AI packages are one-way output; retain the original as the authoritative copy. Generation runs locally. Review the full text, images, diagnostics and `review.md` before sharing. No detected review items does not certify correct conversion.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration and new PDF/Office generation remain experimental. OCR and PDF review images may need configured providers or rasterizers. Complete reconstruction of diagrams, SmartArt and complex layouts is not guaranteed.

See the [user guide](../docs/en/user-guide.md#ai-packages), [supported features](../docs/en/supported-features.md), [format contract](../docs/reference/ai-package.md) and [security and privacy](../docs/en/security-and-privacy.md).

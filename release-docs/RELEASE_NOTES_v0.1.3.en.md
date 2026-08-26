# DocRedock v0.1.3 Public Beta Release Notes

[日本語](RELEASE_NOTES_v0.1.3.md) | English

Release date: 2026-08-26

## Summary

v0.1.3 strengthens the safe defaults and readability of Readable Markdown. The user-supported scope remains one-way DOCX/XLSX/PPTX to Readable Markdown conversion.

## Highlights

- The default `visible` policy excludes recognized Office-hidden text, hidden/very-hidden sheets, hidden rows/columns, hidden slides/objects, notes, comments, and revisions.
- `complete` includes hidden and metadata content and emits `HiddenContentIncluded`. Review all output before sharing it.
- `sanitized` is stricter than `visible`, additionally removing metadata, derived/OCR content, and document furniture.
- The GUI now exposes content-policy selection and a `complete` warning; CLI help documents `--content-policy`.
- Readable output improves DOCX document titles/heading hierarchy/lists, XLSX key-value versus table separation and missing formula-cache markers, and PPTX document/slide headings and native-chart output.
- Experimental CLI HTML rendering improves headings, emphasis, nested lists, tables, images, code, explicit breaks, and relative image paths, while preventing relative image paths from escaping the Markdown source directory.
- `docredock --version` is derived from assembly metadata.
- Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` to disable the GUI update metadata request.
- Unedited restoration uses the original-byte F0 path directly, with strengthened identity regression coverage including hidden XLSX cells.
- Hidden-image OCR and policy-excluded image assets are no longer emitted by safe readable policies; hidden XLSX chart sources and oversized worksheet/chart ranges are handled conservatively.

## Experimental workflows

PDF, round-trip/audit export, restore, render, diff, rebase, pack, unpack, and migrate remain experimental. Explicitly opt in before launching the distributed GUI or CLI (the public library APIs are engineering surfaces and do not enforce this entry-point gate):

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

These workflows and `.drmd`/`.drmdpkg` are outside user support. Sidecars and packages may contain the source document or restoration data.

## Security guidance

`visible` is the safe default, but Office visibility metadata can vary between producers. Review Markdown, images, OCR, cached calculation results, and diagnostics before sharing. Handle `complete` output with the same confidentiality as the source.

## Verification

- Release build and complete automated test suite
- Synthetic hidden-content regressions for DOCX/XLSX/PPTX
- CLI version, experimental gate, F0/F1, pack/unpack, and tamper-rejection smoke tests
- Readable Markdown and HTML structure regressions
- LicenseAudit, SBOM, and conversion QA
- Visual inspection of the GUI and rendered HTML

The release-attached `RELEASE-EVIDENCE.md` is authoritative for the workflow run, commit, hashes, and signing/notarization status.

## Packages

| OS / CPU | Archive |
| --- | --- |
| Windows x64 | `DocRedock-v0.1.3-win-x64.zip` |
| Windows arm64 | `DocRedock-v0.1.3-win-arm64.zip` |
| macOS x64 | `DocRedock-v0.1.3-osx-x64.zip` |
| macOS arm64 | `DocRedock-v0.1.3-osx-arm64.zip` |
| Linux x64 | `DocRedock-v0.1.3-linux-x64.tar.gz` |
| Linux arm64 | `DocRedock-v0.1.3-linux-arm64.tar.gz` |

Verify each archive's SHA-256 and signing status on the GitHub Release page.

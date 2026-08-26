# DocRedock Release Documentation

[日本語](README.md) | English

<p align="center">
  <img src="../assets/brand/docredock/app-icons/png/DocRedock-appicon-128x128.png" alt="DocRedock app icon" width="96" height="96">
</p>

This directory is the entry point for the user-facing documentation and maintainer policies required to publish DocRedock.

> v0.1.2 is a Public Beta, not a production-stable release. Review the release notes for known limitations and signing status.
>
> **Current restriction:** PDF conversion/rendering and restoration to original file formats have not been validated sufficiently and may not work. Do not use them yet. The approved internal-evaluation scope is one-way **Markdown-only** export from DOCX, XLSX, and PPTX. Signing and notarization are optional; the absence of a certificate alone does not block a release.

[Download v0.1.2 Public Beta from GitHub](https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.2) · [English release notes](RELEASE_NOTES_v0.1.2.en.md) · [日本語リリースノート](RELEASE_NOTES_v0.1.2.md)

## Why convert to Markdown before giving a document to AI?

Reading an Office file directly usually requires an AI agent to repeatedly discover and extract sheets, cell ranges, drawings, relationships, and package parts. DocRedock projects that structure into semantic Markdown so headings, tables, and body text can be read as normal searchable text.

In a controlled 12-question local benchmark over the same synthetic XLSX, `.md` used **74.1% fewer input tokens** than direct Excel access and finished in 34.9 seconds instead of 155.5 seconds, with 11/11 text questions correct. `.md + .drmd` answered all 12 questions, including image-only evidence, while using **58.9% fewer input tokens** than Excel.

- 🧠 Use `.md` for the smallest and fastest text search, summaries, and Q&A
- 🖼️ Add `.drmd` when images, OCR, original material, or source evidence is required
- 🔄 Keep the Office/PDF source authoritative and give AI the Markdown projection
- ✅ For edits, run `verify` and `diff` before `restore`

These measurements are specific to the tested fixture and environment and are not a universal performance guarantee. See the [full methodology and results](../docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_2026-08-25.md).

## For users

- [RELEASE_NOTES_v0.1.2.en.md](RELEASE_NOTES_v0.1.2.en.md): latest Public Beta fixes, downloads, and known limitations
- [USER_GUIDE.en.md](USER_GUIDE.en.md): installation, GUI/CLI basics, and the difference between Readable Markdown and round-trip editing
- [SECURITY_AND_PRIVACY.en.md](SECURITY_AND_PRIVACY.en.md): local processing, trust boundaries, OCR and external tools, and vulnerability reporting
- [Format capability matrix](../docs/FORMAT_CAPABILITY_MATRIX.md): supported and unsupported DOCX, XLSX, PPTX, and PDF operations
- [DRMD Markdown specification](../docs/DRMD_MARKDOWN_SPEC.md): the round-trip Markdown format
- [AI editing rules](../docs/DRMD_AI_EDITING_RULES.md): mandatory rules for editing DRMD Markdown with AI

## For maintainers

- [PUBLICATION_SCOPE.en.md](PUBLICATION_SCOPE.en.md): files to publish, files to exclude, and the test-material policy
- [RELEASE_CHECKLIST.en.md](RELEASE_CHECKLIST.en.md): pre-release checks covering builds, signing, licensing, and artifact verification

## Release principles

1. DocRedock is local-first. Built-in processing does not upload documents to remote services.
2. Readable Markdown is a one-way reading format and cannot be restored to the source document.
3. `.md + .drmd` is the storage option when future restoration may be needed, but restoration must not be used at the current stage.
4. Unsupported structures are diagnosed or rejected instead of being silently flattened.
5. The public source repository includes tests required for reproducibility, but excludes local settings, generated results, and visual-review corpora by default.

## Two release deliverables

- Public source repository: source code, specifications, schemas, reproducible tests, and licensing information.
- End-user distributions: an OS/architecture-specific application, LICENSE, THIRD-PARTY-NOTICES, Japanese and English quick starts and security guidance, artifact-file checksums, an artifact-linked SBOM, provenance, and an explicit signing-status record. They do not contain tests or development tools. The release page also carries archive-level checksums, SBOM/provenance, and completed release evidence.

PUBLICATION_SCOPE.en.md is authoritative for the exact boundary between these deliverables.

## License

DocRedock is released under the [MIT License](../LICENSE). Third-party dependencies and bundled assets remain subject to their own licenses; see [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt).
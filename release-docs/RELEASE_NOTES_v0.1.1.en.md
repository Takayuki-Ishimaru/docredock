# DocRedock v0.1.1 Public Beta

[日本語](RELEASE_NOTES_v0.1.1.md) | English

Released: August 26, 2026

v0.1.1 is a Public Beta update that fixes Windows UI, XLSX conversion, CLI safety, and release-process issues found before wider internal evaluation.

> **The currently approved scope is one-way “Markdown only” export from DOCX, XLSX, and PPTX.**
> PDF conversion/rendering and restoration to original file formats have not been validated sufficiently and may not work. Do not use them at this stage.

## Downloads

Choose the asset matching your operating system and CPU on GitHub Releases.

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.1-win-x64.zip` | `DocRedock-v0.1.1-win-arm64.zip` |
| macOS | `DocRedock-v0.1.1-osx-x64.zip` | `DocRedock-v0.1.1-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.1-linux-x64.tar.gz` | `DocRedock-v0.1.1-linux-arm64.tar.gz` |

Each package includes the GUI, CLI, Japanese and English quick starts and security guidance, licenses, artifact-file checksums, an artifact-linked SBOM, provenance, and an explicit signing-status record. A separate .NET SDK installation is not required.

## Fixes

- Restored the native Windows title bar so the window can be dragged and moved normally.
- Removed the overlap between the Windows minimize/maximize/close controls and the “Local processing” indicator.
- Renamed output choices to “Markdown only” and “.md + .drmd (when future restoration to the original file format may be needed).”
- Fixed XLSX phonetic shared-string runs being appended to Markdown as unwanted katakana.
- Split `verify` output into workspace integrity, edit applicability, and restore readiness so “valid” is not mistaken for “restorable.”
- Changed `--force` to finish work in a staging area before replacement, preserving the previous valid output on failure.
- Removed the repository-only `licenses` command from end-user help and command dispatch.
- Removed the ineffective `restore --strict` option from help; specifying it now explains that strict validation is always active and rejects the option.
- Disabled single-file compression to avoid a runtime crash seen during repeated execution of published binaries.
- The GUI now checks public GitHub Releases metadata at startup and shows a non-modal notice only when a newer version exists. It never downloads or installs updates automatically, and a failed check does not affect startup.

## Release and verification

- A release requires locked restore, Release build, the complete test suite, conversion QA, and LicenseAudit to pass.
- Every OS/CPU archive is extracted to a fresh directory before testing DOCX/XLSX/PPTX readable export, F0 SHA comparison, F1 regression, pack/unpack, tamper rejection, and GUI startup. Restore tests are mechanical regression checks, not approval for user operation.
- An existing release tag is never overwritten. Every correction requires a new version number.
- RID-specific runtime locks, artifact hashes, commit, SBOM, provenance, attestations, and a completed `RELEASE-EVIDENCE.md` are linked.
- Windows signing and macOS signing/notarization are applied only when credentials are configured. Missing certificates do not block a Public Beta release; each package records that it is unsigned when applicable.

## Known limitations

- Do not use PDF conversion or rendering.
- Do not restore DOCX, XLSX, PPTX, or PDF files to their original formats.
- `.drmd` and `.drmdpkg` can contain the original document and must be protected at the same confidentiality level.
- Tesseract, language models, Mermaid CLI, and a PDF rasterizer are not bundled.
- Macros, signatures, encryption, protection, and unsafe or unsupported package structures may be rejected.

See the [user guide](USER_GUIDE.en.md), [format capability matrix](../docs/FORMAT_CAPABILITY_MATRIX.md), and [security and privacy guide](SECURITY_AND_PRIVACY.en.md) for details.

## License

DocRedock is licensed under the MIT License. Third-party dependencies and bundled assets remain subject to their respective licenses.

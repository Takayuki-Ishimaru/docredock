# DocRedock v0.2.3 Public Beta release notes

## Changes

- PDF table reconstruction and partial visual fallback are bounded and diagnosable.
- `docredock doctor` / `doctor --json` report OCR, PDF rasterizer, and other capabilities.
- Discovery supports an explicit `DOCREDOCK_PDF_RASTERIZER` path, PATH-based pdftoppm / mutool, and `DOCREDOCK_DISABLE_PDF_RASTERIZER=1`.
- Command help is dispatched before the experimental gate and input validation.
- The GUI shows OCR status, actionable setup guidance, and an export completion summary.
- Small gaps and noise in human diagrams are tolerated where possible; resolved partial topology and labels are retained. `native-only` accepts only explicit source connections, `safe` accepts only unique high-confidence estimates, and `balanced` considers a wider set of estimates. Ambiguous, contradictory, duplicate, or low-confidence relations remain unresolved through fallback or diagnostics instead of fabricated arrows.

## Updating

1. Preserve your source documents and settings, then close DocRedock.
2. Download the v0.2.3 package for your OS and CPU from this release, verify it against `SHA256SUMS`, and extract it into a separate directory.
3. Follow the bundled `QUICKSTART.en.md`. Run `docredock doctor` to inspect optional tools.

## Support and limitations

- This is a Public Beta. [Supported features](../docs/en/supported-features.md) defines the support contract; see the [user guide](../docs/en/user-guide.md) for instructions.
- Readable Markdown is one-way output. Keep the source document. Complex tables, diagrams, curves, or competing endpoints may produce partial output or notes.
- Desktop GUI PDF input is available by default. CLI PDF export, restoration, and rendering still require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.
- PDF rasterizers, Tesseract, Mermaid CLI, and Japanese PDF fonts are not bundled. Install them as needed. Windows and macOS OCR helpers are included but require the corresponding OS features and runtime.
- Visual fallback text and repeated diagnostics have output limits. Review omission counts and the source drawing when needed. These limits do not truncate native body text.
- Check `SIGNING-STATUS.json` inside each package for its signing and notarization status.

## Release validation

Publication requires a clean-source build and tests, Office conversion QA, PDF table and dense-visual checks, 124 diagram regression cases, and extracted-package smoke tests on six targets: Windows, macOS, and Linux, each on x64 and ARM64. The quality gate requires at least 95/100 overall, at least 90% in each section, and no critical evidence failure.

For actual results and the separate product and workflow commits, see the attached `RELEASE-EVIDENCE.md`, `QUALITY-SCORE-v0.2.3.json`, and `PDF-SEMANTICS-EVIDENCE.json`. Checksums, SBOM, and provenance are also attached.

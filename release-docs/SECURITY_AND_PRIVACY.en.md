# Security and Privacy

[日本語](SECURITY_AND_PRIVACY.md) | English

## Local processing

DocRedock's built-in document processing runs locally and does not upload documents to remote services. It does not fetch external URLs, execute spreadsheet formulas, or implicitly discover plugins from the wider environment.

Programs installed and explicitly selected by the user, such as Tesseract, Mermaid CLI, or a PDF rasterizer, are not part of the DocRedock distribution. Review each provider's security, configuration, and network behavior separately.

## Information contained in output

Before publishing or sending any output, review the following:

- A .drmd directory, zip-form .drmd, or .drmdpkg can contain the original document binary, extracted graph, coordinates, assets, and diagnostic reports. Handle it with the same confidentiality level as the source document.
- Readable Markdown can contain body text, metadata, cached calculation results, images, and OCR text.
- With --embed-images, images are included as data URIs inside the Markdown, so sharing that single file also shares the images.
- OCR may extract hidden or very small text from an image.
- diff, export-report, restore-report, and similar diagnostics can reveal document structure or content.

## Untrusted input

DocRedock checks XML DTDs, archive traversal and excessive expansion, symlink escapes, oversized worker requests, suspicious formulas, and PDF resource exhaustion at trust boundaries. Unsupported or protected Office structures are rejected or diagnosed instead of being silently flattened.

The main input limits are:

- Source document: 200 MiB
- Markdown: 16 MiB
- Restore input: 496 MiB
- Verified embedded images: 10 MiB each and 50 MiB total

Even below these limits, process untrusted files with isolated user privileges and apply your organization's malware and document-scanning controls before opening generated output.

## Round-trip integrity

- Markdown alone cannot restore the source document. Keep the matching .drmd or .drmdpkg.
- Output paths are not overwritten by default. Use --force only for an intentional replacement.
- Do not use restore operationally after verify has failed.
- Review diff for unintended nodes, deletions, or format changes.
- Open restored output in the target Office application and visually inspect the changed content and layout.

These controls do not guarantee semantic correctness, the safety of external programs, or protection from every malicious input.

## OCR privacy

Apple Vision on macOS, Windows.Media.Ocr on Windows, and the optional Tesseract fallback are used on the local device. Users install Tesseract language models separately. Also review operating-system logging, crash reporting, organizational policy, and the behavior of any external executable.

## Reporting vulnerabilities

Do not submit confidential vulnerability details, exploit samples, or real documents to a public issue. If Private vulnerability reporting is enabled for the public repository, use that private channel. Otherwise, use the private maintainer contact listed on the release page or in the repository's SECURITY.md.

Before public release, maintainers must enable Private vulnerability reporting and publish a root SECURITY.md that states supported versions, the private contact method, and an initial-response target.

## Dependencies and licenses

DocRedock itself is licensed under the MIT License. Resolved dependencies are pinned in packages.lock.json and checked with licenses/allowlist.json and LicenseAudit. Every public distribution must include LICENSE, THIRD-PARTY-NOTICES, and an SBOM, and dependencies must be revalidated for each release.
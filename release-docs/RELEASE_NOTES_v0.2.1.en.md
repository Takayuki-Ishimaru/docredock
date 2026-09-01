# DocRedock v0.2.1 Public Beta Release Notes

Release date: 2026-09-01

v0.2.1 fixes conversion-quality and distribution-flow gaps found while evaluating v0.2.0 with real documents. Updating is recommended for every v0.2.0 user.

## Highlights

- The GUI always shows the running version. It checks non-draft published releases, including Public Beta builds, in the background at startup and also provides a manual **Check for updates** action. Updates are never installed automatically.
- PDF inference excludes regular table grids, keeps an exact arrowhead-to-shaft association, and retains unknown direction as an undirected edge with a dedicated diagnostic.
- PPTX diagonal connectors use transformed physical endpoints, and edge labels are conservatively scored by distance to the actual segment and position along it.
- XLSX emits Warnings for formulas without saved cached results and for unsupported legacy comments. Safely skipped external links are Information diagnostics.
- Repeated diagnostics are deterministically aggregated. The GUI presents common diagnostics with a Japanese summary, count, and suggested action.
- Every package includes a documented `docredock` launcher, version-specific release notes, user guides, supported-feature statements, and security guidance. Linux adds user-local `install.sh` and `uninstall.sh` scripts.

## Updating

1. Check the current version at the top of the app and select **Check for updates**.
2. If an update is available, open the trusted GitHub release page and download the package for your OS and CPU.
3. Verify `SHA256SUMS` and `SIGNING-STATUS.json`.
4. Keep the source documents and any required output, then replace the old package. For a Linux user-local installation, rerun `./install.sh` from the newly extracted package.

Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable startup checks. An offline or rate-limited check never blocks startup or conversion.

## Known limitations

- Readable Markdown is one-way output; complete reconstruction of Office drawing objects or PDF layout is not guaranteed.
- Even in `safe` mode, connectors, directions, and labels that cannot be resolved uniquely remain in fallback output and diagnostics instead of being asserted in Mermaid.
- Image-only PDF OCR needs an available rasterizer and OCR provider. The GUI disables OCR and explains why when the required capability is unavailable.
- Office restore, edited-PDF generation, and new-document generation remain experimental.

See [Supported features](../docs/en/supported-features.md), the [User guide](../docs/en/user-guide.md), and [Security and privacy](../docs/en/security-and-privacy.md) for the current contract.

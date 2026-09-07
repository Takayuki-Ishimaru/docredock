# DocRedock v0.2.6 Public Beta Release Notes

Release date: 2026-09-07

v0.2.6 preserves ordinary text in Word, Excel, and PowerPoint documents that resembles Markdown links or images. It fixes remaining v0.2.5 issues where that text became a live link or image reference, or a reference-definition line disappeared from view.

## Fixes

- **Keep ordinary text visible**: Source text such as `[LABEL](...)` and `[x](y)` in an Excel cell stays text instead of becoming a clickable link. `![ALT](image.png)` also remains visible text instead of becoming an image reference.
- **Preserve reference-style notation**: `[REFERENCE][id]` and `[id]: ...` remain in the body. Definition lines are no longer consumed as Markdown link targets and removed from the displayed text.
- **Preserve content and formatting**: The fix covers body text, headings, table cells, and formatted text. Supported real hyperlinks, images, bold, italics, lists, generated Mermaid diagrams, and OCR detail sections remain available. The change applies to both GUI and CLI conversion.
- **Experimental rendering and round-trip editing**: Markdown used for round-trip editing and HTML output from the experimental `render` command handle escaped punctuation as text. Image labels containing brackets or backslashes are also preserved more accurately.
- **Reject output through hard links**: When the CLI identifies an output as a hard link to an input document, it rejects that destination even with `--force`. The command exits with code 2 without modifying the original document.

## Updating

1. Close DocRedock and save any documents you are editing.
2. Download the v0.2.6 archive for your operating system and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check the environment.

GUI and CLI packages are available for Windows, macOS, and Linux on x64 and ARM64.

## Limitations

- This is a Public Beta. See [supported features](../docs/en/supported-features.md) and the [user guide](../docs/en/user-guide.md) for the supported scope.
- Readable Markdown is one-way output. Keep the original document. This update does not add support for links or drawing features that a source format does not yet support.
- Markdown files include backslashes to preserve the displayed text. Use a Markdown viewer that honours escapes. Viewer-specific features, such as automatic linking of bare URLs, may affect the result.
- Hard-link detection depends on file identity information available from the operating system and file system. If that information cannot be obtained, the existing path and symlink checks still apply.
- Round-trip editing, restoration, CLI PDF conversion, and new-document generation remain experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See the [experimental features guide](../docs/en/experimental-features.md).
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

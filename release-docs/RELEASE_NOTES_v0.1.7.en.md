# DocRedock v0.1.7 Public Beta release notes

## Summary

v0.1.7 is a visual-correctness update that prevents false diagram relations. Mermaid is emitted only for clearly determined connections; uncertain content remains available as source text, image fallback, and diagnostics.

## Changes

- Recognized visual information remains available as Mermaid, source text, an image fallback, or diagnostics.
- DOCX anchored/VML drawings, XLSX directional shapes, PDF vectors, and textless PPTX arrows are handled conservatively.
- With `--no-diagrams`, resolved connections remain available in a readable list.
- Diagnostics identify the source format and location, inference confidence, and fallback used.

## Compatibility and limits

Readable Markdown remains one-way. Pixel-perfect drawing reconstruction and rebuilding source shapes from Readable Markdown are not guaranteed. A Warning and exit code 1 can mean partial output requiring review, not a missing output.

## Usage guidance

When output includes a Warning, review the source document, diagnostics, and generated images as well as the Markdown.

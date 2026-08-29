# DocRedock v0.1.7 Public Beta release notes

## Summary

v0.1.7 is a visual-correctness update focused on **false-topology prevention**. Mermaid is emitted only for validated semantic graphs; uncertain connections fall back to source-visible output and stable diagnostics.

## Changes

- Source visual items are accounted as nodes, edges, fallback, diagnostics, duplicate suppression, or justified decoration.
- The common safety basis covers DOCX anchor/VML, XLSX directional shapes, PDF vectors, and textless PPTX arrows.
- `--no-diagrams` is a user choice, not a warning; resolved connections remain in a readable list.
- Diagnostics can identify format, part, partition, source object/type, confidence, and fallback.
- Release smoke validates Mermaid node/edge/label structure and writes machine-readable evidence.

## Compatibility and limits

Readable Markdown remains one-way. Pixel-perfect drawing reconstruction and rebuilding source shapes from Readable Markdown are not guaranteed. A Warning and exit code 1 can mean partial output requiring review, not a missing output.

## Structural verification

Before publication, the workflow requires tag-derived expected-version checks, RID package smoke, and semantic evidence. See `artifacts/visual-semantics-evidence.json` and release evidence.

# DocRedock v0.2.0 Public Beta Release Notes

## 1. Overview

v0.2.0 handles diagram and flow connections more safely in one-way DOCX/XLSX/PPTX/PDF to Readable Markdown conversion. It does not promise complete diagram reconstruction: only clearly determined relations become Mermaid, while uncertain information remains available as source text, an image or page fallback, and diagnostics.

## 2. Connection inference modes

- `native-only`: uses only connections explicitly stored by the source format.
- `safe` (default and recommended): uses explicit connections and uniquely determined high-confidence connections.
- `balanced`: considers additional inferred candidates. Output includes a notice and a Warning when inference is used.

The CLI exposes `--visual-inference native-only|safe|balanced`; the GUI presents the same choices as no inference, safety first, and recovery first.

## 3. Safe fallback behavior

Ambiguous, contradictory, or low-confidence connections are not asserted as Mermaid. Available source text, embedded images, PDF page previews, placeholders, and diagnostics are retained.

With `--no-diagrams`, DocRedock emits no Mermaid and keeps resolved relations as a readable list.

## 4. Improvements by format

- DOCX: better handling of DrawingML/WPS/VML shapes and connectors across separated paragraphs, including multi-paragraph labels. Incompatible coordinate frames remain unresolved and are reported.
- XLSX: conservative handling of connected connectors, relations supported by cell layout, directional shapes, and flowchart shapes. Unknown shapes retain their labels.
- PPTX: support for connected and unsnapped connectors, arrows, labels, hidden shapes, and multiple diagrams. Detached arrowheads are associated only when position and direction are clear.
- PDF: conditional handling of simple vector flows. Decorative triangles and V shapes are not guessed as connections.

## 5. Compatibility and known limits

No migration is required for the supported one-way workflow. Markdown-to-Office restoration and new-document generation remain experimental.

Complete arbitrary PDF-vector reconstruction, complete SmartArt semantics, incompatible Word coordinate frames, and every detached-arrow arrangement are not guaranteed. See [Supported features](../docs/en/supported-features.md) and the [v0.1.7 errata](ERRATA_v0.1.7.en.md).

## 6. Usage guidance

`safe` is recommended for normal use. When output includes a Warning, the CLI returns exit code 1. Review diagnostics, generated assets, and the source document as well as the Markdown.

# DocRedock v0.1.6 Public Beta

[日本語](RELEASE_NOTES_v0.1.6.md) | English

## Summary

v0.1.6 reduces lost diagram and flow information, duplicate DOCX output, and failures during experimental round-trip processing.

## User impact

- PPTX shapes/connectors project to Mermaid when native connections or unique geometry inference resolve them; ambiguous connectors/labels remain explicit diagnostics.
- DOCX conditionally projects supported connector fragments when native or uniquely inferred endpoints form valid topology; otherwise source text and diagnostics remain.
- Known XLSX `flowChart*` presets map to semantic Mermaid nodes; unknown presets retain their labels as generic nodes.
- PDF native text remains available; supported simple vector paths with valid topology can project to Mermaid, while partial paths and image-only pages retain diagnostics/fallbacks.
- Existing paragraph, heading, list, table, image, hyperlink, and header/footer output remains compatible.

## Format-specific visual behavior

| Format | Projection | Fallback/diagnostic | Known boundary |
|---|---|---|---|
| DOCX | Conditional native/uniquely inferred connector topology projects to Mermaid | Invalid or unresolved topology retains source text and diagnostics | Complete drawing reconstruction is out of scope |
| PPTX | Resolved shape/connector topology projects to Mermaid | Diagnostics for unresolved connectors/labels | Full SmartArt recovery is out of scope |
| XLSX | Supported shapes and `flowChart*` presets project to Mermaid | Unknown presets retain labels as generic nodes | Complete arbitrary-shape semantics are out of scope |
| PDF | Conditional simple vector topology projects to Mermaid | Partial/unresolved paths and image-only content retain diagnostics/placeholders | Full arbitrary vector-graph recovery is out of scope |

## Diagnostic contract

Representative codes added or propagated in this version are `VisualSemanticProjectionPartial`, `VisualSemanticProjectionUnavailable`, `VisualConnectorUnresolved`, `VisualEdgeLabelUnresolved`, and `PdfRasterizerUnavailable`. Format warnings are promoted to stable codes by the API; unrelated warnings retain their existing fallback code.

## Compatibility and limits

Readable Markdown cannot be restored to the original Office format. Round-trip/restore remains experimental and safety-boundary constrained. Pixel-perfect reproduction, complete SmartArt recovery, arbitrary PDF vector-graph reconstruction, and new OCR engine implementation are out of scope. Unsupported content must not be silently discarded.

## Distribution and upgrade

Packages are available for supported Windows, macOS, and Linux targets. Check the GitHub Release for checksums, SBOM/provenance, and signing status. Keep source documents and review Markdown, diagnostics, and images before sharing converted output.

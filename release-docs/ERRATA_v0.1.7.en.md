# v0.1.7 errata

DocRedock v0.1.7 Public Beta had material limits in DOCX visual semantics:

- Common Word `wp:anchor`, WPS, and VML flows could lose connections when shapes and connectors lived in separate paragraphs or used incompatible coordinate frames.
- Unsnapped connectors could remain unresolved even when an endpoint looked unique.
- Conversely, the earlier proximity-based inference could guess a false relation between distant nodes when a long connector crossed an intermediate node.
- Edge labels, multiple diagrams, flip/rotation, detached arrowheads, and textless shapes had conditional support.

v0.2.0 applies the same conservative connection rules across supported formats and exposes `native-only`, `safe`, and `balanced` modes. Unresolved, ambiguous, or low-confidence connectors are not asserted as relations; source text, visual fallback, and diagnostics remain available. Do not treat v0.1.7 Mermaid output as a complete reconstruction of the source diagram; review the original document and diagnostics.

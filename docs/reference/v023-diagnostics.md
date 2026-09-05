# v0.2.3 diagnostics reference

This page describes capability and PDF visual diagnostics. Diagnostic severity and counts are preserved in CLI and GUI summaries.

## Capability status

`ready` means the capability is available. `partial` means only part of the capability is available. `unavailable` means the local dependency is missing or disabled. `docredock doctor --json` emits a stable `schema_version: 1` report. PDF rasterizer discovery checks `DOCREDOCK_PDF_RASTERIZER`, then PATH entries `pdftoppm` and `mutool`. Set `DOCREDOCK_DISABLE_PDF_RASTERIZER=1` to disable discovery.

## PDF tables

- `PdfTableInferred`: a regular vector grid was reconstructed as a table.
- `PdfTableNative`: table information was available from native extraction.
- `PdfTableAmbiguous`: evidence was insufficient or conflicting for a unique table classification.

A regular 2x2 (or larger) ruled grid with native cell text is treated as table evidence; it does not reject the whole page. These diagnostics describe the projection; they do not claim pixel-perfect or fully editable table restoration.

## Visual fallback

- `VisualFallbackCompacted`: fallback paths were compacted to fit the configured output budget while retaining partial topology.
- `VisualConnectorUnresolved`: a connector endpoint could not be resolved uniquely.
- `VisualEdgeLabelUnresolved`: an edge label could not be assigned uniquely.
- `VisualSemanticProjectionPartial`: only part of a visual could be projected semantically.

Small gaps and drawing noise are tolerated where possible. Fallback is bounded to 100 paths and 32,768 characters per page, with omission counts reported; native text is retained. Uncertain connections remain as fallback or diagnostics instead of being silently inferred.

## External tools

The rasterizer implementations invoke local executables with argument lists and do not use shell evaluation. See [mutool draw documentation](https://mupdf.readthedocs.io/en/latest/tools/mutool-draw.html) for the optional MuPDF tool.

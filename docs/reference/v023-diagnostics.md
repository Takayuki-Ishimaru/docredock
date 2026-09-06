# DocRedock diagnostics reference

This page describes capability and PDF visual diagnostics. Diagnostic severity and counts are preserved in CLI and GUI summaries.

## Capability status

`ready` means the capability is available. `partial` means only part of the capability is available. `unavailable` means the local dependency is missing or disabled. `docredock doctor --json` emits a stable `schema_version: 1` report; every capability additionally carries `tier` (`required` or `optional`) and an optional `satisfied_by`, naming a provider that already performs the same function (for example `ocr-native` reports `satisfied_by: "tesseract"` when Tesseract already provides OCR on a platform with no bundled native provider — a `partial` provider never counts as satisfying). The report also carries top-level `strict`, `exit_code`, and a `summary` (`required_ready`, `optional_gaps`, `strict_failures`); these fields are additive, `schema_version` stays `"1"`. `docredock doctor` and `docredock doctor --json` always return the same exit code: by default, exit 0 when every `required` capability is `ready` and 1 otherwise, with optional gaps listed but never affecting the exit code; `--strict` instead exits 1 if any capability is not `ready`, unless it is an optional gap satisfied by a ready alternative. PDF rasterizer discovery checks `DOCREDOCK_PDF_RASTERIZER`, then PATH entries `pdftoppm` and `mutool`. Set `DOCREDOCK_DISABLE_PDF_RASTERIZER=1` to disable discovery.

## PDF tables

- `PdfTableInferred`: a regular vector grid was reconstructed as a table.
- `PdfTableNative`: table information was available from native extraction.
- `PdfTableAmbiguous`: evidence was insufficient or conflicting for a unique table classification.
- `PdfNativeTextUnaccounted` (Warning): a native text fragment on a page with a reconstructed table was represented by neither a table cell nor a flow region, or a cell referenced a fragment the page does not carry. The fragment is kept as flow text; table reconstruction never removes native text silently.

A regular 2x2 (or larger) ruled grid with native cell text is treated as table evidence; it does not reject the whole page. These diagnostics describe the projection; they do not claim pixel-perfect or fully editable table restoration. Rules drawn as several subpaths of one stroked content-stream path (for example, reportlab's `canvas.grid()`) are treated the same as rules drawn as individually stroked paths, per the PDF specification's rule that a painting operator applies to every subpath of the current path.

## Visual fallback

- `VisualFallbackCompacted`: fallback paths were compacted to fit the configured output budget while retaining partial topology.
- `VisualConnectorUnresolved`: a connector endpoint could not be resolved uniquely.
- `VisualEdgeLabelUnresolved`: an edge label could not be assigned uniquely.
- `VisualSemanticProjectionPartial`: only part of a visual could be projected semantically.
- `VisualSemanticProjectionUnavailable` (PDF only): some visual information could not be projected — for example, a connector stayed unresolved or a vector element remains available only as fallback. The message identifies the page and the unresolved or fallback counts. A page where every recognized connector resolved and no visual content remains only as fallback does not report this diagnostic.
- `VisualFallbackUsed`: at least one visual item remains available only as fallback instead of a reconstructed node or edge. This diagnostic and the `FallbackPaths` summary count use the same rule.

Small gaps and drawing noise are tolerated where possible. Fallback is bounded to 100 paths and 32,768 characters per page, with omission counts reported; native text is retained. Uncertain connections remain as fallback or diagnostics instead of being silently inferred.

## Export summary counters

The CLI's `Visual summary:` line and its `Export completed` block use the same finalized summary values.

- `diagrams=` / `Diagrams reconstructed`: graphs with at least one resolved edge between two known nodes. This is the only counter that means "a diagram was successfully reconstructed."
- `vector_pages=`: how many pages/partitions carried a visual graph at all, whether or not anything on them resolved. A page can have `vector_pages=1` and still contribute `diagrams=0` — for example a page whose only vector content is a ruled table.
- `Tables reconstructed`: document nodes classified as a table.
- `native=` / `high=` / `medium=`: resolved edges by evidence confidence band (native connection, high-confidence inferred, medium-confidence inferred).
- `unresolved=`: edges with a null endpoint or an explicit `Unresolved` resolution, summed across every graph.
- `fallback=` / `FallbackPaths`: visual items retained as fallback. A connector's raw stroke does not count after it has been represented as a resolved edge.
- `Fallback pages`: pages/partitions with at least one graph whose `fallback=` count is greater than zero, counted once per page even when several graphs on that page carry fallback content. `fallback=0` and `Fallback pages: 0` always agree.
- `rejected=`: visual graph validation errors, summed across every graph.
- `Warnings`: diagnostics at `Warning` severity on the finalized export.

## XLSX sheet selection

- `XlsxSheetExcludedByPolicy` (Warning): a sheet requested with `--sheets` exists but is hidden, and the `visible` or `sanitized` content policy excludes it, so nothing from it appears in the output. Use `--content-policy complete` to include it. A requested name that matches no worksheet is not a diagnostic: the export fails with exit code 2, lists the available sheets, and writes no output file (partial mismatches are never ignored silently).

## External tools

The rasterizer implementations invoke local executables with argument lists and do not use shell evaluation. See [mutool draw documentation](https://mupdf.readthedocs.io/en/latest/tools/mutool-draw.html) for the optional MuPDF tool.

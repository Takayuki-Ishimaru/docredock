# AI package format

An AI package is a local, one-way readable projection of a document. It contains no original document or restoration sidecar. Directory and ZIP forms have the same relative layout; ZIP entries live directly at the root, use `/` separators, and have fixed timestamps and deterministic ordering. Identical inputs/settings produce identical package bytes on the same runtime/provider configuration. External OCR/rasterizer differences can change results.

## API

```csharp
var result = await new DocumentService().ExportAiPackageAsync(
    new AiPackageExportOptions(
        new ReadableDocumentExportOptions("input.docx", "unused.md", ContentPolicy: "visible"),
        "input.ai-package.zip", AiPackageForm.Zip, TargetCharacters: 12_000),
    cancellationToken);
```

`Document.MarkdownPath` is replaced with the package's staging path and `EmbedImages` is disabled. Other readable options are honored. `OutputPath` must not exist; this API never overwrites. It creates a sibling staging directory and moves a complete result into place. Cancellation/failure removes owned staging files. The CLI wraps this API in its existing replacement transaction for `--force`; it protects the source file and containing directories. GUI callers can choose unique numbered names. No network calls are added by this feature.

`AiPackageContentBuilder.Build(graph, options, sourceFileName, targetCharacters, token)` can also be used in memory. Each `AiPackagePart` contains its ID, relative path, Markdown, node IDs, source locations, and `ExceedsTarget`. PDF pages, slides and sheets stay whole. Word boundaries must retain parent/child trees, nested tables, diagram membership and list sequences; uncertain relationships conservatively retain a larger unit. The target is soft, counted in .NET characters; it is not a token budget. Default 12,000; export API/CLI range 128–1,000,000.

## Files and JSON contracts

- `document.md`: full readable projection, with image destinations under `assets/`.
- `parts/0001.md`, …: stable numeric order; image destinations under `../assets/`. Word parts carry `section_path: Parent > Child` source metadata after the source filename, including table-only and list-only parts. These breadcrumbs do not repeat the section body or invent missing headings.
- `assets/`: selected/copied media when present.
- `review.md`: human review guidance, affected part links and oversized-unit notices.
- `manifest.json`: schema version `2.0`, generator version, `source` (`file_name`, lowercase SHA-256, format), normalized `content_policy`, `visual_inference`, `target_characters`, the document/review/report paths, `source_index`, shared `locations`, `parts`, and `files`.
- `source-index.json`: compact machine-readable schema version `1.0`; complete node identities and Excel cell coordinates. Load this file when detailed node/source correspondence is needed, rather than passing it to the AI with every part.
- `report.json`: schema version `1.0`, `summary`, `review`, and grouped diagnostic `code`/`severity`/`count`. Diagnostic messages are omitted because they can contain source text or names outside the selected scope.

JSON property names use snake_case; enum names are lowercase snake_case. Consumers should ignore unknown properties and dispatch on the manifest schema version. Each manifest part has `id`, `path`, `node_count`, `source_ids`, and `exceeds_target`. `source_ids` refer to the shared `locations` array. A location has `id`, `partition_id`, a human label, and nullable `page_number`, `slide_number`, `sheet_name`, and `heading_path`. The Word heading path comes from ordered source headings; missing headings remain absent, with no fabricated page number.

The source index has `parts`, each with an `id` and `sources` groups. Each group has `source_id` referring to a manifest location and the complete `node_ids` array. Groups containing worksheet cells also have `cell_addresses`, aligned one-for-one with `node_ids` (null for a non-cell node in the same group). These are original worksheet addresses such as `A1` or `F501`; sheet names live in the shared location. A location identifies the extracted node's source, not a guaranteed character-range correspondence after inference. The in-memory `AiPackagePart` API retains complete `NodeIds` and `Sources`, with `CellAddress` on cell sources.

The released v0.3.0 manifest used schema `1.0` with `node_ids` and per-node `sources` inline in each part. Schema `2.0` moves those details to the source index and replaces them with `node_count` and `source_ids`; consumers of the older layout must handle this version explicitly. Source/file hashes and content-policy boundaries are retained.

Each manifest file entry contains a relative `path`, `bytes`, and SHA-256. All package files except `manifest.json` are indexed (a manifest cannot hash itself). Consumers can validate these before importing. The source hash lets a person match a package to their retained original; it is not a signature. The package includes the original basename, not its absolute path.

Content policies and selected sheets apply throughout. `complete` may contain hidden text, comments, revisions or notes; `summary.hidden_content_included` and the readable review notice identify that sharing decision. Conversion/source-review pages, OCR review items and hidden-content sharing review remain separate. No warning does not certify complete conversion. Image links and source filenames are escaped as Markdown literals/destinations; source document text remains content, not instructions issued by DocRedock.

`report.summary.tables` is the legacy count of canonical graph `Table` nodes; `native_tables` exposes the same count explicitly (including inferred PDF tables). `rendered_tables` counts actual GFM tables in `document.md`, including tables formed from Excel cells or chart data and excluding fenced code examples. Graph-only inspection has no rendered projection and leaves this counter null. A zero graph-table count for a worksheet does not mean its readable tables were lost. Visual conversion counters do not certify text reading order or semantic correctness.

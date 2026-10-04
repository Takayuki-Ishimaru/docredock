# AI package format — v0.3.0

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
- `parts/0001.md`, …: stable numeric order; image destinations under `../assets/`.
- `assets/`: selected/copied media when present.
- `review.md`: human review guidance, affected part links and oversized-unit notices.
- `manifest.json`: schema version `1.0`, generator version, `source` (`file_name`, lowercase SHA-256, format), normalized `content_policy`, `visual_inference`, `target_characters`, the document/review/report paths, `parts`, and `files`.
- `report.json`: schema version `1.0`, `summary`, `review`, and grouped diagnostic `code`/`severity`/`count`. Diagnostic messages are omitted because they can contain source text or names outside the selected scope.

JSON property names use snake_case; enum names are lowercase snake_case. Consumers should ignore unknown properties. Each manifest part has `id`, `path`, `node_ids`, `sources`, and `exceeds_target`. A source has `node_id`, `partition_id`, a human label, and nullable `page_number`, `slide_number`, `sheet_name`, and `heading_path`. The Word heading path comes from ordered source headings; missing headings remain absent, with no fabricated page number. A location identifies the extracted node's source, not a guaranteed character-range correspondence after inference.

Each manifest file entry contains a relative `path`, `bytes`, and SHA-256. All package files except `manifest.json` are indexed (a manifest cannot hash itself). Consumers can validate these before importing. The source hash lets a person match a package to their retained original; it is not a signature. The package includes the original basename, not its absolute path.

Content policies and selected sheets apply throughout. `complete` may contain hidden text, comments, revisions or notes; `summary.hidden_content_included` and the readable review notice identify that sharing decision. Conversion/source-review pages, OCR review items and hidden-content sharing review remain separate. No warning does not certify complete conversion. Image links and source filenames are escaped as Markdown literals/destinations; source document text remains content, not instructions issued by DocRedock.

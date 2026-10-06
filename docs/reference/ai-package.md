# AI package format

An AI package is a local, one-way readable projection of a document. It contains no original document or restoration sidecar. Directory and ZIP forms have the same relative layout; ZIP entries live directly at the root, use `/` separators, and have fixed timestamps and deterministic ordering. Identical inputs/settings produce identical package bytes on the same runtime/provider configuration. External OCR/rasterizer differences can change results.

## API

```csharp
var result = await new DocumentService().ExportAiPackageAsync(
    new AiPackageExportOptions(
        new ReadableDocumentExportOptions("input.docx", "unused.md", ContentPolicy: "visible"),
        "input.ai-package.zip", AiPackageForm.Zip, TargetCharacters: 12_000, TableRowBlocks: false),
    cancellationToken);
```

`Document.MarkdownPath` is replaced with the package's staging path and `EmbedImages` is disabled. Other readable options are honored. `OutputPath` must not exist; this API never overwrites. It creates a sibling staging directory and moves a complete result into place. Cancellation/failure removes owned staging files. The CLI wraps this API in its existing replacement transaction for `--force`; it protects the source file and containing directories. GUI callers can choose unique numbered names. No network calls are added by this feature.

`AiPackageContentBuilder.Build(graph, options, sourceFileName, new AiPackagePartOptions(targetCharacters, TableRowBlocks: false), token)` can also be used in memory (the older `Build(graph, options, sourceFileName, targetCharacters, token)` overload keeps working). Each `AiPackagePart` contains its ID, relative path, Markdown, node IDs, source locations, `ExceedsTarget`, `EstimatedTokens`, and, for a block of a cut worksheet table, `TableBlock`. PDF pages, slides and sheets stay whole unless table row blocks are requested. Word boundaries must retain parent/child trees, nested tables, diagram membership and list sequences; uncertain relationships conservatively retain a larger unit. The target is soft, counted in .NET characters; it is not a token budget. Default 12,000; export API/CLI range 128–1,000,000.

## Files and JSON contracts

- `document.md`: full readable projection, with image destinations under `assets/`.
- `parts/0001.md`, …: stable numeric order; image destinations under `../assets/`. Word parts carry `section_path: Parent > Child` source metadata after the source filename, including table-only and list-only parts. These breadcrumbs do not repeat the section body or invent missing headings. A block of a cut worksheet table carries a `table_block:` line there instead (see [Table row blocks](#table-row-blocks)).
- `assets/`: selected/copied media when present.
- `review.md`: what was detected for review (source-review pages, OCR items, worksheet table boundaries), how reading order and tables were obtained, hidden-content notice, affected part links and oversized-unit notices.
- `manifest.json`: schema version `2.1`, generator version, `source` (`file_name`, lowercase SHA-256, format), normalized `content_policy`, `visual_inference`, `target_characters`, `table_row_blocks`, the document/review/report paths, `source_index`, shared `locations`, `parts`, and `files`.
- `source-index.json`: compact machine-readable schema version `1.0`; complete node identities and Excel cell coordinates. Load this file when detailed node/source correspondence is needed, rather than passing it to the AI with every part.
- `report.json`: schema version `1.1`, `summary`, `review`, and grouped diagnostic `code`/`severity`/`count`. Diagnostic messages are omitted because they can contain source text or names outside the selected scope.

JSON property names use snake_case; enum names are lowercase snake_case. Strings keep non-ASCII text of the Basic Multilingual Plane (Japanese included) as UTF-8 rather than `\uXXXX` escapes; characters outside it (some emoji and rare kanji), HTML-sensitive and control characters are still escaped, as any JSON parser accepts. Consumers should ignore unknown properties and dispatch on the manifest schema version (see [Reading packages](#reading-packages-and-compatibility)). Each manifest part has `id`, `path`, `node_count`, `source_ids`, `exceeds_target` and `estimated_tokens`, plus `table_block` only on a block of a cut worksheet table. `source_ids` refer to the shared `locations` array. A location has `id`, `partition_id`, a human label, and nullable `page_number`, `slide_number`, `sheet_name`, and `heading_path`. The Word heading path comes from ordered source headings; missing headings remain absent, with no fabricated page number.

`estimated_tokens` is a rough, model-independent size: about four ASCII characters per token and one token for every other character (Japanese text included), rounded up. Real tokenizers differ by model. Use it to compare parts and plan batches, not as a model limit (`AiTokenEstimate.Estimate` in the API).

The source index has `parts`, each with an `id` and `sources` groups. Each group has `source_id` referring to a manifest location and the complete `node_ids` array. Groups containing worksheet cells also have `cell_addresses`, aligned one-for-one with `node_ids` (null for a non-cell node in the same group). These are original worksheet addresses such as `A1` or `F501`; sheet names live in the shared location. A location identifies the extracted node's source, not a guaranteed character-range correspondence after inference. The in-memory `AiPackagePart` API retains complete `NodeIds` and `Sources`, with `CellAddress` on cell sources.

Each manifest file entry contains a relative `path`, `bytes`, and SHA-256. All package files except `manifest.json` are indexed (a manifest cannot hash itself). Consumers can validate these before importing (`AiPackageManifestReader.VerifyFilesAsync`). The source hash lets a person match a package to their retained original; it is not a signature. The package includes the original basename, not its absolute path.

Content policies and selected sheets apply throughout. `complete` may contain hidden text, comments, revisions or notes; `summary.hidden_content_included` and the readable review notice identify that sharing decision. Conversion/source-review pages, OCR review items, worksheet table boundaries and hidden-content sharing review remain separate. No warning does not certify complete conversion. Image links and source filenames are escaped as Markdown literals/destinations; source document text remains content, not instructions issued by DocRedock.

## Table row blocks

By default a worksheet stays one part even above the target, so a table is never separated from its header. With `--table-row-blocks` (CLI), 「大きなExcelの表を行ブロックに分割」 (GUI) or `TableRowBlocks: true` (API), a worksheet whose readable projection exceeds the target has every table that is too large for one part cut between data rows:

- Each block is a slice of the same Markdown as `document.md`; rows are never re-rendered, repeated or reordered, and a part never holds rows of two different cut tables. Short text just before a cut table (its title, a heading or unit note; up to about a quarter of the target) goes with its first block. Between two cut tables, text nearer the first table's last row stays with that table's last block, and text nearer the second table goes with the second table's first block.
- When the target leaves less room than about 512 characters after a part's fixed lines, blocks still hold about 512 characters of rows; such parts are marked `exceeds_target`.
- Blocks after the first start with the table's header lines again. The part's `table_block:` line says so, for example `table_block: table-0001 2/13; rows A48:F90; header A1:F1 (repeated)`.
- The manifest part's `table_block` has `table_id` (unique in the package), `index` and `count` (1-based block number and number of blocks), `header_range` (null when the table has no header row), `row_range` (the original cells of this block's data rows), and `previous_part`/`next_part` (null at either end).
- Every data cell belongs to exactly one block; header cells belong to every block of their table, so their node IDs appear in each block's source-index entry. A shape drawn over a table row belongs to the block that holds the row its marker is in. Nodes not written on their own (a title cell used as the document heading) stay with the sheet's first part.
- Small tables, sheets that fit the target, and PDF/PowerPoint/Word documents are unchanged.

## Reading packages and compatibility

| Manifest schema | Written by | Layout |
| --- | --- | --- |
| `1.0` | v0.3.0 | `node_ids` and per-node `sources` inline in each part; no source index |
| `2.0` | v0.3.1 | shared `locations`, per-part `node_count`/`source_ids`, separate `source-index.json` |
| `2.1` | current | `2.0` plus `table_row_blocks`, per-part `estimated_tokens` and optional `table_block` |

Read the major version first. A minor version only adds fields, so a reader for `2.x` accepts `2.1` (and later `2.x`) by ignoring what it does not know. A different major version, a missing `schema_version`, or a value that is not `MAJOR.MINOR` must be refused with an explicit error rather than read as if it had a known layout.

.NET consumers can use `AiPackageManifestReader`, which normalizes all three layouts and refuses anything else with `AiPackageSchemaException` (a `NotSupportedException` naming the value and `supported: 1.x, 2.x`). A manifest that is not JSON or lacks a required field (`source`, a file's `bytes`, a `2.x` `source_index` when the index is read) raises `InvalidDataException`. Names that would leave the package (absolute, drive or `..` paths) and symbolic links inside a package folder are refused rather than followed:

```csharp
var manifest = await AiPackageManifestReader.ReadAsync("input.ai-package.zip");   // directory or ZIP
foreach (var part in manifest.Parts)
    Console.WriteLine($"{part.Id} {part.Path} ~{part.EstimatedTokens?.ToString() ?? "-"} tokens " +
        string.Join("; ", part.Locations.Select(location => location.Label)));
// Only when an answer has to be traced back to nodes or cells:
var index = await AiPackageManifestReader.ReadSourceIndexAsync("input.ai-package.zip", manifest);
var problems = await AiPackageManifestReader.VerifyFilesAsync("input.ai-package.zip", manifest);
```

For a `1.x` manifest the reader builds the shared locations from the inline sources (`source-0001`, … in order of first appearance) and `ReadSourceIndexAsync` groups the inline node IDs (with any inline `cell_address`); `NodeIds` on a part is filled only for `1.x`.

[`docs/examples/ai_package_reader.py`](../examples/ai_package_reader.py) is a standard-library Python version of the same rules (directory or ZIP, schema `1.x`/`2.x`, `UnsupportedSchemaError`, hash verification) and a small CLI:

```sh
python3 docs/examples/ai_package_reader.py input.ai-package.zip            # parts, labels, token estimates
python3 docs/examples/ai_package_reader.py input.ai-package.zip --verify   # check sizes and SHA-256
python3 docs/examples/ai_package_reader.py input.ai-package --part part-0002
```

The three real sample packages used by both readers' tests are in `tests/DocRedock.Tests/Fixtures/AiPackage/`.

## Giving a package to a model

`document.md`, the parts and `source-index.json` serve different purposes; do not send all of them on every request.

- Send `document.md` when the whole document fits the model's context, or the parts one by one (or a few at a time, using `estimated_tokens`) when it does not. Each part names its source file and location (`section_path`, `table_block`), so it can be read alone.
- Keep `source-index.json` on your side. Load it only when an answer has to be traced back to node IDs or worksheet cells — for example to turn "part-0007, row 3" into a cell address — and do not add it to the prompt.
- Read `review.md` (or `report.json`) before relying on the output: it lists pages to compare with the source, OCR items, worksheet table boundaries to check, and whether hidden content was included.

## Report

`report.summary.tables` is the legacy count of canonical graph `Table` nodes; `native_tables` exposes the same count explicitly (including inferred PDF tables). `rendered_tables` counts actual GFM tables in `document.md`, including tables formed from Excel cells or chart data and excluding fenced code examples and DocRedock's own OCR review tables. Graph-only inspection has no rendered projection and leaves this counter null. A zero graph-table count for a worksheet does not mean its readable tables were lost.

Report schema `1.1` adds the fields below. Two `1.0` fields also count differently: `review.required` is also `true` when worksheet table boundaries need comparison, and `summary.rendered_tables` no longer counts DocRedock's own OCR review tables.

- `summary.review_items_detected`: whether anything was detected for comparison (figures, unanalyzed content, OCR, worksheet table boundaries). `false` means nothing was detected, not that the conversion was verified.
- `summary.reading_order` and `summary.table_structure`: how each was obtained — `source` (taken from the file: Word body order; Word/PowerPoint/chart table markup; tagged PDF tables), `inferred` (reconstructed from positions or cell layout, not compared with the source), `needs_comparison` (at least one place could not be decided), `not_applicable` (no tables) or `not_evaluated` (no readable projection was made).
- `summary.table_boundary_review_items` and `review.table_boundaries`: worksheet blank columns at which two tables were output separately although their rows sit side by side with nothing in the layout to tell whether they belong together. Each entry has `sheet_name` and `gap_range`, `left_range` and `right_range` to compare; the Markdown carries an `<!-- inferred: … -->` note at the same place.

Visual conversion counters, `review_items_detected: false` and an `inferred` basis do not certify text reading order or semantic correctness.

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
- `parts/0001.md`, …: stable numeric order; image destinations under `../assets/`. Every part starts with the same title line as `document.md` (the document's title, never one taken from the part's own first cell), and a sheet, page or slide in a part is written exactly as in `document.md`. Word parts carry `section_path: Parent > Child` source metadata after the source filename, including table-only and list-only parts. These breadcrumbs do not repeat the section body or invent missing headings. A block of a cut worksheet table carries a `table_block:` line there instead (see [Table row blocks](#table-row-blocks)).
- `assets/`: selected/copied media when present.
- `review.md`: what was detected for review (source-review pages, OCR items, worksheet table boundaries), how reading order and tables were obtained, hidden-content notice, affected part links and oversized-unit notices.
- `manifest.json`: schema version `2.2`, generator version, `source` (`file_name`, lowercase SHA-256, format), normalized `content_policy`, `visual_inference`, `target_characters`, `table_row_blocks`, the document/review/report paths, `source_index`, shared `locations`, `parts`, and `files`.
- `source-index.json`: compact machine-readable schema version `1.0`; complete node identities and Excel cell coordinates. Load this file when detailed node/source correspondence is needed, rather than passing it to the AI with every part.
- `report.json`: schema version `1.2`, `summary`, `review`, `sharing_review`, and grouped diagnostic `code`/`severity`/`count`. Diagnostic messages are omitted because they can contain source text or names outside the selected scope.

JSON property names use snake_case; enum names are lowercase snake_case. Strings keep non-ASCII text of the Basic Multilingual Plane (Japanese included) as UTF-8 rather than `\uXXXX` escapes; characters outside it (some emoji and rare kanji), HTML-sensitive and control characters are still escaped, as any JSON parser accepts. Consumers should ignore unknown properties and dispatch on the manifest schema version (see [Reading packages](#reading-packages-and-compatibility)). Each manifest part has `id`, `path`, `node_count`, `source_ids`, `exceeds_target` and `estimated_tokens`, plus `table_block` only on a block of a cut worksheet table. `source_ids` refer to the shared `locations` array. A location has `id`, `partition_id`, a human label, and nullable `page_number`, `slide_number`, `sheet_name`, and `heading_path`. The Word heading path comes from ordered source headings; missing headings remain absent, with no fabricated page number.

`estimated_tokens` is a rough, model-independent estimate: ordinary ASCII characters (excluding digits and `|`) count at about four per token, each consecutive run of digits at three per token (rounding each run up independently), Markdown table delimiters (`|`) at two per token, and each non-ASCII Unicode scalar at one per token. The ordinary-ASCII and `|` category totals are rounded up separately. It is not measured with a model tokenizer and does not guarantee an exact token count or that content fits a token limit. Use it to compare parts and plan batches (`AiTokenEstimate.Estimate` in the API).

The source index has `parts`, each with an `id` and `sources` groups. Each group has `source_id` referring to a manifest location and the complete `node_ids` array. Groups containing worksheet cells also have `cell_addresses`, aligned one-for-one with `node_ids` (null for a non-cell node in the same group). These are original worksheet addresses such as `A1` or `F501`; sheet names live in the shared location. A location identifies the extracted node's source, not a guaranteed character-range correspondence after inference. The in-memory `AiPackagePart` API retains complete `NodeIds` and `Sources`, with `CellAddress` on cell sources.

Each manifest file entry contains a relative `path`, `bytes`, and SHA-256. All package files except `manifest.json` are indexed (a manifest cannot hash itself, so an entry for it is refused), so every file the manifest refers to — `document`, `review`, `report`, `source_index` and each part — is listed; the readers below refuse a manifest that leaves one out instead of passing verification without checking it, and verification also reports any other file in the package, such as an image under `assets/` that the manifest does not list (files Finder and Explorer leave behind — `.DS_Store`, `Thumbs.db`, `desktop.ini`, AppleDouble `._` files, `__MACOSX/` — are ignored). Consumers can validate these before importing (`AiPackageManifestReader.VerifyFilesAsync`). Matching hashes say the package is intact as written, not that the conversion is correct or that the content may be shared; that is what `review.md`, `report.json` and `summary.hidden_content_included` are for. The source hash lets a person match a package to their retained original; it is not a signature, and neither are the file hashes: someone able to change a file can change the manifest too. The package includes the original basename, not its absolute path.

Content policies and selected sheets apply throughout. `complete` may contain hidden text, comments, revisions or notes; `summary.hidden_content_included` and the readable review notice identify that sharing decision. Conversion/source-review pages, OCR review items, worksheet table boundaries and hidden-content sharing review remain separate. No warning does not certify complete conversion. Image links and source filenames are escaped as Markdown literals/destinations; source document text remains content, not instructions issued by DocRedock.

## Revision sharing policy

Readable exports accept `--revisions final|markup|reject` (API: `ReadableDocumentExportOptions.Revisions`, GUI: Word revision selector). `final` includes insertions and current properties while removing deleted/moved-from content and revision annotations, even under `complete`; it does not modify the source document. `markup` retains the previous revision display, including deleted text and old property values. `reject` refuses a DOCX containing tracked changes before creating an output. With no explicit choice, the CLI, GUI and API use `final` for `sanitized` and `markup` for `visible`/`complete`. Explicit `sanitized` + `markup` requires `--allow-revision-sharing` (API: `AllowRevisionSharing`, GUI: session-only consent). These choices apply to one-way readable exports; restoration sidecars retain the original document and its history.

Report schema `1.2` adds `sharing_review` with required booleans `required`, `revisions_present`, `revisions_included`, `deleted_text_included`, and `revisions_policy` (`final`, `markup`, `reject`). Inclusion flags are conservative: markup may contain revision text even when some revisions are outside the selected content scope; `deleted_text_included` also covers potentially deleted objects. The conversion review and revision sharing review are independent, and `review.required` combines them for existing consumers. `summary.hidden_content_included` remains about hidden content and cannot be used as a revision-sharing decision.

Both current readers validate these fields and combine both gates. Historical packages lacking `sharing_review` but containing `TrackedRevisionsPresent` require review conservatively, including historical sanitized packages. Upgrade the reader too: an older reader cannot enforce this additive policy. Hash verification does not establish authorization to share.

## Table row blocks

By default a worksheet stays one part even above the target, so a table is never separated from its header. With `--table-row-blocks` (CLI), 「大きなExcelの表を行ブロックに分割」 (GUI) or `TableRowBlocks: true` (API), a worksheet whose readable projection exceeds the target has every table that is too large for one part cut between data rows:

- Each block is a slice of the same Markdown as `document.md`; rows are never re-rendered, repeated or reordered, and a part never holds rows of two different cut tables. Short text just before a cut table (its title, a heading or unit note; up to about a quarter of the target) goes with its first block. Between two cut tables, text nearer the first table's last row stays with that table's last block (or, when that block is full, in a part of its own), and text nearer the second table goes with the second table's first block.
- When the target leaves less room than about 512 characters after a part's fixed lines, blocks still hold about 512 characters of rows; such parts are marked `exceeds_target`.
- Blocks after the first start with the table's header lines again. The part's `table_block:` line says so, for example `table_block: table-0001 2/13; rows A48:F90; header A1:F1 (repeated)`.
- The cell text directly above a cut table — its title, unit or condition notes, such as `売上実績（単位: 千円、税抜）` and `対象: 国内全店` — is repeated before the header in every block after the first, exactly as written in `document.md`, so a block read alone still says what its numbers mean. Only lines written from cells qualify, each at most one blank row above the next and over the table's columns, up to about an eighth of the target; another table, an image, a diagram, text further up and text DocRedock wrote itself (an inferred note, a label/value list it formed) are not repeated, and nothing is reworded or added. The `table_block:` line names its cells: `table_block: table-0001 2/3; rows A44:C81; header A5:C5 (repeated); context A3:A4 (repeated)`.
- The manifest part's `table_block` has `table_id` (unique in the package), `index` and `count` (1-based block number and number of blocks), `header_range` (null when the table has no header row), `row_range` (the original cells of this block's data rows), `context_range` (the cells of the repeated text above the table, null when there is none; from schema `2.2`), and `previous_part`/`next_part` (null at either end).
- Every data cell belongs to exactly one block; header cells, and the cells of the repeated text above the table, belong to every block of their table, so their node IDs appear in each block's source-index entry. A shape drawn over a table row belongs to the block that holds the row its marker is in. Nodes not written on their own (a title cell used as the document heading) stay with the sheet's first part.
- Small tables, sheets that fit the target, and PDF/PowerPoint/Word documents are unchanged.

## Product reading workflow

```sh
docredock package search input.ai-package.zip "売上" --json
docredock package read input.ai-package.zip --part part-0002 > part.md
docredock package read input.ai-package.zip --part part-0002 --json --with-sources
docredock package review input.ai-package.zip --json --require-hidden-review
```

`package review <directory|zip>` runs the same whole-package verification and emits the checked `review.md`, even while body access is gated. `--json` returns `verified`, `review_required`, `conversion_review`, `revision_sharing` (including available revision policy details), `hidden_content`, `stop_reasons`, `affected_parts`, `affected_parts_scope`, and `review_markdown`. Stop reasons are `conversion_review`, `revision_sharing`, and optionally `hidden_content`. Reports do not map every sharing/OCR item to individual parts: affected IDs conservatively cover the whole package (`affected_parts_scope: package`) when required, or are empty (`none`) otherwise. Inspection normally exits 0; `--fail-on-review` emits this metadata and exits 11 when required. Malformed or changed packages emit no review or body, even with `--allow-review`.

`--require-hidden-review` on package search/read/review adds a recipient policy: `hidden_content_included: true` requires acknowledgement before body access. An absent/null flag on older `complete` packages is conservatively gated too. This option does not change the stored report's `review.required`; it changes the reader's effective gate. `--allow-review` acknowledges all effective reasons for one invocation. The Python example accepts the same option, automatically enables `--verify`, and exits 3 before body output; omit the option after reviewing. API callers use `Review.StopReasons(requireHiddenReview, manifest.ContentPolicy)` for this policy.

The GUI's optional hidden-content-review checkbox applies the same recipient policy. Confirmation updates the status; loading another package clears the search query as well as old content and review confirmation.

Final DOCX tables whose surviving grid cannot be resolved retain their cell text in row order instead of dropping it or guessing column relationships. `DocxFinalTableGridUnresolved` and `review.pages[].elements[].kind: table_structure_uncertain` identify the source-review item, and `review.required` is true. Rectangular control tables and uniform whole-column deletion keep normal table rendering without this extra review.

Final-table fallback also retains text from absorbed overlay figures as separate notes with unresolved cell placement. Uniform deleted column ranges are compared independently of how merged/adjacent cells represent them, so equivalent deletions do not add unnecessary review.

CLI `package review` uses `ReadVerifiedReviewAsync`, which retains the checked report/review and validates the source index without loading part Markdown. CLI `package read` uses `ReadVerifiedPartAsync`, retaining only its selected Markdown. Every other listed file is verified with a bounded streaming buffer, including the same ZIP opening, size and CRC rules; whole-package verification remains mandatory. Returned text is still exactly the bytes checked. Source-index/manifest metadata and selected text still need memory. Full-text search and the GUI retain their verified full-text snapshot.

With the Python example's `--require-hidden-review`, stored package review is also enforced. JSON `review.required` and `review.effective_required` include the hidden-content policy; `review.reported_required` preserves the original report-aware state. `review.stop_reasons` contains `package_review` and/or `hidden_content`. Without the option, the previous JSON shape and `--fail-on-review` behavior are retained.

`package read --json` returns `estimated_tokens` and `token_estimate_multiplier` at the root, with or without `--with-sources`. `package search --json` returns both fields on each entry of `matches`, alongside its `part_id`, `locations` and matching `lines`. The estimate describes the whole part, not just the matching lines. Both commands return the stored, already adjusted estimate without applying the multiplier again. For example, a part stored with estimate 858 and multiplier 2 returns `"estimated_tokens": 858` and `"token_estimate_multiplier": 2`.

Each field is explicitly `null` when the package does not record it: schema 1.0/2.0 parts have no estimates, while schema 2.1/2.2 packages may retain estimates without a recorded multiplier. A missing multiplier does not establish that the estimate used a multiplier of 1. These are heuristic estimates, not exact tokenizer counts or token-limit guarantees. Plain `read` output remains only Markdown, and plain `search` output keeps its part/location/line display.

These commands always verify every file, the source index and the report before emitting any selected content. They stop with exit 11 and no stdout when either conversion or revision-sharing review is required. Read `review.md` and perform the relevant checks, then use `--allow-review` to acknowledge that review for one invocation. A malformed or changed package produces no body; verification errors exit 7, unreadable paths exit 2, and unsupported schemas exit 4. Plain `read` output contains only the selected Markdown; source-index data is included only with `--json --with-sources`. Search ignores case, full-width forms, Markdown escapes and supported inline decoration. It is text matching, not semantic retrieval.

The GUI's primary 「パッケージを読む」 action opens a directory or ZIP, verifies it, displays `review.md` and blocks content pending explicit review confirmation. Search selects matching part IDs; the selected Markdown can be copied on its own. Source-index cells stay in a separate details panel. Loading another package clears all old content and review confirmation. The API's `ReadVerifiedPackageAsync` returns an in-memory snapshot whose Markdown and review text are exactly the bytes checked; callers must check `Review.Required` before giving content to a model.

The GUI puts row splitting and the target character count beside the package output choice, shows an OCR-free estimate for the first selected document, and reports actual part count, largest part, target overshoots and full/total-part character counts after export. Estimates can differ when OCR or providers add content. A single part often offers little input reduction; repeated headers and context can make all parts combined larger than the full document. Character counts use .NET UTF-16 units, not model tokens. API: `PreviewAiPackageAsync` and `AiPackageExportResult.Size`.

## Reading packages and compatibility

| Manifest schema | Written by | Layout |
| --- | --- | --- |
| `1.0` | v0.3.0 | `node_ids` and per-node `sources` inline in each part; no source index |
| `2.0` | v0.3.1 | shared `locations`, per-part `node_count`/`source_ids`, separate `source-index.json` |
| `2.1` | v0.3.2 | `2.0` plus `table_row_blocks`, per-part `estimated_tokens` and optional `table_block` |
| `2.2` | v0.3.3 | `2.1` plus `table_block.context_range`; row blocks after the first repeat the text above their table |

Read the major version first. A minor version only adds fields, so a reader for `2.x` accepts `2.2` (and later `2.x`) by ignoring what it does not know. A different major version, a missing `schema_version`, or a value that is not exactly `MAJOR.MINOR` (no spaces or line breaks) must be refused with an explicit error rather than read as if it had a known layout. `source-index.json` and `report.json` have their own schema versions; readers accept `1.x` of both.

Check a manifest before reading anything through it, and refuse it rather than reading part of it when:

- a field the reader uses is missing or has the wrong JSON type: `source` (`file_name`, a 64-digit hexadecimal `sha256`, `format`), `content_policy`, `files` (each with a relative `path`, `bytes` as a whole number and a 64-digit `sha256`), `parts` (each with `id` and `path`; `2.x` also `node_count` and `source_ids`; `1.x` `node_ids` and `sources` of the same length), and in `2.x` `locations` (each with `id`, `partition_id` and `label`) and `source_index` (node identities live only there). Optional fields may be absent or null, but have their documented type when present;
- a JSON object names a key twice, or an id or path appears twice (`locations`, `parts`, `files`), or a part names an unknown location or neighbouring part;
- the blocks of a cut table do not form one chain: the parts with one `table_id` must number its `count`, hold each `index` from 1 to `count` once, and name exactly the blocks before and after them in `previous_part` and `next_part` (null at either end), so following `next_part` reaches every block of the table once and never another table or a circle;
- a file the manifest refers to is missing from `files` (see above), `files` lists `manifest.json`, or a path would leave the package or holds a control, line-separator or bidirectional formatting character (DocRedock writes none; they could fake lines or reorder names when printed);
- a JSON file is nested more than 64 levels deep or contains an unpaired surrogate escape (`\ud800` without its pair);
- a ZIP is not plain (below): readers would disagree about which file is meant or what it holds.

Both readers read a package ZIP only when it is plain, and check it the same way, in the same order, as Python's `zipfile` does (including the checks only Python 3.12 has), so every Python version and .NET agree:

- the whole archive: its end record describes the central directory right before it (the same entry count, one disk, nothing in between); a Zip64 end record sits where its locator points and ends where the locator begins (Python 3.12 checks this, earlier versions did not); every entry needs at most ZIP 6.3 to extract and has a name without NUL or backslash that no other entry has, and no Unicode Path extra field (names without the UTF-8 flag are read as UTF-8 when they are valid UTF-8, as macOS `ditto` writes them, else as code page 437);
- an entry when it is used: not encrypted, stored or deflate-compressed, its local header found with the same name and without flag bits 5 or 6, its data ending before the next entry, and its bytes (up to its size) matching the archive's CRC-32.

In a package folder, a link, pipe or device is refused without being opened, a folder where a file should be is a missing file, and names are matched exactly, as in a ZIP: `parts/0003.MD` is not `parts/0003.md`, even where the file system would open one for the other (macOS, Windows). Either reader refuses a file larger than 2,147,483,520 bytes rather than reading it into memory.

When the source index is read, it must list each manifest part exactly once, give each part groups that name exactly that part's locations, add up to its `node_count`, and have one cell address (or null) per node ID where `cell_addresses` is present. A part lists a node once, and every part that lists a node (a repeated table header, for example) puts it at the same location and cell, so joining text to its source by node ID has one answer; this holds for `1.x` inline sources too.

[`tests/DocRedock.Tests/Fixtures/AiPackage/reader-cases.json`](https://github.com/Takayuki-Ishimaru/docredock/blob/v0.3.3/tests/DocRedock.Tests/Fixtures/AiPackage/reader-cases.json) states these rules as data: packages derived from the samples, each with the result both readers must give. `tools/test_ai_package_reader_cases.py` and `AiPackageReaderCaseTests` run it in CI.

.NET consumers can use `AiPackageManifestReader`, which normalizes every layout and refuses another schema with `AiPackageSchemaException` (a `NotSupportedException` naming the value and `supported: 1.x, 2.x`; a source index or report of another schema is refused the same way). A manifest that fails one of the checks above and a package JSON file that is not UTF-8 JSON raise `InvalidDataException`, naming the file and the field (`AI package manifest.json: files[0].bytes is missing.`). Names that would leave the package (absolute, drive, colon or `..` paths) and symbolic links inside a package folder are refused rather than followed. `ReadVerifiedPartAsync` does in one call what a consumer handing a part to a model needs: it verifies the whole package (every listed file, nothing unlisted), checks the source index and report, and returns the part's Markdown as the exact bytes it verified, with the part's locations and table block, its source-index groups and the review state. It raises `AiPackageVerificationException` (whose `Problems` are `VerifyFilesAsync`'s lines) when a file is missing, changed, refused or not listed, and `KeyNotFoundException` for an unknown part:

```csharp
var verified = await AiPackageManifestReader.ReadVerifiedPartAsync("input.ai-package.zip", "part-0003");
if (verified.Review.Required) { /* read review.md first, or stop */ }
var text = verified.Markdown;                                       // for the model
var cells = verified.Sources.SelectMany(group => group.CellAddresses ?? []);   // kept on your side
// Or step by step:
var manifest = await AiPackageManifestReader.ReadAsync("input.ai-package.zip");   // directory or ZIP
foreach (var part in manifest.Parts)
    Console.WriteLine($"{part.Id} {part.Path} ~{part.EstimatedTokens?.ToString() ?? "-"} tokens " +
        string.Join("; ", part.Locations.Select(location => location.Label)));
var index = await AiPackageManifestReader.ReadSourceIndexAsync("input.ai-package.zip", manifest);
var problems = await AiPackageManifestReader.VerifyFilesAsync("input.ai-package.zip", manifest);   // empty: intact
var review = await AiPackageManifestReader.ReadReviewAsync("input.ai-package.zip", manifest);    // Required, ReviewItemsDetected, HiddenContentIncluded
```

For a `1.x` manifest the reader builds the shared locations from the inline sources (`source-0001`, … in order of first appearance) and `ReadSourceIndexAsync` groups the inline node IDs (with any inline `cell_address`); `NodeIds` on a part is filled only for `1.x`. `ReadReviewRequiredAsync` remains as a shortcut for `ReadReviewAsync(…).Required`.

[`docs/examples/ai_package_reader.py`](../examples/ai_package_reader.py) is a standard-library Python version of the same rules (directory or ZIP, schema `1.x`/`2.x`, the checks above, `PackageError` and its subclass `UnsupportedSchemaError`, hash verification, `read_review`, `read_verified_part` and `search_parts`) and a small CLI:

```sh
python3 docs/examples/ai_package_reader.py input.ai-package.zip            # parts, labels, token estimates
python3 docs/examples/ai_package_reader.py input.ai-package.zip --verify   # check the package
python3 docs/examples/ai_package_reader.py input.ai-package --search "売上"  # which parts mention it
python3 docs/examples/ai_package_reader.py input.ai-package --part part-0002
python3 docs/examples/ai_package_reader.py input.ai-package --verify --part part-0002 --fail-on-review > part.md
python3 docs/examples/ai_package_reader.py input.ai-package --verify --part part-0002 --json   # text, cells and review state
```

Checks run before anything is printed. `--verify` prints `files ok` when every listed file matches its size and SHA-256, the package holds no other file, and the source index and report are well formed; otherwise one line per file that is missing, changed, refused (a link, pipe or device, an encrypted or unreadable ZIP entry) or not listed, or one error line when the source index or report is malformed. Together with `--part` or `--search`, those lines go to stderr and the part, as the exact bytes that were verified whatever the console encoding, or the matches are printed only when everything matches. `--search TEXT` lists the parts containing the text, ignoring case, full-width forms, Markdown escapes and the underline, strikethrough and bold marks (`CANDIDATE_ONE` finds `CANDIDATE\_ONE`, `重要な点` finds `これは<u>重要</u>な点`), each with up to three matching lines. `--json` prints one JSON object instead of text: `source`, `content_policy`, `verified` (true only when `--verify` was given and passed), `review` (`required`, `review_items_detected`, `hidden_content_included`, `file`), and `part` (with `markdown`, `locations`, `table_block` and its source-index `sources`), `parts` or `search`; when verification fails it holds `problems` and no text. `--fail-on-review` stops without printing the part, parts or matches when `report.json` says review is required. Labels, file names and lines are printed with control, line-separator and bidirectional formatting characters as `\xNN`/`\uNNNN` escapes, so a package cannot fake lines or terminal output (the part's own Markdown, and `--json`'s strings once parsed, are unchanged). Exit codes: 0 success, 1 `--verify` found a file missing, changed, refused or not listed, 2 the package cannot be read (not a package or ZIP, an ambiguous ZIP, unsupported schema, malformed manifest, source index, report or JSON, a manifest path that would leave the package, unknown part), 3 review required with `--fail-on-review`, 141 the output pipe was closed early (as by `head`); each error is one line on stderr. The .NET `VerifyFilesAsync` returns the same lines in the same words, and raises `InvalidDataException` where the CLI exits with 2. `tests/DocRedock.Tests/Api/AiPackageReaderParityTests.cs` runs both readers on the same crafted packages.

The real sample packages used by both readers' tests, one per manifest schema, are in `tests/DocRedock.Tests/Fixtures/AiPackage/`.

## Giving a package to a model

`document.md`, the parts and `source-index.json` serve different purposes; do not send all of them on every request.

- Send `document.md` when the whole document fits the model's context, or only the parts the question needs (a few at a time, using `estimated_tokens`) when it does not. Each part names its source file and location (`section_path`, `table_block`), so it can be read alone; for the same reason every part repeats the title and source line, and a row block repeats its table's header (and the text above it), so all parts together are larger than `document.md` — splitting saves input only when you send fewer parts. `--search` (or `search_parts`) finds the parts that mention a term.
- Keep `source-index.json` on your side. Load it only when an answer has to be traced back to node IDs or worksheet cells — for example to turn "part-0007, row 3" into a cell address — and do not add it to the prompt. `ai_package_reader.py PACKAGE --verify --part part-0007 --json` (or `ReadVerifiedPartAsync`) returns the verified text together with these cells in one call; send only `part.markdown` to the model.
- Read `review.md` (or `report.json`) before relying on the output: it lists pages to compare with the source, OCR items, worksheet table boundaries to check, and whether hidden content was included.
- In an automated pipeline, stop before the model when review is required instead of relying on exit code 0 alone: `docredock export … --ai-package zip --fail-on-review` returns 11 when either review gate requires attention (the package is still written), and for a package you received, `ai_package_reader.py PACKAGE --verify --fail-on-review` returns 1 or 3, or check `review.required` in the `--json` result, `ReadVerifiedPartAsync`'s `Review` or `ReadReviewAsync`. Hidden content is reported separately in `summary.hidden_content_included`.

## Report

`report.summary.tables` is the legacy count of canonical graph `Table` nodes; `native_tables` exposes the same count explicitly (including inferred PDF tables). `rendered_tables` counts actual GFM tables in `document.md`, including tables formed from Excel cells or chart data and excluding fenced code examples and DocRedock's own OCR review tables. Graph-only inspection has no rendered projection and leaves this counter null. A zero graph-table count for a worksheet does not mean its readable tables were lost.

Report schema `1.1` adds the fields below. Two `1.0` fields also count differently: `review.required` is also `true` when worksheet table boundaries need comparison, and `summary.rendered_tables` no longer counts DocRedock's own OCR review tables.

- `summary.review_items_detected`: whether anything was detected for comparison (figures, unanalyzed content, OCR, worksheet table boundaries). `false` means nothing was detected, not that the conversion was verified.
- `summary.reading_order` and `summary.table_structure`: how each was obtained — `source` (taken from the file: Word body order; Word/PowerPoint/chart table markup; tagged PDF tables), `inferred` (reconstructed from positions or cell layout, not compared with the source), `needs_comparison` (at least one place could not be decided), `not_applicable` (no tables) or `not_evaluated` (no readable projection was made).
- `summary.table_boundary_review_items` and `review.table_boundaries`: worksheet blank columns at which two tables were output separately although their rows sit side by side with nothing in the layout to tell whether they belong together. Each entry has `sheet_name` and `gap_range`, `left_range` and `right_range` to compare; the Markdown carries an `<!-- inferred: … -->` note at the same place.

Visual conversion counters, `review_items_detected: false` and an `inferred` basis do not certify text reading order or semantic correctness.

## Safety margin for the token estimate (`--token-estimate-multiplier`)

`estimated_tokens` in `manifest.json` is a model-independent heuristic that needs no tokenizer: ordinary ASCII characters (excluding digits and `|`) count at about four per token, each consecutive run of digits at three per token (rounding each run up independently), Markdown table delimiters (`|`) at two per token, and each non-ASCII Unicode scalar at one per token. The ordinary-ASCII and `|` category totals are rounded up separately. Real tokenizers differ by model, so this is not an exact tokenizer count and does not guarantee that content fits a token limit; use it to compare parts and plan batches.

`export --ai-package dir|zip --token-estimate-multiplier 2` widens that heuristic by a margin you
choose: every part's `estimated_tokens` becomes the plain estimate times the multiplier, rounded up. The plain estimate already rounds up each character category separately.
The value is invariant-culture, finite, and between 1 and 100 inclusive (1 is the default, so
estimates exported without the option are unchanged). `NaN`, `Infinity`, out-of-range and
non-numeric values, an omitted value, and the option used without `--ai-package` are rejected with
exit code 2 before any output is written. The margin changes only the reported estimate: part text,
splitting, node ids, source ids, checksums, review gates and the chunk target are untouched.

```
docredock export report.docx --ai-package dir --token-estimate-multiplier 2
```

The same margin is available through the API as
`AiPackageExportOptions.TokenEstimateMultiplier` / `AiPackagePartOptions.TokenEstimateMultiplier`,
and `manifest.json` records it as the root `token_estimate_multiplier` for provenance. The typed
reader exposes it as nullable `AiPackageManifest.TokenEstimateMultiplier`; a missing or JSON-null
field becomes null. A present value must be a finite JSON number from 1 to 100 inclusive; a wrong
type or out-of-range value raises `InvalidDataException` naming the field. `AiPackageSearchMatch`
exposes nullable `EstimatedTokens` and `TokenEstimateMultiplier` as stored, without recomputation.
Estimates saturate at `int.MaxValue` (2,147,483,647) rather than overflowing.

```csharp
var options = new AiPackageExportOptions(documentOptions, "report.zip", AiPackageForm.Zip)
{
    TokenEstimateMultiplier = 2,
};
```

Revision sharing is also identified by `summary.revision_sharing_required`. The existing
`summary.review_items_detected` flag continues to describe source-comparison items; the displayed
summary names revision sharing explicitly while package review keeps the two decisions separate.

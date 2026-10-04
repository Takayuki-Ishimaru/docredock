# DocRedock User Guide

[日本語](../ja/user-guide.md) | English

This guide covers the v0.3.1 Public Beta supported workflow: desktop-GUI conversion of local DOCX, XLSX, PPTX, and PDF files to **Readable Markdown** or an **AI package**.

## 1. Get DocRedock

Download the package for your OS/CPU from GitHub Releases and verify the published SHA-256. Self-contained packages do not require a separate .NET SDK.

## 2. Convert a file

1. Start DocRedock.
2. Select or drop a DOCX, XLSX, PPTX, or PDF file.
3. Select **Readable Markdown**.
4. Keep **Visible content only (recommended)** unless you intentionally need another policy.
5. Keep visual inference at **Safe (recommended)** unless you specifically need native-only or balanced behavior.
6. Choose an output location, convert, then review the Markdown, diagnostics, and assets.

The desktop GUI accepts PDF by default. It extracts native PDF text; textless-page OCR and previews for diagram-like pages may require a configured rasterizer/OCR provider. When unavailable, review the page placeholder and diagnostic.

Two-column pages are read column by column (the whole left column, then the right column) when a vertical gutter is confirmed on three or more consecutive lines. A single wide gap on one line, such as a label and its value or a title and a page number, is not treated as a column boundary. On pages with a reconstructed table, body text outside the table is kept, and any unexplained omission is reported as `PdfNativeTextUnaccounted`.

CLI PDF export, restoration, and rendering remain experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`, like other experimental CLI workflows. Read-only `docredock inspect <file.pdf>` remains available without the flag.

CLI export defaults to Readable Markdown:

```sh
docredock export input.docx --content-policy visible --visual-inference safe --output input.md
```

`native-only` accepts explicit source-format connections only. `safe` promotes unique high-confidence geometry assignments. `balanced` may additionally promote medium-confidence assignments, but it still leaves ties and contradictory or globally ambiguous relations unresolved.

Use `--profile roundtrip` explicitly only for the experimental sidecar workflow.

## AI packages

**AI packages** are available in v0.3.1. In the GUI, select **AI package** (「AI向けパッケージ」) and choose a folder or ZIP. Choose the content policy, OCR, and diagram settings as for Readable Markdown, then export. Review `review.md` and the converted content before handing the package to an AI tool. Conversion runs locally; exporting a package does not upload anything.

```sh
docredock export input.docx --ai-package dir --content-policy visible --ocr off
docredock export input.xlsx --ai-package zip --sheets Summary,Data --output workbook.ai-package.zip
docredock export input.docx --ai-package zip --chunk-chars 16000 --output input.ai-package.zip
```

The default name is `<source>.ai-package` or `<source>.ai-package.zip`. The package contains:

| File | Purpose |
| --- | --- |
| `document.md` | The full Readable Markdown |
| `parts/0001.md`, … | Independently readable semantic parts with the source file name and Word section breadcrumbs |
| `assets/` | Referenced images, when present; part links use `../assets/` |
| `review.md` | Conversion limitations, OCR review counts, hidden-content notice, and links to affected parts |
| `manifest.json` | Source file name/hash, settings, each part's source locations, and output file hashes |
| `source-index.json` | Detailed node IDs and original Excel cell addresses; load when needed |
| `report.json` | Structured conversion summary/review and diagnostic codes/counts |

Word splits at headings and between intact semantic groups, with a soft size target of 12,000 characters. Tables (including nested tables), diagram members, and list sequences remain together. Each PDF page, PowerPoint slide, or Excel sheet remains one part. Large units can exceed the target; `exceeds_target` marks them and `review.md` lists them. This is a character target, not a model token limit. The CLI uses `--chunk-chars` and the API uses `TargetCharacters`, accepting 128–1,000,000; the GUI uses the default.

The compact manifest records actual PDF page numbers, slide numbers, sheet names, or Word heading paths in shared locations. The separate source index retains node IDs and Excel cell addresses. Word exports do not invent page numbers. Blank or entirely excluded units create no part. `visible`, `sanitized`, `complete`, and selected sheets apply to the Markdown, parts, source map, and copied images. `complete` can include hidden content and adds a sharing-review notice. The original Office/PDF file and absolute source path are not included.

The current manifest uses schema `2.0`; consumers of the released v0.3.0 schema `1.0` must use the new shared locations/source index as described in the format contract. Conversion results also show how many tables were rendered in Markdown, including those assembled from Excel cells. Counts do not certify correct content or reading order.

Images remain in `assets/`, so `--embed-images` cannot be combined with `--ai-package`. Existing conversion switches, including `--no-diagrams`, `--show-formulas`, `--ocr-review`, and PDF fallback settings, still apply. CLI PDF conversion retains its experimental flag requirement. A successful export with conversion warnings returns 1; failed/cancelled conversion leaves no partial package. Existing targets require `--force` in the CLI; the GUI selects a numbered name for repeated exports. Packages are one-way inputs and are not restoration sidecars. See the [format contract](../reference/ai-package.md) for the API and JSON details.

## 3. Generated files

| Output | Contents | Current use |
| --- | --- | --- |
| `.md` | Body text, headings, lists, tables, visual semantic projections, notes, and placeholders | Use |
| `.assets/` | Images and previews referenced by Markdown as visual fallback | Use when generated |
| Report/diagnostics | Unresolved connectors, partial projection, fallback, and omission reasons | Always review when warnings exist |
| `.drmd` | Source/restoration sidecar | Experimental; treat like the source document |
| `.drmdpkg` | Markdown and restoration data package | Experimental; treat like the source document |

Mermaid is emitted only when connections are clear and consistent. Recognized visuals retain the best available form in this order: (1) Mermaid, (2) an image or page-preview fallback, and (3) an explicit diagnostic. Preserving shape text alone does not prove that connections and branches were preserved.

## 4. Choose a content policy

- **visible** (default): filters recognized hidden text, hidden sheets/rows/columns, hidden slides/objects, notes, comments, and revisions, as well as PDF text entirely outside the visible area or on a layer that is off, out of the Markdown projection.
- **complete**: includes hidden/metadata content and emits a warning.
- **sanitized**: also filters metadata, derived/OCR content, and furniture such as headers and footers.

XLSX exports also accept `--sheets Sheet1,Sheet2` (CLI only) to project only the named worksheets. Every requested name must exactly match an existing worksheet (case-insensitive); an unknown name fails the export with exit code 2 and writes no output file — a partial mismatch (some names found, some not) is never silently ignored. Requesting a hidden worksheet that the `visible`/`sanitized` policy excludes is not an error, but it emits an `XlsxSheetExcludedByPolicy` warning (exit code 1) instead of a silently empty-looking result; use `--content-policy complete` to include it.

OCR text inherits its parent image's visibility. If DocRedock cannot resolve the parent partition, it places the evidence in a dedicated `derived-assets` partition and emits `OcrParentPartitionUnresolved`.

## 5. Review the result

For documents with visuals, check these in addition to normal text and table content:

- heading hierarchy, nested lists, merged-table blanks, formula-cache warnings, and slide boundaries;
- flow node labels, connection directions, branches, and edge labels such as YES/NO;
- the report distinction between `native-connection` and `geometry-inferred`;
- diagnostics such as `VisualConnectorUnresolved`, `VisualEdgeLabelUnresolved`, and `VisualSemanticProjectionPartial`;
- every referenced asset/page preview/placeholder against the corresponding source page, slide, or sheet;
- that text boxes and AlternateContent fallbacks are not duplicated.

When a Warning is present, giving an AI only the Markdown may conceal missing meaning. Review the diagnostics/report and assets together, and consult the source document when necessary. A semantic omission or partial projection at Warning severity makes the CLI return exit code 1.

Readable output is not pixel-perfect reconstruction and does not guarantee restoration to original Office drawing objects. Keep the source document as the authoritative copy. See [Supported features](supported-features.md) for format-specific boundaries.

## 6. Experimental PDF rendering

DocRedock does not bundle or download a Japanese font. ASCII-only PDF output uses Base14 Helvetica. Non-ASCII output resolves an embeddable TrueType font in this order:

1. `--font-path` and optional `--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH` and optional `DOCREDOCK_PDF_FONT_FACE_INDEX`
3. installed system fonts

```sh
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock render input.md --format pdf \
  --font-path /path/to/font.ttc --font-face-index 0 --verbose
```

The resolver rejects unsupported CFF/CFF2 outlines, missing glyph coverage, invalid collections, and embedding-restricted fonts. The user must comply with the selected font's license. `--verbose` includes the selected path; `--quiet` suppresses informational lines, not warnings. A render with omissions/truncation returns exit code 1.

## 7. Capability and PDF OCR diagnostics

Check local capabilities with doctor. It is outside the experimental gate and requires no input file.

    docredock doctor
    docredock doctor [--strict]
    docredock doctor --json

ready means the dependency was probed and is available, partial means only some functions (or OCR languages) are available, and unavailable means a dependency is missing or disabled. Native PDF OCR may still be partial when page content is unclear. Image-only PDF OCR needs both an OCR engine and a PDF rasterizer. Discovery checks an explicit path (DOCREDOCK_PDF_RASTERIZER), then pdftoppm, then mutool on PATH. Set DOCREDOCK_DISABLE_PDF_RASTERIZER=1 to disable discovery. If unavailable, install pdftoppm or mutool, or configure the executable path. Processing remains local and does not use the network. Raster fallback is bounded to 100 paths and 32,768 characters per page; native text is retained when fallback is compacted.

**Enabling OCR on Windows.** Windows Media OCR (the native provider reported as `ocr-native`/`windows-media`) needs the OCR language feature for each language, which is a separate optional Windows component from the display language — installing Japanese as a display language does not install Japanese OCR. Check what is actually installed with `docredock doctor` (or `docredock doctor --json`): once a usable language pack is found, `ocr-native` reports `ready`, and its `action` text names exactly which language is still missing and how to add it. To install a language pack, use Settings > Time & Language > Language & region > Add a language > (language) > Options > "Optical character recognition" ("光学式文字認識 (OCR)" for Japanese), or run `Add-WindowsCapability -Online -Name Language.OCR~~~ja-JP~0.0.1.0` (Japanese) / `Language.OCR~~~en-US~0.0.1.0` (English) from an elevated PowerShell. The PDF rasterizer (pdftoppm/mutool) is unrelated to this: it is needed only to OCR image-only PDF pages, never for OCR of images embedded in DOCX/XLSX/PPTX or of PDF pages that already carry native text, so a missing rasterizer no longer disables the OCR toggle. If Windows Media OCR is unavailable, install Tesseract as a portable alternative (see [Supported features](supported-features.md)).

Every capability carries a tier, `required` (docx-readable, xlsx-readable, pptx-readable, pdf-text) or `optional` (everything else, including OCR, the PDF rasterizer, and mermaid-render). Plain `docredock doctor` and `docredock doctor --json` always return the same exit code: 0 when every required capability is ready, 1 otherwise; optional gaps are reported but never change this exit code. Add `--strict` to fail (exit 1) on any capability that is not ready, including optional ones — except when an optional gap is already covered by a ready alternative, reported as `satisfied_by`. For example, on a platform with no bundled native OCR helper, `ocr-native` reports `satisfied_by: "tesseract"` once Tesseract is ready, and `--strict` does not fail on that gap; a `partial` alternative never counts as satisfying it. The JSON report adds `tier`, `satisfied_by`, `strict`, `exit_code`, and a `summary` (`required_ready`, `optional_gaps`, `strict_failures`) alongside the existing fields.

PdfTableInferred means a regular ruled grid was reconstructed as a table; PdfTableNative means existing table information was used; PdfTableAmbiguous means evidence was not unique enough to classify as a table. VisualFallbackCompacted means fallback output was reduced to fit the output budget while retaining partial topology. Small gaps and noise in human-drawn diagrams may remain as partial topology and fallback; unresolved connections are reported instead of silently guessed.

## 8. Privacy and updates

Conversion runs locally. The app always shows its running version. At startup it checks non-draft published releases, including Public Beta builds, through the public GitHub Releases API in the background and shows current/latest versions when an update exists. **Check for updates** runs a manual check; offline and API-limit failures never block startup or conversion. Updates are not auto-installed: the user chooses a package from the trusted GitHub release page. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable automatic checks.

For experimental workflows, see [Experimental features](experimental-features.md). For handling guidance, see [Security and privacy](security-and-privacy.md).

## Reviewing conversion results and protecting originals

The GUI distinguishes completion, completion with warnings, and failure. Warnings put the need for source comparison, affected pages, and unresolved content in the main result area.

Unresolved PDF figures/tables receive source page images when a rasterizer is available, independently of OCR. Use the GUI page-image checkbox or `--pdf-fallback-images auto|off` (default `auto`). Images use `.assets/`, or inline data with `--embed-images`. Attaching an image does not resolve semantic warnings. Unavailable or failed rasterization remains explicit.

OCR details include provider word/line confidence and coordinates. Values below 80% are marked for review; missing confidence is reported as unavailable. Identifiers and numbers are never automatically corrected. External-image links include `xywh` media fragments; region navigation depends on viewer support. Coordinates remain available for manual comparison. Vision's bottom-left normalized coordinates are converted to top-left percentages in links.

OCR review rows follow the body's line and within-line order and include line numbers. Readable output defaults to details only for confidence below 80% or missing confidence. Use the GUI OCR review detail selector or `--ocr-review low-confidence|all|summary`: `all` includes every record; `summary` includes counts only. All modes retain the OCR body and original image. Audit/roundtrip sidecars retain all records. Confidence is an engine estimate, not a measured accuracy rate.

`Fallback pages` counts pages with vector path fallback. Separate counters show `Pages requiring review`, `Review image pages`, and `Unresolved visual elements` (shapes, connections, and labels). Attaching a review image does not reduce unresolved counts. A path and the unresolved connection backed by it count as one element.

The export summary starts with three separate results. `Output written` says the Markdown was saved; `Visual elements converted` says whether every recognized shape, line, and label was expressed in the Markdown (`all`) or some remain unresolved (`partial`); `Human review` names why a person should check the result (pages with figures to review, pages with content that could not be analyzed, OCR items). `all` does not mean attributes DocRedock does not model, such as color or line width, were kept. The `Unanalyzed content` line counts parts a page draws whose contents could not be analyzed at all (such as a PDF Form XObject) and the pages they are on. `Visual elements converted: all` speaks only for the elements that were recognized, so the page as a whole was not fully analyzed unless `Unanalyzed content` is `none`. Each page to review then gets one line such as `Review page 1: 1 diagonal line(s) across a table …; review image: …`, naming what to look at (diagonal lines or arrows, lines with undetermined endpoints, text not assignable to one line or shape, shapes kept as fallback, drawing components not analyzed) and where the review image is. Counts are elements and pages to check, not diagnostic records.

When `complete` includes hidden content, `Human review` also says `hidden content included - review before sharing`. If no source or OCR comparison is needed, the line starts with `source comparison not required`. This sharing check does not add review pages or images; the `HiddenContentIncluded` warning still produces exit code 1.

OCR results with confidence below 80% or no confidence are counted separately from warnings as `OCR summary: images=…; regions=…; review_items=…; review_required=true|false`. OCR review alone is not a warning and does not change the exit code. The GUI also shows "OCR確認 N件" on its own line.

The GUI result panel leads with counts such as "要確認 1ページ／照合画像 1ページ添付／未解決の図形 1件" and a short explanation such as "1ページ目：表の上の斜めの線1件を表の記号に変換できませんでした。表の文字は書き出されています。照合画像で線の意味を確認してください。". Diagnostic codes, internal IDs, and confidences are in the collapsed "詳細（診断コードと対処）" section, where diagnostics that follow from the same unresolved element are grouped into one entry. "該当ページを確認" opens a review window with the source page image (unresolved lines and shapes marked in red; a drawing component that could not be analyzed is outlined in dashed red over the area it may paint, and text whose characters or visibility could not be determined over where it sits) next to that page's Markdown. "要確認箇所へ拡大" zooms to each marked element in turn and centers it; "＋" and "－" (or Ctrl + mouse wheel) change the zoom, and "全体" returns to the whole page. The red marks keep their on-screen width at any zoom, and clearing "強調表示" hides them so the original detail can be inspected once its position is known (the setting holds across pages). The Markdown pane switches between "表示", which shows tables as ruled tables and each Mermaid diagram as its list of connections such as "START → END（label）", and "ソース", the Markdown itself (a Mermaid diagram the preview cannot read is shown as source). When no review image exists (no rasterizer, attachment turned off, or an Office source), the window tells you which page of the source to open. When the content policy left hidden content out of the Markdown (hidden Word text, PDF text outside the visible area, and so on), the result panel names each kind and its count.

`DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock preflight edited.md [--json]` checks integrity, detects edits, and actually tries restoration in a disposable sidecar copy without modifying inputs or reports. `verify` checks integrity, `diff` describes edits, and `preflight` integrates integrity and restore applicability. Ordinary edits alone do not cause a warning exit. Exit codes are 0 for readiness, 1 for readiness with warnings, 3 for invalid workspace integrity, and 6 for unsupported/conflicting edits. Use `--allow-render-fallback` to explicitly permit edited-PDF regeneration. Success does not guarantee visual identity in Office.

Restoring over the historical source requires `--force --replace-original`; `--force` alone is insufficient. After a successful restore trial, the destination's previous bytes are retained as `<output>.docredock-original-<ID>.bak` before replacement. Prefer a new output name for normal restoration.

# Quick guides by task

## Give a document to an AI

1. In the GUI, keep **Visible content only (recommended)** selected and add a DOCX, XLSX, PPTX or PDF.
2. Choose **AI package** when you want to trace answers back to the source.
3. After export, review any source-review pages, tracked changes and excluded hidden content.
4. Give the AI only the part it needs; keep the source index for local reference.

Readable Markdown and AI packages repeat vertically merged cell values in each row by default. To leave the rows below the first blank, use `--no-expand-merged-cells` or clear the option under **Advanced settings** in the GUI.

In the CLI, use `docredock export input.docx --ai-package dir --content-policy visible --ocr off`. See the [revision defaults](../reference/revision-defaults.md) before sharing Word documents with tracked changes.

## Use DocRedock from an MCP client

`docredock mcp` is an MCP server over standard input and output (JSON-RPC 2.0, one message per line, UTF-8). Register it in the client, for example:

```json
{ "mcpServers": { "docredock": { "command": "docredock", "args": ["mcp"] } } }
```

In Claude Code: `claude mcp add docredock -- docredock mcp`. The server offers five tools that run the same conversions as the CLI on this computer and return its `--json` results:

| Tool | What it does |
| --- | --- |
| `export_markdown` | Converts a document to Readable Markdown and returns the result with the Markdown (cut at `max_chars`, 40,000 by default; the note names the whole file). |
| `export_ai_package` | Writes an AI package and returns its folder as `package_path`. |
| `package_review` | Returns the package's review state and `review.md`. |
| `package_search` | Finds the parts that mention a text. |
| `package_read` | Returns one part. |

A package that needs review returns no text from `package_search` or `package_read` until `allow_review` is passed, after a person has checked `package_review`.

The tools read the files the client names, with the rights of the user who started the server. Outputs go to a new folder under the system's temporary folder unless `output_dir` is given; existing files are replaced only with `overwrite`. PDFs need `allow_pdf`, as `--allow-pdf` in the CLI.

## Check privacy before sharing

`visible` excludes recognized hidden content. `sanitized` additionally removes metadata and derived content. `complete` includes hidden content and needs review before sharing. Word revision `markup` may include deleted text. Check the GUI result and **Review page**, or the CLI diagnostics and `review.md`.

## Add an export to automation

Add `--fail-on-review` when downstream steps must stop until someone reviews the output; export exits 11 when review is required. The bundled Python reader also blocks `--part` and `--search` body output with exit 11 when review is required. After reading `review.md`, pass `--allow-review` to print the body, or use the legacy `--fail-on-review` option to keep its exit code 3.

The exit code, the warnings and the review flag answer different questions: exit 1 means a diagnostic that is not informational (for example `XlsxFormulaCachedValueMissing`, a formula without a stored result) or an empty output; `review_required` means something must be compared with the source (pages, OCR text, table boundaries, images missing from the Markdown) or revision history must be cleared for sharing. A warning does not always require review, and OCR review alone exits 0. With `--json`, read `needs_attention` and `attention` instead of combining them yourself: `attention` lists `warnings`, `source_review`, `revision_sharing`, `hidden_content`, `empty_output`, or `failed` for an input that could not be converted. A batch exits with its most serious result: an error, then 11, then 1.

```sh
docredock export input.xlsx --json --ocr off -o input.md | jq '.results[] | {input, needs_attention, attention}'
```

## Choose settings for the task

| Task | Settings |
| --- | --- |
| Share the final text with an AI | `--content-policy sanitized` (final view of Word revisions, no hidden content, comments, headers or footers) |
| Check revision history | `--revisions markup --allow-revision-sharing` and read `review.md` before sharing |
| Look up rows of a large table | `--ai-package dir --table-row-blocks --chunk-chars 2000`, then `package search` and `package read --part … --with-sources` |
| Read the document yourself | the default `readable` profile; round-trip markers are not needed |

Without `--table-row-blocks` a worksheet table is never cut, so a large sheet stays one part; the CLI and `review.md` say so when a part exceeds the target.

## Convert several files or write to stdout

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export incoming --output-dir converted --json
docredock export input.docx -o - --ocr off > input.md
```

Directory input selects supported files directly inside that directory. `--json` prints the `docredock.export.v2` object with one entry per input in `results`, `--quiet` suppresses the usual summary, and `--verbose` prints details. `-o -` sends only Markdown to stdout and sends the summary to stderr. Even `--force` refuses symbolic links and special files such as devices.

## Handle PDFs, merged cells and older Office files

```sh
docredock export report.pdf --allow-pdf --lang en --fail-on-review
docredock export expenses.docx --no-expand-merged-cells --ocr off
```

`--allow-pdf` enables PDF conversion alone. Choose generated headings and notes with `--lang ja|en`. PDF comparison images use smaller previews than OCR input; use `--pdf-fallback-images off` to omit them and compare review items with the original instead.

For `.doc`, `.xls` and `.ppt`, keep the original and save a separate `.docx`, `.xlsx` or `.pptx` copy with Word, Excel, PowerPoint or LibreOffice before converting.

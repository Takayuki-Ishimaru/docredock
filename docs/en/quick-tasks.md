# Quick guides by task

## Give a document to an AI

1. In the GUI, keep **Visible content only (recommended)** selected and add a DOCX, XLSX, PPTX or PDF.
2. Choose **AI package** when you want to trace answers back to the source.
3. After export, review any source-review pages, tracked changes and excluded hidden content.
4. Give the AI only the part it needs; keep the source index for local reference.

For Word and other native tables, AI packages repeat vertically merged cell values in each row. For a standalone Readable Markdown export, turn on the same behavior in **Advanced settings** when needed.

In the CLI, use `docredock export input.docx --ai-package dir --content-policy visible --ocr off`. See the [revision defaults](../reference/revision-defaults.md) before sharing Word documents with tracked changes.

## Check privacy before sharing

`visible` excludes recognized hidden content. `sanitized` additionally removes metadata and derived content. `complete` includes hidden content and needs review before sharing. Word revision `markup` may include deleted text. Check the GUI result and **Review page**, or the CLI diagnostics and `review.md`.

## Add an export to automation

Add `--fail-on-review` when downstream steps must stop until someone reviews the output; export exits 11 when review is required. The bundled Python reader also blocks `--part` and `--search` body output with exit 11 when review is required. After reading `review.md`, pass `--allow-review` to print the body, or use the legacy `--fail-on-review` option to keep its exit code 3.

## Convert several files or write to stdout

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export incoming --output-dir converted --json
docredock export input.docx -o - --ocr off > input.md
```

Directory input selects supported files directly inside that directory. `--json` prints machine-readable results, `--quiet` suppresses the usual summary, and `--verbose` prints details. `-o -` sends only Markdown to stdout and sends the summary to stderr. Even `--force` refuses symbolic links and special files such as devices.

## Handle PDFs, merged cells and older Office files

```sh
docredock export report.pdf --allow-pdf --lang en --fail-on-review
docredock export expenses.docx --expand-merged-cells --ocr off
```

`--allow-pdf` enables PDF conversion alone. Choose generated headings and notes with `--lang ja|en`. PDF comparison images use smaller previews than OCR input; use `--pdf-fallback-images off` to omit them and compare review items with the original instead.

For `.doc`, `.xls` and `.ppt`, keep the original and save a separate `.docx`, `.xlsx` or `.pptx` copy with Word, Excel, PowerPoint or LibreOffice before converting.

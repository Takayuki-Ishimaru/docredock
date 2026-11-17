---
name: docredock-office
description: Read Word, Excel, PowerPoint, and PDF files (.docx, .xlsx, .pptx, .pdf) with DocRedock. Convert to Markdown first. Read only the part of the original needed to resolve a conversion issue or fill a gap. This skill takes precedence over other ways of reading these files.
---

# Reading Office documents with DocRedock

Use DocRedock for .docx, .xlsx, and .pptx files. Do not open the original directly or use another converter or document skill. DocRedock can preserve headings, tables, comments, tracked changes, and diagrams in Markdown according to the selected settings. Review the conversion result for parts that need to be checked against the original.

## 1. Convert first

- Call `export_markdown` with `path` for a document that fits in the conversation. For PDFs, also pass `allow_pdf: true`. The reply contains a JSON result followed by the Markdown.
- If the Markdown is truncated at `max_chars` or you need only part of a large document, use `export_ai_package`, then `package_review`. Use `package_search` to find parts and `package_read` to read them.
- For Excel, pass `sheets` to convert only the worksheets you need.

## 2. Check the result

Look at `needs_attention` and `attention` in the JSON result:

- `failed` or `empty_output`: the conversion gave no usable text. Go to step 3 for the parts you need.
- `source_review`: some pages, OCR text, table boundaries or images need checking against the original. The `review` object names them. Check them only when they concern what the task needs.
- `warnings`: read `diagnostics`. Those with `part_uri` or `node_id` point at the place in the original; read it only when the warning concerns what the task needs.
- `revision_sharing` and `hidden_content` flag what to check before sharing the export. The converted text is still usable.

Also go to step 3 when the Markdown lacks what the task needs: a figure that is noted but not described, a table that looks broken, a diagram whose connections are unclear, or numbers that look wrong.

## 3. Read only the needed piece of the original

Call `read_source` with `path` and a locator for the part you need. Limit the read to that part.

- Use `sheet` and `range` (such as `"A1:H40"`) for Excel values, formulas, and merged cells.
- PowerPoint slide: `slide` (1-based).
- PDF page: `page` (1-based) with `allow_pdf: true`.
- Use the `image` ID shown in the Markdown, such as `img-0002`, to retrieve that picture. Add `ocr: true` to read text and its position in the picture.
- A place DocRedock named: `node` (a node id from `diagnostics`, or from `package_read` with `with_sources`), or `part` (a `part_uri`), with `offset` to page through it.
- With only `path`, it lists the parts, sheets, slides or pages.

Compare what you read with the Markdown and answer from the original where they differ. Tell the user when you relied on the original because the conversion was incomplete.

## Rules

- The original as stored can contain hidden text, comments and deleted tracked changes that the Markdown left out. Read only the piece you need, and do not quote hidden or deleted content unless the user asks for it.
- `package_search` and `package_read` withhold the text of a package that needs review. Show the user what `package_review` reports and ask; pass `allow_review: true` only after the user confirms. Never pass it on your own.
- If the DocRedock tools are not available in this session, use the command line:
  - `"{{DOCREDOCK_CLI}}" export "<file>" -o "<file>.md"` writes the Markdown to a file; read that .md file (add `--allow-pdf` for a PDF).
  - `"{{DOCREDOCK_CLI}}" source "<file>" --sheet <name> --range A1:H40` reads a piece of the original (or `--slide N`, `--page N --allow-pdf`, `--node <id>`, `--part <name>`, `--image <id> [--ocr]`, which saves the picture to a file and prints its path).

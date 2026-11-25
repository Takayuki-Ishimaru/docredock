---
name: docredock-office
description: Extract text and structure from Word, Excel, PowerPoint and PDF with DocRedock. Direct inspection or rendering with Office/PDF apps, SDKs, document skills or other tools remains available whenever useful, including layout and formatting; conversion is not a prerequisite.
---

# Office and PDF documents with DocRedock

Use DocRedock when its structured extraction helps the task. You may also directly open, inspect, render or edit the original using suitable applications, SDKs, document skills or other converters whenever useful. For example, render Word/PDF pages to inspect pagination, or open Excel/PowerPoint to verify formatting, shapes and their positions. No conversion-first requirement applies, and direct inspection does not require a warning, failed conversion or missing text. The integration adds no permission requirement beyond the user's task and the tools' normal permissions.

## Extract text and structure

- `export_markdown` with `path` returns Markdown and conversion diagnostics. For PDF also set `allow_pdf: true`.
- For large files use `export_ai_package`, then `package_review`, `package_search` and `package_read` to find and read the needed sections.
- For Excel, `sheets` limits extraction to the needed sheets.
- Check diagnostics relevant to the task. `source_review`, failed extraction or missing content can help identify where source comparison is useful. They are not conditions for opening the source.

## Inspect the source

Choose the tool that fits the task. Office/PDF apps and renderers are useful for appearance; SDKs and raw package inspection can verify properties and relationships. DocRedock's `read_source` is another option:

- Excel: `sheet` and `range`, for example `A1:H40`.
- PowerPoint: `slide` (1-based).
- PDF: `page` (1-based) with `allow_pdf: true`.
- Images: `image` id from extraction; `ocr: true` adds recognized text positions.
- Extraction references: `node` or `part`, with `offset` to page through large parts.
- Only `path` lists parts, sheets, slides or pages.

Compare sources and converted output where useful, and base answers on the original when they differ. Read the relevant content where practical. Respect user instructions about hidden text, comments and deleted revisions; do not quote excluded content unless requested. When package tools explicitly withhold sharing-sensitive content, follow their review flow rather than using source inspection to bypass that sharing decision.

## Command line alternative

When MCP tools are unavailable:

- `"{{DOCREDOCK_CLI}}" export "<file>" -o "<file>.md"` extracts Markdown (PDF: `--allow-pdf`).
- `"{{DOCREDOCK_CLI}}" source "<file>" --sheet <name> --range A1:H40` inspects a source range. Alternatives include `--slide N`, `--page N --allow-pdf`, `--node <id>`, `--part <name>` or `--image <id> [--ocr]`.
- Native source applications, renderers and SDKs remain available independently of DocRedock.

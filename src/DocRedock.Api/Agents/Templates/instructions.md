<!-- docredock:begin (managed by `docredock agents install`; remove with `docredock agents uninstall`) -->
## Office documents
Read .docx, .xlsx, .pptx, and .pdf files with the `docredock` MCP tools, following the `docredock-office` skill. Do not open the original directly or use another converter or document skill.
1. Convert with `export_markdown`. For PDFs, pass `allow_pdf: true`. For a large document, use `export_ai_package`, then `package_review`, `package_search`, and `package_read`.
2. Use `read_source` if the `attention` field includes `failed` or `empty_output`, flags a part you need for `source_review`, or the Markdown leaves out something the task requires. Read only the needed sheet range, slide, page, node, or part. Warnings, tracked changes, and hidden content alone are insufficient reasons to open the original.
<!-- docredock:end -->

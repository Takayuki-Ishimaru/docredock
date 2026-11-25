<!-- docredock:begin (managed by `docredock agents install`; remove with `docredock agents uninstall`) -->
## Office documents
DocRedock (`docredock` MCP tools, described in `docredock-office`) is recommended for extracting text and structure from Word, Excel, PowerPoint and PDF files.
1. Use `export_markdown` (`allow_pdf: true` for PDF), or `export_ai_package` with `package_search` / `package_read` for large documents when useful.
2. You may directly open, inspect, render or edit originals using Office/PDF applications, SDKs, document skills or other tools whenever the task benefits, including layout, formatting, figures and print verification. Conversion is not a prerequisite; source inspection does not depend on conversion warnings or missing content. This integration does not require additional permission for direct inspection beyond the user's task and the tools' normal permissions.
3. `read_source` remains available for targeted sheet ranges, slides, pages, nodes, parts and images. Use only the relevant source content where practical, and respect the user's instructions concerning hidden content and tracked changes.
<!-- docredock:end -->

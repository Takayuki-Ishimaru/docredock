# v0.1.4 Supported Features

[日本語](../ja/supported-features.md) | English

DocRedock v0.1.4 Public Beta supports local DOCX, XLSX, and PPTX conversion to **Readable Markdown**.

| Feature | v0.1.4 status |
| --- | --- |
| DOCX/XLSX/PPTX → Readable Markdown | Supported as Public Beta |
| `visible`, `complete`, `sanitized` content policies | Supported |
| CLI `render --format html` | Experimental; explicit opt-in required |
| PDF → Markdown | Experimental; explicit opt-in required |
| Markdown editing → restore to Office | Experimental; explicit opt-in required |
| New PDF/Office generation | Experimental; explicit opt-in required |

Readable output supports document and slide headings, paragraphs, nested lists, tables, images, code blocks, emphasis, hard breaks, spreadsheet formula-cache markers, supported native chart/diagram summaries, and relative image paths in experimental HTML rendering.

The safe default is `visible`. `complete` includes hidden/metadata content with a warning. `sanitized` applies stronger privacy filtering.

Readable Markdown is one-way output. `.drmd` and `.drmdpkg` are experimental and may contain source-derived information.

See the [User guide](user-guide.md), [Security and privacy](security-and-privacy.md), and [implementation capability matrix](../FORMAT_CAPABILITY_MATRIX.md).

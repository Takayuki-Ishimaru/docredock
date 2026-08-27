# v0.1.5 Supported Features

[日本語](../ja/supported-features.md) | English

DocRedock v0.1.5 Public Beta supports local DOCX, XLSX, PPTX, and PDF conversion to **Readable Markdown** in the desktop GUI.

| Feature | v0.1.5 status |
| --- | --- |
| DOCX/XLSX/PPTX → Readable Markdown | Supported as Public Beta; CLI default |
| `visible`, `complete`, `sanitized` content policies | Supported |
| PDF → Markdown/OCR | Desktop GUI: supported; native text is extracted by default and OCR needs configured providers. CLI: experimental; explicit opt-in required |
| Markdown editing → restore to Office | Experimental; explicit opt-in required |
| New PDF/Office generation | Experimental; explicit opt-in required |
| CLI `render --format html` | Experimental; explicit opt-in required |

Readable output supports headings, paragraphs, nested lists, merged tables with blank continuation cells, images/OCR, code, emphasis, hard breaks, spreadsheet formula-cache markers, supported chart/diagram summaries, and normalized PPTX bullets.

The safe default is `visible`. `complete` includes hidden/metadata content with a warning. `sanitized` applies stronger privacy filtering. OCR follows the parent image's partition and content layer.

Experimental PDF rendering does not bundle a Japanese font. ASCII uses Base14 Helvetica; non-ASCII requires an embeddable installed or explicitly selected TrueType font with complete glyph coverage. Textless PDF OCR requires an external rasterizer/provider.

Readable Markdown is one-way output. `.drmd` and `.drmdpkg` are experimental and may contain source-derived information.

See the [User guide](user-guide.md), [Experimental features](experimental-features.md), [Security and privacy](security-and-privacy.md), and [implementation capability matrix](../FORMAT_CAPABILITY_MATRIX.md).

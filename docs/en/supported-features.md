# v0.2.2 Supported Features

[日本語](../ja/supported-features.md) | English

DocRedock v0.2.2 Public Beta supports local DOCX, XLSX, PPTX, and PDF conversion to **Readable Markdown** in the desktop GUI.

| Feature | v0.2.2 status |
| --- | --- |
| DOCX/XLSX/PPTX → Readable Markdown | Supported as Public Beta; CLI default |
| `visible`, `complete`, `sanitized` content policies | Supported |
| Visual inference modes | `native-only`, `safe` (default), and `balanced`; unresolved relations remain visible through fallback/diagnostics |
| PDF → Markdown/OCR | Desktop GUI: supported; native text is extracted by default and OCR needs configured providers. CLI: experimental; explicit opt-in required |
| Markdown editing → restore to Office | Experimental; explicit opt-in required |
| New PDF/Office generation | Experimental; explicit opt-in required |
| CLI `render --format html` | Experimental; explicit opt-in required |

Readable output supports headings, paragraphs, nested lists, merged tables with blank continuation cells, images/OCR, code, emphasis, hard breaks, spreadsheet formula-cache markers, semantic projection or fallback for supported visuals, and normalized PPTX bullets.

## Visual and flow semantics

Legend: ○ = supported, △ = conditional or partial, × = unsupported as a semantic structure. This table describes **projection to Readable Markdown**, not round-trip restoration to original Office drawing objects.

| Format | Native shape text | Connector topology | Geometry inference | Edge label | SmartArt/diagram | Vector/image fallback | Diagnostic when incomplete | Support level |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| DOCX | ○ DrawingML/VML text boxes | △ native endpoints, or uniquely resolvable geometry, within supported drawing fragments | △ only unique endpoints | △ only a unique nearby label | △ preserves extracted text; complete topology is not guaranteed | △ embedded images are retained as assets | ○ unresolved/partial topology is diagnosed; source text remains when no valid graph is rendered | Public Beta, conditional |
| XLSX | ○ general shapes and standard `flowChart*` presets | ○ connected connectors | △ cell-layout/geometry projection | △ when existing projection can resolve it | × | △ embedded images are retained as assets | △ unknown `flowChart*` presets keep their labels but are not treated as confirmed connections | Public Beta |
| PPTX | ○ process/decision/terminator/data/generic | ○ native connections | △ only uniquely resolved endpoints, including diagonal connectors | △ when uniquely associated along the connector segment | △ preserves text and reports missing topology | △ embedded images are retained as assets | ○ reports unresolved connectors/labels and partial projection | Public Beta, conditional |
| PDF | ○ native text | △ simple painted vector paths with uniquely matched endpoints | △ only unique vector endpoints; regular table grids are excluded from connector inference | △ only a unique nearby label | △ valid simple topology projects to Mermaid; arrow direction is retained and unknown direction is diagnosed | △ page preview when a rasterizer is available; path/page placeholder otherwise | ○ reports vector/path partiality, unknown direction, unresolved endpoints, and OCR/rasterizer gaps | Public Beta, conditional |

Recognized visual content follows this order:

1. Mermaid when connections are clear and consistent;
2. a safely generated visual fallback such as an image or page preview;
3. an explicit diagnostic when neither projection nor fallback is possible.

Connections explicitly stored in the source remain distinguishable from estimated connections. `native-only` accepts only explicit source-format connections; `safe` accepts only unique high-confidence estimates; `balanced` may include additional estimates. Ambiguous, contradictory, or duplicate relations remain unresolved. When export emits a Warning, the CLI returns exit code 1. Review diagnostics, assets, and the source document as well as the Markdown.

## Content policy and other boundaries

The safe default is `visible`. `complete` includes hidden and metadata content with a warning. `sanitized` applies stronger privacy filtering. OCR content follows the same visibility policy as its source image.

DocRedock does not fully reconstruct DOCX drawing or PDF vector topology. It conditionally projects only supported, uniquely resolvable connector/path cases; all other cases retain source text/path fallback and diagnostics. With a rasterizer it prefers a preview for diagram-like PDF pages; an image-only page still leaves a page placeholder and Warning when rasterization/OCR is unavailable. Experimental PDF rendering does not bundle a Japanese font. ASCII uses Base14 Helvetica; non-ASCII requires an embeddable installed or explicitly selected TrueType font with complete glyph coverage.

Readable Markdown is one-way output. `.drmd` and `.drmdpkg` are experimental and may contain source-derived information. Keep the source document as the authoritative copy.

This document is the canonical public-support statement. See the [v0.2.2 release notes](../../release-docs/RELEASE_NOTES_v0.2.2.en.md), [User guide](user-guide.md), [Experimental features](experimental-features.md), and [Security and privacy](security-and-privacy.md).

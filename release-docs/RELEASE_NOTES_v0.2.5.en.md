# DocRedock v0.2.5 Public Beta Release Notes

Release date: 2026-09-07

v0.2.5 improves input-file protection and content preservation when converting Word, Excel, PowerPoint, and PDF documents to Markdown. Nested Word tables now also preserve the order of surrounding text, line breaks within paragraphs, inner tables, and following text.

## Fixes

- **Protect original files**: Conversion rejects an output that refers to the input file, even with `--force`. This protection applies to Word, Excel, PowerPoint, and PDF.
- **Hidden Word text**: Hidden settings inherited from character, paragraph, and default styles are respected by `visible` and `sanitized`.
- **Hidden PowerPoint groups**: A parent group's hidden setting applies to its children, including text in nested groups.
- **Word content controls**: Text inside block-level content controls is extracted in its original order with surrounding content.
- **Word equations**: Native equations are preserved as linear text. For example, E=mc² becomes `E=mc^2`. A `DocxMathLinearized` warning explains that the original mathematical layout is not reproduced.
- **Images in mixed PDF pages**: When OCR and a rasterizer are available, pages containing both ordinary text and images retain cropped image regions and recognized image text. If an image cannot be extracted, a positioned placeholder and a warning remain.
- **Markdown punctuation**: Literal emphasis markers, HTML tags, backticks, and other syntax characters are escaped so they do not accidentally change the interpretation of later text or tables. This also applies to Markdown used for round-trip editing.
- **Excel number formats**: Zero padding, scaled values, scientific and engineering notation, fractions, and conditional format sections are handled more accurately. For example, 123 formatted as `000000` becomes `000123`, and 1234567 formatted as `#,##0.0,,` becomes `1.2`.
- **Word and PowerPoint tables**: Paragraph boundaries and line breaks are preserved within cells. Nested Word tables are expanded inside their parent cell in order, preserving surrounding text and line breaks within paragraphs. Merged PowerPoint cells also retain paragraph separators.
- **PowerPoint master text**: Visible text from slide masters and layouts is included, with placeholder duplication and visibility settings taken into account.
- **PDF text preservation**: Headings and body text outside a diagram remain even if they match a diagram node label, such as `START`.
- **PDF columns**: Reading order is improved when columns use different line spacing, and footers no longer appear before the right-column body.
- **Multiple PDF tables**: Borders from separate tables no longer produce warnings about diagram connections that do not exist.

## Experimental round-trip editing

Handling of Word content controls, paragraphs containing equations, and body text boxes has been improved. Unchanged linearized equations retain their original equation markup. Editing an equation or changing content-control boundaries produces warnings about changes to representation or structure.

Round-trip editing remains experimental. Read the [experimental features guide](../docs/en/experimental-features.md) and compare the output with the original document.

## Updating

1. Close DocRedock and save any documents you are editing.
2. Download the v0.2.5 archive for your operating system and CPU, verify it against `SHA256SUMS`, and extract it into a new folder.
3. Follow the included `QUICKSTART.en.md`. Use the CLI `doctor` command to check the environment.

GUI and CLI packages are available for Windows, macOS, and Linux on x64 and ARM64.

## Limitations

- This is a Public Beta. See [supported features](../docs/en/supported-features.md) and the [user guide](../docs/en/user-guide.md) for the supported scope.
- Readable Markdown is a one-way output. Keep the original document.
- Linearized equations and expanded nested tables preserve content without reproducing the original mathematical typography or table layout.
- PDF image processing requires OCR and a rasterizer. Rotated images and complex layouts may not be handled; review any warnings.
- PDF-to-Markdown conversion is available in the GUI. CLI PDF conversion and new-document generation are experimental and require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. See [supported features](../docs/en/supported-features.md) for details.
- Check `SIGNING-STATUS.json` at the root of each package for its signing status.

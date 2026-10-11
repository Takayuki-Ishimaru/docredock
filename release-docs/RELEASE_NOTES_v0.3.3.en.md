# DocRedock v0.3.3 Public Beta

[日本語](RELEASE_NOTES_v0.3.3.md)

v0.3.3 improves underlines and strikethroughs in Word, Excel, PowerPoint and PDF, PDF text and reading order, and AI-package reading. Word revisions appear in readable output, and automated pipelines can detect conversions that need review before passing them to an AI tool.

## Underlines, strikethroughs and display

- Preserved underline and strikethrough in Excel cells and rich text, Word and PowerPoint tables, and Word text boxes, headers, footers, footnotes and endnotes. Word decorations inherited from character/paragraph styles and document defaults, and direct formatting that clears them, are respected.
- Recognized lines crossing or underlining text as decorations in supported cases, including PDF strikethrough and underline annotations. Arrows, connectors and table borders remain distinct.
- Underlines use `<u>…</u>` and strikethrough uses `~~…~~`. Combined bold, italic and code formatting also displays in the GUI preview. Literal Markdown symbols and HTML-like source text remain literal.
- Fixed missing content in Word nested tables, lost text or spaces in Excel rich text and at PowerPoint formatting changes, and PowerPoint table tab-stop settings becoming extra tab characters.

Underlines identifying headings or links are not exported as emphasis. When a line crosses only part of the text and the affected characters cannot be determined, strikethrough is not guessed and source comparison is requested. Partial PDF underlines may be omitted when glyph widths are unavailable. HTML underline and insertion marks need a Markdown viewer that displays HTML.

## Word revisions and Excel conditional formatting

Readable Markdown and AI packages show Word deletions/move sources as `~~strikethrough~~` and insertions/move destinations as `<ins>additions</ins>`. Inserted/deleted rows and cells are marked too. Formatting, table structure, paragraph and section changes carry previous/current notes.

**Review before sharing:** Word body revisions appear even with the default `visible` policy or `sanitized`, and may include deleted text. To exclude revisions, finalize the changes in a copy of the source before conversion. Explicitly hidden text still follows the content policy. Historical page layout is not reconstructed. Experimental round-trip editing uses the finalized-body view and restricts edits through revision markup.

Excel conditional strikethrough evaluates supported arithmetic, logical, aggregate, text and date functions, saved cross-sheet values and defined names. Cell formulas are not recalculated. `TODAY` and `NOW` use the device clock at conversion start. Unsupported references/functions, missing saved values and other unresolved conditions retain ordinary formatting and produce a diagnostic.

## PDF text and reading order

- Used glyph widths and text spacing to improve split words at formatting changes, missing word gaps, and superscripts/subscripts appearing on separate lines. Standard fonts with omitted width tables are also supported.
- Fixed producer rounding disrupting heading and table-header character order, and text lost or garbled by string escapes and character encodings.
- Read side-by-side boxes one box at a time and join supported wrapped paragraphs. Tables with dashed rules can also be reconstructed.
- Used applicable Adobe-Japan1 mappings, embedded Encoding CMaps and TrueType character maps to recover text when Unicode mappings are omitted. Ambiguous characters remain warnings and source-review items rather than guesses.

Unruled table boundaries and complex layouts remain inferred. Fonts lacking usable widths or character mappings, rotated text and unsupported encodings still have limitations. No font programs are bundled.

## AI packages and automation

When large Excel tables are split into row blocks, short titles, unit notes and conditions directly above the table are repeated before later blocks' headers. Independent table headings, row associations and document titles across parts are also improved. `--table-row-blocks` remains optional and off by default. Part size is a soft target; a long row may exceed it. `estimated_tokens` is an estimate, not a guarantee of actual token counts or model limits.

**Compatibility:** Manifest schema changes from `2.1` to `2.2`, adding `table_block.context_range`. `report.json` remains schema `1.1`. The API and Python reader example accept manifest `1.x` and `2.x` and reject unsupported major versions. Integration tools should ignore unknown additive fields.

The readers check the manifest, source correspondence and table-block chain. Verification checks file sizes, SHA-256 and unlisted files. Invalid paths, duplicate entries, ambiguous ZIP structures and links are refused. Verified part text, source cells and review state can be returned together; parts can also be searched by text. Previously accepted malformed packages may now fail. When verification fails, `--verify --part` prints no content. Matching hashes do not certify correct conversion or permission to share.

Added `export --fail-on-review`. When source-review pages, OCR items, Excel table boundaries or other review items are present, export saves the output and returns exit code `11`. Default exit codes are unchanged when this option is absent.

```sh
docredock export input.xlsx --ai-package zip --table-row-blocks --fail-on-review
```

For a received package, the Python reader example accepts `--verify --fail-on-review` and can return exit code `3` without printing the content when review is required. See the [user guide](../docs/en/user-guide.md#ai-packages) and [format contract](../docs/reference/ai-package.md).

## Limitations and distributions

This is a Public Beta, not a production-stable release. Readable Markdown and AI packages are one-way output. Keep the source as the authoritative copy and review the full text, images, diagnostics and `review.md` before sharing. No detected review items does not certify correct conversion. Generation runs locally without uploading documents.

Desktop GUI PDF input is available by default. CLI PDF conversion still requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. Round-trip editing, restoration and new PDF/Office generation remain experimental. `.drmd` and `.drmdpkg` may contain the original document and should receive the same confidentiality treatment. OCR and PDF review images may require configured providers or rasterizers. Complete reconstruction of diagrams, SmartArt and complex layouts is not guaranteed.

Distributions target Windows (x64/Arm64), macOS (Intel/Apple Silicon) and Linux (x64/Arm64). Check the GitHub Release for the package matching your OS/CPU and verify `SHA256SUMS`. Each package records signing/notarization status in `SIGNING-STATUS.json`; packages are not necessarily signed.

See [supported features](../docs/en/supported-features.md) and [security and privacy](../docs/en/security-and-privacy.md) for support boundaries and handling guidance.

# Quick guides by task

## Give a document to an AI

1. In the GUI, keep **Visible content only (recommended)** selected and add a DOCX, XLSX, PPTX or PDF.
2. Choose **AI package** when you want to trace answers back to the source.
3. After export, review any source-review pages, tracked changes and excluded hidden content.
4. Give the AI only the part it needs; keep the source index for local reference.

For Word and other native tables, Readable Markdown and AI packages repeat vertically merged values in each row by default. Horizontally merged values appear once. To keep the continuation cells blank in Readable Markdown, use `--no-expand-merged-cells` or clear the option in **Advanced settings**.

In the CLI, use `docredock export input.docx --ai-package dir --content-policy visible --ocr off`. See the [revision defaults](../reference/revision-defaults.md) before sharing Word documents with tracked changes.

## Set up AI assistants (Claude Code, GitHub Copilot, Codex)

Run `docredock agents install` to configure Claude Code, GitHub Copilot (VS Code and Copilot CLI), and Codex (CLI, IDE extension, and desktop app). The setup instructs these assistants to read Word, Excel, PowerPoint, and PDF files as follows.

1. Convert it with DocRedock first.
2. Read the relevant piece of the original only when the result needs checking or the Markdown lacks what they need.

In the desktop app, select **AI integration** (「AI連携」) to configure the same settings.

```sh
docredock agents install                 # all three
docredock agents install --client codex  # one of them
docredock agents status                  # what is set up
docredock agents uninstall               # remove it
```

The setup writes to the current user’s configuration folders.

| Assistant | MCP server | Instructions | Skill |
| --- | --- | --- | --- |
| Claude Code | `claude mcp add --scope user` (`~/.claude.json` when there is no `claude` command) | a marked block in `~/.claude/CLAUDE.md` | `~/.claude/skills/docredock-office/` |
| GitHub Copilot | `~/.copilot/mcp-config.json` | `~/.copilot/instructions/docredock.instructions.md` | `~/.copilot/skills/docredock-office/` |
| Codex | a marked block in `~/.codex/config.toml` | a marked block in `~/.codex/AGENTS.md` | `~/.agents/skills/docredock-office/` |

- `CLAUDE_CONFIG_DIR`, `COPILOT_HOME` and `CODEX_HOME` are honored.
- Before an existing file is changed, it is copied to `<file>.docredock.bak`, and what is already in it is kept.
- A `docredock` entry you wrote yourself is not changed. It is reported as a conflict (exit code 1).
- Restart the assistants after installing.
- If you move DocRedock to another folder, run `install` again to update the registered path.
- Before replacing DocRedock with a new version, quit the assistants. While an assistant runs `DocRedock.Cli` as its MCP server, Windows does not let you overwrite the file.

`--enforce` also adds a hook. When an assistant tries to open a Word, Excel or PowerPoint file directly (with its file-reading tool, or with commands such as `unzip` or `python`), the hook stops it and points it to DocRedock. PDFs are not stopped. In Codex, approve the hook once with `/hooks`.

`--auto-approve` skips approval prompts for DocRedock calls in supported clients. Unless you choose an output folder, exports go to a temporary folder. The option adds the following settings.

| Assistant | What it adds |
| --- | --- |
| Claude Code | `mcp__docredock__*` in `permissions.allow` of `~/.claude/settings.json` |
| Codex | `default_tools_approval_mode = "approve"` in `[mcp_servers.docredock]` of `config.toml` |
| GitHub Copilot | a `permissionRequest` hook in `~/.copilot/hooks/docredock-approve.json` |

This approval hook applies to Copilot CLI. Copilot in VS Code may still prompt for approval when it calls DocRedock.

You can remove the hook and the approval later with `install --no-enforce` and `install --no-auto-approve`. An `install` without these flags keeps the current state. In the desktop app, clear the corresponding checkbox and select 「設定する」 (Apply settings).

Other options:

- `--dry-run` shows the changes without writing them.
- `--json` prints the result as JSON (`docredock.agents.v1`).

VS Code’s legacy Local harness uses `.vscode/mcp.json` or the profile `mcp.json`. `install` does not write those files, so if you still use that harness, register the server as described in the next section.

## Use DocRedock from an MCP client

`docredock mcp` is an MCP server over standard input and output (JSON-RPC 2.0, one message per line, UTF-8). Register it in the client, for example:

```json
{ "mcpServers": { "docredock": { "command": "docredock", "args": ["mcp"] } } }
```

In Claude Code: `claude mcp add docredock -- docredock mcp`. The server offers seven tools. Each runs the same processing as the CLI on this computer and returns its `--json` results:

| Tool | What it does |
| --- | --- |
| `export_markdown` | Converts a document to Readable Markdown and returns the result with the Markdown (cut at `max_chars`, 40,000 by default; the note names the whole file). |
| `export_html` | Converts a document to Readable HTML (one self-contained `.html` file; same arguments as `export_markdown` except `max_chars`) and returns the result with the path of the written file. |
| `export_ai_package` | Writes an AI package and returns its folder as `package_path`. |
| `package_review` | Returns the package's review state and `review.md`. |
| `package_search` | Finds the parts that mention a text. |
| `package_read` | Returns one part. |
| `read_source` | Reads one piece of the original: a worksheet range, a slide, a PDF page, a node (pass its ID as `node`), a zip part or a picture. With only `path`, it lists what can be read, pictures included. Text comes back `max_chars` at a time (20,000 by default), from `offset`. |

A package that needs review returns no text from `package_search` or `package_read` until `allow_review` is passed, after a person has checked `package_review`.

To read a picture, pass its id as `image`. The id appears in the Markdown's picture line (for example `Image: Picture 2 (img-0002, not included in this text-only output)`) and in the links of OCR tables; PDF pictures have ids such as `pdf-img-0025`. The picture itself comes back as MCP image content (PNG, JPEG, GIF or WebP, up to 5 MB). With `ocr: true`, its text and positions are read by OCR as well. Automatic OCR during export skips images extracted directly from PDFs. To read text in one of those images, call `read_source` with its image ID and `ocr: true`.

`read_source` returns the original as stored. It can include hidden text, comments and deleted tracked changes that the Markdown leaves out. The CLI does the same with `docredock source <file> --sheet <name> --range A1:H40` (or `--slide N`, `--page N --allow-pdf`, `--node <id>`, `--part <name>`, `--image <id> [--ocr]`). With `--image`, the CLI saves the picture to `--output` (by default, to a temporary folder) and reports where.

The tools read the files the client names, with the rights of the user who started the server. Outputs go to a new folder under the system's temporary folder unless `output_dir` is given; existing files are replaced only with `overwrite`. PDFs need `allow_pdf`, as `--allow-pdf` in the CLI.

## Check privacy before sharing

`visible` excludes recognized hidden content. `sanitized` additionally removes metadata and derived content. `complete` includes hidden content and needs review before sharing. The default Word revision markup may include deleted text. To exclude it, use `--revisions final` or select 「最終版で書き出し直す」 (Export again as final) in the app. Check the result and review items in the app, or check the CLI diagnostics and `review.md`.

## Add an export to automation

Add `--fail-on-review` when downstream steps must stop until someone reviews the output; export exits 11 when review is required. The bundled Python reader also blocks `--part` and `--search` body output with exit 11 when review is required. After reading `review.md`, pass `--allow-review` to print the body, or use the legacy `--fail-on-review` option to keep its exit code 3.

The exit code, the warnings and the review flag answer different questions: exit 1 means a diagnostic that is not informational (for example `XlsxFormulaCachedValueMissing`, a formula without a stored result) or an empty output; `review_required` means something must be compared with the source (pages, OCR text, table boundaries, images missing from the Markdown) or revision history must be cleared for sharing. A warning does not always require review, and OCR review alone exits 0. With `--json`, read `needs_attention` and `attention` instead of combining them yourself: `attention` lists `warnings`, `source_review`, `revision_sharing`, `hidden_content`, `empty_output`, or `failed` for an input that could not be converted. A batch exits with its most serious result: an error, then 11, then 1.

```sh
docredock export input.xlsx --json --ocr off -o input.md | jq '.results[] | {input, needs_attention, attention}'
```

## Choose settings for the task

| Task | Settings |
| --- | --- |
| Share the final text with an AI | `--content-policy sanitized` (final view of Word revisions, no hidden content, comments, headers or footers) |
| Check revision history | `--revisions markup --allow-revision-sharing` and read `review.md` before sharing |
| Look up rows of a large table | `--ai-package dir --table-row-blocks --chunk-chars 2000`, then `package search` and `package read --part … --with-sources` |
| Read the document yourself | the default `readable` profile; round-trip markers are not needed |

Without `--table-row-blocks` a worksheet table is never cut, so a large sheet stays one part; the CLI and `review.md` say so when a part exceeds the target.

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
docredock export expenses.docx --no-expand-merged-cells --ocr off
```

`--allow-pdf` enables PDF conversion alone. Choose generated headings and notes with `--lang ja|en`. PDF comparison images use smaller previews than OCR input; use `--pdf-fallback-images off` to omit them and compare review items with the original instead.

For `.doc`, `.xls` and `.ppt`, keep the original and save a separate `.docx`, `.xlsx` or `.pptx` copy with Word, Excel, PowerPoint or LibreOffice before converting.

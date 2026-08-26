# DocRedock User Guide

[日本語](USER_GUIDE.md) | English

DocRedock is a desktop application and CLI for converting DOCX, XLSX, PPTX, and PDF files to Markdown locally and safely applying supported edits back to the source format.

## 1. Installation

Download the artifact matching your operating system and CPU from the official GitHub Releases page.

- Windows: win-x64 or win-arm64
- macOS: osx-x64 or osx-arm64
- Linux: linux-x64 or linux-arm64

For a public release, verify the download against the SHA-256 checksum on the release page. Self-contained builds do not require a separate .NET SDK installation.

The CLI executable is DocRedock.Cli.exe on Windows and DocRedock.Cli on macOS/Linux. The GUI executable is DocRedock.exe on Windows and DocRedock on macOS/Linux. Follow the package-specific launch instructions and check the signing status in the release notes.

## 2. GUI basics

### Create Markdown for reading

1. Start the GUI and select or drop a DOCX, XLSX, PPTX, or PDF file.
2. Enable Readable Markdown.
3. Choose an output location and run the conversion.
4. Review the diagnostics and generated Markdown.

This mode cannot be restored to the source document. Embedded images are normally written to a .assets directory beside the Markdown.

### Perform a round-trip edit

1. Disable Readable Markdown and export for round-trip editing.
2. Always keep the generated Markdown and its matching .drmd sidecar together.
3. Edit only permitted body content without changing DRMD control comments.
4. Run verify and diff to check integrity and the exact change set.
5. Use restore to write a new Office or PDF file.
6. Open the result in the target application and visually inspect the changed content and layout.

Removing a Markdown block is not a deletion. Deletion requires an explicit DRMD delete marker. See ../docs/DRMD_AI_EDITING_RULES.md for the full editing contract.

## 3. CLI basics

The examples below use macOS/Linux syntax. On Windows, replace ./DocRedock.Cli with .\DocRedock.Cli.exe.

### Choose an export profile

The CLI export command requires a --profile selected for the intended workflow.

| Goal | Profile | Restorable | Output |
| --- | --- | --- | --- |
| Read, search, summarize, or share | readable | No | Markdown and, when needed, a .assets directory |
| Edit an existing Office document and restore it | roundtrip | Yes, within the supported scope | Markdown plus an adjacent .drmd sidecar |
| Retain additional audit information | audit | Yes, within the supported scope | Markdown, a sidecar, and additional diagnostics |

Readable Markdown is a one-way format. If you might need to restore the document later, specify roundtrip.

### Readable Markdown

```sh
./DocRedock.Cli export input.xlsx --profile readable --output input.md --ocr off
```

Add --embed-images to place verified images directly in the Markdown. Embedded images and OCR text become part of the file being shared, so review them for sensitive information.

### Round-trip editing

```sh
./DocRedock.Cli export input.docx --profile roundtrip --output input.md --ocr auto
# Edit input.md
./DocRedock.Cli verify input.md
./DocRedock.Cli diff input.md
./DocRedock.Cli restore input.md --output restored.docx --strict
```

DocRedock does not overwrite existing output by default. Use --force only when replacement is intentional.

### Transport a sidecar

```sh
./DocRedock.Cli pack input.md --output input.drmdpkg
./DocRedock.Cli verify input.drmdpkg
```

A .drmdpkg contains the Markdown and the original-document data needed for restoration. Handle it with the same confidentiality level as the source document.

### Render a new document

```sh
./DocRedock.Cli render input.md --format pdf --output rendered.pdf
./DocRedock.Cli render input.md --format docx --template template.docx --output rendered.docx
```

Use render to create a new document and restore to revise an existing source document.

## 4. OCR and Mermaid

- OCR is optional. DocRedock prefers Apple Vision on macOS and Windows.Media.Ocr on Windows, with an optional local Tesseract fallback.
- Tesseract and its language models are not bundled.
- OCR for scanned PDF pages requires a separately supplied PDF rasterizer implementation.
- Mermaid rendering requires a local mmdc executable explicitly selected by the user. DocRedock does not download Mermaid at runtime.

## 5. Main limitations

- DOCX: supported paragraphs, headings, lists, same-shape table cells, and a limited rich-text subset can be edited.
- XLSX: editing is centered on existing cell values and formulas. Structural changes to rows, columns, sheets, merges, or styles are unsupported.
- PPTX: editing is centered on existing shape text. Notes, tables, images, and shape insertion or movement are unsupported.
- PDF: extraction is conservative. Restoring an edited PDF uses an explicit render fallback and does not guarantee the original layout.
- Macros, signatures, encryption, protection, and unsafe or unsupported package structures may be rejected.

The capability matrix at ../docs/FORMAT_CAPABILITY_MATRIX.md and the diagnostics produced by each operation are authoritative.

## 6. Troubleshooting

1. Preserve the original document and sidecar without modification.
2. Record the verify and diff output, DocRedock version, OS/CPU, and exact command.
3. Do not attach confidential documents to a public issue; replace them with a minimal synthetic reproducer.
4. Follow SECURITY_AND_PRIVACY.en.md for security-related reports.
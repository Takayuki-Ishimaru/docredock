# Experimental Features

[日本語](../ja/experimental-features.md) | English

> These command-line workflows are unsupported experiments in v0.1.5. They are disabled unless you explicitly opt in; the desktop GUI accepts PDF input by default.

Set the environment variable before starting the CLI:

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

PowerShell:

```powershell
$env:DOCREDOCK_ENABLE_EXPERIMENTAL = "1"
```

The environment gate applies to experimental CLI workflows, including CLI PDF export (conversion), restoration, and rendering. Read-only CLI inspection, including `docredock inspect <file.pdf>`, remains available without the flag. Public library APIs are engineering surfaces and do not enforce this entry-point gate. Readable DOCX/XLSX/PPTX export remains available without the flag. The GUI accepts DOCX, XLSX, PPTX, and PDF input by default; PDF OCR still requires a configured rasterizer and OCR provider. `.drmd` and `.drmdpkg` can contain restoration data equivalent in sensitivity to the original document, so handle them with the same confidentiality controls.

## PDF input and OCR

PDF extraction preserves native text page partitions. Textless-page OCR requires an explicitly configured rasterizer and OCR provider; v0.1.5 does not bundle a PDF rasterizer. If unavailable, DocRedock emits `PdfRasterizerUnavailable` instead of implying that OCR ran.

## PDF rendering and fonts

DocRedock does not bundle or download a Japanese font. ASCII-only output uses Base14 Helvetica. Non-ASCII output resolves `--font-path`/`--font-face-index`, then `DOCREDOCK_PDF_FONT_PATH`/`DOCREDOCK_PDF_FONT_FACE_INDEX`, then installed system fonts. Only embeddable TrueType outlines with complete glyph coverage are accepted.

Font selection and coverage are informational. Omissions and truncation are warnings; CLI render returns 1 when warnings exist. `--quiet` hides information, while `--verbose` includes the selected font path.

Experimental artifacts such as `.drmd` and `.drmdpkg` may contain the original source or restoration data. They require the same confidentiality controls as the source. F0/F1 tests and package smoke checks are engineering evidence, not a promise of layout-preserving user support.

Use [Supported features](supported-features.md) for the release contract and [Security and privacy](security-and-privacy.md) for handling guidance.

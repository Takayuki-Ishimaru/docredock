# AI package samples for manifest compatibility

Real AI packages written by DocRedock itself, one per manifest schema, so that readers are tested against what released versions actually produced rather than hand-written JSON. They are read by `tests/DocRedock.Tests/Api/AiPackageManifestReaderTests.cs` (the .NET `AiPackageManifestReader`) and `tools/test_ai_package_reader_example.py` (the Python example `docs/examples/ai_package_reader.py`). Every file is listed with its size and SHA-256 in the package's own `manifest.json`; do not edit them.

| Folder | Manifest schema | Written by | Source |
| --- | --- | --- | --- |
| `schema-1.0` | `1.0` | DocRedock v0.3.0 | `example.docx`, a synthetic two-page description of the AI package itself (the v0.3.0 demo package) |
| `schema-2.0` | `2.0` | DocRedock v0.3.1 release binary | `tests/DocRedock.Tests/Fixtures/Evaluation/V030/chunking.docx`, `--ai-package dir --chunk-chars 2000` |
| `schema-2.1` | `2.1` | current development build | `tests/DocRedock.Tests/Fixtures/Evaluation/V030/workbook_base.xlsx`, `--ai-package dir --chunk-chars 128 --table-row-blocks` (two tables cut into row blocks) |

The `schema-2.1` manifest says `generator_version: "0.3.1"` because it was written by a development build before the version number was raised; readers must dispatch on `schema_version`, never on `generator_version`.

All text is generic synthetic content covered by the repository MIT license (`LICENSE`). The packages contain no original documents, author properties or absolute paths.

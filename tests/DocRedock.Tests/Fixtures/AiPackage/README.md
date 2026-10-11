# AI package samples for manifest compatibility

Real AI packages written by DocRedock itself, one per manifest schema, so that readers are tested against what released versions actually produced rather than hand-written JSON. They are read by `tests/DocRedock.Tests/Api/AiPackageManifestReaderTests.cs` (the .NET `AiPackageManifestReader`) and `tools/test_ai_package_reader_example.py` (the Python example `docs/examples/ai_package_reader.py`). Every file is listed with its size and SHA-256 in the package's own `manifest.json`; do not edit them.

| Folder | Manifest schema | Written by | Source |
| --- | --- | --- | --- |
| `schema-1.0` | `1.0` | DocRedock v0.3.0 | `example.docx`, a synthetic two-page description of the AI package itself (the v0.3.0 demo package) |
| `schema-2.0` | `2.0` | DocRedock v0.3.1 release binary | `tests/DocRedock.Tests/Fixtures/Evaluation/V030/chunking.docx`, `--ai-package dir --chunk-chars 2000` |
| `schema-2.1` | `2.1` | development build before v0.3.2 | `tests/DocRedock.Tests/Fixtures/Evaluation/V030/workbook_base.xlsx`, `--ai-package dir --chunk-chars 128 --table-row-blocks` (two tables cut into row blocks) |
| `schema-2.2` | `2.2` | development build after v0.3.2 | `tests/DocRedock.Tests/Fixtures/Evaluation/V032/captioned_sales.xlsx`, `--ai-package dir --chunk-chars 1000 --table-row-blocks` (one table cut into three row blocks; blocks 2 and 3 repeat the unit and condition notes above the header, `context_range` A3:A4) |

The `schema-2.1` manifest says `generator_version: "0.3.1"` and the `schema-2.2` manifest `"0.3.2"` because each was written by a development build before the version number was raised; readers must dispatch on `schema_version`, never on `generator_version`.

`reader-cases.json` is not a package: it lists packages derived from these samples (a changed part, a missing field, a broken table-block chain, an unlisted file, …), each with the result both readers must give for `--verify`. `tools/test_ai_package_reader_cases.py` (Python) and `tests/DocRedock.Tests/Api/AiPackageReaderCaseTests.cs` (.NET, which also runs the Python reader when it is installed) apply it; add a case there when a reader rule changes.

All text is generic synthetic content covered by the repository MIT license (`LICENSE`). The packages contain no original documents, author properties or absolute paths.

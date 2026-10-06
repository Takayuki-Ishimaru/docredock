# Synthetic regression fixtures

These minimal inputs are generated independently by `tools/generate_v032_regression_fixtures.py` for the v0.3.2 regression tests. They reproduce single-blank-column table boundaries, where one empty column must keep one table together or separate two tables depending on the surrounding cells, and declared Excel tables (ListObjects) that a converter must read. The report-provided binaries are not redistributed. All text is generic synthetic content.

Generator and generated content are covered by the repository MIT license (`LICENSE`). Office packages have no author properties, external relationships or templates.

Regenerate from the repository root using Python 3 (standard library only):

```sh
python3 tools/generate_v032_regression_fixtures.py --output tests/DocRedock.Tests/Fixtures/Evaluation/V031
python3 -m unittest discover -s tools -p test_generate_v032_regression_fixtures.py
```

ZIP timestamps are fixed; repeated generation produces identical bytes. The regression tests consume these inputs directly from the source tree.

| File | SHA-256 |
|---|---|
| excel_table_with_side_list.xlsx | f1cc838923c0e1fe186c593c0a61019215448789ac696f46c8ae0fb980188a26 |
| excel_tables_side_by_side.xlsx | 1d6870bbffa13df9d1d625aff47da4624c5c371f375fd0f23034c14c88e63541 |
| independent_one_column.xlsx | 15864ccda07474bc466cbbf8db59e187caa1eeb07f5c916cacfc867be2922763 |
| independent_two_columns.xlsx | 1fa6b1f557b6bf80258c7db18a37430ce7db1bbdbd1337282f6760d2b2ffda59 |
| row_labels_spacer.xlsx | d6cdc2a098dab3fe90fbb4bc5aa94d8569a19ba21c9f555f6635cda140b43f81 |
| unified_numeric_id.xlsx | 2337a07af607f2679e9b60c461cb15cf26077d1fd613084a5e42240105bdec73 |
| unified_text_labels.xlsx | 51f989b48ba6ad011b02278a5c99907b3f9e95a08b658067cf3e43a13b56c81c |

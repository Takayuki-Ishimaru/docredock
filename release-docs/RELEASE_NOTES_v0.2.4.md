# DocRedock v0.2.4 Public Beta リリースノート

公開日: 2026-09-06

v0.2.4は、PDFのテキスト保持と読み順、XLSXのシート選択、環境診断、出力サマリーの信頼性を改善する品質更新です。対応機能の範囲に変更はありません。

## 変更内容

- PDF: 表を再構成したページでも、表の外にある本文を保持するよう修正しました。表セルと本文を混在させず、説明できないテキスト断片は本文に残して `PdfNativeTextUnaccounted` 警告を出します。
- PDF: 複数の罫線が1つの描画パスにまとめられた表も、罫線が個別に描かれた表と同様に再構成できるようにしました。
- PDF: 二段組のページで左右の段が行ごとに混ざる問題を改善しました。継続する段間の余白を確認できる場合は、左の段を上から下まで出力してから右の段を出力します。1行だけの広い空白は段として扱いません。
- XLSX: `--sheets` に存在しないシート名を指定した場合、終了コード2で失敗し、出力ファイルを作りません。エラーには利用可能なシート名（非表示シートは `(hidden)` 付き）を表示します。一部の名前だけが存在しない場合も暗黙に無視せず失敗します。空の指定（`--sheets ""`）も終了コード2です。存在するが非表示で、content policy により除外されるシートを指定した場合は `XlsxSheetExcludedByPolicy` 警告を出し、終了コード1になります。
- doctor: テキスト出力と `--json` が常に同じ終了コードを返します。各capabilityに `required` / `optional` のtierを付け、既定では必須capabilityがすべてreadyなら0、そうでなければ1です。`--strict` を付けると任意のcapabilityの不足でも1になりますが、ready な代替手段で満たされている不足（`satisfied_by`）は除きます。Tesseractがreadyの環境では `ocr-native` の対処が「Tesseractを導入」ではなく「OCRはtesseractで提供済み」になります。JSONには `tier`、`satisfied_by`、`strict`、`exit_code`、`summary` を追加しました（`schema_version` は1のまま）。
- 出力サマリー: `Visual summary`、`Export completed`、GUIの図サマリーで同じ確定値を表示するようにしました。`diagrams=` は再構成済みの図の数を表し、ベクター内容を持つページ数は新設の `vector_pages=` に分離しました。解決済みの矢印だけを含むPDFで出ていた誤警告をなくし、残る警告には対象ページと未解決件数を明記します。

## 互換性に関する注意

- 既定の `docredock doctor` は、OCR・rasterizer・mermaidなど任意ツールの不足だけでは終了コード1を返しません。従来の厳密な判定が必要な自動化は `docredock doctor --strict` に切り替えてください。
- `Visual summary` 行に `vector_pages=` が追加され、`diagrams=` の意味が「再構成済みの図の数」に変わりました。
- ライブラリ利用者向け: `PdfTableCell.TextRegionIndexes` は `SourceTextIds` に、`ExportSummary.Diagrams` は `DiagramsReconstructed` に名称を変更しました。`PdfTextRegion.ReadingOrder` は最終的な読み順の連番になります。

## 更新方法

1. 元の文書と必要な設定を保存し、起動中のDocRedockを終了します。
2. このリリースからOS／CPUに合うパッケージを取得し、`SHA256SUMS`で確認して別のフォルダーへ展開します。
3. 同梱の`QUICKSTART.ja.md`に従って起動します。外部ツールの状態は`docredock doctor`で確認できます。

## 対応範囲と制約

- Public Betaです。対応範囲の正本は[対応機能](../docs/ja/supported-features.md)、操作手順は[利用ガイド](../docs/ja/user-guide.md)を参照してください。
- 二段組の検出は保守的です。左右で行の高さがそろわない段組（各段が独立に折り返す場合）は従来どおり行順で出力されることがあります。

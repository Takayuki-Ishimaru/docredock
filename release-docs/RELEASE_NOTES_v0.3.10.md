# DocRedock v0.3.10 Public Beta

[English](RELEASE_NOTES_v0.3.10.en.md)

v0.3.10では、PDFの読み順と原本確認、Word・Excelの表やグラフの出力、複数文書のCLI変換を改善しました。AI向けパッケージを共有する前の確認も分かりやすくしました。

## PDFの読みやすさと原本確認

- 段組みの本文と表の順序、見出し、単語間の空白、表の直後の本文の扱いを改善しました。繰り返しのヘッダー・フッターやページ番号を識別し、`sanitized`では除外します。
- 解決できない図の細かな線の一覧はレポートにまとめ、本文には原本の参照案内を表示します。Markdownだけで図の意味を確認できない場合は、原本と照合してください。
- 段組みなど確認が必要なページには、小さな照合用プレビューを添付します。照合画像からの重複OCRを避け、文字のないページやOCRを利用できない場合の確認案内を改善しました。
- PDFの画像化で複数ページを並行処理し、タイムアウト・キャンセル・一部ページの失敗時の終了処理を改善しました。

## Officeの表・グラフとAI向けパッケージ

- Wordの日本語などのスタイル名や継承された設定から、リストとコード段落を認識する処理を改善しました。
- AI向けパッケージでは、表の縦結合セルの値を各行に繰り返して表示します。全文と分割本文の両方に適用されます。単独の閲覧用Markdownでも、CLIの`--expand-merged-cells`またはGUIの詳細設定で有効にできます。
- Excelの文書タイトルとシート内の見出し・単位を分けて扱い、グラフの複数系列をカテゴリ名で揃えた表として出力します。時間の推移と判断できないカテゴリに増減の説明を付けないようにしました。
- `complete`で含めた非表示内容の表示と、パッケージ本文を検索するときの出典メタデータの扱いを改善しました。

## CLIとGUIの操作

CLIで複数ファイルやフォルダー直下の文書をまとめて変換できます。`--output-dir`で出力先を指定し、`--json`・`--quiet`・`--verbose`で結果の表示を選べます。単一ファイルの閲覧用Markdownは`-o -`で標準出力へ送れます。処理結果の要約は標準エラーへ出力します。

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export input.docx -o - --ocr off > input.md
docredock export report.pdf --allow-pdf --lang en --fail-on-review
```

`--allow-pdf`で、その実行のPDF変換を有効にできます。従来の`DOCREDOCK_ENABLE_EXPERIMENTAL=1`も利用できます。生成する見出しや注記の言語は`--lang ja|en`で指定できます。原本や他の入力文書と出力先が衝突する場合は拒否し、`--force`でもシンボリックリンクやデバイスなどの特殊ファイルは置き換えません。

GUIでは日本語の操作案内、OCRの利用可否、原本確認の表示を改善しました。起動時に渡された対応ファイルを選択でき、Linuxではインストール後にファイルマネージャーの「アプリケーションで開く」から文書を開けます。

## 互換性と共有前の確認

manifestはschema `2.2`、`report.json`はschema `1.2`のままです。schema `1.0`／`2.0`／`2.1`の既存パッケージも引き続き読み取れます。既存パッケージの本文や保存済みの概算トークン数は、読み取り時に書き換えません。

同梱Pythonリーダーは、確認が必要なパッケージの`--part`／`--search`による本文表示を既定で止め、終了コード11を返すようになりました。`review.md`を確認したうえで表示する場合は`--allow-review`を指定してください。従来の`--fail-on-review`は終了コード3を維持し、`--allow-review`より優先します。自動処理ではこの終了コードの変更を確認してください。CLIの書き出しは`--fail-on-review`を指定すると、確認事項がある場合に出力を保存したうえで終了コード11を返します。

Word変更履歴の既定値は従来どおり、CLI・GUI・APIともに`visible`／`complete`で`markup`、`sanitized`で`final`です。履歴付き出力には削除した文字などが含まれる場合があります。[変更履歴の既定値](../docs/reference/revision-defaults.md)と`review.md`を共有前に確認してください。

Public Betaです。閲覧用MarkdownとAI向けパッケージは一方向の出力です。原本を保持し、本文・画像・診断を確認してください。図・SmartArt・複雑なレイアウトの完全再現や、確認事項がない場合の変換の正しさは保証しません。PDFの照合画像・OCRには利用環境に応じたrasterizerやOCR providerが必要です。デスクトップGUIのPDF入力は既定で利用できます。往復編集・復元・PDF／Officeの新規生成は引き続き実験機能で、CLIでは`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。`.doc`／`.xls`／`.ppt`は、原本を残して対応する新しいOffice形式へ別名保存してから変換してください。

## 配布物と更新方法

GitHub ReleaseでOS／CPUに合うアーカイブを選んでください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.10-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.10-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.10-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.10-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.10-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.10-linux-arm64.tar.gz` |

`SHA256SUMS`で照合し、展開したフォルダーの`QUICKSTART.ja.md`を参照してください。別途.NET SDKは不要です。署名・notarizationの適用状況は各パッケージの`SIGNING-STATUS.json`を確認してください。既存の文書と出力を保管し、アプリを終了して新しいパッケージへ置き換えてください。

詳しくは[目的別クイックガイド](../docs/ja/quick-tasks.md)、[利用ガイド](../docs/ja/user-guide.md)、[対応機能](../docs/ja/supported-features.md)、[形式仕様](../docs/reference/ai-package.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

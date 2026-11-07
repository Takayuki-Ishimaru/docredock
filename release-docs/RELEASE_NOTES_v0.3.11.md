# DocRedock v0.3.11 Public Beta

[English](RELEASE_NOTES_v0.3.11.en.md)

v0.3.11では、PDFの本文と見出しの読み取り、Office文書の画像と表の出力、AI向けパッケージの分割と検索を改善しました。MCPクライアントから文書を変換し、AI向けパッケージを確認できるようになりました。

## PDFとOffice文書の変換

- PDFでは、段やページをまたぐ段落をつなげ、本文と同じ文字サイズでも書体や太字で区別された見出しを認識しやすくしました。日本語と数字の間の不要な空白を減らし、ページ番号だけが異なる繰り返しのヘッダー・フッターをまとめます。区切り線や変更バーを図の接続線と誤認するケースも減らしました。タグ付きPDFでは、利用可能な図の代替テキストを使用します。
- Wordでは、変更履歴の削除箇所を取り消し線付きの`<del>`で表し、`complete`ではコメントを参照先の近くに表示します。画像だけの段落や改ページを含むWord文書でも、見出しに沿ってAI向けパッケージを分割しやすくしました。
- Excelでは、大きなシートの変換に必要なメモリを減らしました。グラフの値にブックの数値書式を使い、`complete`ではセルのメモやコメントを含め、非表示の行・列・シートを示します。
- PowerPointでは、一部の接続線の近くにあるラベルを図に反映します。ExcelとPowerPointでは説明や名前が空の画像にも既定のラベルを付けます。出力対象の画像がMarkdownから参照されない場合は、`ImageNotWritten`で原本との照合を案内します。
- 閲覧用Markdownでは、縦に結合した表セルの値を既定で各行に表示します。下の行を空欄にしたい場合は、CLIで`--no-expand-merged-cells`を指定するか、GUIの詳細設定で無効にしてください。

## AI向けパッケージとMCP

パッケージの検索が空白や改行をまたぐ語句に対応し、結果に該当箇所の見出しを表示します。分割サイズの目安を超えた箇所は`review.md`で理由を確認できます。大きなExcelの表を行単位のまとまりに分けたい場合は`--table-row-blocks`を指定してください。GUIでは形式に応じた案内、結果のコピー、最終版としての再出力を利用できます。

MCPクライアントに`docredock mcp`を標準入出力のサーバーとして登録すると、ローカル文書のMarkdown変換、AI向けパッケージの作成、確認・検索・読み取りができます。確認が必要なパッケージの本文を取得する場合は、確認内容を読んだうえで`allow_review`を指定します。サーバーは起動したユーザーの権限で、クライアントが指定するファイルを読み取ります。[MCPの設定手順](../docs/ja/quick-tasks.md#mcpクライアントから使う)を参照してください。

## CLIと互換性

- `export --json`は`docredock.export.v2`の`results`配列に、ファイルごとの結果を出力します。自動処理では`needs_attention`と`attention`で確認事項を判別できます。複数ファイルの変換では、失敗、`--fail-on-review`による終了コード11、警告などの終了コード1の順に優先されます。
- `--images files|embed|omit`で画像の出力方法を選べます。閲覧用Markdownを`-o -`で標準出力に送るときは、既定で画像の説明を出力します。画像を埋め込む場合は`--images embed`を指定します。拡張子だけが異なる入力には`--keep-source-extension`を使うと、`report.pdf.md`のように出力名を区別できます。
- AI向けパッケージのmanifestはschema `2.2`、`report.json`はschema `1.2`です。既存のmanifest schema `1.0`、`2.0`、`2.1`も読み取れます。Word変更履歴の既定値は`visible`・`complete`で`markup`、`sanitized`で`final`です。共有前に`review.md`と[変更履歴の既定値](../docs/reference/revision-defaults.md)を確認してください。
- 実験機能の往復編集では、`verify`が通常の編集を検出した場合に終了コード0を返します。Excelでは、編集が数式の結果に影響しうるとき、`preflight`と`restore`が該当する数式セルを示します。計算結果はExcelで開いて再計算されます。v0.3.10で書き出した表の範囲が重なるMarkdownも、セルの所属を特定できれば編集できます。

## 配布物と更新方法

GitHub ReleasesでOSとCPUに合うアーカイブを選び、`SHA256SUMS`で照合してください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.11-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.11-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.11-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.11-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.11-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.11-linux-arm64.tar.gz` |

展開したフォルダーの`QUICKSTART.ja.md`に沿って起動またはインストールしてください。配布版に別途.NET SDKは不要です。署名・公証の適用状況は`SIGNING-STATUS.json`で確認できます。更新時は既存の文書と出力を保管し、アプリを終了してから入れ替えてください。

Public Betaでは、閲覧用MarkdownとAI向けパッケージは一方向の出力です。原本を保持し、図や複雑なレイアウト、OCR結果は必要に応じて照合してください。GUIはPDFを既定で受け付けます。CLIでPDFを変換するには`--allow-pdf`を指定できます。OCRやPDFの照合画像には環境に応じた外部ツールが必要です。往復編集・復元・新規生成をCLIで使うには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。

[目的別クイックガイド](../docs/ja/quick-tasks.md) · [利用ガイド](../docs/ja/user-guide.md) · [対応機能](../docs/ja/supported-features.md)

# 目的別クイックガイド

## AIに文書を渡す

1. GUIで「表示中の内容（推奨・変更履歴は印付き）」を選び、DOCX／XLSX／PPTX／PDFを追加します。
2. AIに出典を示しながら答えさせたい場合は「AI向けパッケージ」を選びます。
3. 書き出し後は、要確認ページ・変更履歴・除外した非表示内容を確認します。
4. パッケージから必要な分割本文だけをAIへ渡し、出典索引は手元で参照します。

Wordなどの表で縦に結合したセルの値は、閲覧用MarkdownでもAI向けパッケージでも各行に表示します（横に結合したセルは1回だけ）。空欄のままにするときはCLIで`--no-expand-merged-cells`、GUIでは「詳細設定」のチェックを外します。

CLIでは`docredock export input.docx --ai-package dir --content-policy visible --ocr off`を使えます。Word変更履歴の既定値は[こちら](../reference/revision-defaults.md)を参照してください。

## MCPクライアントから使う

`docredock mcp`は、標準入出力で動くMCPサーバーです（JSON-RPC 2.0、1行に1メッセージ、UTF-8）。クライアントには、たとえば次のように登録します。

```json
{ "mcpServers": { "docredock": { "command": "docredock", "args": ["mcp"] } } }
```

Claude Codeでは`claude mcp add docredock -- docredock mcp`です。CLIと同じ変換をこのコンピューター上で実行し、`--json`の結果を返す5つのツールがあります。

| ツール | 内容 |
| --- | --- |
| `export_markdown` | 文書を閲覧用Markdownに変換し、結果とMarkdownを返します（`max_chars`、既定40,000文字で切り、ファイル全体の場所を添えます）。 |
| `export_ai_package` | AI向けパッケージを書き出し、そのフォルダーを`package_path`で返します。 |
| `package_review` | パッケージの確認状態と`review.md`を返します。 |
| `package_search` | 文字列を含む分割本文を探します。 |
| `package_read` | 分割本文を1つ返します。 |

確認が必要なパッケージでは、人が`package_review`の内容を確かめて`allow_review`を指定するまで、`package_search`と`package_read`は本文を返しません。

ツールは、クライアントが指定したファイルを、サーバーを起動したユーザーの権限で読みます。`output_dir`を指定しなければ、出力はシステムの一時フォルダーの下に新しく作るフォルダーに書きます。既存のファイルは`overwrite`を指定したときだけ置き換えます。PDFはCLIの`--allow-pdf`と同じく`allow_pdf`が必要です。

## 共有前にプライバシーを確認する

`visible`は認識できる非表示内容（非表示の文字・行・列・シート・スライド、発表者ノート、コメント、Excelのセルのメモ）を除外し、`sanitized`はヘッダー・フッターやメタデータもさらに除外します。`complete`はそれらを含め、非表示だったものには「（非表示）」の印を、コメントには対象の文字を付けます。共有前に確認してください。Wordの変更履歴は既定で、挿入を`<ins>`、削除を`<del>`の印付きで出力します（書式の取り消し線は`~~`）。削除した文字を渡さないときは`--revisions final`、GUIでは結果の「最終版で書き出し直す」を使います。GUIの結果欄と「該当ページを確認」、CLIでは出力された診断と`review.md`を確認します。

## 自動処理に組み込む

CLIで終了コードを確認し、レビューが必要な文書を後続処理へ渡さないようにするには、出力へ`--fail-on-review`を付けます。変換時の確認終了コードは11です。同梱Pythonリーダーで特定パートの表示や検索を行う場合も、レビューが必要なら既定で終了コード11になります（`--require-hidden-review`もCLIと同じ11）。

`--json`の出力は、入力の数や成否に関係なく`{"schema": "docredock.export.v2", "results": [...]}`の形です。各結果は`input`・`output`・`exit_code`・`format`・`mode`・`visual_inference`・`review_required`・`warning_count`・`needs_attention`・`attention`・`diagnostics`（`severity`は`"warning"`などの文字列）・`review`・`error`（失敗時は`code`と`message`）を持ちます。`review.md`を確認した後に本文を表示するには`--allow-review`、従来どおり終了コード3で止めるには`--fail-on-review`を指定します。

終了コード・警告・`review_required`は別々のことを表します。終了コード1は情報以外の診断（例: 保存済みの計算結果がない数式の`XlsxFormulaCachedValueMissing`）があるか、出力が空だったことを示します。`review_required`は原本との照合（ページ、OCR、表の区切り、本文に出なかった画像）か変更履歴の共有確認が必要なことを示します。警告があっても照合が不要な場合があり、OCRの確認だけなら終了コードは0です。自動処理では、これらを自分で組み合わせる代わりに`needs_attention`と`attention`を見てください。`attention`には`warnings`・`source_review`・`revision_sharing`・`hidden_content`・`empty_output`、変換できなかった入力では`failed`が入ります。複数ファイルの一括変換は、最も重い結果（エラー、次に11、次に1）で終了します。

```sh
docredock export input.xlsx --json --ocr off -o input.md | jq '.results[] | {input, needs_attention, attention}'
```

## 用途に合わせて設定を選ぶ

| 用途 | 設定 |
| --- | --- |
| AIへ最終版を共有する | `--content-policy sanitized`（Wordの変更履歴は最終版、非表示内容・コメント・ヘッダー・フッターを除外） |
| 変更履歴を確認する | `--revisions markup --allow-revision-sharing`。共有前に`review.md`を確認 |
| 大きな表の一部を調べる | `--ai-package dir --table-row-blocks --chunk-chars 2000`で書き出し、`package search`で探して`package read --part … --with-sources`で読む |
| 人が読む | 既定の`readable`。往復編集用の制御マーカーは不要 |

`--table-row-blocks`を付けないとExcelの表は行の途中で分けないため、大きなシートは1つのパートになります。パートが目安を超えたときは、CLIの表示と`review.md`でこの指定を案内します。

## 複数ファイルを変換する・標準出力へ送る

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export incoming --output-dir converted --json
docredock export input.docx -o - --ocr off > input.md
```

フォルダー指定は、その直下の対応ファイルを対象にします。`--json`は処理結果をJSONで出し、`--quiet`は通常の要約を消し、`--verbose`は詳細を表示します。`-o -`ではMarkdown本文だけを標準出力へ、要約を標準エラーへ送ります。画像は既定で説明文（代替テキスト）に置き換えます。画像をbase64で埋め込むには`--images embed`を付けます（トークン数が大きく増えます）。同じ名前の`.docx`と`.pdf`を一括変換するときは`--keep-source-extension`で`名前.docx.md`・`名前.pdf.md`に分けます。`--force`でもシンボリックリンクやデバイスなどの特殊ファイルは置き換えません。

## PDF・結合セル・旧形式を扱う

```sh
docredock export report.pdf --allow-pdf --lang en --fail-on-review
docredock export expenses.docx --no-expand-merged-cells --ocr off
```

`--allow-pdf`はPDF変換だけを有効にします。CLIのメッセージは`--lang ja`で日本語になります。パスワード付きのOffice文書や暗号化されたPDFは、その旨を伝えて変換を止めます。解除して保存し直してから変換してください。生成する見出しや注記の言語は`--lang ja|en`で指定できます。PDFの照合画像は、OCR入力より小さなプレビューを作成します。添付しない場合は`--pdf-fallback-images off`を指定し、要確認箇所は原本で確認してください。

`.doc`／`.xls`／`.ppt`は、元のファイルを残し、Word／Excel／PowerPointまたはLibreOfficeでそれぞれ`.docx`／`.xlsx`／`.pptx`として別名保存してから変換します。

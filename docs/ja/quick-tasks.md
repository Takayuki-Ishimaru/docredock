# 目的別クイックガイド

## AIに文書を渡す

1. GUIで「表示中の内容のみ（推奨）」を選び、DOCX／XLSX／PPTX／PDFを追加します。
2. AIに出典を示しながら答えさせたい場合は「AI向けパッケージ」を選びます。
3. 書き出し後は、要確認ページ・変更履歴・除外した非表示内容を確認します。
4. パッケージから必要な分割本文だけをAIへ渡し、出典索引は手元で参照します。

Wordなどの表をAI向けパッケージにすると、縦に結合したセルの値を各行に表示します。単独の閲覧用Markdownでは、必要な場合に「詳細設定」から同じ表示を有効にできます。

CLIでは`docredock export input.docx --ai-package dir --content-policy visible --ocr off`を使えます。Word変更履歴の既定値は[こちら](../reference/revision-defaults.md)を参照してください。

## 共有前にプライバシーを確認する

`visible`は認識できる非表示内容を除外し、`sanitized`はメタデータや派生情報もさらに除外します。`complete`は非表示内容を含めるため、共有前に確認してください。Wordの変更履歴で`markup`を選ぶと削除した文字が含まれる場合があります。GUIの結果欄と「該当ページを確認」、CLIでは出力された診断と`review.md`を確認します。

## 自動処理に組み込む

CLIで終了コードを確認し、レビューが必要な文書を後続処理へ渡さないようにするには、出力へ`--fail-on-review`を付けます。変換時の確認終了コードは11です。同梱Pythonリーダーで特定パートの表示や検索を行う場合も、レビューが必要なら既定で終了コード11になります。`review.md`を確認した後に本文を表示するには`--allow-review`、従来どおり終了コード3で止めるには`--fail-on-review`を指定します。

## 複数ファイルを変換する・標準出力へ送る

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export incoming --output-dir converted --json
docredock export input.docx -o - --ocr off > input.md
```

フォルダー指定は、その直下の対応ファイルを対象にします。`--json`は処理結果をJSONで出し、`--quiet`は通常の要約を消し、`--verbose`は詳細を表示します。`-o -`ではMarkdown本文だけを標準出力へ、要約を標準エラーへ送ります。`--force`でもシンボリックリンクやデバイスなどの特殊ファイルは置き換えません。

## PDF・結合セル・旧形式を扱う

```sh
docredock export report.pdf --allow-pdf --lang en --fail-on-review
docredock export expenses.docx --expand-merged-cells --ocr off
```

`--allow-pdf`はPDF変換だけを有効にします。生成する見出しや注記の言語は`--lang ja|en`で指定できます。PDFの照合画像は、OCR入力より小さなプレビューを作成します。添付しない場合は`--pdf-fallback-images off`を指定し、要確認箇所は原本で確認してください。

`.doc`／`.xls`／`.ppt`は、元のファイルを残し、Word／Excel／PowerPointまたはLibreOfficeでそれぞれ`.docx`／`.xlsx`／`.pptx`として別名保存してから変換します。

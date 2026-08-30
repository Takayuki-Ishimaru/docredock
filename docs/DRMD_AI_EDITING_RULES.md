# DRMD Markdown AI編集ルール

この資料は、DRMD MarkdownをAIで編集する際の運用契約です。AIは本文を改善できますが、識別・復元情報を変更してはいけません。

CLIを利用できる場合は`docredock rules`で同じ内容を取得できます。DRMD Markdownとルールを同じAIコンテキストへ入力してください。

## 必須ルール（MUST）

1. 編集前にfront matter、すべての`drmd:*`コメント、既存block ID、partition構造を読み、制御領域を変更しない。
2. 既存blockでは本文だけを変更する。`id`、`kind`、`editability`、`operations`、`constraints`、`rich-text`、`role`、`range`、`source-columns`、`source-rows`、partition ID、`baseline_nodes`、`document-end`を変更しない。
3. 削除が明示された場合だけ`<!--drmd:delete id=...-->`を追加する。本文やmarkerを消すだけでは削除にならない。
4. Markdownに見えない元内容も、削除指示がない限り保持される前提で編集する。
5. partitionをまたぐ移動・並べ替え、markerの複製、新しいIDの作成、既存IDの再利用をしない。
6. `roundtrip_store`で指定された`.drmd`サイドカーを保持し、Markdown単体でrestoreしない。
7. 保存後は`verify`と`diff`を実行し、診断と差分を確認する。既存Officeの改版には`restore`、新規Office／PDFの生成には`render`を使う。
8. 出力を共有する前に対象アプリケーションで開き、変更箇所を目視確認する。
9. フォント、文字サイズ、色、余白、列幅、図形座標をMarkdown本文へ書き足さない。AIは本文と許可されたインライン装飾だけを編集する。

DocRedockは基本的な構造違反を検出しますが、元形式固有の制約は`verify`、`diff`、restore時の診断でも確認してください。

## してはいけない編集（MUST NOT）

- front matterやDRMDコメントを削除、翻訳、整形する。
- blockのID／kindを書き換える。
- document-endの後ろに追記する。
- IDが分からないblockを既存内容として作る。
- 依頼と対応範囲を確認せず、URL、asset path、数式、セルアドレスを変更する。
- `rich-text=inline-v1`のないblockへ、Office装飾になると推測して強調記号やHTMLを追加する。
- 対応外のHTML、フィールド、リンク、変更履歴、描画オブジェクト相当の構文をRich Textへ追加する。
- `restore`で新規文書を作ろうとする。新規文書には`render --format ...`を使う。

## 追加block

追加が明示された場合だけ、次の形式を使います。

```md
<!--drmd:new kind=paragraph-->
追加本文
```

安全に追加できるのは主にDOCXのparagraph、heading、list-itemです。XLSXセルとPPTX図形の追加は対応していません。markerの能力属性または対応表で許可されていなければ追加しません。

## 形式別の最小注意

- DOCX: 既存の段落、見出し、リスト、対応する表セルを編集できます。`rich-text=inline-v1`では太字、斜体、下線、取消線、インラインコード、改行、タブだけを使います。保護、マクロ、署名、フィールド、リンク、変更履歴、描画オブジェクトを含む対応外のRich Textは変更しません。
- XLSX: `drmd:sheet-table`内の既存セル値・数式だけを編集します。表の範囲、行列数・順序、空き座標、シート構造、結合、書式を変更せず、危険な数式を追加しません。
- PPTX: `role=title|subtitle|body|other`を維持し、既存図形のテキストだけを編集します。ノート、表、画像、図形の追加・削除・移動はしません。
- PDF: 抽出と編集は限定的です。編集済みPDFの代替生成には`--allow-render-fallback`が必要です。

## 推奨手順

```text
export -> 内容確認 -> AI編集 -> verify -> diff -> (restore または render) -> 出力確認
```

`diff`に意図しないblock、削除、レイアウト変更があれば処理を止めてMarkdownを修正します。`verify`が失敗した状態の出力を納品用途に使わないでください。

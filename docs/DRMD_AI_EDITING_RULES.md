# DRMD Markdown AI編集ルール

この資料は、DRMD MarkdownをAIへ入力して編集させる際の短い運用契約です。AIは文書本文を改善してよい一方、DRMDの識別・復元情報を変更してはいけません。

CLIを利用できる環境では、`docredock rules`でこの正本と同じ内容を標準出力へ取得できます。正規DRMD Markdownとこの出力を同じAIコンテキストへ入力してください。

## 必須ルール（MUST）

1. 編集前にfront matter、すべての`drmd:*`コメント、既存のblock ID、partition構造を読み、制御領域を変更しない。
2. 変更対象は既存blockの本文だけにする。`id`、`kind`、`editability`、`operations`、`constraints`、`rich-text`、`role`、`range`、`source-columns`、`source-rows`、partition ID、`baseline_nodes`、`document-end`を変更しない。
3. 既存blockを消しただけでは削除しない。削除が明示された場合だけ`<!--drmd:delete id=...-->`を追加する。
4. Markdown内に存在しないbaseline nodeは、意図的な削除指示がない限り保持される前提で編集する（missing preserve）。
5. partitionをまたぐ移動・並べ替え、markerの複製、IDの新規発明、既存IDの再利用をしない。
6. `roundtrip_store`で示された`.drmd`サイドカーを保持し、Markdown単体でrestoreしない。
7. 保存後は必ず`verify`、`diff`を実行し、診断と差分を確認する。問題がなければ既存Officeの改版は`restore`、新規Office/PDFの生成は`render`を使う。
8. `restore`の出力を顧客へ渡す前に、対象Officeアプリケーションで開けることと、変更箇所を目視確認する。
9. font名、文字サイズ、色、余白、列幅、shape座標等をMarkdown本文へ書き足さない。これらは元Office側から復元されるため、AIは本文と許可されたinline装飾だけを編集する。

現行parser/editorは`kind`変更、partition移動、不正なpartition構造などを検出します。ただし、format adapter固有のすべての制約をMarkdown parse時点で判定するわけではありません。AI側でmarkerの能力属性を守り、`diff`とrestore diagnosticsで確認します。

## してはいけない編集（MUST NOT）

- front matterやDRMDコメントを削除・翻訳・整形する。
- `<!--drmd:block ...-->`のID/kindを書き換える。
- document-endの後ろに説明や追記を書く。
- `id`が分からないblockを既存nodeとして作る。
- 参照先URL、asset path、数式、セルアドレスを、依頼と対象formatの能力確認なしに変更する。
- `rich-text=inline-v1`のないblockへ、Office装飾になると推測して`**太字**`やHTMLを追加する。
- `rich-text=inline-v1` blockで、対応外HTML、field、hyperlink、revision、drawing相当の構文を追加する。対応する強調記号は必ず閉じ、既存の意味を保つ。
- restoreで新規文書を作ろうとする。新規文書は`render --format ...`の責務である。

## 追加block

追加は明示的に依頼された場合だけ、次の形式で行います。

```md
<!--drmd:new kind=paragraph-->
追加本文
```

現行editorは追加blockを、このmarkerが置かれたpartitionへ追加します。ただし、built-in Markdown経路で安全に追加できるのは主にDOCXのparagraph/heading/list-itemです。XLSX cell追加にはaddress/source anchorが必要で、PPTX shape追加は未対応です。能力属性またはformat表で追加が許可されていなければ追加しません。

## format別の最小注意

- DOCX: 既存の段落・見出し・list item・同じ形状のtable cell等を編集できる。`rich-text=inline-v1`では`**bold**`、`_italic_`、`<u>underline</u>`、`~~strike~~`、inline code、`<br>`、`&#9;`のsubsetだけを使う。元runのfont/size/colorと段落・page layoutはrestoreが保持するので、Markdownへfont指定を追加しない。保護境界、macro、署名、protection、field、hyperlink、revision、drawing/objectを含むRich Textは変更しない。
- XLSX: 同一sheetに複数ある場合を含め、`drmd:sheet-table`内の既存cellの値・数式だけを編集する。表自体と`range`、`source-columns`、`source-rows`、`baseline_nodes`、行列数・順序、表示されていない空き座標は変更しない。行番号とExcel列名は非表示メタデータであり、本文へ追加しない。cellを新規追加せず、sheet、row/column、merge、style等の構造も変更しない。不審・危険な数式を追加しない。
- PPTX: `role=title|subtitle|body|other`を維持し、既存shape textだけを編集する。bodyの各`- `行は既存shape内の段落になる。notes、table、image、shape追加・削除・移動はしない。
- PDF: 抽出は保守的。通常の編集restoreはF0/F1 Officeと同じではなく、編集済みPDFのrestore fallbackは`--allow-render-fallback`が必要でF3になる。

## 推奨手順

```text
export -> 内容確認 -> AI編集 -> verify -> diff -> (restore または render) -> 出力確認
```

`diff`で意図しないnode、delete、format変更が出たら処理を止め、Markdownを修正する。`verify`が失敗した状態でrestore/renderを納品用途に使わない。

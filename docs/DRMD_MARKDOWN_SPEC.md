# DRMD Markdown仕様

## 1. 目的と適用範囲

DRMD Markdownは、Office文書の内容をMarkdownで編集し、元形式へ安全に反映するための編集用投影です。Markdown単体は元文書の正本でも、完全な復元情報でもありません。復元に必要な情報、元内容、画像、整合性情報は隣接する`.drmd`サイドカー、または`.drmdpkg`バンドルに保持されます。

この文書は、DocRedock v0.2が受理・生成するDRMD Markdownの公開契約です。任意の図形追加、Wordの全装飾、セル移動など、対応表にない操作は含みません。

## 2. バージョン

| 名前 | 現行値 | 意味 |
| --- | --- | --- |
| DRMD Markdown schema | `drmd_schema: 1.0` | Markdownの構文バージョン |
| サイドカーmanifest schema | `schema_version: 1.1` | `.drmd`内の復元情報のバージョン |

`drmd_schema`を変更したり、サイドカーの`schema_version`をMarkdownへ書いたりしないでください。

## 3. ファイル構造

DRMD Markdownは次の順序を持ちます。

1. front matter（必須）
2. 0個以上のpartition
3. document-end marker（必須、末尾）

```yaml
---
drmd_schema: 1.0
drmd_rules: 1.0
document_id: doc_example
source_format: docx
roundtrip_store: document.drmd
---
```

`drmd_rules`はAI編集ルールのバージョンです。`document_id`と`source_format`は元文書との対応確認に使われ、`roundtrip_store`は隣接するサイドカーを指定します。これらの値を変更しないでください。

## 4. 制御領域

front matterと次のHTMLコメントはDRMDの制御領域です。Markdownプレビューでは通常表示されません。

```html
<!--drmd:partition-begin id=part-0001 baseline_nodes=2-->
<!--drmd:block id=n_001 kind=heading editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->
<!--drmd:sheet-table range=A1:C4 source-columns=A,B,C source-rows=1,2,3,4 baseline_nodes=10 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
<!--drmd:new kind=paragraph-->
<!--drmd:delete id=n_002-->
<!--drmd:partition-end id=part-0001 baseline_nodes=2-->
<!--drmd:document-end id=doc_example partitions=1-->
```

`id`、`kind`、partitionの所属・順序、`baseline_nodes`、document-endを変更、削除、複製してはいけません。編集対象は原則としてblock markerの直後から次のmarkerまでの本文だけです。文書終端後の内容、切断された文書、重複ID、対応しないpartition begin/endは不正です。

## 5. blockと本文

```md
<!--drmd:block id=n_001 kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->
本文だけを変更する。
```

`id`はexport時に割り当てられる安定識別子、`kind`は本文の種類です。`editability`、`operations`、`constraints`は安全に利用できる操作を示します。これらの属性は変更しないでください。代表的なkindは`heading`、`paragraph`、`list-item`、`table`、`cell`、`image`、`link`、`quote`、`code-block`です。

- `heading` / `title`: `#`から始まる見出し。
- `quote`: `> `から始まる引用。
- `code-block`: fenced code block。
- `table`: Markdown表。行列は復元先の制約を受けます。
- `sheet-table`: XLSXの既存セルをGFM表として表示します。`source-rows`と`source-columns`が元セル位置を保持するため、表の行列数や順序を変えないでください。数式はcode spanで表示します。
- `shape`: PPTXの既存図形テキストです。`role=title|subtitle|body|other`を維持してください。
- `image` / `link`: 通常のMarkdownリンクです。XLSX画像は元の行・列順と表示寸法を可能な範囲で反映します。参照先の変更は一般編集操作ではありません。

DOCXで`rich-text=inline-v1`があるblockは、次の表現を往復編集できます。

| 表現 | 効果 |
| --- | --- |
| `**太字**` | 太字 |
| `_斜体_` | 斜体 |
| `<u>下線</u>` | 下線 |
| `~~取消~~` | 取消線 |
| インラインコード | 等幅／コード |
| `<br>` | 改行 |
| `&#9;` | タブ |

対応する装飾以外のフォント、サイズ、色、段落設定、ページ設定、表レイアウトは元文書から保持されます。フィールド、変更履歴、描画オブジェクト等を含む対応外のRich Text編集は拒否されます。

XLSX `sheet-table`では、`range`、`source-columns`、`source-rows`、`baseline_nodes`、行列数・順序を変更できません。既存セルの値を空にする、別の値へ置換する、安全な数式へ置換する操作は可能です。新しいセル、行、列、結合、書式の追加は対象外です。

PPTXは既存図形の本文だけを変更できます。元のフォント、色、テーマ、位置、寸法は保持され、Markdownは見た目を編集するインターフェースではありません。

## 6. partition

partitionはOfficeのシート／スライドやPDFページ等の文書領域です。

```html
<!--drmd:partition-begin id=sheet-Summary baseline_nodes=1-->
## Summary
...
<!--drmd:partition-end id=sheet-Summary baseline_nodes=1-->
```

既存blockを別partitionへ移動したり、partition IDや順序を変えたりしないでください。追加blockは`drmd:new` markerを置いたpartitionへ追加されます。安全な追加は主にDOCXの段落、見出し、リストです。XLSXセルとPPTX図形の追加は対応していません。

## 7. 明示的な削除

block本文やmarkerを消すだけでは元内容は削除されません。削除する場合は対象IDを指定します。

```html
<!--drmd:delete id=n_002-->
```

保護された内容、未知ID、重複markerの削除は拒否されることがあります。

## 8. 整合性とサイドカー

Markdownと`roundtrip_store`で指定された`.drmd`を必ず同じ組として扱ってください。Markdownだけでは元Officeを安全に復元できません。

`document_id`、元形式、内容の整合性が一致しない場合は`verify`で検出されます。`.drmd`にはディレクトリ形とzip形があり、Markdown内の参照はどちらでも同じです。完全な受け渡しには`pack`で`.drmdpkg`を作成し、受け手側で`unpack`してから`verify`してください。

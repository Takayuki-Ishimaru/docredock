# RTMD Markdown仕様（現行実装）

## 1. 目的と適用範囲

RTMD Markdownは、Office/PDF等の元バイナリに対応する**編集可能な投影**（editable projection）です。Markdown単体は元文書の正本でも、完全な復元情報でもありません。元バイナリ、canonical `DocumentGraph`、raw slice、asset、integrity情報などは隣接する`.rtmd`ワークスペース（または`.rtmdpkg`）に保持されます。

この文書は、現在のRTMD v0.2実装が受理・生成するMarkdownの契約です。任意のShape追加、Word全機能の文字装飾、セル移動などは、実装されるまでこの仕様には含めません。

## 2. バージョンの区別

混同してはいけないバージョンが複数あります。

| 名前 | 例 | 意味 |
| --- | --- | --- |
| RTMD Markdown schema | `rtmd_schema: 1.0` | Markdown投影の構文バージョン。現行parserが受理する値は`1.0`。 |
| Graph schema | `DocumentGraph` schema `1.1` | canonical graphのJSON契約。Markdown schemaとは別物。 |
| RoundTrip manifest schema | `schema_version: 1.1` | `.rtmd` manifestのJSON契約。 |
| provider/interface/generator version | manifest内の各`version` | 実装/providerの識別・再現性情報。Markdown schemaを意味しない。 |

`rtmd_schema`を勝手に上げたり、manifestの`schema_version`をMarkdownに書いたりしてはいけません。

## 3. ファイル構造

RTMD Markdownは、次の順序を持ちます。

1. YAML風front matter（必須）
2. 0個以上のpartition
3. document-end marker（必須、末尾）

front matterの現行必須キーは次のとおりです。

```yaml
---
rtmd_schema: 1.0
rtmd_rules: 1.0
document_id: doc_example
source_format: docx
roundtrip_store: document.rtmd
---
```

`rtmd_rules`はAI編集契約のバージョンです。現行parserは、省略された旧1.0投影との互換性を保ちつつ、記載されている未知のrules versionを拒否します。serializerは通常、`content_policy`と`preserve_rtmd_comments: true`も出力します。`document_id`と`source_format`はbaselineと結び付けられ、`roundtrip_store`は隣接workspace解決に使われます。front matterの未知キーを、現在のparser/editorが意味のある編集契約として扱うとは限りません。

## 4. 制御領域（Control Region）

次のfront matterおよびHTMLコメントはRTMDの制御領域です。人間向けMarkdown previewでは通常非表示ですが、parser、integrity検証、graph editorが利用します。

```html
<!--rtmd:partition-begin id=part-0001 baseline_nodes=2-->
<!--rtmd:block id=n_001 kind=heading editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->
<!--rtmd:sheet-table range=A1:C4 baseline_nodes=10 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
<!--rtmd:new kind=paragraph-->
<!--rtmd:delete id=n_002-->
<!--rtmd:partition-end id=part-0001 baseline_nodes=2-->
<!--rtmd:document-end id=doc_example partitions=1-->
```

制御領域の`id`、`kind`、partitionの所属・順序、`baseline_nodes`、document-endの値を変更・削除・複製してはいけません。編集対象は原則としてblock markerの直後から次のmarkerまでの**block本文（block body）だけ**です。markerがMarkdown本文に見える位置に書かれていても、RTMDコメントを通常の本文として扱う契約ではありません。

現行parserは厳格にfront matter、document-end、重複ID、partitionのbegin/end対応などを検査します。文書終端後の内容、切断された文書、重複block IDは不正です。

## 5. blockと本文

既存blockは次の形です。

```md
<!--rtmd:block id=n_001 kind=paragraph editability=text operations=replace-text,explicit-delete constraints=preserve-kind,preserve-order-->
本文だけを変更する。
```

`id`はbaseline graph nodeの安定IDです。`kind`はserializerが出力したnode kindであり、別のkindへ変更してはいけません。`editability`、`operations`、`constraints`は、現在のbuilt-in Markdown editorとrestore adapterの両方が安全に表現できる操作を保守的に示します。これらの属性も制御領域であり変更禁止です。現行serializerで代表的に出力されるkindは`heading`、`paragraph`、`list-item`、`table`、`cell`、`image`、`link`、`quote`、`code-block`などです。

現行editorはbaselineの`kind`およびpartition所属との不一致をエラーにします。parserはpartitionの入れ子・順序、partition外block、`baseline_nodes`不一致も検査し、fenced code内のRTMD風文字列は制御markerとして扱いません。`diff`とrestore結果の確認は引き続き必要です。

本文の解釈はkindに依存します。

- `heading`/`title`: `#`から始まる見出し。restore時は見出し記号を除いた文字列が本文になる。
- `quote`: `> `行。restore時は引用記号を除いた文字列になる。
- `code-block`: fenced code block。
- `table`: Markdown表。行列の解釈は復元先の制約を受ける。
- `sheet-table`: XLSX sheetの既存cellを座標付きGFM表へ集約する特殊block。新規投影では、値が存在する行・列だけを表示し、離れた領域は同一partition内の複数表へ分割する。先頭列は`Row`、列見出しは`A`、`B`、`C`等。数式は`` `=SUM(A1:A2)` ``のようなcode spanになる。旧版が生成した空白込みの連続座標表もparser/editorは受理する。
- `shape`: PPTX shape。markerの`role=title|subtitle|body|other`に従い、titleは見出し、bodyはlistとして人間向け表示され、復元時は既存shapeの複数段落へ戻る。
- `image`/`link`: Markdownリンク表現。現行restoreではラベルを編集対象として扱い、参照先の変更を一般編集契約とはしない。

DOCXでmarkerに`rich-text=inline-v1`があるblockは、次のinline表現を構造化して往復します。

| 表現 | DOCX run属性 |
| --- | --- |
| `**太字**` | bold |
| `_斜体_` | italic |
| `<u>下線</u>` | underline |
| `~~取消~~` | strike |
| `` `code` `` | code/monospace文字style |
| `<br>` | run内改行 |
| `&#9;` | tab |

このsubsetでは、無変更のMarkdownは元run/style IDをそのまま保持し、編集時も同じ装飾の既存style IDを可能な限り再利用します。font family、size、color、language等の非投影run propertyはMarkdownへ記述せず、元DOCXのrun境界から復元します。文字数が増えた場合は対応する最後の元runの書式を延長します。段落style、配置、spacing、section/page設定、表geometryおよび未変更package partも保持されます。対応subsetのrunは構造化したままF1復元します。hyperlink、field、revision、drawing/objectを含むRich Text段落の編集は黙ってflattenせず拒否します。`rich-text`属性のない通常blockへ任意のinline Markdownを追加しても、DOCX装飾へ変換される契約ではありません。

XLSX `sheet-table`では、各表の`range`、`baseline_nodes`、`Row`列、Excel列見出し、行番号、列順を変更できません。表の追加・削除・重複や、baseline cellを表の範囲から外す変更も拒否されます。表示されていない空き座標を追加したり、空き座標へ値を入れたりする操作は新規cell追加になるため拒否されます。既存cellの値を空文字へすること、値を別の値へ置換すること、安全な数式へ置換することは編集対象です。復元時は既存cellの`style`参照、workbookのfont/style定義、列幅、行高、merge、page setupを変更しません。

PPTX shape本文の復元は、既存の`a:r/a:rPr`境界へ文字列を再配分します。文字数が増えた場合は段落の最後のrunへ割り当て、run font、size、language、color、theme、shape座標・寸法を保持します。Markdownはこれらの見た目属性を編集するインターフェースではありません。

## 6. partition

partitionはOffice sheet/slideやPDF page等の文書領域です。

```html
<!--rtmd:partition-begin id=sheet-Summary baseline_nodes=1-->
## Summary
...
<!--rtmd:partition-end id=sheet-Summary baseline_nodes=1-->
```

partitionをまたぐ既存blockの移動、partition IDの変更、既存partitionの並べ替えは、現行restoreで安全に表現できる編集ではありません。追加blockは`rtmd:new` markerを置いたpartitionへ追加されます。ただし、Markdown editorが生成できる追加kindとformat adapterがrestoreできる追加構造は別の制約です。現行の安全なMarkdown追加経路は主にDOCXのparagraph/heading/list-itemであり、XLSX cellにはaddress/source anchorが必要、PPTX shape追加は未対応です。

`baseline_nodes`は、そのpartitionにある既存baseline nodeの在庫数です。`rtmd:block`と`rtmd:delete`を数え、`rtmd:new`は数えません。XLSXの`rtmd:sheet-table`は、marker自身の`baseline_nodes`で宣言した既存cell数を在庫として数えます。

## 7. explicit deletionとmissing preserve

block本文を削除したり、block markerをMarkdownから除去したりしても、削除にはなりません。baseline nodeは保持され、通常`RTMD007` warning（missing baseline node）になります。

削除は、対象IDを指定したmarkerだけで行います。

```html
<!--rtmd:delete id=n_002-->
```

保護またはpassthrough nodeの削除は拒否されます。blockと同じIDのdelete marker、未知ID、制御markerの重複は不正またはrestore conflictになり得ます。

## 8. integrityとworkspace

編集前後に、Markdownと`roundtrip_store`で指定された隣接`.rtmd` workspaceを同じ組として扱います。workspaceにはmanifest、baseline graph、projection map、raw slice index、source binary、assetsなどが含まれます。Markdownだけを別環境へ渡しても、元Officeを安全にrestoreできません。

`document_id`、source format、baseline hash、projection hashなどの不一致は検証で検出されます。完全な受け渡しには`pack`で`.rtmdpkg`を作成し、受け手側で`unpack`してから検証します。

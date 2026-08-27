# Format capability matrix（現行実装）

> **これはコードとして実装されている能力の技術リファレンスです。v0.1.5の公開サポート状況を示す表ではありません。**
> v0.1.5 Public Betaで利用者向けにサポートする操作は、デスクトップGUIでのDOCX／XLSX／PPTX／PDFから閲覧用Markdownへの一方向変換です。CLIのPDF変換、往復編集、元形式への反映、新規文書の生成は`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要な実験機能です。[日本語の対応状況](ja/supported-features.md) / [English support status](en/supported-features.md)

記号: ○ = コードとして実装が対象にしている、△ = 条件付き・限定的、× = 安全な往復編集の対象外。これは「新規文書の生成（render）」だけの機能一覧ではなく、主にエクスポートしたDRMD Markdownから元文書を改版する実験エンジンの実装能力を示します。

| 操作 | DOCX | XLSX | PPTX | PDF |
| --- | ---: | ---: | ---: | ---: |
| 元バイナリを変更せず保持（F0 baseline） | ○ | ○ | ○ | ○ |
| 既存本文/テキストの限定編集（F1） | ○ 段落・見出し・list・対応Rich Text | ○ 既存cell値/数式 | ○ 既存shapeのtitle/subtitle/body text | △ 抽出中心 |
| 元のfont・文字style・layoutを保持した本文差替え | ○ 元run/style、段落・page/table layoutを保持 | ○ cell style ID、row/column、page setupを保持 | ○ run font、theme、shape geometryを保持 | — |
| 同じ形状の表/cell編集 | ○ table cell | ○ 座標付きsheet table内の既存cell | × slide table編集は対象外 | × |
| 明示的delete | ○ 対象nodeがeditableの場合 | × Markdown restoreでは非対応 | × Markdown restoreでは非対応 | × package restore非対応 |
| Markdownからの構造追加 | △ paragraph/heading/list-item | × address/source anchorを指定不可 | × shape追加なし | × |
| 新規文書のrender（F3等） | ○ | ○ | ○ | ○ ASCIIはBase14、非ASCIIは解決したTrueTypeを埋め込む |
| 新規renderでのMermaid図埋め込み | ○ PNG/DrawingML | ○ PNG/one-cell anchor | ○ PNG/picture | ○ PNG/Image XObject |
| Office template付きrenderでのMermaid図埋め込み（F2） | ○ 関係/画像を衝突回避merge | ○ worksheet→drawing→画像を衝突回避merge | ○ 関係/画像を衝突回避merge | × PDF template非対応 |
| 埋め込み画像のMarkdown投影 | ○ alt付き | ○ DrawingML anchor行・同一行の列順・表示寸法付き | ○ alt付き | △ rasterizer利用時 |
| 編集済みPDFのrestore render fallback | × | × | × | △ `--allow-render-fallback`必須、F3報告 |

## Readable Markdown（read-only projection）

`readable`の既定ポリシーは`visible`で、認識できるOfficeの非表示情報を除外します。`complete`は警告付きで非表示／メタデータを含み、`sanitized`はメタデータ、派生情報、文書付帯要素をさらに除外します。

`readable` は復元用の座標・sidecarを持たない一方向出力です。見出し、
表の境界、数値表示、XLSX DrawingML の図はセル位置・結合・スタイルから
推定されるため、横並び領域や装飾のない表では曖昧になり得ます。数式は
評価せず、保存済みの計算値がある場合だけ表示します。数式そのものは
明示オプションで表示できます。図は既定で標準 Mermaid fence として出力し、
大きな inline SVG は明示オプション時だけ追加します。XLSXの埋め込み画像は
DrawingML anchorの行位置へ投影し、位置のない画像は埋め込み画像節へまとめます。
PNG/JPEG/GIF/WebP/BMP/SVGは通常の画像リンク、TIFF/EMF/WMFは資産を保持したうえで
Markdown previewでは表示できない旨のプレースホルダーと診断を出します。PDF は ToUnicode の
`bfchar` / `bfrange` を持つ Type0/CID フォントを解決しますが、CMap のない
フォント、object stream の構成、スキャン PDF では文字欠落・失敗があり得ます。
出力時の diagnostics と生成ファイルを確認してください。

`roundtrip`のXLSX画像もsheet tableとアンカー行順に並び、同じ行の画像は列順に横並びになります。
DrawingML寸法は96 DPIでHTML `img`の`width`/`height`へ換算し、狭いpreviewでは最大49%幅へ縮小します。
これは画像のcrop・rotation・z-orderやExcelセル装飾まで再現する完全な紙面rendererではありません。

## 共通の非対応・拒否対象

unsupported/protected Office structureは、内容を黙ってflattenせず拒否されます。代表例は、macro-enabled package、署名・暗号化・protection、保護されたfield boundary、XLSXの構造編集、PPTXのnotes/table/image編集、未対応のpackage contentです。実際の可否はmanifestのcapabilities、restore結果のdiagnostics、`diff`で確認してください。

## 表現上の注意

Markdownは人間が読める自然な投影ですが、Markdownの全機能が元formatの機能へ写像されるわけではありません。font名、文字サイズ、色、余白、座標などはMarkdown本文へ露出させず、元Office packageを復元時の書式正本として扱います。DOCXの`rich-text=inline-v1` blockでは、太字、斜体、下線、取消線、inline code、改行、tabを構造化して往復し、それ以外の元run propertyは同じ文字領域へ引き継ぎます。任意の新規Word文字装飾、hyperlink、field、revision、drawingを含むRich Text編集は保証せず、安全に適用できないものは拒否します。

`render`へ渡す通常Markdownの`mermaid` code fenceは、明示的なローカル`mmdc`実行でPNGへ変換し、DOCX/PPTX/XLSX/PDFへ画像として埋め込みます。XLSXでは既存の表・テキスト範囲の下に1行空け、図ごとに高さを確保した行へone-cell DrawingML anchorで縦に配置します。DOCX/PPTX/XLSXのOffice templateと併用する場合は、テンプレート既存partを保持し、衝突しないrelationship IDとpart名を割り当て、PNG・DrawingML・relationship・`[Content_Types].xml`を形式別にmergeします。これはF2の新規文書生成であり、既存Office drawingをMermaidへ逆変換したり、`restore`で図を追加・置換したりする契約ではありません。PDF templateは引き続き非対応です。

XLSX adapter自体にはcell additionのpatch能力がありますが、現行のMarkdown sheet tableからはcell address/source anchorを新規生成しないため、AI編集契約では新規cellを許可しません。XLSXのGFM表は行番号とExcel列名を本文へ表示せず、`source-rows`と`source-columns`の非表示marker属性に保持します。表の行列数・順序・range・座標メタデータ・空き座標を変更せず、既存cellだけを編集します。数式はcode spanになります。PPTXはslide partitionの中で既存shapeを`role=title|subtitle|body|other`として表現し、本文の複数段落は改行で保持します。PDFはページ単位の抽出が保守的で、スキャンページOCRは注釈由来のderived evidenceです。新規PDF renderはフォントを同梱せず、ASCIIはBase14 Helvetica、非ASCIIは明示パス・環境変数・システムの順に全グリフを持つ埋め込み可能なTrueTypeを解決します。

## F-levelの意味

- F0: 元bytesを変更せず保持。OCR補正などderived-only変更もF0。
- F1: Office packageをpatchし、未変更partsを保持。
- F2: 検証済みtemplateから新規文書をrender。
- F3: 標準layoutの新規文書、または明示opt-inしたPDF restore fallback。
- FX: 安全に適用できず、出力を成功扱いしない。

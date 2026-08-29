# v0.1.6 の対応機能

[English](../en/supported-features.md) | 日本語

DocRedock v0.1.6 Public Betaでは、デスクトップGUIでDOCX、XLSX、PPTX、PDFをローカルの**閲覧用Markdown**へ変換する操作をサポートします。

| 機能 | v0.1.6での扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート。CLI既定 |
| `visible`／`complete`／`sanitized` | サポート |
| PDF → Markdown／OCR | デスクトップGUI: サポート。ネイティブテキストは既定で抽出し、OCRにはprovider構成が必要。CLI: 実験機能・明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |
| CLI `render --format html` | 実験機能・明示的な有効化が必要 |

閲覧用出力は、見出し、段落、入れ子リスト、空継続セルを使う結合表、画像／OCR、コード、強調、改行、数式キャッシュ警告、対応する視覚要素の意味投影またはfallback、PPTX bulletの正規化に対応します。

## 図・フローの意味保持

記号: ○ = 対応、△ = 条件付き／部分対応、× = 意味構造としては非対応。ここでいう対応は**閲覧用Markdownへの投影**であり、元Office図形へのroundtrip復元能力とは別です。

| 形式 | 図形テキスト | connector topology | geometry推定 | edge label | SmartArt／diagram | vector／image fallback | 不完全時のdiagnostic | 対応レベル |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| DOCX | ○ DrawingML／VML textbox | △ 対応drawing fragment内でnative endpointまたは一意に解決できるgeometry | △ 一意な端点のみ | △ 一意に近接するlabelのみ | △ 抽出textを保持。完全topologyは保証しない | △ 埋め込み画像をassetとして保持 | ○ 未解決／partial topologyをdiagnostic化。有効graphを描画しない場合はsource textを保持 | Public Beta・条件付き |
| XLSX | ○ 一般図形と標準`flowChart*` preset | ○ 接続済みconnector | △ cell layout／geometry由来 | △ 近接・既存projectionで解決可能な場合 | × | △ 埋め込み画像をassetとして保持 | △ 未知`flowChart*` presetはlabelを保持したgeneric node。未解決／非対応shapeのstable diagnosticは現時点で断定しない | Public Beta |
| PPTX | ○ process／decision／terminator／data／generic | ○ native connection | △ 一意な端点だけ推定 | △ 一意に対応付く場合 | △ textを保持し、topology不足を報告 | △ 埋め込み画像をassetとして保持 | ○ 未解決connector／label／partial projectionを報告 | Public Beta・条件付き |
| PDF | ○ ネイティブテキスト | △ 単純なpainted vector pathで端点が一意に対応する場合 | △ 一意なvector端点のみ | △ 一意に近接するlabelのみ | △ 有効な単純topologyをMermaidへ投影 | △ rasterizer利用時はpage preview、それ以外はpath/page placeholder | ○ vector/path partial、未解決端点、OCR/rasterizer不足を報告 | Public Beta・条件付き |

次の優先順位で、認識した視覚情報を処理します。

1. topologyが有効ならMermaid等のsemantic projection
2. 安全に生成できる画像／ページpreview等のvisual fallback
3. projectionもfallbackもできない場合は明示的なdiagnostic

PPTXと条件付き対応のDOCX／PDFでは、`VisualGraph` metadataがnative connectionとgeometry inferenceを区別します。曖昧な接続は推測で確定せず、`VisualConnectorUnresolved`等を出します。exportがWarningを出した場合、CLIは終了コード1を返します。Markdownだけでなくdiagnostic/reportとassetも確認してください。

## 内容ポリシーとその他の制約

安全な既定値は`visible`です。`complete`は非表示／メタデータを警告付きで含め、`sanitized`はさらに強く除外します。OCRは親画像のpartitionとcontent layerに従います。

DOCX drawingとPDF vector topologyは完全復元しません。対応fragment内で一意に解決できるconnector／pathだけを条件付きで投影し、それ以外はsource text／path fallbackとdiagnosticを保持します。rasterizerがあれば図的PDFページのpreviewを優先し、画像のみページでrasterizer／OCRを利用できない場合もpage placeholderとWarningを残します。実験的PDF生成は日本語フォントを同梱しません。ASCIIはBase14 Helvetica、非ASCIIは全グリフを持つ埋め込み可能なTrueTypeをシステムまたは明示パスから選択する必要があります。

閲覧用Markdownは一方向の出力です。`.drmd`と`.drmdpkg`は実験用で、元文書由来の情報を含む可能性があります。元文書を正本として保持してください。

この文書がpublic supportの正本です。コード上の能力は[実装能力表](../FORMAT_CAPABILITY_MATRIX.md)、版ごとの差分は[リリースノート](../../release-docs/RELEASE_NOTES_v0.1.6.md)、公開方針は[PUBLICATION_SCOPE](../../release-docs/PUBLICATION_SCOPE.md)を参照してください。[利用ガイド](user-guide.md)、[実験機能](experimental-features.md)、[セキュリティとプライバシー](security-and-privacy.md)も参照してください。

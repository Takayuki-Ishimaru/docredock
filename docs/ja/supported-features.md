# v0.1.5 の対応機能

[English](../en/supported-features.md) | 日本語

DocRedock v0.1.5 Public Betaでは、デスクトップGUIでDOCX、XLSX、PPTX、PDFをローカルの**閲覧用Markdown**へ変換する操作をサポートします。

| 機能 | v0.1.5での扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート。CLI既定 |
| `visible`／`complete`／`sanitized` | サポート |
| PDF → Markdown／OCR | デスクトップGUI: サポート。ネイティブテキストは既定で抽出し、OCRにはprovider構成が必要。CLI: 実験機能・明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |
| CLI `render --format html` | 実験機能・明示的な有効化が必要 |

閲覧用出力は、見出し、段落、入れ子リスト、空継続セルを使う結合表、画像／OCR、コード、強調、改行、数式キャッシュ警告、対応するグラフ／図の要約、PPTX bulletの正規化に対応します。

安全な既定値は`visible`です。`complete`は非表示／メタデータを警告付きで含め、`sanitized`はさらに強く除外します。OCRは親画像のpartitionとcontent layerに従います。

実験的PDF生成は日本語フォントを同梱しません。ASCIIはBase14 Helvetica、非ASCIIは全グリフを持つ埋め込み可能なTrueTypeをシステムまたは明示パスから選択する必要があります。文字のないPDFページのOCRには外部rasterizer／providerが必要です。

閲覧用Markdownは一方向の出力です。`.drmd`と`.drmdpkg`は実験用で、元文書由来の情報を含む可能性があります。

[利用ガイド](user-guide.md)、[実験機能](experimental-features.md)、[セキュリティとプライバシー](security-and-privacy.md)、[実装能力表](../FORMAT_CAPABILITY_MATRIX.md)も参照してください。

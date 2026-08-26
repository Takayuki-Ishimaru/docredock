# v0.1.3 の対応機能

[English](../en/supported-features.md) | 日本語

DocRedock v0.1.3 Public Betaでは、DOCX、XLSX、PPTXをローカルで**閲覧用Markdown**へ変換する操作をサポートします。

| 機能 | v0.1.3での扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート |
| `visible`／`complete`／`sanitized` | サポート |
| CLI `render --format html` | 実験機能・明示的な有効化が必要 |
| PDF → Markdown | 実験機能・明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |

閲覧用出力は、文書・スライド見出し、段落、入れ子リスト、表、画像、コードブロック、強調、改行、表計算の数式キャッシュ警告、対応するネイティブグラフ／図の要約、実験的なHTMLレンダリングの相対画像パスに対応します。

安全な既定値は`visible`です。`complete`は非表示／メタデータを警告付きで含め、`sanitized`はさらに強いプライバシーフィルターを適用します。

閲覧用Markdownは一方向の出力です。`.drmd`と`.drmdpkg`は実験用で、元文書由来の情報を含む可能性があります。

[利用ガイド](user-guide.md)、[セキュリティとプライバシー](security-and-privacy.md)、[実装能力表](../FORMAT_CAPABILITY_MATRIX.md)も参照してください。

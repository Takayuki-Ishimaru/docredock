# DocRedock v0.1.0 Public Beta

[日本語] | [English](RELEASE_NOTES_v0.1.0.en.md)

公開日: 2026-08-26

DocRedock の最初の公開ベータです。DOCX、XLSX、PPTX、PDF をローカルで Markdown に変換し、対応範囲内の編集を元形式へ安全に反映できます。

## ダウンロード

GitHub Releases のファイル名から OS と CPU に合うものを選んでください。

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.0-win-x64.zip` | `DocRedock-v0.1.0-win-arm64.zip` |
| macOS | `DocRedock-v0.1.0-osx-x64.zip` | `DocRedock-v0.1.0-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.0-linux-x64.tar.gz` | `DocRedock-v0.1.0-linux-arm64.tar.gz` |

各パッケージには、アイコン付きGUI、CLI、日英クイックスタート、MIT License、第三者ライセンス通知が含まれます。.NET SDK の別途インストールは不要です。

ダウンロード後は `SHA256SUMS` でハッシュを確認してください。依存関係は `sbom.cdx.json` でも確認できます。

## 主な機能

- Office/PDFを、AIが読みやすい意味的なMarkdownへローカル変換
- Readable Markdown: 検索、要約、質問回答向けの一方向出力
- Round-trip Markdown + `.drmd`: 元文書を保持した制御付き編集
- `verify` と `diff` による復元前レビュー
- DOCX、XLSX、PPTX の対応範囲内編集と、保護対象の明示的な拒否
- PDFの保守的な抽出と明示的なrender fallback
- macOS/WindowsのオンデバイスOCRと任意のローカルTesseract fallback
- `.drmdpkg` によるワークスペースの持ち運び
- Windows、macOS、Linuxの x64 / ARM64 配布

同一の合成XLSXに12問を質問した検証では、Excel直接参照と比べ、Markdown単体は入力トークンを74.1%削減し、テキスト問題11/11に正答しました。`.md + .drmd` は入力トークンを58.9%削減しながら、画像内情報を含む12/12に正答しました。これは特定fixture・環境の測定であり、一般的な性能保証ではありません。

## 起動時の注意

このPublic BetaのmacOSアプリはAppleのnotarizationを、Windows実行ファイルはコード署名をまだ行っていません。OSの警告内容と、公開されているチェックサムを確認したうえで起動してください。LinuxパッケージにはデスクトップエントリーとPNGアイコンを含みますが、システム全体への自動インストールは行いません。

## 既知の制約

- Readable Markdownは元文書へ復元できません。復元が必要ならRound-tripを使ってください。
- `.drmd` と `.drmdpkg` は元文書を含み得ます。元文書と同じ機密性で扱ってください。
- XLSXの行列、シート、結合、スタイルなどの構造変更は対象外です。
- PPTXは既存shape text中心です。ノート、表、画像、shapeの追加・移動は対象外です。
- PDF抽出はfont/CMap構成により不完全になる場合があります。編集済みPDFの復元は元レイアウトを保証しません。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizerは同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応のpackage構造は拒否されることがあります。

詳細は [利用ガイド](USER_GUIDE.md)、[対応形式一覧](../docs/FORMAT_CAPABILITY_MATRIX.md)、[セキュリティとプライバシー](SECURITY_AND_PRIVACY.md)を参照してください。

## ライセンス

DocRedock本体はMIT Licenseです。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

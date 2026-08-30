# DocRedock v0.1.5 Public Beta リリースノート

リリース日: 2026-08-28

DocRedock v0.1.5はPublic Betaの信頼性hotfixです。利用者向けのサポート範囲は引き続き、**閲覧用Markdown**へのローカルな一方向変換です。デスクトップGUIはDOCX／XLSX／PPTXに加えてPDF入力を既定で受け付けます。往復編集、復元、レンダリング／新規文書生成、CLIのPDF変換／生成は実験機能です。読み取り専用のCLI inspectは利用できます。

## 主な変更

- CLI `export`の既定profileを一方向の`readable`へ変更しました。往復処理の自動化は`--profile roundtrip`を明示してください。
- OCRテキストを対応する画像またはPDFページとともに表示し、対応先が不明な内容は診断付きで分けて表示します。
- 横・縦結合表を空の継続セルで出力し、往復処理で継続セル・表形状の変更を拒否します。
- 実験的PDF生成から日本語フォントの同梱・固定前提を除去しました。
- デスクトップGUIはPDF入力を既定で受け付けます。ネイティブテキストを直接抽出し、文字のないページのOCRには引き続きrasterizerとOCR providerの構成が必要です。
- 強調内にある場合を含め、PPTXのリテラルbulletをMarkdownリストへ正規化します。

## 移植可能なPDFフォント

ASCIIのみのPDFはフォントプログラムを埋め込まずBase14 Helveticaを使います。非ASCIIは次の順で埋め込み可能なTrueTypeを解決します。

1. `--font-path`と任意の`--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH`と任意の`DOCREDOCK_PDF_FONT_FACE_INDEX`
3. OSにインストールされたシステムフォント

DocRedockは選択したTrueTypeフォントの埋め込み許可と必要文字を確認します。不正、過大、埋め込み禁止、または文字不足のフォントは診断付きで拒否します。フォントを自動ダウンロードしないため、選択フォントのライセンス確認は利用者の責任です。

フォント選択とcoverageは情報、欠落と切り詰めは警告です。警告があるCLI renderは終了コード1を返します。`--quiet`は情報行を抑制し、`--verbose`は選択フォントのパスを表示します。

## OCR・表・変換品質

- OCRテキストは対応する画像またはPDFページと同じ表示ポリシーに従います。
- PDFページのプレビューは対応ページに配置し、rasterizerがない場合は診断を表示します。
- 閲覧用の結合表は、行／列結合の継続セルを空欄にします。
- 強調された記号を含むPPTXの箇条書きをMarkdownリストへ変換します。
- 変換結果が空の場合は、その状態を明示します。

## 配布

公開パッケージはフォントを同梱せず、チェックサムで検証できます。

## 安全性と互換性

安全な内容ポリシーの既定値は引き続き`visible`です。`complete`は非表示情報・メタデータを警告付きで含み、`sanitized`はさらに強く除外します。閲覧用Markdownは一方向出力です。元のOffice文書を正本として保持してください。

CLIのPDF変換（export）・復元・生成（render）を含む実験的CLIエントリポイントには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。デスクトップGUIのPDF入力は既定で利用できますが、OCR／rasterizerおよびPDFフォントの制約は引き続き適用されます。`.drmd`や`.drmdpkg`には元文書由来または復元用の情報が含まれる場合があり、元文書と同じ機密管理が必要です。

## 更新方法

使用中のv0.1.4 GUI／CLIを、OSとCPUに合うv0.1.5パッケージへ置き換えてください。roundtrip出力に依存するスクリプトには`--profile roundtrip`を追加します。公開されたSHA-256と署名／notarization状況を確認してください。

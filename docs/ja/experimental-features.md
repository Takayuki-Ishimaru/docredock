# 実験機能

日本語 | [English](../en/experimental-features.md)

> v0.1.6では、ここにあるCLIワークフローはサポート対象外の実験機能です。明示的に有効化しない限り実行できません。デスクトップGUIのPDF入力は既定で利用できます。

CLIを起動する前に環境変数を設定します。

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

PowerShell:

```powershell
$env:DOCREDOCK_ENABLE_EXPERIMENTAL = "1"
```

環境変数gateは、CLIのPDF変換（export）・復元・生成（render）を含む実験的なCLIワークフローに適用されます。PDFを含む読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。公開ライブラリAPIはこの入口gateを強制しません。DOCX／XLSX／PPTXの閲覧用出力は設定なしで利用できます。GUIではDOCX／XLSX／PPTXに加えPDF入力も既定で利用でき、PDF OCRには引き続きrasterizerとOCR providerの構成が必要です。`.drmd`や`.drmdpkg`には元文書と同等の機密性を持つ復元情報が含まれる場合があるため、元文書と同じ機密管理が必要です。

## PDF入力とOCR

PDF抽出はネイティブテキストをページpartitionに保持します。文字のないページでOCRするには、PDF rasterizerとOCR providerの明示的な構成が必要です。v0.1.6はPDF rasterizerを同梱せず、利用できない場合はOCRを実行したように見せず`PdfRasterizerUnavailable`を出します。

## PDF生成とフォント

DocRedockは日本語フォントを同梱・ダウンロードしません。ASCIIのみはBase14 Helveticaを使います。非ASCIIは`--font-path`／`--font-face-index`、`DOCREDOCK_PDF_FONT_PATH`／`DOCREDOCK_PDF_FONT_FACE_INDEX`、システムフォントの順に解決し、全グリフを持つ埋め込み可能なTrueTypeだけを受け付けます。

フォント選択とcoverageは情報です。欠落・切り詰めは警告で、警告があるCLI renderは終了コード1を返します。`--quiet`は情報を隠し、`--verbose`は選択フォントのパスを含めます。

`.drmd`や`.drmdpkg`には元文書または復元情報が含まれる場合があり、元文書と同じ機密管理が必要です。F0／F1テストやパッケージsmoke testは技術的な回帰証跡であり、レイアウト保持を含む利用者サポートの約束ではありません。

リリース契約は[対応機能](supported-features.md)、取り扱いは[セキュリティとプライバシー](security-and-privacy.md)を参照してください。

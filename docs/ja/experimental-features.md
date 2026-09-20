# 実験機能

日本語 | [English](../en/experimental-features.md)

> v0.2.9 Public Betaでは、ここにあるCLIワークフローはサポート対象外の実験機能です。明示的に有効化しない限り実行できません。デスクトップGUIのPDF入力は既定で利用できます。

CLIを起動する前に環境変数を設定します。

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

PowerShell:

```powershell
$env:DOCREDOCK_ENABLE_EXPERIMENTAL = "1"
```

環境変数gateは、CLIのPDF変換（export）・復元・生成（render）を含む実験的なCLIワークフローに適用されます。PDFを含む読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。公開ライブラリAPIはこの入口gateを強制しません。DOCX／XLSX／PPTXの閲覧用出力は設定なしで利用できます。Visual inferenceは`native-only`、既定の`safe`、`balanced`を選べ、曖昧な関係は未解決としてsource text・fallback asset・diagnosticに残ります。GUIではDOCX／XLSX／PPTXに加えPDF入力も既定で利用でき、PDF OCRには引き続きrasterizerとOCR providerの構成が必要です。`.drmd`や`.drmdpkg`には元文書と同等の機密性を持つ復元情報が含まれる場合があるため、元文書と同じ機密管理が必要です。

## PDF入力とOCR

PDF抽出はネイティブテキストをページ単位で保持します。文字のないページのOCRには、利用可能なPDF rasterizerとOCR providerが必要です。`DOCREDOCK_PDF_RASTERIZER`による明示設定、PATH上のpdftoppm／mutoolの順で探索しますが、これらのツールは同梱しません。利用可否は`docredock doctor`で確認でき、`DOCREDOCK_DISABLE_PDF_RASTERIZER=1`で探索を無効にできます。利用できない場合はOCRを実行したように見せず`PdfRasterizerUnavailable`を出します。

## 復元前チェックと元文書の保護

`docredock preflight edited.md` は、サイドカーの整合性と編集内容を確認し、コピー上で復元を試します。入力文書や既存レポートを変更せず、`--json` で結果を取得できます。編集したPDFの再生成を許可する場合は `--allow-render-fallback` を明示してください。成功は対応範囲での復元可能性を示し、外観の一致を保証しません。

履歴上の元文書への復元は `--force` だけでは実行できません。意図的に置き換える場合は `--force --replace-original` が必要で、置換前の内容を `.bak` ファイルに保存します。通常は別名に復元してください。詳細と終了コードは[利用ガイド](user-guide.md)を参照してください。

## Markdown生成（render）

`render`はインラインMarkdownを一度だけ解釈し、そのひとつの解釈からすべての出力形式を書き出すため、HTML・DOCX・PPTX・XLSX・PDFのテキストは一致します。文字参照は共通のポリシーに従い、WHATWG HTML5 のセミコロン付き名前 2,125 件すべてに対応します。名前は大文字・小文字を区別し、`&NotEqualTilde;` などの 2 コードポイント展開にも対応します。`&amp;`や`&copy;`などの名前付き参照、10進の`&#65;`、16進の`&#x41;`はそれぞれが示す文字に解決し、未知の名前やセミコロンのない記述はリテラルのテキストのまま残ります。コードスパンとフェンスコードブロックの中の参照は記述どおりに保持します。HTML出力は解決後のテキストを一度だけエスケープするため、`&lt;u&gt;`のようにタグを綴ったテキストはテキストとして表示され、マークアップになることはありません。

## PDF生成とフォント

DocRedockは日本語フォントを同梱・ダウンロードしません。ASCIIのみはBase14 Helveticaを使います。非ASCIIは`--font-path`／`--font-face-index`、`DOCREDOCK_PDF_FONT_PATH`／`DOCREDOCK_PDF_FONT_FACE_INDEX`、システムフォントの順に解決し、全グリフを持つ埋め込み可能なTrueTypeだけを受け付けます。

フォント選択とcoverageは情報です。欠落・切り詰めは警告で、警告があるCLI renderは終了コード1を返します。`--quiet`は情報を隠し、`--verbose`は選択フォントのパスを含めます。

`.drmd`や`.drmdpkg`には元文書または復元情報が含まれる場合があり、元文書と同じ機密管理が必要です。

リリース契約は[対応機能](supported-features.md)、取り扱いは[セキュリティとプライバシー](security-and-privacy.md)を参照してください。

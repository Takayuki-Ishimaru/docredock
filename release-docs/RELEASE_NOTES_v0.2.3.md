# DocRedock v0.2.3 Public Beta リリースノート

## 変更内容

- PDF の表再構成と図の部分 fallback を出力予算に沿って診断可能にしました。
- `docredock doctor` / `doctor --json` で OCR、PDF rasterizer、その他の capability を確認できます。
- `DOCREDOCK_PDF_RASTERIZER` による明示設定、PATH 上の pdftoppm / mutool 探索、`DOCREDOCK_DISABLE_PDF_RASTERIZER=1` による無効化に対応しました。
- CLI の command help は experimental gate と入力検証より先に処理されます。
- GUI に OCR 状態、対処方法、完了時の出力サマリーを表示します。
- 小さな図の隙間やノイズは許容し、解決済みの部分 topology とラベルを保持します。`native-only`は元形式の明示接続だけ、`safe`は一意で確度の高い推定だけ、`balanced`はより広い推定候補を扱います。曖昧、矛盾、重複、または確度の低い接続は未解決としてfallback／diagnosticに残し、矢印を捏造しません。

## 更新方法

1. 元の文書と必要な設定を保存し、起動中のDocRedockを終了します。
2. このリリースからOS／CPUに合うv0.2.3パッケージを取得し、`SHA256SUMS`で確認して別のフォルダーへ展開します。
3. 同梱の`QUICKSTART.ja.md`に従って起動します。外部ツールの状態は`docredock doctor`で確認できます。

## 対応範囲と制約

- Public Betaです。対応範囲の正本は[対応機能](../docs/ja/supported-features.md)、操作手順は[利用ガイド](../docs/ja/user-guide.md)を参照してください。
- 閲覧用Markdownは一方向変換です。元文書を保持してください。複雑な表・図、曲線や競合する接続先は部分的な出力や注記になる場合があります。
- GUIのPDF入力は既定で利用できます。CLIのPDF変換・復元・生成には引き続き`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。
- PDF rasterizer、Tesseract、Mermaid CLI、日本語PDFフォントは同梱しません。必要な機能に応じて別途用意してください。Windows／macOSのOCRヘルパーは同梱しますが、対応するOS機能・実行環境が必要です。
- 図形の代替テキストと反復診断には出力上限があります。省略数を診断で確認し、必要に応じて原図を参照してください。ネイティブ本文はこの上限で切り詰めません。
- 署名・公証の適用状況は各パッケージの`SIGNING-STATUS.json`で確認してください。各パッケージには`BINARY-SHA256SUMS`、SBOM、provenanceを同梱し、リリースページの`SHA256SUMS`はアーカイブを対象とします。

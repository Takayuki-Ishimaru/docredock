# DocRedock v0.3.7 Public Beta

[English](RELEASE_NOTES_v0.3.7.en.md)

v0.3.7は、PDFの図形と文字の対応、Wordの変更履歴を含む出力の確認案内、AI向けパッケージのトークン数の見積もりを改善する更新です。

## PDFの図形とラベルの対応を改善

表の記号とフロー図が同じページにあるPDFで、表の記号に別の図形のラベルが割り当てられ、図の文字や接続が正しく出力されないことがある問題を修正しました。図形がファイル内に記録された順序に左右されず、位置関係に基づいてラベルを対応させます。

どの図形のラベルか一意に判断できない文字は、無理に割り当てません。複雑な図や曖昧な配置では、引き続き診断や代替表示を確認し、元のPDFと照合してください。

## 変更履歴の共有前確認を結果の要約に表示

Wordの変更履歴付き出力で共有前確認が必要な場合に、変換結果の要約にもその理由を表示するよう修正しました。原本との照合事項がない場合でも、変更履歴を共有してよいかの確認は別に必要であることが分かります。

変更履歴の選択と既定値、共有許可の指定方法、`--fail-on-review`の動作は維持します。`visible`／`complete`の既定の履歴付き出力には、削除した文字が含まれる場合があります。共有前に内容を確認してください。

## トークン数の見積もりに余裕を持たせるオプション

CLIとAPIのAI向けパッケージ出力に、概算トークン数を指定倍率で増やす設定を追加しました。CLIでは次のように指定します。

```sh
docredock export input.docx --ai-package zip --token-estimate-multiplier 2 --output input.ai-package.zip
```

倍率は`1`以上`100`以下の有限の数値で、既定値は`1`です。`--ai-package dir|zip`と組み合わせて使います。不正な値や値の省略、AI向けパッケージ以外での指定は、出力を作成せず終了コード`2`で拒否します。GUIには倍率を指定する操作を追加していません。

この設定は各分割の`estimated_tokens`を増やすもので、本文や分割方法は変わりません。モデルごとのtokenizerによる実測値ではなく、トークン上限内に収まることを保証するものでもありません。

## 互換性と利用上の制約

既存のコマンドはそのまま利用できます。倍率を指定しない場合の見積もりは従来どおりです。APIでは`AiPackageExportOptions.TokenEstimateMultiplier`と`AiPackagePartOptions.TokenEstimateMultiplier`を指定できます。

AI向けパッケージのmanifestは引き続きschema `2.2`、`report.json`はschema `1.2`です。manifestには`token_estimate_multiplier`、reportのsummaryには`revision_sharing_required`を追加します。JSONを直接読む連携ツールでは追加フィールドを許容してください。

Public Betaです。閲覧用MarkdownとAI向けパッケージは一方向の出力で、元文書を正本として保持してください。共有前に本文、画像、診断、`review.md`を確認してください。確認事項が検出されなかったことは、変換の正しさを保証しません。

デスクトップGUIのPDF入力は既定で利用できます。CLIのPDF変換には引き続き`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。往復編集、元形式への復元、PDF／Officeの新規生成は実験機能です。OCR・PDF照合画像には、利用環境に応じたproviderやrasterizerの構成が必要です。図やSmartArt、複雑なレイアウトの完全再現は保証しません。

## 配布物と更新方法

GitHub Releaseで、OS／CPUに合うアーカイブを選んでください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.7-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.7-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.7-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.7-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.7-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.7-linux-arm64.tar.gz` |

ダウンロード後は`SHA256SUMS`でチェックサムを照合し、展開したフォルダーの`QUICKSTART.ja.md`を参照してください。別途.NET SDKは不要です。署名・notarizationの適用状況は、各パッケージの`SIGNING-STATUS.json`を確認してください。署名済みとは限りません。既存の文書と出力を保管したうえで、アプリを終了して新しいパッケージへ置き換えてください。

詳しくは[利用ガイド](../docs/ja/user-guide.md)、[対応機能](../docs/ja/supported-features.md)、[形式仕様](../docs/reference/ai-package.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

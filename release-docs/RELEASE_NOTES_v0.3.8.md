# DocRedock v0.3.8 Public Beta

[English](RELEASE_NOTES_v0.3.8.en.md)

v0.3.8では、AI向けパッケージの読み取り・検索で概算トークン数を取得できるようにし、OCRとMermaid描画の読み込み制限・タイムアウト処理を改善しました。

## AI向けパッケージの読み取り・検索にトークン情報を追加

CLIの`package read --json`と`package search --json`で、各分割に保存された概算トークン数`estimated_tokens`と、パッケージに記録された見積もり倍率`token_estimate_multiplier`を取得できます。AIへ渡す分割を選ぶ際に、本文と同じ結果からサイズの目安を確認できます。

```sh
docredock package read input.ai-package.zip --part part-0001 --json
docredock package search input.ai-package.zip "検索する語" --json
```

`--part`にはパッケージ内の実際の分割IDを指定してください。確認事項があるパッケージでは、従来どおり確認を求めます。内容を確認したうえで読み進める場合に`--allow-review`を指定します。検証に失敗したパッケージは読み取り・検索できません。

APIでは`AiPackageManifest.TokenEstimateMultiplier`と、検索結果の`AiPackageSearchMatch.EstimatedTokens`／`TokenEstimateMultiplier`から取得できます。各分割の見積もりは出力時の倍率を含む保存済みの値で、読み取り・検索時に倍率を再適用しません。見積もりまたは倍率が記録されていない場合は、その項目を`null`として返します。倍率がないことから値を補完することはありません。

概算トークン数は、モデルごとのtokenizerで計測した値ではありません。AIのトークン上限内に収まることを保証するものではないため、利用するAIの仕様に合わせて確認してください。

## OCRと図の描画の待ち時間・読み込み量を改善

OCR入力を読み込む際に、設定された処理量の上限に応じて読み込み量を抑えるよう修正しました。上限を超える入力は、従来どおり診断付きで処理を見送ります。

OCRとMermaid描画では、外部ツールの終了後も出力の受け取りが終わらない場合に、処理が待ち続けることがある問題を修正しました。出力の受け取りにもタイムアウトとキャンセルを適用します。Mermaid描画は実験的な文書生成で利用する機能です。

## 互換性と利用上の制約

既存のコマンドと、`package read`の通常の本文出力はそのまま利用できます。CLIのJSON出力には上記のフィールドが追加されます。JSONを直接読む連携ツールでは追加フィールドと`null`を許容してください。

manifestは引き続きschema `2.2`、`report.json`はschema `1.2`です。schema `1.0`／`2.0`／`2.1`の既存パッケージも引き続き読み取れます。manifestに倍率がある場合は`1`以上`100`以下の有限の数値であることを確認し、不正な値は拒否します。

Public Betaです。閲覧用MarkdownとAI向けパッケージは一方向の出力で、元文書を正本として保持してください。共有前に本文、画像、診断、`review.md`を確認してください。確認事項が検出されなかったことは、変換の正しさを保証しません。`visible`／`complete`の既定のWord変更履歴付き出力には、削除した文字が含まれる場合があります。

デスクトップGUIのPDF入力は既定で利用できます。CLIのPDF変換には引き続き`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。往復編集、元形式への復元、PDF／Officeの新規生成は実験機能です。OCR・PDF照合画像には、利用環境に応じたproviderやrasterizerの構成が必要です。図やSmartArt、複雑なレイアウトの完全再現は保証しません。

## 配布物と更新方法

GitHub Releaseで、OS／CPUに合うアーカイブを選んでください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.8-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.8-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.8-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.8-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.8-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.8-linux-arm64.tar.gz` |

ダウンロード後は`SHA256SUMS`でチェックサムを照合し、展開したフォルダーの`QUICKSTART.ja.md`を参照してください。別途.NET SDKは不要です。署名・notarizationの適用状況は、各パッケージの`SIGNING-STATUS.json`を確認してください。署名済みとは限りません。既存の文書と出力を保管したうえで、アプリを終了して新しいパッケージへ置き換えてください。

詳しくは[利用ガイド](../docs/ja/user-guide.md)、[対応機能](../docs/ja/supported-features.md)、[形式仕様](../docs/reference/ai-package.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

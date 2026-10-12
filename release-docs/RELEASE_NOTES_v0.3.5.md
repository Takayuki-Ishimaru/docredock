# DocRedock v0.3.5 Public Beta

[English](RELEASE_NOTES_v0.3.5.en.md)

v0.3.5は、Wordの変更履歴を最終版として出力する際の表の欠落を修正し、AI向けパッケージを読む前の確認を改善する更新です。

## Wordのセル削除後も残存文字を保持

変更履歴にセル削除があるWord文書を`final`で出力すると、一部の表で削除していないセルの文字まで欠落することがありました。残存セルの文字を保持するよう修正しました。`sanitized`で既定の最終版出力を使う場合も対象です。

削除後の列対応を確定できない表は、残存文字を行順に出力し、`DocxFinalTableGridUnresolved`で原本照合を案内します。列や見出しとの対応は推測しません。表に重なる図形の文字も、セル対応が未確定の注記として保持します。同じ列を削除した履歴は、結合セルと隣接セルの表現の違いによる不要な確認を減らしました。

以前の版で対象文書を変換した場合は、原本から再変換し、表の内容を照合してください。元のWord文書と、`final|markup|reject`の選択・既定は変わりません。

## 確認レポートをCLIで取得

`package review`はフォルダー／ZIP全体の整合性を検証し、確認レポートを取得します。

```sh
docredock package review input.ai-package.zip
docredock package review input.ai-package.zip --json --require-hidden-review --fail-on-review
```

通常の出力は`review.md`の内容です。`--json`では、変換の確認、変更履歴の共有確認、非表示内容、読み取りを止める理由、対象の分割IDを取得できます。すべての確認事項を分割単位で特定できるわけではないため、確認が必要な場合の対象IDはパッケージ全体を示します。

通常は終了コード`0`です。`--fail-on-review`を付けると、確認が必要な場合はレポートを出力したうえで`11`を返します。不正・改変されたパッケージのレポートや本文は出力しません。

## 非表示内容を読む前に確認

CLIの`package search/read/review`に`--require-hidden-review`を追加しました。非表示内容を含むパッケージと、包含情報が不明な古い`complete`パッケージを、受信側でも確認対象にできます。この選択は保存済みレポートを書き換えません。

```sh
docredock package read input.ai-package.zip --part part-0002 --require-hidden-review
# 確認レポート・原本・共有範囲を確認した後、1回の実行について確認済みとする
docredock package read input.ai-package.zip --part part-0002 --require-hidden-review --allow-review
```

確認前の検索・読み取りは、本文を出さず終了コード`11`を返します。GUIでは「非表示内容を含むパッケージも共有範囲の確認後に読む」を選べます。確認済み・未確認の表示が実際の読み取り状態に追従し、別のパッケージを開くと以前の検索語・本文・確認状態をリセットします。

同梱のPython読み取り例にも同名オプションを追加しました。全体検証を行い、保存済みの変換・履歴共有確認も含めて、確認が必要なら本文を出さず終了コード`3`を返します。確認を完了してから、このオプションを外して読み取ってください。

## 大きいパッケージと互換性

CLIの確認レポート取得と単一分割の読み取りは、選択していない分割本文をメモリに保持せず、全ファイルの検証を続けます。manifest・出典索引などの情報と選択した本文には引き続きメモリが必要です。全文検索とGUIは全文をメモリに保持するため、今回の改善は適用されません。

manifestは引き続きschema `2.2`、`report.json`はschema `1.2`です。表構造の確認項目と`package review --json`の追加項目を使う連携は、読み取り側を更新してください。Pythonの`--require-hidden-review --json`では、`review.required`／`effective_required`に読み取り時の実効状態、`reported_required`に保存済みの確認状態、`stop_reasons`に理由を返します。このオプションを使わない場合のJSON形式は維持します。

## 配布物と利用上の制約

GitHub Releaseで、OS／CPUに合うアーカイブを選んでください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.5-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.5-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.5-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.5-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.5-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.5-linux-arm64.tar.gz` |

ダウンロード後は`SHA256SUMS`でチェックサムを照合し、展開したフォルダーの`QUICKSTART.ja.md`を参照してください。別途.NET SDKは不要です。署名・notarizationの適用状況は、各パッケージの`SIGNING-STATUS.json`に記録します。署名済みとは限りません。

Public Betaです。閲覧用MarkdownとAI向けパッケージは一方向の出力で、元文書を正本として保持してください。生成はローカルで行います。全文・画像・診断・`review.md`を共有前に確認してください。確認事項が検出されなかったことは、変換の正しさを保証しません。

デスクトップGUIのPDF入力は既定で利用できます。CLIのPDF変換には引き続き`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。往復編集、元形式への復元、PDF／Officeの新規生成は実験機能です。OCR・PDF照合画像には、利用環境に応じてproviderやrasterizerの構成が必要です。図やSmartArt、複雑なレイアウトの完全再現は保証しません。

詳しくは[利用ガイド](../docs/ja/user-guide.md#ai向けパッケージ)、[対応機能](../docs/ja/supported-features.md)、[形式仕様](../docs/reference/ai-package.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

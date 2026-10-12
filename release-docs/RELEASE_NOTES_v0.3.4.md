# DocRedock v0.3.4 Public Beta

[English](RELEASE_NOTES_v0.3.4.en.md)

v0.3.4では、AI向けパッケージから必要な分割を探して読み取る操作をGUIとCLIに追加しました。Wordの変更履歴を出力に含めるか選べるようにし、削除した内容を共有する前の確認も改善します。

## AI向けパッケージを検索して読む

GUIの「パッケージを読む」から、フォルダーまたはZIPを開けます。ファイルの整合性と出典情報を検証し、確認事項がある場合は、確認を終えるまで本文の表示・コピーを止めます。検索で分割を選び、選択したMarkdownだけをコピーできます。元のセルなどの出典情報は別の詳細欄で確認できます。別のパッケージを開くと、前の本文と確認状態をリセットします。

CLIには次の操作を追加しました。

```sh
docredock package search input.ai-package.zip "売上" --json
docredock package read input.ai-package.zip --part part-0002
docredock package read input.ai-package.zip --part part-0002 --json --with-sources
```

検索・読み取りは、本文を返す前にパッケージ全体を検証します。要確認の場合は本文を出さず終了コード `11` を返します。`review.md`と該当する原本・共有内容を確認した後、1回の実行について確認済みとするには`--allow-review`を指定します。不正・改変されたパッケージからは本文を出力しません。

検索は大文字・小文字、全角・半角、対応するMarkdownの装飾やエスケープの違いを吸収して語句を照合します。意味による検索や言い換えの照合には対応しません。AIに渡す本文と、手元で照合する出典情報を分けて取り出せます。

## 分割サイズを確認しやすく

AI向けパッケージの出力設定の近くに、Excelの行ブロック分割と文字数目安を配置しました。選択した最初の文書について、OCRを使わない分割の見積もりを表示します。変換後は、実際の分割数、最大の分割の文字数、目安を超えた分割、全文と分割本文の合計文字数を確認できます。

OCRなどで内容が追加されると、見積もりと実際の結果は異なる場合があります。表などのまとまりは文字数目安を超える場合があり、文字数目安はAIモデルのトークン上限を保証しません。各分割に表題・見出し等を再掲するため、すべての分割を合わせると全文より大きくなる場合があります。質問に必要な分割を選んで利用してください。

## Wordの変更履歴を選択

閲覧用MarkdownとAI向けパッケージでは、GUIの選択欄またはCLIの`--revisions`で次を選べます。

| 設定 | 出力 |
| --- | --- |
| `final` | 挿入と現在の内容・書式を残し、削除・移動元の内容と変更履歴の注記を除く最終版 |
| `markup` | 削除した文字や変更前の情報を含み得る履歴付き表示 |
| `reject` | 変更履歴のあるWord文書を、出力を作成する前に拒否 |

`sanitized`の既定を`final`に変更しました。`visible`／`complete`の既定は引き続き`markup`です。`sanitized`で履歴付き表示を明示的に選ぶ場合は、CLIの`--allow-revision-sharing`またはGUIでの共有確認が必要です。

```sh
docredock export input.docx --ai-package zip --content-policy sanitized --revisions final --fail-on-review
```

履歴付きの出力では、変換結果の原本照合と、削除内容を含む共有前確認を区別して案内します。`export --fail-on-review`は、いずれかの確認が必要な場合に、出力を保存したうえで終了コード `11` を返します。指定しない場合の終了コードは従来どおりです。

これらの選択は元のWord文書を変更しません。実験的な往復編集の`.drmd`／`.drmdpkg`は原本と変更履歴を含み得るため、原本と同じ機密区分で扱ってください。

## 連携ツールの互換性

manifestは引き続きschema `2.2`です。`report.json`はschema `1.1`から`1.2`へ更新し、変更履歴の共有確認を示す`sharing_review`を追加しました。既存の`review.required`にも、変換確認と変更履歴の共有確認を合わせた結果を反映します。

最新版のAPIとPython読み取り例は、古いパッケージに`sharing_review`がなくても、変更履歴の診断がある場合は確認が必要と判定します。共有確認を使う連携では読み取り側も更新し、未知の追加項目を無視できるようにしてください。ファイルのハッシュ一致は、変換の正しさや共有可否を保証しません。

## 配布物と利用上の制約

GitHub Releaseで、OS／CPUに合うアーカイブを選んでください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.4-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.4-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.4-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.4-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.4-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.4-linux-arm64.tar.gz` |

ダウンロード後は`SHA256SUMS`でチェックサムを照合し、展開したフォルダーの`QUICKSTART.ja.md`を参照してください。別途.NET SDKは不要です。署名・notarizationの適用状況は、各パッケージの`SIGNING-STATUS.json`に記録します。署名済みとは限りません。

Public Betaです。閲覧用MarkdownとAI向けパッケージは一方向の出力で、元文書を正本として保持してください。生成はローカルで行います。全文・画像・診断・`review.md`を共有前に確認してください。確認事項が検出されなかったことは、変換の正しさを保証しません。

デスクトップGUIのPDF入力は既定で利用できます。CLIのPDF変換には引き続き`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。往復編集、元形式への復元、PDF／Officeの新規生成は実験機能です。OCR・PDF照合画像には、利用環境に応じてproviderやrasterizerの構成が必要です。図やSmartArt、複雑なレイアウトの完全再現は保証しません。

詳しくは[利用ガイド](../docs/ja/user-guide.md#ai向けパッケージ)、[対応機能](../docs/ja/supported-features.md)、[形式仕様](../docs/reference/ai-package.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

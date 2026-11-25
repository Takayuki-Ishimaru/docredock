# DocRedock v0.4.1 Public Beta

[English](RELEASE_NOTES_v0.4.1.en.md)

v0.4.1では、Office文書の表・図形・グラフを閲覧用HTMLで原本に近い配置で表示する処理を改善しました。Excelをシートごとに確認できるレビュー用フォルダーも追加しました。

## 今回の変更

- **Officeの見た目を保持**。対応する図形の位置・回転・重なり順、画像の切り抜き、文字の書式、保存済みデータのあるグラフをHTMLに反映します。Excelでは列幅・セルの色・結合・リンク・印刷設定の扱いも改善しました。対応できない表現は確認事項として残します。
- **Excelをシートごとに確認**。CLIの `--split-sheets` で、シート別Markdown、履歴シートの内容、画像、閲覧用HTMLを1つのフォルダーにまとめられます。単一のMarkdownには `--portable-markdown` でraw HTMLを使わない出力を選べます。
- **PDFの本文と画像を保持**。箇条書き、表、文字の配置、繰り返される本文画像の扱いを改善しました。複雑なページは、利用できる場合に原本のページ画像で確認できます。`sanitized`では、除外対象の内容が画像に残ることを避けるため、このページ画像を出力しません。
- **AI連携で原本を直接確認**。Office／PDFアプリ、SDK、文書用スキルなどによる直接の原本確認を許可する案内へ変更しました。`docredock agents install` で連携を更新すると、DocRedockが作成した旧来の制限フックを取り除きます。`--enforce`／`--no-enforce` は互換用の非推奨オプションとなり、原本の読み取りを制限しません。任意の自動承認設定は引き続き選べます。

## 使い方

GUIでは「閲覧用HTML」を選んで書き出します。CLIでは次のように指定します。

```sh
docredock export report.xlsx --format html --ocr off -o report.html
docredock export report.xlsx --split-sheets --ocr off -o report.review
docredock export report.xlsx --portable-markdown --ocr off -o report.md
docredock agents install
```

レビュー用フォルダーは、1回につき1つのXLSXが対象です。AI向けパッケージとは別の出力で、既存フォルダーを置き換える場合は `--force` が必要です。詳しくは[利用ガイド](../docs/ja/user-guide.md#excelのレビュー用フォルダー)を参照してください。

## 互換性と制約

AI向けパッケージのmanifestはschema `2.2`、reportはschema `1.2`のままです。閲覧用HTMLとMarkdownは一方向の出力で、Office文書への復元用ではありません。グラフは保存済みデータを使い、外部データの取得や数式の再計算は行いません。すべての図形・効果・フォント・ページ配置を完全に再現するものではありません。原本を保持し、正確な配置や値が必要な箇所は原本と照合してください。

変換はローカルで行います。`complete`では非表示内容、Wordの変更履歴の既定設定では削除した文字が含まれる場合があります。削除した文字を除くには `--revisions final` を指定してください。共有前に本文・画像・確認事項を確認してください。

GUIではPDFをそのまま開けます。CLI／MCPのPDF入力には `--allow-pdf`／`allow_pdf: true` が必要です。OCRとページ画像には環境に応じた外部ツールが必要です。往復編集・復元・新規生成は引き続き実験機能です。

## ダウンロードと更新

[GitHub Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)でOSとCPUに合う配布物を選び、`SHA256SUMS`で照合してください。

| OS | CPU | ファイル |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.4.1-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.4.1-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.4.1-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.4.1-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.4.1-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.4.1-linux-arm64.tar.gz` |

アプリを終了してから新しい配布物へ置き換えてください。別途.NET SDKは不要です。起動方法は同梱の `QUICKSTART.ja.md`、署名と公証の状況は `SIGNING-STATUS.json` を参照してください。

[目的別ガイド](../docs/ja/quick-tasks.md) · [対応機能](../docs/ja/supported-features.md) · [セキュリティとプライバシー](../docs/ja/security-and-privacy.md)

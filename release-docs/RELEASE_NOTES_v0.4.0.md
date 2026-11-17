# DocRedock v0.4.0 Public Beta

[English](RELEASE_NOTES_v0.4.0.en.md)

Word・Excel・PowerPoint・PDFを、ブラウザーでそのまま読めるHTMLに変換できるようになりました。AIに渡すときは、これまでどおり閲覧用MarkdownやAI向けパッケージを選べます。変換はお手元のPCで行います。

## 今回の変更

- **ブラウザーで閲覧・共有**。新しい「閲覧用HTML」は、対応する画像とSVGの図を1つのファイルにまとめます。オフラインで開けて、ライト／ダーク表示と印刷用の書式を使えます。見出しが一定数あれば目次も付きます。GUIで「閲覧用HTML」を選ぶか、CLIで `docredock export report.xlsx --format html` を実行してください。MCPでは `export_html` を使えます。
- **AIによる原本確認**。アプリの「AI連携」または `docredock agents install` で、Claude Code、GitHub Copilot、CodexにDocRedockを設定できます。AIはまずMarkdownを読み、原本の確認が必要なときは `read_source` でシートの範囲、スライド、PDFのページ、画像などを指定できます。CLIでも `docredock source` で原本の一部を確認できます。原本には非表示の内容や変更履歴で削除した文字が含まれる場合があります。
- **Officeの図とPDFの読み取りを改善**。PDFの2段組、箇条書き、ページをまたぐ表を読み取りやすくし、対応する埋め込み画像も抽出します。Officeの図形で描いたフローチャートやシーケンス図は、接続を判断できる場合にMarkdownではMermaid、HTMLではSVGとして書き出します。図として出力できない場合も、抽出できた図形内の文字を残します。

## 変換比較とHTMLの調査

原本と実際の変換結果を並べたサンプルで、フローチャートの矢印と分岐ラベルを見比べられます。変換時間はHTML対応の開発ビルドで測りました。[フローチャートの比較](https://github.com/Takayuki-Ishimaru/docredock/blob/v0.4.0/README.ja.md#フローチャートの比較) · [変換比較・HTML出力レポート](CONVERSION_REPORT_v0.4.0.md)

## 対応範囲と互換性

- HTMLは閲覧・共有用です。元のページ配置の再現やOfficeへの復元には使えず、`--ai-package`、`--profile roundtrip|audit` と併用できません。対応外の画像は説明に置き換わる場合があります。図や表の正確な位置関係が必要なときは原本を確認してください。
- `export --json` の結果に `output_format` が加わります。AI向けパッケージのmanifestとreportのスキーマは引き続き `2.2` と `1.2` です。
- GUIではPDFをそのまま開けます。CLIからPDFを変換するには `--allow-pdf` を指定します。OCRや原本照合用のページ画像には、環境に応じてOCRエンジンやPDFを画像にするツールが必要です。往復編集、復元、新規生成は引き続き実験機能です。
- WordのHTMLを共有する前に、削除した文字が含まれていないか確認してください。削除内容を除くには、変更履歴を「最終版のみ」にします。CLIでは `--revisions final` を指定します。

## ダウンロードと更新

[GitHub Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)から、OSとCPUに合う配布物を選び、`SHA256SUMS` で確認してください。

| OS | CPU | ファイル |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.4.0-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.4.0-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.4.0-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.4.0-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.4.0-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.4.0-linux-arm64.tar.gz` |

アーカイブを展開し、Windowsでは `DocRedock.exe`、macOSでは `DocRedock.app`、Linuxでは `./DocRedock` を起動します。別途.NET SDKを入れる必要はありません。CLIの起動方法は同梱の `QUICKSTART.ja.md`、署名と公証の状況は `SIGNING-STATUS.json` で確認できます。更新の際は、アプリを終了してから置き換えてください。

[目的別ガイド](../docs/ja/quick-tasks.md) · [利用ガイド](../docs/ja/user-guide.md) · [対応機能](../docs/ja/supported-features.md)

# DocRedock v0.1.1 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.1.en.md)

公開日: 2026-08-26

v0.1.1 は、公開前に見つかった Windows UI、XLSX 変換、CLI の安全性、配布上の問題を修正する Public Beta 更新です。

> **現在使用してよい範囲は、DOCX／XLSX／PPTX からの一方向の「Markdownのみ」出力です。**
> PDF の変換・レンダリングと元ファイル形式への復元は動作確認が不十分で、正常に動かない可能性があります。現段階では使用しないでください。

## ダウンロード

GitHub Releases から OS と CPU に合うファイルを選んでください。

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.1-win-x64.zip` | `DocRedock-v0.1.1-win-arm64.zip` |
| macOS | `DocRedock-v0.1.1-osx-x64.zip` | `DocRedock-v0.1.1-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.1-linux-x64.tar.gz` | `DocRedock-v0.1.1-linux-arm64.tar.gz` |

各パッケージには GUI、CLI、日英クイックスタート／セキュリティ文書、ライセンス、成果物ファイルのチェックサム、実ファイルへ紐づく SBOM、provenance、署名状況が含まれます。.NET SDK の別途インストールは不要です。

## 修正内容

- Windows でネイティブのタイトルバーを表示し、画面を掴んで移動できるようにしました。
- Windows の閉じる／最大化／最小化ボタンと「ローカル処理」表示の重なりを解消しました。
- 出力選択を「Markdownのみ」と「.md + .drmd（元ファイル形式へ復元する可能性がある場合はこちら）」へ変更しました。
- XLSX の shared string に含まれるふりがな情報が、余計なカタカナとして Markdown 本文へ混入する問題を修正しました。
- `verify` の表示を「ワークスペース整合性」「編集適用可能性」「復元可能性」に分け、valid を復元可能と誤解しにくくしました。
- `--force` は一時領域で処理を完了してから置換する方式に変更し、失敗時に以前の正常な出力を保持します。
- 配布版で利用できなかった `licenses` コマンドを利用者向け help とコマンド一覧から外しました。
- 実質未使用だった `restore --strict` を help から外し、指定時は「常に strict 検証済み」と明示して拒否します。
- 配布版を連続実行した際に起きることがあったランタイムクラッシュを回避しました。
- GUI 起動時に GitHub Releases の公開情報を確認し、新しい版がある場合だけ非モーダル通知を表示します。自動ダウンロード／自動インストールは行わず、通信失敗は起動を妨げません。

## 配布の信頼性

- 同じタグの既存リリースは上書きせず、修正時は新しい版番号を使用します。
- 公開パッケージにはチェックサム、SBOM、provenance、署名／notarization状況を添付します。
- 署名されていないPublic Betaパッケージは、その状態を明記します。

## 既知の制約

- PDF の変換・レンダリングは使用しないでください。
- DOCX／XLSX／PPTX／PDF への復元は使用しないでください。
- `.drmd` と `.drmdpkg` は元文書を含み得るため、元文書と同じ機密区分で扱ってください。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizer は同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応の package 構造は拒否される場合があります。

詳細は [利用ガイド](USER_GUIDE.md)、[対応形式一覧](../docs/FORMAT_CAPABILITY_MATRIX.md)、[セキュリティとプライバシー](SECURITY_AND_PRIVACY.md)を参照してください。

## ライセンス

DocRedock 本体は MIT License です。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

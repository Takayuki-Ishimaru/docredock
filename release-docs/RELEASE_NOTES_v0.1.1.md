# DocRedock v0.1.1 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.1.en.md)

公開日: 2026-08-26

v0.1.1 は、社内評価前に見つかった Windows UI、XLSX 変換、CLI の安全性、配布工程の問題を修正する Public Beta 更新です。

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
- 配布環境では成立しなかった `licenses` コマンドを利用者向け help とコマンド面から外しました。
- 実質未使用だった `restore --strict` を help から外し、指定時は「常に strict 検証済み」と明示して拒否します。
- single-file 圧縮を無効化し、配布版を連続実行した際のランタイムクラッシュを回避しました。
- GUI 起動時に GitHub Releases の公開情報を確認し、新しい版がある場合だけ非モーダル通知を表示します。自動ダウンロード／自動インストールは行わず、通信失敗は起動を妨げません。

## 配布と検証

- リリースは通常の locked restore、Release build、全テスト、conversion QA、LicenseAudit の成功を必須とします。
- 各 OS/CPU の成果物を別ディレクトリへ展開してから、DOCX／XLSX／PPTX の readable export、F0 SHA 比較、F1 回帰、pack/unpack、改ざん拒否、GUI 起動を確認します。復元試験は機械的回帰確認であり、利用承認ではありません。
- 同じタグの既存リリースは上書きしません。修正時は必ず新しい版番号を使います。
- RID 別 runtime lock、成果物ハッシュ、commit、SBOM、provenance、attestation、チェック済み `RELEASE-EVIDENCE.md` を結び付けます。
- Windows 署名と macOS signing/notarization は、資格情報が設定されている場合のみ適用します。証明書がなくても Public Beta の配布は停止せず、未署名状態を各パッケージへ明記します。

## 既知の制約

- PDF の変換・レンダリングは使用しないでください。
- DOCX／XLSX／PPTX／PDF への復元は使用しないでください。
- `.drmd` と `.drmdpkg` は元文書を含み得るため、元文書と同じ機密区分で扱ってください。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizer は同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応の package 構造は拒否される場合があります。

詳細は [利用ガイド](USER_GUIDE.md)、[対応形式一覧](../docs/FORMAT_CAPABILITY_MATRIX.md)、[セキュリティとプライバシー](SECURITY_AND_PRIVACY.md)を参照してください。

## ライセンス

DocRedock 本体は MIT License です。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

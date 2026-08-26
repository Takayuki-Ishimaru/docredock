# DocRedock v0.1.3 Public Beta リリースノート

[English](RELEASE_NOTES_v0.1.3.en.md) | 日本語

リリース日: 2026-08-26

## 概要

v0.1.3は、閲覧用Markdownの安全な既定値と読みやすさを強化するPublic Beta更新です。利用者向けにサポートする範囲は、DOCX／XLSX／PPTXから閲覧用Markdownへの一方向変換です。

## 主な変更

- 既定の`visible`ポリシーで、認識できるOfficeの非表示テキスト、非表示／veryHiddenシート、非表示行・列、非表示スライド・オブジェクト、ノート、コメント、変更履歴を除外します。
- `complete`は非表示情報とメタデータを含め、`HiddenContentIncluded`警告を出します。共有前に必ず出力を確認してください。
- `sanitized`は`visible`より強く、メタデータ、派生・OCR情報、ヘッダー／フッター等を除外します。
- GUIに内容ポリシー選択と`complete`の警告を追加し、CLIヘルプに`--content-policy`を追加しました。
- DOCXの文書タイトル／見出し階層／リスト、XLSXのキー・値と表の分離／数式キャッシュ欠落表示、PPTXの文書・スライド見出し／ネイティブグラフ表示を改善しました。
- 実験的なCLI HTMLレンダリングの見出し、強調、入れ子リスト、表、画像、コード、明示改行、相対画像パスを改善し、Markdown元ディレクトリ外へ相対画像パスが逸脱しないようにしました。
- `docredock --version`はassemblyのバージョンから生成されます。
- GUI更新確認は`DOCREDOCK_DISABLE_UPDATE_CHECK=1`で無効化できます。
- 編集なしの復元は元バイト列へ直接戻すF0経路を使用し、非表示XLSXセルを含む同一性回帰を強化しました。
- 非表示画像のOCRと選択ポリシー外の画像資産を安全側ポリシーから除外し、非表示XLSXセルを参照するグラフや過大なワークシート／グラフ範囲を保守的に扱うようにしました。

## 実験機能

PDF、roundtrip／audit出力、restore、render、diff、rebase、pack、unpack、migrateは実験機能です。配布版GUI／CLIでの実行には起動前の明示的な設定が必要です（公開ライブラリAPIは技術者向けの表面で、この入口gateを強制しません）。

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

これらの機能と`.drmd`／`.drmdpkg`は利用者向けサポート対象外です。サイドカーやパッケージには元文書または復元情報が含まれる場合があります。

## セキュリティ上の注意

`visible`は安全な既定値ですが、Office文書の可視性情報は作成ソフトによって異なる場合があります。Markdown、画像、OCR、保存済み計算結果、診断を共有前に確認してください。`complete`の出力は元文書と同じ機密区分で扱ってください。

## 検証

- Release buildと全自動テスト
- DOCX／XLSX／PPTXの合成非表示コンテンツ回帰
- CLI version／実験機能gate／F0・F1／pack・unpack／改ざん拒否smoke test
- Readable MarkdownとHTMLプレビューの構造回帰
- LicenseAudit、SBOM、conversion QA
- GUIとHTML出力の目視確認

最終的なworkflow run、commit、SHA-256、署名／notarization状況はリリースに添付される`RELEASE-EVIDENCE.md`を正本とします。

## 配布対象

| OS / CPU | アーカイブ |
| --- | --- |
| Windows x64 | `DocRedock-v0.1.3-win-x64.zip` |
| Windows arm64 | `DocRedock-v0.1.3-win-arm64.zip` |
| macOS x64 | `DocRedock-v0.1.3-osx-x64.zip` |
| macOS arm64 | `DocRedock-v0.1.3-osx-arm64.zip` |
| Linux x64 | `DocRedock-v0.1.3-linux-x64.tar.gz` |
| Linux arm64 | `DocRedock-v0.1.3-linux-arm64.tar.gz` |

各アーカイブのSHA-256と署名状況をGitHub Releaseで確認してください。

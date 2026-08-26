# DocRedock

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
  <img alt="DocRedock" src="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
</picture>

Office文書をローカルで、AIが読みやすいMarkdownへ。往復編集は引き続き実験機能です。

[English](README.md) · [現在のPublic Betaをダウンロード](https://github.com/Takayuki-Ishimaru/docredock/releases) · [利用ガイド](docs/ja/user-guide.md) · [対応状況](docs/ja/supported-features.md)

## v0.1.3 Public Betaの対応状況

| 機能 | 扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート |
| PDF → Markdown | 実験機能・明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |

利用者向けの正本は[リリース対応表](docs/ja/supported-features.md)、コード上の能力は[実装能力表](docs/FORMAT_CAPABILITY_MATRIX.md)です。

## 30秒で使う

1. [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)からOSとCPUアーキテクチャに合うパッケージを取得します。
2. DocRedockを起動します。
3. DOCX、XLSX、PPTXのいずれかをドロップします。
4. **閲覧用Markdown**と**表示中の内容のみ（推奨）**を選びます。
5. 変換後のMarkdownと診断を確認してからAIツールで利用します。

画像を含む文書では通常、次のように出力されます。

```text
input.xlsx
  ↓
input.md
input.assets/
```

## 内容ポリシー

GUIとCLIの閲覧用出力には3種類のポリシーがあります。ポリシーはMarkdown投影をフィルターし、外部の`.assets/`には選択ポリシーで含まれるノードから参照される画像だけを出力します。共有前にMarkdownと`.assets/`の両方を確認してください。

| ポリシー | 動作 |
| --- | --- |
| `visible` | 既定値。Office上の非表示テキスト、非表示／veryHiddenシート、非表示行・列、非表示スライド・オブジェクト、ノート、コメント、変更履歴を、抽出器が認識できる範囲で除外します。 |
| `complete` | 非表示情報とメタデータを含め、警告を出します。共有前に必ず内容を確認してください。 |
| `sanitized` | `visible`に加え、プライバシーに関わるメタデータ、派生・OCR情報、ヘッダー等をさらに除外します。 |

CLI例:

```sh
docredock export input.xlsx --profile readable --content-policy visible --output input.md
```

## 特長

- **ローカルファースト:** 組み込み変換は端末上で動作し、文書内容を外部へ送信しません。
- **構造を認識:** 文書タイトル、見出し階層、リスト、表、スライド区切り、ネイティブグラフ、表計算の領域を読みやすく出力します。
- **画像に対応:** 選択ポリシーに含まれるOffice画像をMarkdownの隣へ出力するか、Markdown内へ埋め込めます。
- **検査しやすい安全な既定値:** 認識できるOfficeの非表示情報はMarkdown投影と参照画像出力から既定で除外され、広い出力を選ぶと警告されます。
- **AIで効率的:** 合成XLSXを使ったローカル実験では、Excel直接参照より入力トークンを74.1%削減しました。条件と全結果は[検証資料](docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_2026-08-25.md)を参照してください。

## 主な制約

- v0.1.3はPublic Betaであり、本番向けの安定版ではありません。
- 生成されたMarkdownと画像は共有前に必ず確認してください。Officeの可視性情報は複雑で、作成ソフトによって表現が異なる場合があります。
- 閲覧用Markdownは一方向の出力です。元のOffice文書を正本として保持してください。
- 配布版GUI／CLIの実験機能は`DOCREDOCK_ENABLE_EXPERIMENTAL=1`を設定した場合だけ利用できます。公開ライブラリAPIは技術者向けの表面で、この入口gateを強制しません。実験機能の`.drmd`や`.drmdpkg`には元文書由来または復元用の情報が入る場合があります。
- GUIは公開GitHub Releases APIへ更新情報を問い合わせることがあります。起動前に`DOCREDOCK_DISABLE_UPDATE_CHECK=1`を設定すると無効化できます。
- 各配布物のSHA-256と署名／notarization状況を確認してください。

## ドキュメント

- [利用ガイド](docs/ja/user-guide.md)
- [v0.1.3の対応状況](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [v0.1.3リリースノート](release-docs/RELEASE_NOTES_v0.1.3.md)
- [実験機能](docs/ja/experimental-features.md)
- [コントリビュート、ビルド、テスト](CONTRIBUTING.md)

## ライセンス

DocRedockは[MIT License](LICENSE)で公開されています。第三者依存関係と同梱資産は[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)を参照してください。

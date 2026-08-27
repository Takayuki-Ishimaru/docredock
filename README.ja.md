# DocRedock

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
  <img alt="DocRedock" src="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
</picture>

Office文書をローカルで、AIが読みやすいMarkdownへ。往復編集は引き続き実験機能です。

[English](README.md) · [現在のPublic Betaをダウンロード](https://github.com/Takayuki-Ishimaru/docredock/releases) · [利用ガイド](docs/ja/user-guide.md) · [対応状況](docs/ja/supported-features.md)

## v0.1.5 Public Betaの対応状況

| 機能 | 扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート |
| PDF → Markdown | デスクトップGUIではサポート。CLIは明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |

利用者向けの正本は[リリース対応表](docs/ja/supported-features.md)、コード上の能力は[実装能力表](docs/FORMAT_CAPABILITY_MATRIX.md)です。

## 30秒で使う

1. [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)からOSとCPUアーキテクチャに合うパッケージを取得します。
2. DocRedockを起動します。
3. DOCX、XLSX、PPTX、PDFのいずれかをドロップします。
4. **閲覧用Markdown**と**表示中の内容のみ（推奨）**を選びます。
5. 変換後のMarkdownと診断を確認してからAIツールで利用します。

画像を含む文書では通常、次のように出力されます。

```text
input.xlsx
  ↓
input.md
input.assets/
```

CLIの既定profileも閲覧用になったため、`--profile readable`は省略できます。

```sh
docredock export input.xlsx --content-policy visible --output input.md
```

## v0.1.5の信頼性改善

- OCR証跡を親画像またはPDFページのpartitionへ配置し、解決できない証跡は診断付きで`derived-assets`へ隔離します。
- 横・縦結合表はMarkdownの継続セルを空欄にし、往復編集では継続セルや表形状の変更を拒否します。
- 実験的PDF生成は日本語フォントを同梱・固定参照しません。ASCIIのみはBase14 Helvetica、非ASCIIは`--font-path`、環境変数、システムフォントの順で埋め込み可能なTrueTypeを解決します。
- PDF生成の選択フォント・coverage情報を、欠落・切り詰め警告から分離しました。警告がある場合、CLI renderは終了コード1を返します。
- 強調された箇条書きを含むPPTXのリテラルbulletをMarkdownリストへ正規化します。
- 変換QAはDOCX／XLSX／PPTXの全形式を必須とし、配布スモークはチェックサムを検証してフォントバイナリの混入を拒否します。

## 内容ポリシー

GUIとCLIの閲覧用出力には3種類のポリシーがあります。外部の`.assets/`には選択ポリシーで含まれるノードから参照される画像だけを出力します。共有前にMarkdownと画像の両方を確認してください。

| ポリシー | 動作 |
| --- | --- |
| `visible` | 既定値。認識できるOfficeの非表示テキスト、シート、行・列、スライド・オブジェクト、ノート、コメント、変更履歴を除外します。 |
| `complete` | 非表示情報とメタデータを含め、警告を出します。 |
| `sanitized` | `visible`に加え、メタデータ、派生・OCR情報、ヘッダー等をさらに除外します。 |

## 主な制約

- v0.1.5はPublic Betaであり、本番向けの安定版ではありません。
- 生成されたMarkdownと画像は共有前に必ず確認してください。
- 閲覧用Markdownは一方向の出力です。元のOffice文書を正本として保持してください。
- 実験的なCLIワークフローには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。CLIのPDF変換（export）、往復／audit操作、復元、レンダリング／新規文書生成も対象です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。
- デスクトップGUIはDOCX／XLSX／PPTXに加えてPDFを既定で受け付けます。PDF OCRには引き続きrasterizerとOCR providerの構成が必要で、利用できない場合は診断を確認してください。
- DocRedockは日本語PDFフォントを同梱・ダウンロードしません。フォントの導入・選択と埋め込みライセンスの確認は利用者の責任です。
- GUIは公開GitHub Releases APIへ更新情報を問い合わせることがあります。起動前に`DOCREDOCK_DISABLE_UPDATE_CHECK=1`を設定すると無効化できます。
- 各配布物のSHA-256と署名／notarization状況を確認してください。

## ドキュメント

- [利用ガイド](docs/ja/user-guide.md)
- [v0.1.5の対応状況](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [v0.1.5リリースノート](release-docs/RELEASE_NOTES_v0.1.5.md)
- [実験機能](docs/ja/experimental-features.md)
- [コントリビュート、ビルド、テスト](CONTRIBUTING.md)

## ライセンス

DocRedockは[MIT License](LICENSE)で公開されています。第三者依存関係と資産は[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)を参照してください。

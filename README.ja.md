# DocRedock

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
  <source media="(prefers-color-scheme: light)" srcset="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
  <img alt="DocRedock" src="assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
</picture>

Office文書をローカルで、AIが読みやすいMarkdownへ。往復編集は引き続き実験機能です。

[English](README.md) · [現在のPublic Betaをダウンロード](https://github.com/Takayuki-Ishimaru/docredock/releases) · [利用ガイド](docs/ja/user-guide.md) · [対応状況](docs/ja/supported-features.md)

## v0.1.6 Public Betaの対応状況

| 機能 | 扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート |
| PDF → Markdown | デスクトップGUIではサポート。CLIは明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |

利用者向けの公開サポートと図の保証境界は[対応状況](docs/ja/supported-features.md)、コード上の能力は[実装能力表](docs/FORMAT_CAPABILITY_MATRIX.md)が正本です。版固有の変更は[v0.1.6リリースノート](release-docs/RELEASE_NOTES_v0.1.6.md)へ集約します。

## 30秒で使う

1. [Releases](https://github.com/Takayuki-Ishimaru/docredock/releases)からOSとCPUアーキテクチャに合うパッケージを取得します。
2. DocRedockを起動します。
3. DOCX、XLSX、PPTX、PDFのいずれかをドロップします。
4. **閲覧用Markdown**と**表示中の内容のみ（推奨）**を選びます。
5. 変換後のMarkdown、診断、生成されたassetを確認してからAIツールで利用します。

画像を含む文書では通常、次のように出力されます。

```text
input.xlsx
  ↓
input.md
input.assets/
```

CLIの既定profileも閲覧用のため、`--profile readable`は省略できます。

```sh
docredock export input.xlsx --content-policy visible --output input.md
```

## 図の意味保持とfallback

対応するPPTXフローを認識した場合、DocRedockはMermaidの意味投影を優先します。topologyを安全に再構成できない場合は、利用可能なtext／fallbackを残して明示的なdiagnosticを出します。PPTXのnative connectionとgeometry推定は区別し、曖昧なconnectorを推測で確定しません。

pixel-perfect再現ではありません。SmartArtやDOCX／PDFのvector topologyは部分的な場合があり、PDFではpage previewまたはplaceholderを使うことがあります。Warningがある場合、Markdownだけでは意味が欠ける可能性があるため、diagnostic/report、asset、元文書も確認してください。

## 内容ポリシー

GUIとCLIの閲覧用出力には3種類のポリシーがあります。外部の`.assets/`には選択ポリシーで含まれるノードから参照される画像だけを出力します。

| ポリシー | 動作 |
| --- | --- |
| `visible` | 既定値。認識できるOfficeの非表示テキスト、シート、行・列、スライド・オブジェクト、ノート、コメント、変更履歴を除外します。 |
| `complete` | 非表示情報とメタデータを含め、警告を出します。 |
| `sanitized` | `visible`に加え、メタデータ、派生・OCR情報、ヘッダー等をさらに除外します。 |

## 主な制約

- v0.1.6はPublic Betaであり、本番向けの安定版ではありません。
- 閲覧用Markdownは一方向の出力です。元文書を正本として保持してください。
- 共有前にMarkdown、診断、assetを必ず確認し、部分的な図の投影を完全なものとして扱わないでください。
- 実験的なCLIワークフローには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。CLIのPDF変換、往復／audit操作、復元、レンダリング／新規文書生成も対象です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。
- PDF OCRとvisual fallbackにはrasterizerとOCR providerの構成が必要な場合があります。利用できない場合はpage placeholderとWarningを確認してください。
- DocRedockは日本語PDFフォントを同梱・ダウンロードしません。フォントの導入・選択と埋め込みライセンスの確認は利用者の責任です。
- GUIは公開GitHub Releases APIへ更新情報を問い合わせることがあります。起動前に`DOCREDOCK_DISABLE_UPDATE_CHECK=1`を設定すると無効化できます。
- 各配布物のSHA-256と署名／notarization状況を確認してください。

## ドキュメント

- [利用ガイド](docs/ja/user-guide.md)
- [v0.1.6の対応状況](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [v0.1.6リリースノート](release-docs/RELEASE_NOTES_v0.1.6.md)
- [実験機能](docs/ja/experimental-features.md)
- [コントリビュート、ビルド、テスト](CONTRIBUTING.md)

## ライセンス

DocRedockは[MIT License](LICENSE)で公開されています。第三者依存関係と資産は[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)を参照してください。

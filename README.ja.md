# DocRedock

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/dark/DocRedock-banner-dark-1200x400.png">
    <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png">
    <img alt="DocRedock — Office文書をローカルでMarkdownへ" src="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/banners/light/DocRedock-banner-light-1200x400.png" width="1200">
  </picture>
</p>

<p align="center">
  <img alt="DocRedockアプリアイコン" src="https://raw.githubusercontent.com/Takayuki-Ishimaru/docredock/main/assets/brand/docredock/app-icons/png/DocRedock-appicon-128x128.png" width="96" height="96">
</p>

Office文書をローカルで、AIが読みやすいMarkdownへ。往復編集は引き続き実験機能です。

[English](README.md) · [現在のPublic Betaをダウンロード](https://github.com/Takayuki-Ishimaru/docredock/releases) · [利用ガイド](docs/ja/user-guide.md) · [対応状況](docs/ja/supported-features.md)

## v0.2.3 Public Betaの対応状況

| 機能 | 扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート |
| PDF → Markdown | デスクトップGUIではサポート。CLIは明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |

利用者向けの公開サポートと図の保証境界は[対応状況](docs/ja/supported-features.md)が正本です。版固有の変更は[v0.2.3リリースノート](release-docs/RELEASE_NOTES_v0.2.3.md)へ集約します。

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

対応するフローの関係を曖昧さなく判断できた場合、DocRedockはMermaidを出力します。それ以外は利用可能なテキストや画像／ページの代替表示を残し、解決できなかった内容を診断で知らせます。

接続推定の既定値は`safe`です。元形式に明示された接続だけを使う場合は`native-only`、より広い推定候補を扱う場合は`balanced`を選べます。`safe`は一意で確度の高い推定だけを採用します。曖昧、矛盾、重複、または確度の低い関係は未解決のままfallback／diagnosticに残し、矢印を捏造しません。CLI例: `docredock export input.pptx --visual-inference safe --output input.md`。

pixel-perfect再現ではありません。SmartArtやDOCX／PDFのvector topologyは部分的な場合があり、PDFではpage previewまたはplaceholderを使うことがあります。Warningがある場合、Markdownだけでは意味が欠ける可能性があるため、diagnostic/report、asset、元文書も確認してください。

## 内容ポリシー

GUIとCLIの閲覧用出力には3種類のポリシーがあります。外部の`.assets/`には、生成されたMarkdownから参照される画像だけを出力します。

| ポリシー | 動作 |
| --- | --- |
| `visible` | 既定値。認識できるOfficeの非表示テキスト、シート、行・列、スライド・オブジェクト、ノート、コメント、変更履歴を除外します。 |
| `complete` | 非表示情報とメタデータを含め、警告を出します。 |
| `sanitized` | `visible`に加え、メタデータ、派生・OCR情報、ヘッダー等をさらに除外します。 |

## 主な制約

- v0.2.3はPublic Betaであり、本番向けの安定版ではありません。
- 閲覧用Markdownは一方向の出力です。元文書を正本として保持してください。
- 共有前にMarkdown、診断、assetを必ず確認し、部分的な図の投影を完全なものとして扱わないでください。
- 実験的なCLIワークフローには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。CLIのPDF変換、往復／audit操作、復元、レンダリング／新規文書生成も対象です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。
- PDF OCRとvisual fallbackにはrasterizerとOCR providerの構成が必要な場合があります。利用できない場合はpage placeholderとWarningを確認してください。
- DocRedockは日本語PDFフォントを同梱・ダウンロードしません。フォントの導入・選択と埋め込みライセンスの確認は利用者の責任です。
- GUIは実行中バージョンを常時表示し、起動時または「更新を確認」からPublic Betaを含む非draftの公開版を確認できます。更新は自動インストールせず、`DOCREDOCK_DISABLE_UPDATE_CHECK=1`で起動時確認を無効化できます。
- 各配布物のSHA-256と署名／notarization状況を確認してください。

## ドキュメント

- [利用ガイド](docs/ja/user-guide.md)
- [v0.2.3の対応状況](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [v0.2.3リリースノート](release-docs/RELEASE_NOTES_v0.2.3.md)
- [実験機能](docs/ja/experimental-features.md)
- [コントリビュート、ビルド、テスト](CONTRIBUTING.md)

## ライセンス

DocRedockは[MIT License](LICENSE)で公開されています。第三者依存関係と資産は[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)を参照してください。

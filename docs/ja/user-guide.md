# DocRedock 利用ガイド

日本語 | [English](../en/user-guide.md)

このガイドは、v0.1.5 Public Betaでサポートする、デスクトップGUIでのDOCX／XLSX／PPTX／PDFから**閲覧用Markdown**へのローカル変換を説明します。

## 1. 入手する

GitHub ReleasesからOS／CPUに合うパッケージを取得し、公開されたSHA-256を確認します。自己完結型パッケージには別途.NET SDKは不要です。

## 2. 変換する

1. DocRedockを起動します。
2. DOCX、XLSX、PPTX、PDFのいずれかを選択またはドロップします。
3. **閲覧用Markdown**を選びます。
4. 特別な目的がなければ**表示中の内容のみ（推奨）**のままにします。
5. 出力先を選んで変換し、Markdownと診断を確認します。

デスクトップGUIはPDFを既定で受け付けます。ネイティブPDFテキストを抽出し、文字のないページのOCRには引き続きrasterizerとOCR providerの構成が必要です。いずれかを利用できない場合は診断を出します。

CLIのPDF変換（export）・復元・生成（render）は、他の実験的CLIワークフローと同様に`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。

CLIの既定も閲覧用Markdownです。

```sh
docredock export input.docx --content-policy visible --output input.md
```

実験的なサイドカー往復処理を使う場合だけ`--profile roundtrip`を明示します。

## 3. 生成されるファイル

| 生成物 | 内容 | v0.1.5での利用 |
| --- | --- | --- |
| `.md` | 本文、見出し、リスト、表、対応する図の説明 | 使用する |
| `.assets/` | Markdownから参照する画像 | 必要な場合に使用する |
| `.drmd` | 元文書／復元用サイドカー | 実験用。元文書と同じ機密区分で扱う |
| `.drmdpkg` | Markdownと復元情報のパッケージ | 実験用。元文書と同じ機密区分で扱う |

## 4. 内容ポリシーを選ぶ

- **visible**（既定）: 認識できる非表示テキスト、シート・行・列、スライド・オブジェクト、ノート、コメント、変更履歴を除外します。
- **complete**: 非表示情報とメタデータを含め、警告を出します。
- **sanitized**: メタデータ、派生・OCR情報、ヘッダー／フッター等も除外します。

OCRテキストは親画像の可視性を引き継ぎます。親partitionを解決できない場合は専用の`derived-assets`へ配置し、`OcrParentPartitionUnresolved`を出します。

## 5. 結果を確認する

見出し階層、リストの入れ子、結合表の空継続セル、表計算の数式キャッシュ警告、スライド区切り、画像、OCR、診断を確認します。元のOffice文書を正本として保持してください。閲覧用出力からの復元はできません。

## 6. 実験的PDF生成

DocRedockは日本語フォントを同梱・ダウンロードしません。ASCIIのみのPDFはBase14 Helveticaを使い、非ASCIIは次の順で埋め込み可能なTrueTypeを解決します。

1. `--font-path`と任意の`--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH`と任意の`DOCREDOCK_PDF_FONT_FACE_INDEX`
3. OSにインストールされたシステムフォント

```sh
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock render input.md --format pdf \
  --font-path /path/to/font.ttc --font-face-index 0 --verbose
```

CFF／CFF2、グリフ不足、不正なcollection、埋め込み禁止フォントは拒否します。選択フォントのライセンス遵守は利用者の責任です。`--verbose`は選択パスを表示し、`--quiet`は情報行だけを抑制します。欠落・切り詰め警告があれば終了コード1です。

## 7. プライバシーと更新確認

変換はローカルで行います。GUIは公開GitHub Releases APIへ更新情報を問い合わせる場合があります。起動前に`DOCREDOCK_DISABLE_UPDATE_CHECK=1`を設定すると無効化できます。

実験ワークフローは[実験機能](experimental-features.md)、取り扱いは[セキュリティとプライバシー](security-and-privacy.md)を参照してください。

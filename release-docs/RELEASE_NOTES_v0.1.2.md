# DocRedock v0.1.2 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.2.en.md)

公開日: 2026-08-26

v0.1.2 は、Office 文書をAIで検索・要約するための「閲覧用Markdown」出力を改善する Public Beta 更新です。

## この更新を検討すべき人

- DOCX、XLSX、PPTXをAIの検索、要約、質問回答へ渡したい利用者。
- 横長の表、複数領域の表、2カラムのスライド、図や画像を含む文書をMarkdown化したい利用者。
- PDF変換、往復編集、元形式への反映、新規文書の生成を必要とする利用者は、このリリースをその用途に使わないでください。

## 利用者から見える改善

| 対象 | 変更前 | v0.1.2後 |
| --- | --- | --- |
| XLSXの表と値 | 離れた領域が同じ表になったり、生の数値が表示されたりすることがあった | 離れた表を分け、日付・時刻・割合などを表示値として出力しやすくなりました |
| PPTXの読み順 | 2カラムや全幅要素の順序が分かりにくいことがあった | 左右カラムを読みやすい順に並べ、チャートの要点を確認しやすくしました |
| 出力の安全性 | 大きい・壊れた入力で処理負荷が増えることがあった | 大きい入力や不正な入力に対する制限を強化し、失敗時の出力を保護します |

## v0.1.2のサポート範囲

| 操作 | 扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown（`readable`） | サポート対象 |
| PDF → Markdown | サポート対象外 |
| 往復編集（`roundtrip`）と元形式への反映（`restore`） | サポート対象外・実験機能 |
| 新規文書の生成（`render`） | サポート対象外・実験機能 |

閲覧用Markdownは一方向の出力です。入力文書の内容を確認してから共有してください。画像がある場合は、Markdownと同名の `.assets` ディレクトリも共有対象になります。

## ダウンロード

GitHub Releases から OS と CPU に合うファイルを選んでください。

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.2-win-x64.zip` | `DocRedock-v0.1.2-win-arm64.zip` |
| macOS | `DocRedock-v0.1.2-osx-x64.zip` | `DocRedock-v0.1.2-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.2-linux-x64.tar.gz` | `DocRedock-v0.1.2-linux-arm64.tar.gz` |

各パッケージには GUI、CLI、日英クイックスタート／セキュリティ文書、ライセンス、成果物ファイルのチェックサム、実ファイルへ紐づく SBOM、provenance、署名状況が含まれます。.NET SDK の別途インストールは不要です。

## 品質確認

複雑なXLSX／PPTXと複数スライドのPPTXで閲覧用出力を確認しました。往復編集と復元の確認は、これらを利用者向けサポート対象にするものではありません。

## 既知の制約

- PDFの変換・レンダリングは本リリースのサポート対象外です。
- DOCX／XLSX／PPTX／PDFへの元形式への反映は本リリースのサポート対象外です。
- `.drmd` と `.drmdpkg` は元文書を含み得るため、元文書と同じ機密区分で扱ってください。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizer は同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応の package 構造は拒否される場合があります。
- Windows 署名と macOS signing/notarization は資格情報が設定されている場合のみ適用し、各パッケージに状態を記録します。

## 実験機能

往復編集、`.drmd`／`.drmdpkg`、`verify`、`diff`、`restore`、Mermaid図を含む新規文書生成、Office template、PDF fallbackは、v0.1.2の利用者向けサポート対象外です。大きいまたは不正な入力には安全上の制限が適用されます。

詳細は [利用ガイド](../docs/ja/user-guide.md)、[対応形式一覧](../docs/ja/supported-features.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

## ライセンス

DocRedock 本体は MIT License です。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

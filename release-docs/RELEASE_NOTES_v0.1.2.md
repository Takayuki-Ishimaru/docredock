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

## 検証

- Release 構成の全 259 テストに合格しました。
- 複雑な実 XLSX／PPTX と 15 スライドの合成 PPTX で readable／roundtrip 出力を確認しました。
- 検証対象の XLSX／PPTX は、未編集の F0 復元で元ファイルと同一ハッシュになりました。この確認は機械的回帰試験であり、roundtrip／restoreの利用を意味しません。
- リリースワークフローでは locked restore、Release build、全テスト、conversion QA、LicenseAudit、6 RID の展開後 smoke test を必須とします。

## 既知の制約

- PDFの変換・レンダリングは本リリースのサポート対象外です。
- DOCX／XLSX／PPTX／PDFへの元形式への反映は本リリースのサポート対象外です。
- `.drmd` と `.drmdpkg` は元文書を含み得るため、元文書と同じ機密区分で扱ってください。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizer は同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応の package 構造は拒否される場合があります。
- Windows 署名と macOS signing/notarization は資格情報が設定されている場合のみ適用し、各パッケージに状態を記録します。

## Experimental engine changes — not supported in v0.1.2

以下は実装・回帰検証に含まれる変更ですが、v0.1.2の利用者向けサポート範囲ではありません。

- roundtripプレビュー、`.drmd`／`.drmdpkg`、`verify`、`diff`、`restore`の経路を維持・改善しました。
- PPTXのネストしたグループについて、`off/ext/chOff/chExt`、回転、反転、異方スケールを合成して座標を求め、欠損値やゼロサイズでも非有限座標を避けます。
- relationship XML、ZIPの展開サイズ・圧縮率、グラフの疎な点数に上限を設けます。複数出力の確定後にバックアップ削除が失敗しても、確定済み出力を巻き戻さず、可能なロールバック処理を継続して報告します。
- `render`のMermaid図、Office template、PDF fallback、および元形式への反映は実験用エンジンの機能です。利用者向けサポート対象ではありません。

詳細は [利用ガイド](../docs/ja/user-guide.md)、[対応形式一覧](../docs/ja/supported-features.md)、[セキュリティとプライバシー](../docs/ja/security-and-privacy.md)を参照してください。

## ライセンス

DocRedock 本体は MIT License です。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

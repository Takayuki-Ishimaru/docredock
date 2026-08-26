# DocRedock v0.1.2 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.2.en.md)

公開日: 2026-08-26

v0.1.2 は、複雑な XLSX／PPTX の構造認識、Markdown の読みやすさ、roundtrip プレビュー、および出力処理の安全性を改善する Public Beta 更新です。

> **現在使用してよい範囲は、DOCX／XLSX／PPTX からの一方向の「Markdownのみ」出力です。**
> PDF の変換・レンダリングと元ファイル形式への復元は動作確認が不十分で、正常に動かない可能性があります。現段階では使用しないでください。

## ダウンロード

GitHub Releases から OS と CPU に合うファイルを選んでください。

| OS | Intel / AMD 64-bit | ARM 64-bit |
| --- | --- | --- |
| Windows | `DocRedock-v0.1.2-win-x64.zip` | `DocRedock-v0.1.2-win-arm64.zip` |
| macOS | `DocRedock-v0.1.2-osx-x64.zip` | `DocRedock-v0.1.2-osx-arm64.zip` |
| Linux | `DocRedock-v0.1.2-linux-x64.tar.gz` | `DocRedock-v0.1.2-linux-arm64.tar.gz` |

各パッケージには GUI、CLI、日英クイックスタート／セキュリティ文書、ライセンス、成果物ファイルのチェックサム、実ファイルへ紐づく SBOM、provenance、署名状況が含まれます。.NET SDK の別途インストールは不要です。

## 主な改善

### XLSX

- 離れた表領域、横長表、繰り返し列ギャップ、近接する孤立セルを区別し、無関係なセルを同じ表へ誤結合しにくくしました。
- 1900／1904 日付システムを判別し、日付、日時、時刻、経過時間、比率の表示値をセル書式に合わせて出力します。
- グラフの疎なポイント番号を維持し、カテゴリと値の対応ずれを防ぎます。
- readable Markdown の表領域と表示値について、編集可能な roundtrip 投影との整合性を強化しました。

### PPTX

- スライド内の全幅要素と左右カラムを判定し、2カラム資料を左列から右列の順に読みやすく出力します。
- 棒／折れ線グラフでは系列の増減・最小・最大を、円／ドーナツグラフでは最大・最小構成要素と全体比を要約します。
- ネストしたグループ図形について、`off/ext/chOff/chExt`、回転、水平／垂直反転、異方スケールを階層的に合成し、絶対スライド座標へ変換します。
- 欠損値やゼロサイズを含む退化グループでも、非有限座標を生成せず安全に処理します。

### Markdown とプレビュー

- readable と roundtrip の役割を分離したまま、見出し、表、ノート、図形、チャートの投影順と表示を改善しました。
- roundtrip 専用プレビューでは DRMD 制御コメントを編集契約として保持しつつ、表示時のノイズを抑えます。
- インライン装飾と改行の扱いを改善し、PPTX の図形テキストや表の可読性を高めました。

### 安全性と安定性

- Office ZIP のメディア以外のエントリーにも、1エントリー、合計展開サイズ、圧縮率の上限を適用しました。
- relationship XML とグラフポイント数を制限し、巨大入力による過剰なメモリ消費を防ぎます。
- 複数出力の置換後にバックアップ削除が失敗しても、正常に確定した出力を巻き戻さないようにしました。
- ロールバックは可能な復元処理を最後まで継続し、複数の失敗をまとめて報告します。

## 検証

- Release 構成の全 259 テストに合格しました。
- 複雑な実 XLSX／PPTX と 15 スライドの合成 PPTX で readable／roundtrip 出力を確認しました。
- 検証対象の XLSX／PPTX は、未編集の F0 復元で元ファイルと同一ハッシュになりました。
- リリースワークフローでは locked restore、Release build、全テスト、conversion QA、LicenseAudit、6 RID の展開後 smoke test を必須とします。

## 既知の制約

- PDF の変換・レンダリングは使用しないでください。
- DOCX／XLSX／PPTX／PDF への復元は使用しないでください。今回の復元試験は機械的回帰確認であり、利用承認ではありません。
- `.drmd` と `.drmdpkg` は元文書を含み得るため、元文書と同じ機密区分で扱ってください。
- Tesseract、言語モデル、Mermaid CLI、PDF rasterizer は同梱しません。
- マクロ、署名、暗号化、保護、危険または未対応の package 構造は拒否される場合があります。
- Windows 署名と macOS signing/notarization は資格情報が設定されている場合のみ適用し、各パッケージに状態を記録します。

詳細は [利用ガイド](USER_GUIDE.md)、[対応形式一覧](../docs/FORMAT_CAPABILITY_MATRIX.md)、[セキュリティとプライバシー](SECURITY_AND_PRIVACY.md)を参照してください。

## ライセンス

DocRedock 本体は MIT License です。第三者依存関係と同梱資産には、それぞれのライセンスが適用されます。

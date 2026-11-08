# DocRedock v0.3.12 Public Beta

[English](RELEASE_NOTES_v0.3.12.en.md)

v0.3.12では、OCRで言語が足りないときの案内、Excelの往復編集での再計算の扱い、AI向けパッケージの検索とMCPからの読み取り、`export --json`のバッチ時の結果出力を改善しました。

## PDFとOffice文書の変換

- OCRでは、Tesseractに要求した言語がない場合、要求した言語・実際に使用した言語・不足した言語を一覧にし、導入の案内を表示します。`--ocr-strict`を指定すると、出力を書き出したあとに終了コード8を返します。
- 往復編集で保護された画像のリンク先を変更すると、`ProtectedNodeEdit`で拒否し、変更が適用できないことを表示します。

## AI向けパッケージとMCP

- パッケージ内の語句検索は、表示上の改行をまたぐ語句にも一致します。見出しに直接一致した場合は、その見出しをそれ自体の文脈として結果に表示します。
- Excelでは、確認が必要なパッケージを`review.md`で示します。シート内の表の隣に置かれた独立したメモは、表の外として扱われます。
- MCPの`package_read`は、確認のため本文を保留した場合、`structuredContent`の`status`、`stop_reasons`、`next_action`で理由と次の操作を示します。短い出典が必要なときは`source_detail: "compact"`でシート名と範囲を取得できます。詳細な出典には`with_sources: true`を指定します。[MCPの設定手順](../docs/ja/quick-tasks.md#mcpクライアントから使う)を参照してください。

## CLIと互換性

- `export --json`は`docredock.export.v2`の`results`配列に、入力ファイルごとの結果を出力します。出力先が衝突すると書き込み前に一括処理を中止し、入力ごとに失敗結果を返します。入力を特定できない引数エラーでは`results`が空になり、トップレベルの`error`に詳細が入ります。
- 実験機能のExcel往復編集では、計算結果に影響しうる編集について`XlsxRecalculationRequired`で再計算待ちを警告します。AI向けパッケージでは、再計算待ちのブックを`review.md`で確認が必要な状態にします。値を確定するにはExcelで開いて再計算し、保存してください。
- GUIはAvalonia 12.1.5を同梱します。配布版に別途.NET SDKは不要です。

## 配布物と更新方法

GitHub ReleasesでOSとCPUに合うアーカイブを選び、`SHA256SUMS`で照合してください。

| OS | CPU | ファイル名 |
| --- | --- | --- |
| Windows | Intel／AMD 64-bit | `DocRedock-v0.3.12-win-x64.zip` |
| Windows | Arm64 | `DocRedock-v0.3.12-win-arm64.zip` |
| macOS | Intel | `DocRedock-v0.3.12-osx-x64.zip` |
| macOS | Apple Silicon | `DocRedock-v0.3.12-osx-arm64.zip` |
| Linux | Intel／AMD 64-bit | `DocRedock-v0.3.12-linux-x64.tar.gz` |
| Linux | Arm64 | `DocRedock-v0.3.12-linux-arm64.tar.gz` |

展開したフォルダーの`QUICKSTART.ja.md`に沿って起動またはインストールしてください。署名・公証の適用状況は`SIGNING-STATUS.json`で確認できます。更新時は既存の文書と出力を保管し、アプリを終了してから入れ替えてください。

Public Betaでは、閲覧用MarkdownとAI向けパッケージは一方向の出力です。原本を保持し、図や複雑なレイアウト、OCR結果は必要に応じて照合してください。GUIはPDFを既定で受け付けます。CLIでPDFを変換するには`--allow-pdf`を指定できます。OCRやPDFの照合画像には環境に応じた外部ツールが必要です。往復編集・復元・新規生成をCLIで使うには`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。

[目的別クイックガイド](../docs/ja/quick-tasks.md) · [利用ガイド](../docs/ja/user-guide.md) · [対応機能](../docs/ja/supported-features.md)

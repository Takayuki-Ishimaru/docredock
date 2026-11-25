# DocRedock パッケージ・クイックスタート

日本語 / English guide: `QUICKSTART.en.md`

現在のバージョンはアプリ上部に常時表示されます。CLIでは`docredock --version`で確認できます。

## 起動

- Windowsでは `DocRedock.exe` を起動します。CLIは `docredock.cmd` を使います。
- macOSでは `DocRedock.app` を起動します。CLIはターミナルから `./docredock` を使います。
- Linuxでは展開したフォルダーで `./DocRedock` を起動します。CLIは `./docredock` を使います。ユーザー領域にインストールするには `./install.sh`、アンインストールするには `./uninstall.sh` を使います。既定の導入先は`$HOME/.local`です。対応文書をファイルマネージャーの「アプリケーションで開く」からDocRedockで開くこともできます。

## 最初の変換

Word・Excel・PowerPoint・PDFのファイルをドロップし、「閲覧用HTML」を選んで「書き出す」を押します。変換後に「HTMLを開く」を押すと、ブラウザーで読めます。

CLIを試すには、展開先でターミナルを開いてください。`report.xlsx` を手元のファイルのパスに置き換えます。

```powershell
.\docredock.cmd export "report.xlsx" --format html -o "report.html"
```

```sh
./docredock export "report.xlsx" --format html -o "report.html"
```

上段はWindows PowerShell、下段はmacOS／Linux用です。CLIでPDFを変換するときは `--allow-pdf` を追加します。Wordで削除した文字を除外するには、変更履歴を「最終版のみ」にするか、CLIで `--revisions final` を指定します。共有する前に出力と警告を確認してください。

## AIツールで使う

Claude Code・GitHub Copilot・Codexに、DocRedockによる本文抽出と、必要に応じた原本の直接確認を案内する設定を追加できます。GUIでは画面上部の「AI連携」を使います。CLIでは`docredock agents install`（Windowsでは`docredock.cmd agents install`）を実行します。解除するときは`docredock agents uninstall`です。設定後は、開いているAIツールを再起動してください。詳しくは[目的別ガイド](docs/ja/quick-tasks.md)を参照してください。

## 更新

起動時にPublic Betaを含む非draftの公開版をバックグラウンド確認し、新版があれば現在版と最新版を表示します。「更新を確認」で手動確認もできます。更新は自動インストールされません。「リリースページを開く」からOS／CPUに合うパッケージを取得し、SHA-256と署名状況を確認して置き換えてください。`DOCREDOCK_DISABLE_UPDATE_CHECK=1`で起動時確認を無効化できます。

- [利用ガイド](docs/ja/user-guide.md)
- [対応機能](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [この版の変更点](release-docs/RELEASE_NOTES_v0.4.1.md)

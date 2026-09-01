# DocRedock パッケージ・クイックスタート

日本語 / English guide: `QUICKSTART.en.md`

現在のバージョンはアプリ上部に常時表示されます。CLIでは`docredock --version`で確認できます。

## 起動

- Windows: `DocRedock.exe`。CLIは`docredock.cmd`を使います。
- macOS: `DocRedock.app`。CLIはターミナルから`./docredock`を使います。
- Linux: 展開先で`./DocRedock`、CLIは`./docredock`を使います。ユーザー領域へ導入する場合は`./install.sh`、削除は`./uninstall.sh`です。既定の導入先は`$HOME/.local`です。

## 更新

起動時にPublic Betaを含む非draftの公開版をバックグラウンド確認し、新版があれば現在版と最新版を表示します。「更新を確認」で手動確認もできます。更新は自動インストールされません。「リリースページを開く」からOS／CPUに合うパッケージを取得し、SHA-256と署名状況を確認して置き換えてください。`DOCREDOCK_DISABLE_UPDATE_CHECK=1`で起動時確認を無効化できます。

- [利用ガイド](docs/ja/user-guide.md)
- [対応機能](docs/ja/supported-features.md)
- [セキュリティとプライバシー](docs/ja/security-and-privacy.md)
- [この版の変更点](release-docs/RELEASE_NOTES_v0.2.1.md)

# 実験機能

日本語 | [English](../en/experimental-features.md)

> v0.1.3では、ここにあるワークフローはサポート対象外の実験機能です。明示的に有効化しない限り実行できません。

GUIまたはCLIを起動する前に環境変数を設定します。

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

PowerShell:

```powershell
$env:DOCREDOCK_ENABLE_EXPERIMENTAL = "1"
```

この配布版GUI／CLIの制御は、roundtrip／audit出力、restore、render、diff、rebase、pack、unpack、migrate、PDF経路に適用されます。公開ライブラリAPIは技術者向けの表面で、この入口環境変数gateを強制しません。DOCX／XLSX／PPTXの閲覧用出力は設定なしで利用できます。

`.drmd`や`.drmdpkg`には元文書または復元情報が含まれる場合があり、元文書と同じ機密管理が必要です。F0／F1テストやパッケージsmoke testは技術的な回帰証跡であり、レイアウト保持を含む利用者サポートの約束ではありません。

リリース契約は[対応機能](supported-features.md)、取り扱いは[セキュリティとプライバシー](security-and-privacy.md)を参照してください。

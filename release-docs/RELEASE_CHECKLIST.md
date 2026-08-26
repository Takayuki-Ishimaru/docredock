# 公開前チェックリスト

日本語 | [English](RELEASE_CHECKLIST.en.md)

この文書は、公開用 commit/tag と各 OS 向け配布物を作る際の再利用可能な基準テンプレートです。ここにある未チェック欄は過去リリースが未確認だったことを示す証跡ではありません。各リリースの実行結果は、リリースワークフローが生成するチェック済みの `RELEASE-EVIDENCE.md`（workflow run URL、commit、成果物ハッシュを含む）を正本とします。必須の自動検査が一つでも失敗した場合は公開しません。

PDFの変換・レンダリングと元ファイル形式への復元はv0.1.3のサポート対象外で、`DOCREDOCK_ENABLE_EXPERIMENTAL=1`により明示的にgateされ、利用者向けsmoke testから除外します。署名・notarization は設定されている場合に適用しますが、証明書がないことだけを理由に Public Beta の配布を停止しません。各配布物には適用状況を記録します。

## 現状監査メモ（2026-08-26）

- 本体ライセンスは MIT License に確定しています。
- DocRedock.sln はローカルに用意された .NET 10.0.400 SDKで Release ビルドに成功し、警告 0、エラー 0 でした。
- DocRedock への名称変更は作業ツリー上で完了しています。公開前にパス移動と識別子移行を一つの整合した commit としてレビューする必要があります。
- output/ と outputs/ には変換結果、視覚回帰、過去出力、元 Office 文書を含む資材があります。一部は現在の Git index で追跡されており、.gitignore だけでは公開を防げません。
- .codex/、.mcp.json、.tokenlighten/、AGENTS.md、CLAUDE.md、.github/copilot-instructions.md は製品に不要なローカル／AI ツール設定です。個人環境の絶対パスを含むファイルがあります。
- DOCX/PPTX fixture の README にローカル環境の絶対パスがあります。公開するなら再現可能な相対パスへ書き換え、公開しないなら fixture とともに除外します。
- CI は tools/conversion-qa/run.py を実行します。QA ハーネスや必要 fixture を非公開にする場合、公開 CI からこの step を外すか、公開可能な合成 fixture だけで動く形へ変更する必要があります。
- ルートに Office の一時ロックファイルと検証用 workbook が存在します。公開対象へ混入させません。

## P0: 公開ブランチの確定

- [ ] 製品名、namespace、assembly、solution、プロジェクトディレクトリ、文書リンクを DocRedock に統一した
- [ ] 旧製品名・旧プロジェクト名・旧形式識別子が意図せず残っていない
- [ ] rename を含む全変更をレビューし、削除と新規追加が一対一であることを確認した
- [ ] README の画像と文書リンクをクリーン clone 上で確認した
- [ ] 空の test ファイル、Office の ~$ 一時ファイル、ルート直下の検証用 workbook を除外した
- [ ] output/、outputs/、artifacts/、tmp/、.tmp/ を公開ブランチの追跡対象から外した
- [ ] .codex/、.mcp.json、.tokenlighten/、AGENTS.md、CLAUDE.md、ローカル AI 指示ファイルを除外した
- [ ] 公開対象一覧が PUBLICATION_SCOPE.md と一致する

## P0: テストと fixture

- [ ] tests/DocRedock.Tests のテストコードを公開する方針を確認した
- [ ] 各バイナリ fixture が完全な合成物であることを確認した
- [ ] fixture 内に個人情報、顧客情報、社内情報、認証情報、固有の文書プロパティがない
- [ ] fixture 内の画像、フォント、テンプレートを再配布できる
- [ ] fixture ごとに生成元、ライセンス、生成手順、SHA-256 を記録した
- [ ] 大容量の目視検証コーパスと生成済み結果を除外した
- [ ] tools/conversion-qa を公開するか決め、CI と同じ範囲にそろえた
- [ ] README や生成スクリプトから個人環境の絶対パスを除去した

## P0: セキュリティとプライバシー

- [ ] tracked、untracked、release archive の三つを秘密情報スキャンした
- [ ] API key、token、秘密鍵、接続文字列、個人パス、内部 URL が含まれていない
- [ ] .drmd/.drmdpkg、元文書、export/restore report が意図せず含まれていない
- [ ] Git hosting の Private vulnerability reporting を有効化した
- [ ] ルート SECURITY.md に対象バージョン、非公開連絡方法、初動目安を記載した
- [ ] docs/ja/security-and-privacy.md の説明が現行実装と一致する

## P0: ライセンスと由来

- [x] 本体ライセンスを MIT License とする方針を確定した
- [ ] LICENSE の著作権表記と公開主体を法務／権利者が確認した
- [ ] THIRD-PARTY-NOTICES.txt が最新の lock file と一致する
- [ ] licenses/allowlist.json の全依存関係を再検査した
- [ ] Noto Sans JP の OFL 文書、配布元、ハッシュを確認した
- [ ] ブランド素材と test fixture の再配布権を確認した
- [ ] provenance/ の記録が採用コードと一致する
- [ ] SBOM に対象 RID、commit、配布物内の実ファイルと SHA-256 を記録し、成果物 provenance／attestation と結び付けた

## ビルドとテスト

クリーン clone と固定 SDK で次を実行します。名称変更完了後の最終パスを使います。

```sh
dotnet restore DocRedock.sln --locked-mode
dotnet build DocRedock.sln --configuration Release --no-restore
dotnet test tests/DocRedock.Tests/DocRedock.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tools/LicenseAudit/LicenseAudit.csproj --configuration Release -- --root . --output artifacts
```

- [ ] restore が lock file を変更せず成功した
- [ ] Release build が警告 0、エラー 0 で成功した
- [ ] 全テストが成功し、skip の理由をレビューした
- [ ] 公開 CI に conversion-qa を残す場合、合成 fixture だけで成功した
- [ ] LicenseAudit が成功し、SBOM を生成した
- [ ] git status が期待した生成物以外で clean である

## 配布物

- [ ] win-x64、win-arm64、osx-x64、osx-arm64、linux-x64、linux-arm64 を publish した
- [ ] 各成果物を対象 OS/CPU の実機または信頼できる CI runner で展開し、CLI と GUI バイナリを検証した
- [ ] headless CI では Windows GUI の PE 形式・対象 CPU、macOS GUI の Mach-O 形式・対象 CPU・実行権限を検証し、Linux では Xvfb 上の GUI 起動も確認した
- [ ] CLIのv0.1.3バージョン、visible／complete／sanitized、実験機能gate、DOCX／XLSX／PPTX readable export、F0 SHA比較、F1編集、pack/unpack、改ざん拒否を確認した（復元結果は機械的回帰試験のみで、v0.1.3のユーザーサポートを意味しない）
- [ ] PDF変換と元ファイル形式への反映がv0.1.3でサポート対象外であり、DOCREDOCK_ENABLE_EXPERIMENTAL=1が必要なことをREADMEとリリース証跡に記録した
- [ ] 表示可能な実環境で GUI の内容ポリシー選択、complete 警告、DOCREDOCK_DISABLE_UPDATE_CHECK=1、DOCX／XLSX／PPTX の閲覧用Markdownの見た目を確認した
- [ ] macOS の .app bundle と Windows 実行ファイルについて、署名／notarization を設定時のみ適用し、未設定時も未署名状態を明示して継続する
- [ ] 実行ファイルへバージョンと commit を追跡できる情報を付与した
- [ ] 配布アーカイブに LICENSE、THIRD-PARTY-NOTICES、日英 README/QUICKSTART／セキュリティ文書、実ファイル連携 SBOM、provenance、内部チェックサム、署名状況を含めた
- [ ] 配布アーカイブに tests、fixture、output、source document、debug symbol、ローカル設定がない
- [ ] 各アーカイブの SHA-256 を生成した
- [ ] 生成したアーカイブを別ディレクトリへ展開し、そこから smoke test した

## 文書と公開ページ

- [ ] README の概要、コマンド、対応 OS、ファイル名が最終成果物と一致する
- [ ] USER_GUIDE.md の GUI/CLI 手順を新規利用者として再実行した
- [ ] 日本語版と英語版のリリース文書が同じ要件を網羅している
- [ ] FORMAT_CAPABILITY_MATRIX.md が現行実装とテスト結果に一致する
- [ ] Readable Markdown が復元不可であることを明示した
- [ ] .drmd/.drmdpkg が元文書を含み得ることを明示した
- [ ] OCR、Tesseract、Mermaid、PDF rasterizer の同梱有無を明示した
- [ ] 既知の制約と破壊的変更をリリースノートへ記載した
- [ ] CONTRIBUTING.md、CODE_OF_CONDUCT.md、SECURITY.md の公開方針を確定した

## リリース承認

- [ ] 公開 commit/tag が保護され、CI が成功している
- [ ] source archive の全ファイル一覧を PUBLICATION_SCOPE.md と照合した
- [ ] binary archive の全ファイル一覧を PUBLICATION_SCOPE.md と照合した
- [ ] SHA256SUMS、SBOM、provenance、attestations、リリースノート、チェック済み RELEASE-EVIDENCE.md を公開ページへ添付した
- [ ] 既知の P0/P1 問題がなく、残る制約を利用者向けに文書化した
- [ ] 公開責任者が最終成果物のハッシュを承認した
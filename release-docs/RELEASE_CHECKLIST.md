# 公開前チェックリスト

日本語 | [English](RELEASE_CHECKLIST.en.md)

この文書は、公開用 commit/tag と各 OS 向け配布物を作る際の再利用可能な基準テンプレートです。ここにある未チェック欄は過去リリースが未確認だったことを示す証跡ではありません。各リリースの実行結果は、リリースワークフローが生成するチェック済みの `RELEASE-EVIDENCE.md`（workflow run URL、commit、成果物ハッシュを含む）を正本とします。Release Owner が証跡と公開判断を所有し、CI/QA Owner が技術ゲートを実行します。必須ゲートが一つでも未完了または失敗している場合は公開しません。

デスクトップGUIのPDF入力はv0.1.6で既定利用できます。CLIのPDF変換・復元・生成、および往復編集や元ファイル形式への復元は実験機能で、`DOCREDOCK_ENABLE_EXPERIMENTAL=1`により明示的にgateされます。署名・notarizationは設定されている場合に適用し、各配布物に適用状況を記録します。

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
- [ ] 最終archiveに.ttf/.ttc/.otf/.otc/.woff/.woff2のfont binaryが同梱されていないことを確認し、PDF smoke testで使うsystem／user fontのライセンスを記録した
- [ ] ブランド素材と test fixture の再配布権を確認した
- [ ] provenance/ の記録が採用コードと一致する
- [ ] SBOM に対象 RID、commit、配布物内の実ファイルと SHA-256 を記録し、成果物 provenance／attestation と結び付けた

## P0: v0.1.6 visual semantics

- [ ] DOCX／XLSX／PPTX／PDF の合成 fixture で、認識対象ごとに native projection、semantic projection、visual fallback、明示的診断のいずれかが残る
- [ ] `recognized = semantic projection + visual fallback + explicitly diagnosed omission` のaccountingを形式別・文書全体で照合した
- [ ] native connection、geometry inference、unresolved connector、edge label、unsupported visual のstable diagnosticを確認した
- [ ] 既存のparagraph／list／table／image／OCR出力に回帰がなく、出力markerと順序が決定的である
- [ ] 公開バイナリのsmokeでexit code、marker、diagnostic、各count、fixture SHA-256、output SHA-256を`RELEASE-EVIDENCE.md`へ保存した
- [x] DOCX connectorとPDF vector topologyは条件付き対応としてのみ記載し、完全drawing／SmartArt／任意vector graph再構成を対応済みと記載していない

## ビルドとテスト

ローカル事前検証（2026-08-29、.NET 10.0.400）では、main 359件とGUI headless 4件が失敗0／skip 0で成功し、osx-arm64 self-contained CLI／GUI publishと抽出済みbinary smokeも成功しました。これは以下のclean clone、全RID、署名／notarization、公開証跡ゲートを完了扱いにはしません。

クリーン clone と固定 SDK で次を実行します。

```sh
dotnet restore DocRedock.sln --locked-mode
dotnet build DocRedock.sln --configuration Release --no-restore
dotnet test tests/DocRedock.Tests/DocRedock.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/DocRedock.Gui.HeadlessTests/DocRedock.Gui.HeadlessTests.csproj --configuration Release --no-build --no-restore
dotnet run --project tools/LicenseAudit/LicenseAudit.csproj --configuration Release -- --root . --output artifacts
```

- [ ] restore が lock file を変更せず成功した
- [ ] Release build が警告 0、エラー 0 で成功した
- [ ] 全テストが成功し、skip の理由をレビューした
- [ ] 公開 CI に conversion-qa を残す場合、合成 fixture だけで成功した
- [ ] LicenseAudit が成功し、SBOM を生成した
- [ ] 同一入力・同一設定で再実行し、期待する出力と診断が決定的である
- [ ] git status が期待した生成物以外で clean である

## 配布物

- [ ] win-x64、win-arm64、osx-x64、osx-arm64、linux-x64、linux-arm64 を publish した
- [ ] 各成果物を対象 OS/CPU の実機または信頼できる CI runner で展開し、CLI と GUI バイナリを検証した
- [ ] headless CI ではパッケージ前にAvalonia headlessでMainWindowを構築し、Windows GUIのPE形式・CPU、macOS GUIのMach-O形式・CPU・実行権限を検証し、LinuxではXvfb上で実配布GUI子プロセスの生存を確認した
- [ ] CLIのv0.1.6バージョン、visible／complete／sanitized、実験機能gate、DOCX／XLSX／PPTX／PDF readable export、F0 SHA比較、F1編集、pack/unpack、改ざん拒否を確認した
- [ ] 展開後の実バイナリでDOCX／XLSX／PPTX／PDF visual-semantics smokeを実行し、結果をリリース証跡に記録した
- [ ] GUIのPDF入力が既定利用可能であること、CLIのPDF変換／復元／生成にはDOCREDOCK_ENABLE_EXPERIMENTAL=1が必要なことをREADMEとリリース証跡に記録した
- [ ] 表示可能な実環境で GUI の内容ポリシー選択、complete 警告、DOCREDOCK_DISABLE_UPDATE_CHECK=1、DOCX／XLSX／PPTX／PDF入力と閲覧用Markdownの見た目を確認した
- [ ] macOS の .app bundle と Windows 実行ファイルについて、署名／notarization を設定時のみ適用し、未設定時も未署名状態を明示して継続する
- [ ] 実行ファイルへバージョンと commit を追跡できる情報を付与した
- [ ] 配布アーカイブに LICENSE、THIRD-PARTY-NOTICES、日英 README/QUICKSTART／セキュリティ文書、実ファイル連携 SBOM、provenance、内部チェックサム、署名状況を含めた
- [ ] 配布アーカイブに tests、fixture、output、source document、debug symbol、ローカル設定がない
- [ ] 各アーカイブの SHA-256 を生成した
- [ ] 生成したアーカイブを別ディレクトリへ展開し、そこから smoke test した
- [ ] 公開停止、成果物撤回、旧版へのrollback、修正版再展開の手順と責任者を確認した

## 文書と公開ページ

- [ ] README の概要、コマンド、対応 OS、ファイル名が最終成果物と一致する
- [ ] docs/ja/user-guide.md の GUI/CLI 手順を新規利用者として再実行した
- [ ] 日本語版と英語版のリリース文書が同じ要件を網羅している
- [ ] FORMAT_CAPABILITY_MATRIX.md が現行実装とテスト結果に一致する
- [ ] Readable Markdown が復元不可であることを明示した
- [ ] .drmd/.drmdpkg が元文書を含み得ることを明示した
- [ ] OCR、Tesseract、Mermaid、PDF rasterizer の同梱有無を明示した
- [ ] 既知の制約と破壊的変更をリリースノートへ記載した
- [ ] CONTRIBUTING.md、CODE_OF_CONDUCT.md、SECURITY.md の公開方針を確定した
- [ ] `RELEASE-EVIDENCE.md` のworkflow生成者、CI/QA Owner、Release Owner、保存場所、commit、成果物hashを記録した

## リリース承認

- [ ] 公開 commit/tag が保護され、CI が成功している
- [ ] source archive の全ファイル一覧を PUBLICATION_SCOPE.md と照合した
- [ ] binary archive の全ファイル一覧を PUBLICATION_SCOPE.md と照合した
- [ ] SHA256SUMS、SBOM、provenance、attestations、リリースノート、チェック済み RELEASE-EVIDENCE.md を公開ページへ添付した
- [ ] 既知の P0/P1 問題がなく、残る制約を利用者向けに文書化した
- [ ] 公開責任者が最終成果物のハッシュを承認した

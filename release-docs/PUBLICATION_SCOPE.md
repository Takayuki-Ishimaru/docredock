# 公開対象ポリシー

日本語 | [English](PUBLICATION_SCOPE.en.md)

この文書は、DocRedock の公開ソースリポジトリとエンドユーザー向け配布物に何を含めるかの正本です。判断基準は、再現性、利用者への説明責任、ライセンス、機密性、配布サイズです。

## 結論

動作検証用の成果物一式は公開不要です。一方で、公開ソースのビルドと安全性を第三者が再現できるよう、テストコード、セキュリティ境界テスト、最小の合成 fixture は公開する価値があります。

したがって、次のように分けます。

- 公開する: 製品ソース、仕様、スキーマ、単体／統合テストのコード、再生成可能で権利処理済みの最小 fixture。
- 条件付きで公開する: QA ハーネス、fixture 生成スクリプト、期待値。公開 CI が参照するなら必須です。
- 公開しない: 実文書コーパス、生成済み変換結果、目視比較結果、過去の試行、ローカルツール設定、キャッシュ。

## 公開ソースリポジトリ

### 必ず含める

| パス | 理由 |
| --- | --- |
| README.md | 製品概要、最短の導入方法、主要な制約 |
| LICENSE | 本体ライセンス（MIT） |
| THIRD-PARTY-NOTICES.txt | 実行時・開発時依存と同梱資産の通知 |
| DocRedock.sln、Directory.Build.props、global.json | 再現可能なビルド入口と SDK 条件 |
| src/DocRedock.* | 製品本体 |
| schemas/ | DRMD とレポートの公開契約 |
| docs/DRMD_MARKDOWN_SPEC.md | 往復編集形式の仕様 |
| docs/DRMD_AI_EDITING_RULES.md | 人間／AI の安全な編集契約 |
| docs/FORMAT_CAPABILITY_MATRIX.md | 対応範囲と非対応範囲 |
| docs/examples/ | 仕様に対応する小さな例 |
| release-docs/ | 利用案内、公開範囲、チェックリスト |
| licenses/ | 許可ライセンス一覧と同梱フォントのライセンス |
| provenance/ | 技術由来と第三者コードの追跡 |
| 各 packages.lock.json | 依存関係の固定と監査再現性 |
| tests/DocRedock.Tests のテストコード | 挙動、安全境界、回帰を第三者が検証できる |
| tools/LicenseAudit | SBOM とライセンス検査の再現性 |
| tools/publish-cli.*、tools/publish-gui.* | 公式配布物の作り方を監査可能にする |
| .github/workflows/ci.yml | 公開ブランチで実行する品質ゲート |

### 条件付きで含める

| パス／種類 | 公開条件 |
| --- | --- |
| tests/DocRedock.Tests/Fixtures の Office/PDF バイナリ | 完全に合成され、個人・顧客情報がなく、フォント・画像を含む全素材の再配布権が明確で、テストに不可欠 |
| fixture 生成スクリプトと expectations | 固定された依存で再生成でき、絶対パスやローカル環境情報を除去済み |
| tools/conversion-qa | 公開 CI で使う場合は公開。内部だけで使うなら CI からも外す |
| docs/BRAND_DESIGN_GUIDELINES.md | 第三者による名称・ロゴ利用条件を明確にしたうえで公開 |
| assets/brand/docredock | README、Web、実行ファイルに実際に必要なサイズだけを公開。master、SNS バリエーションは別配布でもよい |
| docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_*.md | 入力データの公開可否、測定手順、再現性、誤解を招く比較表現をレビュー後に公開 |
| docs/REVIEW_IMPROVEMENTS_*.md | ロードマップとして公開する意思がある場合だけ。内部レビュー記録のままなら除外 |
| .github の非 CI 設定 | 公開コントリビューターに必要で、ローカル製品や個人パスに依存しない場合だけ |

### 公開しない

以下は公開ブランチ、ソースアーカイブ、バイナリ配布物のいずれからも原則除外します。

```gitignore
.codex/
.codex-work/
.mcp.json
.tokenlighten/
.playwright-cli/
.vscode/
.idea/
AGENTS.md
CLAUDE.md
.github/copilot-instructions.md

.tmp/
tmp/
bin/
obj/
TestResults/
artifacts/
output/
outputs/

*.drmd/
*.drmd
*.drmdpkg
~$*
.DS_Store
```

除外理由は次のとおりです。

- .codex、.mcp.json、.tokenlighten、AGENTS.md、CLAUDE.md: 個人環境、AI/MCP 運用、絶対パスなど製品に不要な情報を含み得ます。
- output、outputs、artifacts: 変換結果、目視比較、公開前ビルド。元文書や抽出内容を再包含する可能性があります。
- .tmp、tmp、bin、obj、TestResults: SDK、キャッシュ、一時ファイル、ビルド／テスト結果です。
- .drmd、.drmdpkg: 元文書バイナリと派生情報を含むため、fixture として個別承認した場合を除き公開しません。
- ~$*: Office が作る一時ロックファイルです。

.gitignore は未追跡ファイルを防ぐだけで、すでに追跡されている output/ や outputs/ を配布対象から外しません。公開ブランチ上の追跡状態を別途整理してください。

## テスト資材の判断基準

### 公開するテスト

- パーサー、変換、往復編集、CLI、GUI workflow のテストコード
- traversal、DTD、サイズ制限、不審な数式などのセキュリティ境界テスト
- 数行／数セル程度の合成 fixture、またはテスト内で生成できる fixture
- 仕様上の正しい例と不正な例

### 公開しない検証セット

- 顧客文書、社内文書、実運用データを加工したもの
- 手作業で作成し再配布権が確認できない DOCX/XLSX/PPTX/PDF
- OCR 精度比較用のスクリーンショット、フォント、画像で権利が不明なもの
- restored、previous-export、visual-regression、comparison-summary などの生成結果
- 同じ挙動を小さな合成 fixture で再現できる大容量コーパス

公開 CI がバイナリ fixture を必要とする場合は、fixture ごとに README へ生成元、生成コマンド、ライセンス、SHA-256、含まれるフォント／画像の由来を記録します。可能なら CI 内で生成し、生成物はコミットしません。

## エンドユーザー向け配布物

OS/CPU ごとの配布アーカイブには、原則として次だけを含めます。

- DocRedock または DocRedock.Cli の実行に必要なファイル
- README または QUICKSTART
- LICENSE
- THIRD-PARTY-NOTICES.txt
- sbom.cdx.json
- バージョン情報とリリースノート

リリースページでは各アーカイブの SHA-256 を SHA256SUMS として公開します。テスト、fixture、ソース、生成スクリプト、ブランド master、変換結果はバイナリ配布物へ含めません。

## 運用ルール

1. 公開は作業中のディレクトリをそのままコピーせず、クリーンな公開用 commit/tag から組み立てます。
2. 許可リスト方式で成果物を作り、想定外ファイルが一つでもあれば失敗させます。
3. 公開前にアーカイブの全ファイル一覧、秘密情報、絶対パス、個人名、元文書の混入を検査します。
4. CI が参照するファイルを除外する場合は、CI 定義も同じ変更で更新します。
5. ソース用とバイナリ用の内容一覧をリリースごとに保存し、チェックサムと SBOM を対応付けます。
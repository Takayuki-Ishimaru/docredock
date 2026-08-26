# DocRedock 公開ドキュメント

日本語 | [English](README.en.md)

<p align="center">
  <img src="../assets/brand/docredock/app-icons/png/DocRedock-appicon-128x128.png" alt="DocRedock アプリアイコン" width="96" height="96">
</p>

このディレクトリは、DocRedock の公開に必要な利用者向け文書と、公開物を組み立てる保守者向け基準をまとめた入口です。

> v0.1.1 は Public Beta です。本番運用向けの安定版ではありません。既知の制約と署名状況はリリースノートを確認してください。
>
> **現段階の利用制限:** PDF の変換・レンダリング、および元ファイル形式への復元は動作確認が不十分で、正常に動かない可能性があります。現在は使用しないでください。社内評価で使用してよい範囲は、DOCX／XLSX／PPTX からの一方向の「Markdownのみ」出力です。署名・notarization は任意で、証明書がないことだけを理由に配布を停止しません。

[GitHubから v0.1.1 Public Beta をダウンロード](https://github.com/Takayuki-Ishimaru/docredock/releases/tag/v0.1.1) · [日本語リリースノート](RELEASE_NOTES_v0.1.1.md) · [English release notes](RELEASE_NOTES_v0.1.1.en.md)

## AIに渡す前にMarkdownへ変換するメリット

Office文書をAIが直接読む場合、シート、セル範囲、図形、関連ファイルなどを何度も探索・抽出する必要があります。DocRedockは文書を意味の分かるMarkdownへ投影するため、AIは見出し、表、本文を通常のテキストとして効率よく読めます。

同一の合成XLSXへ12問を質問したローカル検証では、`.md` はExcel直接参照より入力トークンが **74.1%少なく**、処理時間は34.9秒対155.5秒でした。テキスト問題は11/11正答です。`.md + .drmd` は画像内限定情報を含め12/12正答しながら、Excelより入力トークンが **58.9%少ない**結果でした。

- 🧠 テキスト中心の検索、要約、質問回答は `.md` が最小・最速
- 🖼️ 画像、OCR、原資料の根拠も必要なら `.md + .drmd`
- 🔄 元Office/PDFは正本のまま保持し、AIにはMarkdown投影を渡す
- ✅ 編集時は `verify` と `diff` を通してから `restore` する

数値は特定fixture・環境での実測であり、すべてのAIや文書で同じ結果を保証するものではありません。詳細は[検証条件と全結果](../docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_2026-08-25.md)を参照してください。

## 利用者向け

- [RELEASE_NOTES_v0.1.1.md](RELEASE_NOTES_v0.1.1.md): 最新 Public Beta の修正内容、ダウンロード、既知の制約
- [USER_GUIDE.md](USER_GUIDE.md): インストール、GUI/CLI の基本操作、Readable Markdown と往復編集の違い
- [SECURITY_AND_PRIVACY.md](SECURITY_AND_PRIVACY.md): ローカル処理、信頼境界、OCR・外部ツール、脆弱性報告
- [対応形式一覧](../docs/FORMAT_CAPABILITY_MATRIX.md): DOCX、XLSX、PPTX、PDF の編集可能範囲と制約
- [DRMD Markdown 仕様](../docs/DRMD_MARKDOWN_SPEC.md): 往復編集用 Markdown の形式
- [AI 編集ルール](../docs/DRMD_AI_EDITING_RULES.md): AI に DRMD Markdown を編集させる際の必須ルール

## 保守者向け

- [PUBLICATION_SCOPE.md](PUBLICATION_SCOPE.md): 公開するファイル、除外するファイル、テスト資材の扱い
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md): 公開前の必須確認、ビルド、署名、ライセンス、配布物検証

## 公開時の基本方針

1. DocRedock はローカルファーストです。組み込み処理は文書を外部サービスへ送信しません。
2. Readable Markdown は閲覧用の一方向出力です。元文書へ戻す用途には使えません。
3. `.md + .drmd` は将来元ファイル形式へ復元する可能性がある場合の保存形式ですが、現段階では復元機能を使用しません。
4. 対応外の構造は、黙って単純化せず診断または拒否として扱います。
5. 公開ソースには再現性に必要なテストコードを含めますが、ローカル設定、生成結果、目視検証用コーパスは原則として含めません。

## リリースの二つの成果物

- 公開ソースリポジトリ: ソース、仕様、スキーマ、再現可能なテスト、ライセンス情報を含みます。
- エンドユーザー向け配布物: 対象 OS/CPU の実行ファイル、LICENSE、THIRD-PARTY-NOTICES、日英の利用案内とセキュリティ文書、成果物ファイルのチェックサム、実ファイルへ紐づく SBOM、provenance、署名状況を含みます。テストや開発用ツールは含めません。リリースページにはアーカイブ単位のチェックサム、SBOM／provenance、実行済みチェックの証跡も添付します。

両者の正確な境界は PUBLICATION_SCOPE.md を正本とします。

## ライセンス

DocRedock 本体は [MIT License](../LICENSE) で公開します。第三者依存関係と同梱資産にはそれぞれのライセンスが適用されるため、[THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) も確認してください。
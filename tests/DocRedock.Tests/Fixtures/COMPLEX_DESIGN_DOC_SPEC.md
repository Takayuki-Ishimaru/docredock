# 複合設計書 fixture 仕様（DOCX / PPTX / PDF 並行コーパス）

作成: 2026-08-25。readable（`.md`）変換品質を検証するため、架空の経費精算システム設計書を DOCX/PPTX/PDF で表現した合成コーパスである。方針: **同じ ID 体系・同じ事実を、各形式の慣用的な作り方で表現する**。顧客文書、個人情報、実運用データは使用していない。

## 1. 共通コンテンツ

- 事実の正本: 公開済み fixture と隣接する `expectations.json`。形式間で ID と値を一致させる（IF-05 / TR-09 / API-01 / TC-009 / NFR-AVL-02 / ISSUE-03 / BF-xx / EXP-Exxx など）。文章の言い回しや構成は形式ごとに変えてよい。
- 埋め込み画像: この架空シナリオ用に作成した IMG-01（文字なし）と IMG-02（日本語文字入り）を fixture 内に保持する。外部素材や実文書からの画像は使用しない。
- **禁止事項**: 文字列 `OCR-JP-20260823-017` は IMG-02 画像の中にしか存在しない。fixture の本文テキストには絶対に書かないこと（12 問 QA の Q12 が「画像内限定情報」の検証であるため）。

### 12 問 QA 事実リスト（全形式が本文テキストで回答可能にする。Q12 のみ画像で）

1. 文書番号・版・状態（表紙）
2. IF-05 の送信元・送信先・Timeout・Retry・失敗時処理
3. TR-09 の遷移元・イベント・ガード・遷移先・副作用
4. TX-01 の書き込み対象と HTTP 応答後のイベント配送
5. BR-01 の金額閾値と承認経路
6. F-06 の必須条件・最大サイズ・エラーコード・メッセージ・表示位置
7. API-01 の Method・Path・応答・Idempotency-Key 制約
8. TC-009 の入力・経路・HTTP・最終状態・関連設計
9. NFR-AVL-02 の RTO・RPO・測定方法・実現手段・合意状態
10. ISSUE-03 の論点・作業・Owner・期限・Status
11. outbox_event.status の候補値と retry_count 上限
12. IMG-02 の画像内だけに存在する文字列（本文に書かない）

## 2. DOCX 要素要件（`Fixtures/Docx/complex-design-doc.docx`）

想定文書: Word で書かれた設計書本文。python-docx を基本とし、足りない機能は OOXML を直接注入する。

| ID | 要素 | 狙う変換ギャップ |
| --- | --- | --- |
| D01 | 見出し 1〜4 階層の章立て + 本文段落 | 見出しレベルの保持（回帰ガード） |
| D02 | Heading 7 を付録に 1 箇所 | HeadingLevel 正規表現が 1-6 のみ |
| D03 | 目次（TOC フィールド、キャッシュ済みエントリ付き） | フィールド未対応で地の文化する |
| D04 | ヘッダー（文書名）/ フッター（PAGE フィールドのページ番号） | ヘッダー/フッターが本文に混入 |
| D05 | セクション区切り: 縦 → 横向き（ワイド表）→ 縦。第2セクションは別ヘッダー | sectPr 未解釈 |
| D06 | 通常表（網掛け+太字ヘッダー行）: 外部 I/F 一覧など | 回帰ガード |
| D07 | gridSpan 横結合 + vMerge 縦結合を含む表: 状態コード表など | 表結合が完全未対応で列がずれる |
| D08 | ネスト表 1 箇所 | 内側表がフラットな文字列化 |
| D09 | 箇条書き 3 階層ネスト | 回帰ガード（レベル保持） |
| D10 | 番号付きリスト（間に段落を挟んで継続） | 番号付きが `- ` に化ける |
| D11 | Code 段落スタイルの HTTP/JSON ブロック + インラインコード run | 回帰ガード |
| D12 | ハイパーリンク: 外部 URL + 文書内ブックマーク相互参照 | Link ノード二重出力・URL 消失 |
| D13 | 画像: inline + キャプション段落 / 浮動(anchor)配置 各1 | 回帰ガード + anchor 配置 |
| D14 | テキストボックス（コールアウト注記） | TextBox 二重出力 |
| D15 | run 装飾: 太字/斜体/下線/取消線/文字色/ハイライト | 色・ハイライトの受け皿なし |
| D16 | 脚注 1 件 + 文末脚注 1 件 | 脚注は無番号混入・文末脚注は消失 |
| D17 | コメント 1 件 + 未承認の変更履歴（挿入/削除）1 組 | コメント消失・履歴の無区別混入 |
| D18 | 明示的な改ページ（章間） | PageBreak が改行に化ける |

## 3. PPTX 要素要件（`Fixtures/Pptx/complex-design-doc.pptx`）

想定文書: 設計レビュー説明資料（15 枚前後）。python-pptx を基本とし、足りない機能は OOXML を直接注入する。

| ID | 要素 | 狙う変換ギャップ |
| --- | --- | --- |
| P01 | タイトルスライド + セクション区切りスライド | 回帰ガード（role 判定） |
| P02 | buChar 箇条書き 3〜4 階層 | 回帰ガード |
| P03 | buAutoNum 番号付きリスト（手順） | 番号付きが `- ` に化ける |
| P04 | buNone 明示解除の段落を箇条書きと混在 | 回帰ガード |
| P05 | スライド XML に bullet 指定を書かず layout/master 既定を継承するプレースホルダー | マスター/レイアウト継承未解決 |
| P06 | ネイティブチャート: 棒（テスト実施サマリー）+ 円（経費区分内訳）。系列/カテゴリ/値を実データで | chart が空の「図:」になる |
| P07 | SmartArt（承認プロセス）。python-pptx 非対応のため raw part 注入。困難なら省略して README に記録 | SmartArt テキスト完全消失 |
| P08 | グループ図形 + 図形内テキスト + コネクタ（stCxn/endCxn で論理接続）による状態遷移図 | グループ座標破壊・コネクタ接続無視 |
| P09 | 結合セルを含む表（API 一覧） | 表結合未対応 |
| P10 | スピーカーノート（太字・箇条書き入り） | ノートのプレーンテキスト化 |
| P11 | 画像スライド（IMG-01/IMG-02 + キャプション） | 回帰ガード |
| P12 | 2 カラム構成スライド（左右のコンテンツプレースホルダー） | 読み順 |
| P13 | フッター + スライド番号 + 日付プレースホルダー | furniture のスキップ（回帰ガード） |
| P14 | run 装飾: 太字/斜体/下線/取消線/文字色 | 取消線・色の受け皿なし |
| P15 | 回転シェイプ（45°）+ 矢印図形 | rot 未読み取り |

## 4. PDF（作成のみ先行。変換改善は方針決定後）

| ID | ファイル | 生成方法 |
| --- | --- | --- |
| F01 | `Fixtures/Pdf/complex-design-doc.libreoffice.pdf` | D 完成後、DOCX 版を LibreOffice headless で PDF 化（xref stream / ObjStm / CID 日本語フォントを含む実物系）。注意: 日本語フォントを持つ LibreOffice（brew 版等）で生成すること。フォント無し環境では日本語が欠落した PDF になり fixture として無効 |
| F02 | `Fixtures/Pdf/complex-layout.pdf`（既存） | `generate_complex_pdf.py`（reportlab）。維持 |
| F03 | Chromium `--print-to-pdf` 版 | PDF 改善着手時に追加 |

## 5. expectations.json 契約

各 fixture の隣に `<name>.expectations.json` を置く。readable 変換した .md 全文に対して機械判定する。

```json
{
  "target": "complex-design-doc.docx",
  "profile": "readable",
  "items": [
    {
      "id": "D12-1",
      "desc": "外部ハイパーリンクの URL が [text](url) で残る",
      "severity": "goal",
      "type": "contains",
      "value": "](https://example.invalid/spec/expense-api)"
    }
  ]
}
```

- `type`: `contains` / `not_contains` / `unique`（value がちょうど 1 回出現）/ `regex`（検索一致）/ `count`（`min`/`max` 付き）
- `severity`: `guard` = 現状でも通るべき回帰ガード / `goal` = 現状は落ちてよい改善目標（修正が進むと green になる）
- ラチェット運用: green になった goal は guard へ昇格させ、以降の後退を exit code で検知する（2026-08-25 に DOCX/PPTX の全 goal を昇格済み）。guard は「本文保全」を主張し、マーカー等の表現形式の理想は goal に書く。
- `"ocr": true` の項目は OCR 付き第2エクスポート（--ocr auto）で評価され、OCR エンジンが無い環境では skipped（fail 扱いしない）。
- 各要素 ID（D01〜, P01〜）につき最低 1 項目。12 問 QA の事実（ID 文字列など）は `guard` の `contains`/`unique` として全て登録する。`OCR-JP-20260823-017` は `not_contains` の `guard` にする。
- 判定の実装はハーネス（`tools/conversion-qa/`）側。fixture 側は宣言のみ。

## 6. 公開版での再現性

- 公開済みの `.docx` / `.pptx` / `.pdf` と隣接する `expectations.json` を回帰テストの正本とする。
- 公開 CI は `tools/conversion-qa/run.py --all` で fixture を変換し、すべての `guard` を検査する。
- 元の検証ブック、開発専用 generator、生成途中の成果物は製品のビルド・利用・テストに不要なため公開しない。
- fixture は固定日付・固定値のみを持つ合成データで、ネットワーク、乱数、現在時刻、個人・顧客データに依存しない。

## 7. 公開配置

```
tests/DocRedock.Tests/Fixtures/
  COMPLEX_DESIGN_DOC_SPEC.md
  Docx/complex-design-doc.docx
  Docx/complex-design-doc.expectations.json
  Pptx/complex-design-doc.pptx
  Pptx/complex-design-doc.expectations.json
  Pdf/complex-layout.pdf
```

DOCX/PPTXディレクトリのREADMEにfixtureの合成データ方針、対象要素、既知の制限を記録する。

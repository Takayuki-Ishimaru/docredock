# DRMD 改善点の洗い出し（2026-08-23 時点）

検証用ブック `経費精算システム_設計書_検証用.xlsx` の「読みやすいMarkdown」出力（`outputs/readable-preview/`）を元Excel（セル・結合範囲・DrawingML）と突き合わせ、CLIで再現（出力は既存ファイルと同一）、さらに openpyxl で作った別のブック（日付・数値書式・装飾ヘッダー・横並び表）と DOCX/PPTX/PDF サンプル（`.tmp/qa-final/`）でも試した結果と、GUI/CLI のコードを読んだ結果をまとめる。

## 結論（要約）

- 「読みやすいMarkdown」の XLSX 変換は、**検証用ブック 1 冊に合わせたヒューリスティック**（見出しは E 列、ヘッダー名のハードコード、`BF-`/`GW-` ID、シート名キーワード）で動いている。検証用ブックでも横並び領域の混線・ヘッダー取り違え・表の分断が起きており、別のブックではさらに崩れる（日付が `46235`、`1.2` が見出し化、受注一覧に「項目ID／型／桁…」のヘッダーが付く）。
- GUI は「出力フォルダーを毎回選ぶ → 同名があると停止 → 消して再実行」という反復作業に向いておらず、結果を確認する導線もない。
- PDF は日本語（CID フォント）が文字化けし、別の PDF では内部エラーで落ちる。PPTX は箇条書き・装飾が失われる。
- 優先度は、(1) 領域検出とスタイルベースのヘッダー/見出し判定、(2) 数値書式・数式・コードの表示、(3) GUI の反復ループ改善、(4) 図抽出の汎用化、(5) PDF/PPTX、(6) SVG 方針、の順を提案する。

---

## A. 読みやすいMarkdown（XLSX）— 出力品質

### A1. 横並びの領域が行単位で混線する（最重要）
- 症状: `05 画面・入力仕様` は左に画面モック、右に 5.2/5.3/5.4 の表が並ぶ。出力では 5.1 が空になり、フォーム項目（申請日 *／部門コード *…）が 5.2〜5.3 の表や箇条書きの中に散らばり、5.2 の表は 3 つに割れ、`一時保存 — 申請する` のボタン行が 5.4 の中に箇条書きで出る（出力 298〜363 行付近）。`06 API・データ` では 6.1 と 6.2 が 10 列の 1 つの表に合体し、6.4 と 6.5 が 7 列の表に合体、「6.1」「6.4」の見出しが空になる。別ブックの検証でも左右 2 表が 1 表に結合した。
- 原因: 行ごとに「列番号の並び（signature）が完全一致し、行間隔 ≤4」でグループ化しているだけで、矩形領域を認識していない（[ReadableMarkdownSerializer.cs:75-86](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)）。
- 改善案: セルの列帯・結合範囲・罫線/塗りから矩形領域（ブロック）を先に検出し、領域ごとに読み順（上→下、左→右）で出力する。領域の直上/左上の見出しセルをその領域の所属セクションとする。

### A2. 表ヘッダーの推定がハードコードで、別の表に誤適用される
- 症状: 7.2 実施サマリー・8.4 合意状況サマリーに `| 項目 | テスト入力 | 判定 | 期待メッセージ |`、8.3 設計判断に `| 項目ID | 項目名 | 型／桁 | 必須 | 検証ルール | エラーコード |`、7.3/99.2/99.3 に `| 項目 | 内容 | 補足 |` が付き、本来のヘッダー行（`ID | 論点 | …`、`分類 | 確認事項 | 合格基準`、`対象 | ルール | 例`）がデータ行として出る。別ブックでは受注一覧（受注ID/顧客名/受注日…）に「項目ID／項目名／型／桁…」が付いた。
- 原因: `InferHeaders` が「6 列で ID らしき値があれば 5.2 のヘッダー」「4 列で 3 列目が数式なら 5.3 のヘッダー」と検証用ブックの表名をそのまま返す（[ReadableMarkdownSerializer.cs:293-305](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)）。ヘッダー行の判定も固定の語彙リストで 60% 以上一致を要求する（同 16-24, 324-329）。
- 改善案: `styles.xml` の cellXfs（太字・塗り・罫線・中央揃え）を Adapter で読んでヘッダー行を判定する（今は `style_id` を保持するだけで解決していない: [XlsxAdapter.cs:247](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxAdapter.cs)）。スタイルが無い場合は領域の先頭行をヘッダーとし、ドメイン固有のヘッダー名は生成しない。

### A3. 表が分断され、余った行が箇条書きになる
- 症状: 5.2 の F-05 行（左のモックと同じ行）、5.4 の EXP-E004 行、別ブックの「備考が空の行」が `- a — b — c` の箇条書きに落ちる。99.1 図形凡例は 1 行ずつの表 3 つ＋箇条書き 1 行に分裂。
- 原因: 空セルがあると signature が変わりグループが切れる（A1 と同根）。
- 改善案: 領域単位で列を固定し、空セルは空欄として出す。

### A4. 小数や番号が見出し・番号付きリストに化ける
- 症状: 別ブックで `1.2`（達成率 120%）が `### 1.2`、`0.5` が `### 0.5` になった。`1. 注意事項` が B 列にあると見出しにならず平文で出力され、Markdown では番号付きリストとして描画される。
- 原因: `SectionHeadingRegex` が `^\d+(\.\d+)*[.\s]+\S+` のため `1.2` を「1」+「.」+「2」として見出し扱い（[ReadableMarkdownSerializer.cs:491](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)）。トップレベル見出しは「E 列（Column==5）にある」ことが条件になっている（同 340）。
- 改善案: 数値セル（cell_type n）は見出し候補から除外、見出し判定はフォントサイズ/太字/結合幅などスタイルを主、列位置を使わない。平文出力時は行頭の `1. ` `- ` `#` をエスケープする。

### A5. 日付・数値書式が適用されず生値が出る
- 症状: 別ブックで日付が `46235`、金額が `1250000`、割合が `0.85`。検証用ブックでも `12,800 円` 表示のセルが `12800`。
- 原因: Adapter が `<v>` の生値のみ読み、`styles.xml` の numFmt を解決していない（[XlsxAdapter.cs:338-365](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxAdapter.cs)）。
- 改善案: 組み込み書式 ID（日付 14-22、%、#,##0 等）とカスタム書式を解決して表示文字列を作り、readable では表示値、roundtrip では生値を出す。

### A6. 数式が本文に露出して読みにくい
- 症状: 7.1 の判定列が全行 `` `=IF(AG9="未実施",…)` → 未実施 ``、1.4/7.2/8.4 も同様。
- 原因: [ReadableMarkdownSerializer.cs:201-208](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)。
- 改善案: readable の既定は値のみ。`--show-formulas`（または脚注/`title`）をオプションに。

### A7. 整形済みテキスト（HTTP/JSON）が fenced code にならない
- 症状: 6.6 エラー応答例が行末 2 スペースの段落になり、インデントも崩れる（出力 426-434 行）。
- 原因: 複数行セルは一律 `WriteParagraph`（同 406-410）。
- 改善案: 等幅フォント／先頭空白・`{`・`HTTP/` などを検出して ``` で囲む。

### A8. 画面モック（フォーム）の情報が失われる
- 症状: 5.1 画面イメージの項目・値・ボタンが消えるか他節に混入。
- 改善案: A1 の領域検出後、罫線で囲まれた「ラベル＋値」領域は `| 項目 | 値 |` の定義表または箇条書きとして 5.1 配下に出す。

### A9. 空の見出しが残る（5.1 / 6.1 / 6.4）
- A1 の副作用。領域検出で解消するが、「配下に内容が無い見出しは出さない」ガードも入れる。

### A10. シート見出しで本文タイトルの情報が落ちる
- 症状: `## 02 振る舞い図` になり、A1 の `02 振る舞い図（申請状態遷移）`、`06 API・データ設計`、`99 凡例・参考資料` などの括弧書き・正式名称が消える。
- 原因: `IsRedundantTitle` がシート名と前方一致した行を捨てる（同 355-365）。
- 改善案: シート内タイトルを優先し、シート名はフォールバック。

### A11. 合成した見出し・ヘッダーが元にない言葉を増やす
- `### 文書情報`、`| No. | 項目 | 内容 |`、`| 項目 | 内容 |` など（同 88-92, 238-247）。害は小さいが「元にない語」を生成していることは明示（例: コメントや `<!-- inferred -->`）するか、抑制オプションを用意する。

### A12. 文書管理行の表現
- `> 文書ID: … · 版: …` が各シートに繰り返される。問題ではないが、冒頭の「文書情報」に 1 回まとめる方が読みやすい。低優先。

### A13. インライン SVG がファイルの 65% を占める
- 計測: 本文 62,739 文字中 SVG が 41,091 文字（4 図）。
- 影響: エディタで開くと巨大な SVG ブロックが本文を分断する。GitHub など `<svg>` を除去するレンダラでは何も表示されず（`<details>` の Mermaid だけが残る）、逆に Mermaid 非対応のビューアではソースが見える。
- 改善案: 既定は Mermaid フェンスのみ、SVG は `assets/<sheet>-<n>.svg` に別ファイル出力して `![](...)` 参照（またはオプトイン）。

### A14. 自前 SVG プレビューの品質
- 症状: 構成図は IF-06 の線が PostgreSQL の円柱を貫通して「DB→会計」に見える、ラベルがノードに重なる。状態遷移図はラベルが箱に重なり判読しづらい。シーケンス図は概ね良好、業務フローも概ね良好。
- 原因: ノード中心同士を直線で結び、ラベルは中点に置くだけ（[MermaidSvgPreviewRenderer.cs:242-268](../src/DocRedock.Markdown/MermaidSvgPreviewRenderer.cs)）。状態遷移図は BFS 層に蛇行配置（同 126-159）。
- 改善案: 元セル座標を使った配置（状態遷移図は元のセル位置がある）、直交ルーティング／ポート選択、ラベルの線からのオフセット。`mmdc` があればそれで PNG を作る方が早い。

---

## B. 図（Mermaid）抽出

### B1. 検証用ブック固有のキーワード・ID に依存している
- シート名 `振る舞い/シーケンス/フロー/システム概要…` で図種を決める（[XlsxMermaidProjection.cs:25-32](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxMermaidProjection.cs)）、ノードは `BF-`/`GW-` で始まる必要（同 18）、`Expense API`・`TX-` の特別扱い（同 462-466）、`BF-05→BF-07` を捨てる／`BF-10` 以外から終了へは張らない等の個別ルール（同 482-484）、`EndpointScore` の語彙（expenseapi, approvalorchestrator, 会計…）（同 565-581）。
- 影響: 他のブックでは図が出ないか、辺が欠ける。
- 改善案: 図形＋コネクタのトポロジーから汎用に抽出し、シート名やプレフィックスは補助ヒントに格下げ。プロジェクト固有定数は削除。

### B2. 図形内テキスト・グループ図形・コネクタが使われていない
- Adapter は図形のテキストを読むが（[XlsxAdapter.cs:431-432](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxAdapter.cs)）、投影側は「図形のアンカーセルと同じ左上を持つセル領域」しかノードにしない（[XlsxMermaidProjection.cs:420](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxMermaidProjection.cs)）。`grpSp` は無視（XlsxAdapter.cs:421 は `sp`/`cxnSp` のみ）、辺はブロック矢印（rightArrow 等）だけで、`cxnSp` の `stCxn/endCxn` や直線コネクタは使わない（同 325-336）。
- 影響: 一般的な Excel の図（テキスト入りオートシェイプ＋コネクタ＋グループ）はほぼ抽出できない。検証用ブックはラベルがセルにあり図形は空なので通る。
- 改善案: テキスト入り図形をノード、コネクタ（接続 ID）を辺、グループは平坦化してオフセット加算。

### B3. シーケンス図の複合フラグメント・活性化・番号が失われる
- 元シートの `break [validation NG]`、`alt [amount <= 100,000] / [amount > 100,000]`、`課長承認 TASK-MGR`／`部長承認 TASK-DIR` が Mermaid に無い。メッセージ番号は `autonumber` で振り直され、元の `4a/4b` が `3/4` になり、ノートの `3.` は消える（番号を落とすのは [XlsxMermaidProjection.cs:242-246](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxMermaidProjection.cs)）。設計書内の相互参照（TC-002「シーケンス alt 高額経路」等）と食い違う。
- 改善案: フレーム矩形＋`alt/break/opt/loop` ラベル＋ガードセルを検出して `alt … else … end` を出す。番号は元の表記をラベル先頭に残し `autonumber` を使わない。

### B4. roundtrip プロファイルには図が出ない
- 同じファイルでプロファイルにより内容が変わる（Mermaid 0 件）。設計上の判断だが、GUI 上で違いが説明されていない。保護された派生ブロックとして roundtrip にも入れるか、UI で明示する。低優先。

---

## C. DOCX / PPTX / PDF の readable

### C1. PDF: 日本語 PDF が文字化け、別の PDF は内部エラー
- `sample.pdf`（Type0/Identity-H/CIDFontType2、ToUnicode あり）→ 見出しが空白、本文が `! " # ! " $` の記号列。`docx-render2/sample2.pdf`（TrueType×3）→ `Internal error: PdfExtractionException`。DRMD 自身が render した PDF を DRMD で読めない。
- 原因: 文字列を Latin1 バイト列としてそのまま出力し、フォントの Encoding/ToUnicode/CMap を一切解決しない（[PdfTextExtractor.cs:205-224](../src/DocRedock.Formats.Pdf/PdfTextExtractor.cs)）。ページツリー・オブジェクトストリームも扱わず、`/Contents` が見つからないと全ストリームを読む（同 128-156）。
- 改善案: ToUnicode CMap（bfchar/bfrange）と Identity-H、簡易フォント `/Differences` の解決、xref/オブジェクトストリーム対応。それまでは UI/README で「PDF の readable は ASCII 主体の単純 PDF のみ」と明記し、例外はメッセージ付きで返す。

### C2. PPTX: 箇条書き・装飾が消え、段落構造が崩れる
- readable では本文シェイプの全段落が 1 段落（行末 2 スペース改行）になり太字・斜体も消える。roundtrip では全段落が `- ` の箇条書きになる（箇条書きでない段落も）。
- 原因: PptxAdapter が段落の bullet/level/run 書式を持たない（[PptxAdapter.cs:134-165](../src/DocRedock.Formats.OpenXml/Pptx/PptxAdapter.cs)）、readable は `Shape` を既定分岐で段落出力（[ReadableMarkdownSerializer.cs:141-151](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)）。
- 改善案: 段落ごとに `bullet/level/runs` を保持し、title→見出し、body→リスト/段落、表→GFM 表で出力。

### C3. DOCX: 見出しレベルが潰れる、リストが loose、Code スタイル未対応、画像が引用
- 見出しは最初が `#`、以降は全部 `##`（[ReadableMarkdownSerializer.cs:118](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)、Heading3 以下も同じ）。リスト項目ごとに空行が入る（同 129-131）。`Code` 段落スタイル（サンプルにあり）は CodeBlock にならない（[DocxAdapter.cs:200](../src/DocRedock.Formats.OpenXml/Docx/DocxAdapter.cs) は Heading/ListItem/Paragraph のみ）。画像は `> 図: …` の引用（同 136-140）で `![]()` にならない。

### C4. 例外がそのまま表面化する
- CLI は未知の例外を `Internal error: <型名>` だけで出す（[CliApplication.cs:55](../src/DocRedock.Cli/CliApplication.cs)）。GUI の `IsExpected` に `PdfExtractionException` が無く（[MainWindow.axaml.cs:398](../src/DocRedock.Gui/MainWindow.axaml.cs)）、`async void` ハンドラから未処理で抜けるため、該当 PDF を書き出すとアプリが落ちる可能性が高い（未実測）。
- 改善案: DocumentService 層で `InvalidDataException` 等に変換しメッセージを付ける。

---

## D. GUI の使い勝手

### D1. 出力フォルダーの指定が毎回必須で既定値がない
- 書き出しボタンはフォルダー選択まで無効（[MainWindow.axaml.cs:279](../src/DocRedock.Gui/MainWindow.axaml.cs)）。既定を「元ファイルと同じフォルダー」にし、前回の選択を記憶する。

### D2. 同名ファイルがあると止まり、上書き／連番の選択肢がない
- [MainWindow.axaml.cs:358](../src/DocRedock.Gui/MainWindow.axaml.cs)、[GuiWorkflowService.cs:211-214](../src/DocRedock.Gui/GuiWorkflowService.cs)、[DocumentService.cs:214](../src/DocRedock.Api/DocumentService.cs)。「変換→見る→直す→再変換」の反復で毎回手動削除が要る。上書き確認ダイアログ、または `名前 (2).md` の自動連番を提供する。

### D3. 結果を確認する導線が「出力フォルダーを開く」だけ
- `.md` を開く／プレビュー／パスをコピーが無い（[MainWindow.axaml:182](../src/DocRedock.Gui/MainWindow.axaml)）。書き出し直後に Markdown をアプリ内プレビュー（または既定アプリで開く）できると反復が速い。

### D4. 複数ファイル・フォルダーの一括変換ができない
- `AllowMultiple = false`（[MainWindow.axaml.cs:54](../src/DocRedock.Gui/MainWindow.axaml.cs)）。ドロップも 1 件。

### D5. 処理中のキャンセルと進捗がない
- `CancellationToken` 未使用、進捗は不確定バーのみ。OCR や大きいブックで待つしかない。

### D6. メッセージの英日混在と診断の生ダンプ
- `DOCX, XLSX, PPTX, and PDF files are supported.`（[GuiWorkflowService.cs:54](../src/DocRedock.Gui/GuiWorkflowService.cs)）、`Output already exists; refusing to overwrite it.`（DocumentService）が日本語 UI にそのまま出る。診断は `INFORMATION XlsxFormulaSafe: …` の羅列（[MainWindow.axaml.cs:330](../src/DocRedock.Gui/MainWindow.axaml.cs)）。重要度で絞り、件数を要約する。

### D7. 画面構成
- 46px の見出し（`文書を、往復できる Markdownへ。`）とキャッチコピーが作業領域を圧迫し、MinHeight 620 では操作部がスクロール下に落ちる（[MainWindow.axaml:5-8, 23-32](../src/DocRedock.Gui/MainWindow.axaml)）。OCR 言語欄は OCR 無効時も表示。「読みやすいMarkdown」トグルの説明だけでは roundtrip との違いが伝わりにくい（復元できる／できない、図の有無、`.drmdpkg` の扱い）。readable 側のオプション（数式表示・SVG・図）が GUI から触れない。

### D8. 設定が保存されない
- 出力先・OCR 設定・トグル状態は起動ごとにリセット。

### D9. 復元側の小さな手間
- 書き出し直後に結果をそのまま復元カードへ渡すボタンがない。`.md` を選んだら同名の `.drmdpkg` を自動で探す補助もあると楽。

---

## E. CLI

- E1. 未知例外が `Internal error: 型名` のみ（メッセージ無し）。
- E2. `--output` 既存時は拒否のみで `--force`/`--overwrite` が無い。
- E3. readable でも一時フォルダーに完全な roundtrip ワークスペースを書いてから捨てる（[DocumentService.cs:216-240](../src/DocRedock.Api/DocumentService.cs)）。大きいファイルで無駄が大きい。
- E4. 起動が `dotnet run --project …` 前提で重い。GUI の publish スクリプトはあるが CLI は単一バイナリ／`dotnet tool` 配布が無い。
- E5. readable 用オプションが無い（`--no-svg`、`--show-formulas`、`--sheets`、`--no-diagrams`、タイトル指定など）。
- E6. 数式 1 件ごとに `INFORMATION XlsxFormulaSafe` を出力し、画面が埋まる。要約が必要。

---

## F. テスト・ドキュメント

- F1. readable のテストは 3 本で、いずれも検証用ブックの出力をそのまま期待値にしている（`| No. | 項目 | 内容 |` など、[ReadableMarkdownTests.cs](../tests/DocRedock.Tests/Markdown/ReadableMarkdownTests.cs)）。日付・数値書式・装飾ヘッダー・横並び・結合・テキスト入り図形・コネクタを含む複数ブックのゴールデンテスト、DOCX/PPTX/PDF のフィクスチャを追加する。
- F2. README の「見出し・段落・メタデータ・表を再構成」「DrawingML を図として再構成」は現状より強い表現。`docs/FORMAT_CAPABILITY_MATRIX.md` は readable プロファイルに触れていない。既知の制限を明文化する。
- F3. 生成結果に「推定で作った見出し／ヘッダー」「落とした情報（図形 N 個未使用、数式 N 件）」を報告する診断があると、利用者が出力を信頼できる。

---

## 優先順位（提案）

1. A1〜A4（領域検出・スタイルベースのヘッダー/見出し判定）— 「綺麗に出ない」の大半がここ。
2. A5〜A7（数値書式・数式・コードブロック）。
3. D1〜D3（出力先既定・上書き・結果を開く）— 反復作業のストレス。
4. B1〜B3（図抽出の汎用化・フラグメント）。
5. C1〜C4（PDF・PPTX・DOCX・例外）。
6. A13〜A14（SVG 方針とプレビュー品質）、E・F。

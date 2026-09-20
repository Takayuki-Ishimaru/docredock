# DocRedock 利用ガイド

日本語 | [English](../en/user-guide.md)

このガイドは、v0.2.8 Public Betaでサポートする、デスクトップGUIでのDOCX／XLSX／PPTX／PDFから**閲覧用Markdown**へのローカル変換を説明します。

## 1. 入手する

GitHub ReleasesからOS／CPUに合うパッケージを取得し、公開されたSHA-256を確認します。自己完結型パッケージには別途.NET SDKは不要です。

## 2. 変換する

1. DocRedockを起動します。
2. DOCX、XLSX、PPTX、PDFのいずれかを選択またはドロップします。
3. **閲覧用Markdown**を選びます。
4. 特別な目的がなければ**表示中の内容のみ（推奨）**のままにします。
5. 図の接続推定は、特別な目的がなければ**Safe（推奨）**のままにします。
6. 出力先を選んで変換し、Markdown、診断、assetを確認します。

デスクトップGUIはPDFを既定で受け付けます。ネイティブPDFテキストを抽出し、文字のないページのOCRや図的ページのpreviewにはrasterizer／OCR providerの構成が必要な場合があります。利用できない場合もpage placeholderと診断を確認してください。

二段組のページは、3行以上連続して段間の余白を確認できた場合に、左の段を上から下まで読んでから右の段を読みます。項目名と値、タイトルとページ番号のように1行だけ広く空いた箇所は段として扱いません。表を再構成したページでも、表の外の本文はそのまま保持され、説明できない欠落があれば`PdfNativeTextUnaccounted`で警告します。

CLIのPDF変換（export）・復元・生成（render）は、他の実験的CLIワークフローと同様に`DOCREDOCK_ENABLE_EXPERIMENTAL=1`が必要です。読み取り専用の`docredock inspect <file.pdf>`は設定なしで利用できます。

CLIの既定も閲覧用Markdownです。

```sh
docredock export input.docx --content-policy visible --visual-inference safe --output input.md
```

`native-only`は元形式に明示された接続だけを使います。`safe`は一意なhigh-confidence geometry割当を昇格します。`balanced`はmedium-confidence割当も対象にできますが、同率・矛盾・graph全体で曖昧な関係は引き続き未解決にします。

実験的なサイドカー往復処理を使う場合だけ`--profile roundtrip`を明示します。

## 3. 生成されるファイル

| 生成物 | 内容 | 現行での利用 |
| --- | --- | --- |
| `.md` | 本文、見出し、リスト、表、図のsemantic projection／注記／placeholder | 使用する |
| `.assets/` | Markdownから参照する画像、preview等のvisual fallback | 生成された場合に使用する |
| report／diagnostic | 未解決connector、partial projection、fallback、欠落理由等 | Warning時は必ず確認する |
| `.drmd` | 元文書／復元用サイドカー | 実験用。元文書と同じ機密区分で扱う |
| `.drmdpkg` | Markdownと復元情報のパッケージ | 実験用。元文書と同じ機密区分で扱う |

Mermaidは、接続関係が明確で矛盾しない場合だけ出力します。認識した図は、(1) Mermaid、(2) 画像／ページプレビュー等の代替表示、(3) 明示的な診断の順で、利用可能な形を残します。図形テキストがあることだけでは、接続関係や分岐まで完全に保持したことにはなりません。

## 4. 内容ポリシーを選ぶ

- **visible**（既定）: 認識できる非表示テキスト、シート・行・列、スライド・オブジェクト、ノート、コメント、変更履歴を除外します。
- **complete**: 非表示情報とメタデータを含め、警告を出します。
- **sanitized**: メタデータ、派生・OCR情報、ヘッダー／フッター等も除外します。

XLSXの書き出しでは、`--sheets Sheet1,Sheet2`（CLI限定）で指定したシートだけを対象にできます。指定した名前は既存のシート名と（大文字小文字を区別せず）完全一致する必要があります。存在しない名前を指定するとexit code 2で失敗し、出力ファイルは作成されません。一部の名前だけ一致しない場合（部分的な不一致）も、その名前を黙って無視せず失敗します。指定したシートが非表示で`visible`/`sanitized`ポリシーにより除外される場合はエラーにはなりませんが、`XlsxSheetExcludedByPolicy`という警告（exit code 1）を出し、出力が空に見える理由を説明します。含めるには`--content-policy complete`を使用してください。

OCRテキストは親画像の可視性を引き継ぎます。親partitionを解決できない場合は専用の`derived-assets`へ配置し、`OcrParentPartitionUnresolved`を出します。

## 5. 結果を確認する

通常の内容に加え、図を含む文書では次を確認します。

- 見出し階層、リストの入れ子、結合表の空継続セル、数式キャッシュ警告、スライド区切り
- flowのnode label、接続方向、分岐、YES／NO等のedge label
- report上の`native-connection`と`geometry-inferred`の区別
- `VisualConnectorUnresolved`、`VisualEdgeLabelUnresolved`、`VisualSemanticProjectionPartial`等のdiagnostic
- Markdownから参照されるasset／page preview／placeholderと、元文書の同じページ・slide・sheet
- 同じtextboxやAlternateContent fallbackが重複していないこと

Warningが出た場合、MarkdownだけをAIへ渡すと意味欠落を見落とす可能性があります。diagnostic/reportとassetを一緒に確認し、必要なら元文書も参照してください。意味欠落・部分投影がWarning以上の場合、CLIは終了コード1を返します。

閲覧用出力はpixel-perfect再現や元Office図形への完全復元を保証しません。元文書を正本として保持してください。形式別の保証範囲は[対応状況](supported-features.md)を参照してください。

## 6. 実験的PDF生成

DocRedockは日本語フォントを同梱・ダウンロードしません。ASCIIのみのPDFはBase14 Helveticaを使い、非ASCIIは次の順で埋め込み可能なTrueTypeを解決します。

1. `--font-path`と任意の`--font-face-index`
2. `DOCREDOCK_PDF_FONT_PATH`と任意の`DOCREDOCK_PDF_FONT_FACE_INDEX`
3. OSにインストールされたシステムフォント

```sh
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock render input.md --format pdf \
  --font-path /path/to/font.ttc --font-face-index 0 --verbose
```

CFF／CFF2、グリフ不足、不正なcollection、埋め込み禁止フォントは拒否します。選択フォントのライセンス遵守は利用者の責任です。`--verbose`は選択パスを表示し、`--quiet`は情報行だけを抑制します。欠落・切り詰め警告があれば終了コード1です。

## 7. capability と PDF OCR の診断

環境の capability は doctor で確認できます。doctor は実験機能 gate の外で実行でき、入力ファイルも不要です。

    docredock doctor
    docredock doctor [--strict]
    docredock doctor --json

ready は依存関係を実測して利用可能、partial は一部の機能または OCR 言語だけ利用可能、unavailable は不足または無効化された状態です。ネイティブPDF OCRも内容によってpartialになる場合があります。画像PDFの OCR には OCR engine と PDF rasterizer の両方が必要です。rasterizer は明示パス（DOCREDOCK_PDF_RASTERIZER）、次に PATH 上の pdftoppm、mutool の順で探索します。探索を無効化する場合は DOCREDOCK_DISABLE_PDF_RASTERIZER=1 を設定します。未検出時は pdftoppm または mutool をインストールするか、実行ファイルのパスを設定してください。fallbackはページあたり最大100 path・32,768文字で、圧縮時もネイティブテキストを保持します。

**Windows で OCR を有効にする。** Windows Media OCR（ネイティブ provider。`ocr-native`／`windows-media`として報告されます）を使うには、言語ごとの OCR 言語機能が必要です。これは表示言語とは別の、任意インストールの Windows コンポーネントです。日本語を表示言語として追加しただけでは、日本語 OCR は有効になりません。実際に何がインストールされているかは `docredock doctor`（または `docredock doctor --json`）で確認できます。利用可能な言語パックが見つかると `ocr-native` は `ready` になり、`action` に不足している言語と追加方法が具体的に示されます。言語パックを追加するには、設定 > 時刻と言語 > 言語と地域 > 言語を追加 > (言語) > オプション > 「光学式文字認識 (OCR)」を使うか、管理者権限の PowerShell で `Add-WindowsCapability -Online -Name Language.OCR~~~ja-JP~0.0.1.0`（日本語）／`Language.OCR~~~en-US~0.0.1.0`（英語）を実行します。PDF rasterizer（pdftoppm／mutool）はこれとは無関係です。rasterizer が必要になるのは画像のみの PDF ページを OCR する場合だけで、DOCX/XLSX/PPTX に埋め込まれた画像の OCR や、すでにネイティブテキストを持つ PDF ページには影響しません。そのため rasterizer が未検出でも OCR トグルは無効化されなくなりました。Windows Media OCR が利用できない場合は、可搬な代替として Tesseract を導入してください（[対応状況](supported-features.md)を参照）。

各 capability には tier（`required`: docx-readable、xlsx-readable、pptx-readable、pdf-text／それ以外はすべて `optional`。OCR、PDF rasterizer、mermaid-render を含む）が付きます。素の `docredock doctor` と `docredock doctor --json` は常に同じ終了コードを返します。必須 capability がすべて ready なら 0、そうでなければ 1 です。optional な不足は出力に表示されますが終了コードには影響しません。`--strict` を付けると、optional を含めどれか1つでも ready でない capability があれば終了コード1になります。ただし、その optional な不足がすでに ready な代替手段で満たされている場合（`satisfied_by` で表示）は例外です。たとえばネイティブ OCR ヘルパーが同梱されないプラットフォームでは、Tesseract が ready になると `ocr-native` は `satisfied_by: "tesseract"` を報告し、`--strict` でもこの不足では失敗しません。`partial` な代替手段は満たしたことにはなりません。JSON レポートには既存フィールドに加えて `tier`・`satisfied_by`・`strict`・`exit_code`・`summary`（`required_ready`・`optional_gaps`・`strict_failures`）が追加されます。

PdfTableInferred は規則的な罫線から表を再構成したこと、PdfTableNative は既存表情報を利用したこと、PdfTableAmbiguous は表として一意に決められなかったことを示します。VisualFallbackCompacted は出力予算に合わせて fallback を省略したことを示します。小さな欠落やノイズを含む図では部分 topology と fallback を保持し、接続を推測できない箇所を診断します。

## 8. プライバシーと更新確認

変換はローカルで行います。アプリ上部には実行中のバージョンが常時表示されます。起動時に公開GitHub Releases APIからPublic Betaを含む非draftの公開版をバックグラウンド確認し、新版があれば現在版と最新版を表示します。「更新を確認」で手動確認でき、オフラインやAPI制限時も変換と起動は継続します。更新は自動インストールされず、信頼済みのGitHubリリースページから利用者がパッケージを選びます。起動前に`DOCREDOCK_DISABLE_UPDATE_CHECK=1`を設定すると自動確認を無効化できます。

実験ワークフローは[実験機能](experimental-features.md)、取り扱いは[セキュリティとプライバシー](security-and-privacy.md)を参照してください。

## 変換結果の確認と原本の保護

結果画面は「正常完了」「警告付き完了」「失敗」を区別します。警告付きでは、保存できたことと原本照合が必要なことを主表示に示し、影響ページや未解決の内容を表示します。

PDFの表・図が未解決の場合、利用可能なラスタライザで原本ページの画像を添付します。OCRをオフにしても動作します。GUIの「PDFの未解決ページに原本照合用画像を添付」、CLIの`--pdf-fallback-images auto|off`で切り替えます（既定`auto`）。画像は`.assets/`、`--embed-images`ではMarkdown内に保存します。画像を添付しても未解決警告は残ります。画像生成ができない場合はページを示す診断を確認してください。

OCRの折り畳み表示には、エンジンが返した単語または行ごとの信頼度と位置を追加しています。信頼度80%未満を「要照合」、値がない場合を「未提供」と表示し、識別子・品番・数値を自動修正しません。外部画像への位置リンクは画像の`xywh`フラグメントを含みます。領域への移動は閲覧ソフトの対応に依存し、未対応でも位置座標と原画像を比較できます。Visionの座標は左下原点の正規化座標で表示し、リンクでは左上原点の百分率に変換します。

復元前の統合チェックは、作業領域のコピーで実際の復元を試します。元文書・Markdown・サイドカー・既存レポートは変更しません。

```sh
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock preflight edited.md
DOCREDOCK_ENABLE_EXPERIMENTAL=1 docredock preflight edited.md --json
```

`verify`は整合性、`diff`は編集内容、`preflight`は整合性と編集検出と試験復元を確認します。正常な編集の検出だけでは`preflight`は警告終了になりません。終了コードは0が復元可能、1が警告付きで復元可能、3がサイドカー不整合、6が編集を復元できない状態です。編集済みPDFの再生成を許す場合は`--allow-render-fallback`を明示します。成功は対応範囲での復元可能性を示し、Officeでの外観の一致を保証するものではありません。

元のOfficeファイルを復元先に指定しても、通常の`--force`だけでは置換できません。元文書を置き換える場合は`--force --replace-original`を指定します。復元に成功した後、置換直前のファイルを`<出力名>.docredock-original-<ID>.bak`に保存してから置換します。通常は別名の出力先を推奨します。

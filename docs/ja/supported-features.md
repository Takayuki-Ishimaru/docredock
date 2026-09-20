# v0.2.9 の対応機能

[English](../en/supported-features.md) | 日本語

DocRedock v0.2.9 Public Betaでは、デスクトップGUIでDOCX、XLSX、PPTX、PDFをローカルの**閲覧用Markdown**へ変換する操作をサポートします。

| 機能 | v0.2.9での扱い |
| --- | --- |
| DOCX／XLSX／PPTX → 閲覧用Markdown | Public Betaとしてサポート。CLI既定 |
| `visible`／`complete`／`sanitized` | サポート |
| Visual inference mode | `native-only`、既定の`safe`、`balanced`。未解決関係はfallback／diagnosticで可視に保持 |
| PDF → Markdown／OCR | デスクトップGUI: サポート。ネイティブテキストは既定で抽出し、OCRにはprovider構成が必要。CLI: 実験機能・明示的な有効化が必要 |
| Markdown編集 → Officeへ復元 | 実験機能・明示的な有効化が必要 |
| PDF／Officeの新規生成 | 実験機能・明示的な有効化が必要 |
| CLI `render --format html` | 実験機能・明示的な有効化が必要 |

閲覧用出力は、見出し、段落、入れ子リスト、空継続セルを使う結合表、画像／OCR、コード、強調、改行、数式キャッシュ警告、対応する視覚要素の意味投影またはfallback、PPTX bulletの正規化に対応します。

さらに、表セル内の段落境界と段落内改行（`<br>`）、Word の入れ子表をセル内の元の順序に沿って折り畳む表示、コンテンツコントロール（`w:sdt`）内の本文、Word 数式（OMML）の線形テキスト化（`DocxMathLinearized` 警告付き。例: `E=mc^2`。数式の前後、sdt 内の段落、インライン sdt を含む段落、`mc:AlternateContent` 由来の段落（入れ子を含む）、本文中のテキストボックス、入れ子表のセル（外側表との同時編集を含む。行・セル・段落構造は維持）は F1 編集可能で、数式の線形テキストを変更すると `DocxMathReplaced` 警告付きで通常テキストに置き換わる）、PowerPoint のレイアウト／マスター上の可視文字（プレースホルダを除く）、Excel の表示形式（ゼロ埋め、千／百万単位の縮尺、指数・工学表記、分数、条件付きセクション、負数・ゼロのセクション、通貨記号などのリテラル）に対応します。原文に含まれる `*` `_` `~` バッククォート、`[` `]`、行頭の `#` `-` `1.`、HTMLタグ風の文字列は Markdown 構文として解釈されないようエスケープします。そのため原文に文字列として書かれた `[text](url)` `![alt](path)` `[ref][id]` `[id]: url` はリンクや画像に変わらず（`[id]: url` の行は参照定義として吸収され消えることもなく）そのまま文字として残り、実際のハイパーリンクや画像は従来どおり出力されます。 往復用出力（`--profile roundtrip`）でも同じ規則でエスケープし、編集後の復元時に対称に戻します（仕様は [DRMD_MARKDOWN_SPEC](https://github.com/Takayuki-Ishimaru/docredock/blob/v0.2.9/docs/DRMD_MARKDOWN_SPEC.md) を参照）。

表に重なる矢印・バー・線・マーカー等の図形は、表の後ろの独立した段落や図としてではなく、重なる行・列のセルへ同じ記法の記号として畳み込みます（右矢印 `━━▶`、左矢印 `◀━━`、両矢印 `◀━━ … ━━▶`、バー `━━`、線 `──`、マーカー `◆`／楕円 `●`／三角 `▲`、縦方向 `│`／`▼`／`▲`、テキストボックスはラベル文字列）。対象範囲は形式ごとに異なります。PowerPoint は表に重なる図形全般が対象で、これらの図形は Visual flow の図には使われません。さらに、ネイティブな表以外に、隣接する矩形図形だけで構成されたスライド上の「表」（少なくとも3個の日付／見出しボックスから成るヘッダー行、任意でタスクボックスから成るラベル列、空欄またはボックスで埋まった本体）も、本体部分に矢印・バー・マーカー・線のいずれかの図形が1つ以上重なっていれば表として認識します。ボックスのテキストがヘッダー／ラベル／本体の各セルのテキストになり、重なる図形は上記と同じ記号になり、構成要素のボックスは独立した段落としては出力されなくなります。重なる図形のない整列ボックス（カードレイアウト）はそのまま残り、ネイティブな表と重なるボックス格子は表として合成されません。この認識は閲覧用プロファイルにのみ適用され、往復用プロファイルではこれらの図形を通常の編集可能な図形のまま保持し、合成された表は出力しません。Excel は、DrawingML図形（矢印・矩形・ひし形・矢尻付きの線／コネクタ・テキストボックス）が値のあるセル範囲に重なり、かつ対象行の左に行ラベルのセル、対象列の上にヘッダーのセルがある場合だけ畳み込み対象になります（以前はこれらの図形は読み取り用出力から黙って除外されていました）。DocRedock が既にシーケンス図／フロー図として投影しているシートでも、その投影が実際に使用した図形だけが畳み込み対象から除外され、同じシート上の他の図形は引き続き対象セルへ畳み込まれます。Word は、表セル内の段落に配置された図形（インライン／フローティング、VMLを含む）と、表の直前の段落に「段落基準」の縦位置で配置されたフローティング図形が対象です。縦位置がページ／余白基準の図形、表以外の場所にある図形、フローティング表（`w:tblpPr`）は対象外のままです。列位置は表グリッドの列幅とページ左余白＋表のインデントから、行位置は行高（指定がある場合）から求めます。PDF は、罫線から再構成した表の内側にある塗りつぶし図形（矢印・矩形・ひし形）と矢尻付きの線が対象です。矢印のテキストは表の再構成によって既にセル本文へ割り当てられているため、`設計 ━━` ではなく `設計<br>━━` のように記号がラベルの次の行に入ります。従来は表に矢印などが重なっていると表そのものが再構成されず段落テキストとベクター画像へのfallbackになっていましたが、この変更により該当する表も表として認識されるようになりました。

v0.2.9では、PDF工程表の横・縦の両矢印と、矢印の先端があるセルを保持します。表を横断する斜め線など、表の記号にできない図形は警告と原本照合で確認できるようにします。未解決のパスだけが残るページにも、OCRとは独立して原本照合用画像を添付できます（PDF rasterizerが必要）。見出しの背景塗りと矩形外枠がある表の不要な警告を減らし、要確認ページ数と照合画像の添付ページ数を分けて表示します。OCR照合情報は本文と同じ読み順・行番号で表示し、GUI／CLIで低信頼項目・全件・件数要約を選択できます。操作方法は[利用ガイド](user-guide.md)を参照してください。

## 図・フローの意味保持

記号: ○ = 対応、△ = 条件付き／部分対応、× = 意味構造としては非対応。ここでいう対応は**閲覧用Markdownへの投影**であり、元Office図形へのroundtrip復元能力とは別です。

| 形式 | 図形テキスト | connector topology | geometry推定 | edge label | SmartArt／diagram | vector／image fallback | 不完全時のdiagnostic | 対応レベル |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| DOCX | ○ DrawingML／VML textbox | △ 対応drawing fragment内でnative endpointまたは一意に解決できるgeometry | △ 一意な端点のみ | △ 一意に近接するlabelのみ | △ 抽出textを保持。完全topologyは保証しない | △ 埋め込み画像をassetとして保持 | ○ 未解決／partial topologyをdiagnostic化。有効graphを描画しない場合はsource textを保持 | Public Beta・条件付き |
| XLSX | ○ 一般図形と標準`flowChart*` preset | ○ 接続済みconnector | △ cell layout／geometry由来 | △ 近接・既存projectionで解決可能な場合 | × | △ 埋め込み画像をassetとして保持 | △ 未知`flowChart*` presetはlabelを保持するが、確定した接続としては扱わない | Public Beta |
| PPTX | ○ process／decision／terminator／data／generic | ○ native connection | △ 斜めconnectorを含む一意な端点だけ推定 | △ 線分上で一意に対応付く場合 | △ textを保持し、topology不足を報告 | △ 埋め込み画像をassetとして保持 | ○ 未解決connector／label／partial projectionを報告 | Public Beta・条件付き |
| PDF | ○ ネイティブテキスト | △ 単純なpainted vector pathで端点が一意に対応する場合 | △ 一意なvector端点のみ。規則的な表罫線はconnector推定から除外 | △ 一意に近接するlabelのみ | △ 有効な単純topologyをMermaidへ投影。arrowhead方向を保持し、不明時は専用診断 | △ rasterizer利用時はpage preview、それ以外はpath/page placeholder | ○ vector/path partial、方向不明、未解決端点、OCR/rasterizer不足を報告 | Public Beta・条件付き |

次の優先順位で、認識した視覚情報を処理します。

1. 接続関係が明確で矛盾しない場合だけMermaidへ投影
2. 安全に生成できる画像／ページpreview等のvisual fallback
3. projectionもfallbackもできない場合は明示的なdiagnostic

元文書に明示された接続と、配置から推定した接続は区別して扱います。`native-only`は元形式に明示された接続だけ、`safe`は一意で確度の高い推定だけ、`balanced`は追加の推定候補も扱います。曖昧、矛盾、または重複する関係は未解決のままです。exportがWarningを出した場合、CLIは終了コード1を返します。Markdownだけでなく診断、asset、元文書も確認してください。

## 内容ポリシーとその他の制約

安全な既定値は`visible`です。`complete`は非表示情報とメタデータを警告付きで含め、`sanitized`はさらに強く除外します。OCR内容には元画像と同じ表示ポリシーを適用します。日本語OCRの行は、隣接する認識単語が両方とも高信頼度のCJK文字で、かつ間隔が狭い場合に限り、余計な空白を挟まずに結合します。信頼度が低い単語、英数字、間隔が広い場合は元の空白のまま保持します。 非表示の判定は直接指定だけでなく、Word の文字／段落スタイルや文書既定値から継承した非表示属性、PowerPoint の非表示グループに含まれる図形にも適用します。

DOCX drawingとPDF vector topologyは完全復元しません。対応fragment内で一意に解決できるconnector／pathだけを条件付きで投影し、それ以外はsource text／path fallbackとdiagnosticを保持します。rasterizerがあれば図的PDFページのpreviewを優先し、画像のみページでrasterizer／OCRを利用できない場合もpage placeholderとWarningを残します。本文と同居する埋め込み画像は、その位置にプレースホルダを置いて `PdfEmbeddedImageOmitted` 警告を出し、OCRが有効でrasterizerがあれば、各画像をページ画像から切り出して個別にOCRします（Form XObject 内の画像、`/Pages` から継承される Resources やページ寸法にも対応。回転ページなど切り出せない場合はページ全体をOCRし、本文と重複する認識結果を除外します）。実験的PDF生成は日本語フォントを同梱しません。ASCIIはBase14 Helvetica、非ASCIIは全グリフを持つ埋め込み可能なTrueTypeをシステムまたは明示パスから選択する必要があります。

閲覧用Markdownは一方向の出力です。`.drmd`と`.drmdpkg`は実験用で、元文書由来の情報を含む可能性があります。元文書を正本として保持してください。

この文書が利用者向けサポート範囲の正本です。[v0.2.9リリースノート](../../release-docs/RELEASE_NOTES_v0.2.9.md)、[利用ガイド](user-guide.md)、[実験機能](experimental-features.md)、[セキュリティとプライバシー](security-and-privacy.md)も参照してください。

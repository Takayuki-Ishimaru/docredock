# 目的別クイックガイド

## AIに文書を渡す

1. GUIで「表示中の内容（推奨・変更履歴は印付き）」を選び、DOCX／XLSX／PPTX／PDFを追加します。
2. AIに出典を示しながら答えさせたい場合は「AI向けパッケージ」を選びます。
3. 書き出し後は、要確認ページ・変更履歴・除外した非表示内容を確認します。
4. パッケージから必要な分割本文だけをAIへ渡し、出典索引は手元で参照します。

Wordなどの表で縦に結合したセルの値は、閲覧用MarkdownとAI向けパッケージで既定では各行に表示します。横に結合したセルの値は1回だけ表示します。閲覧用Markdownで2行目以降を空欄にするには、CLIで `--no-expand-merged-cells` を指定するか、アプリの「詳細設定」でチェックを外します。

CLIでは`docredock export input.docx --ai-package dir --content-policy visible --ocr off`を使えます。Word変更履歴の既定値は[こちら](../reference/revision-defaults.md)を参照してください。

## AIツール（Claude Code・GitHub Copilot・Codex）に設定する

`docredock agents install` は、Claude Code、GitHub Copilot（VS CodeとCopilot CLI）、Codex（CLI・IDE拡張・デスクトップアプリ）に、DocRedockで文書を読むための設定を追加します。設定したAIツールには、Word・Excel・PowerPoint・PDFを次の手順で読むよう指示します。

1. 本文や構造の抽出に役立つときはDocRedockを使う。
2. 体裁・図・印刷などを確認するときは、Office／PDFアプリ・SDK・文書用ツールで原本を直接開く。先に変換する必要はありません。

GUIでは、画面上部の「AI連携」から同じ設定ができます。

```sh
docredock agents install                 # 3つすべてに設定
docredock agents install --client codex  # 1つだけに設定
docredock agents status                  # 設定状態を確認
docredock agents uninstall               # 解除
```

設定は、現在のユーザーの設定フォルダーに書き込みます。

| AIツール | MCPサーバーの登録 | 指示 | Skill |
| --- | --- | --- | --- |
| Claude Code | `claude mcp add --scope user`（`claude`コマンドがなければ`~/.claude.json`） | `~/.claude/CLAUDE.md`に印付きの段落 | `~/.claude/skills/docredock-office/` |
| GitHub Copilot | `~/.copilot/mcp-config.json` | `~/.copilot/instructions/docredock.instructions.md` | `~/.copilot/skills/docredock-office/` |
| Codex | `~/.codex/config.toml`に印付きの段落 | `~/.codex/AGENTS.md`に印付きの段落 | `~/.agents/skills/docredock-office/` |

- 環境変数`CLAUDE_CONFIG_DIR`・`COPILOT_HOME`・`CODEX_HOME`があれば、その場所を使います。
- 既存のファイルを変更する前に、`<ファイル名>.docredock.bak`へ控えを取ります。ファイルの既存の内容は残します。
- `docredock`という名前の設定を自分で書いていた場合は、変更しません。競合として報告します（終了コード1）。
- 設定したら、開いているAIツールを再起動してください。
- DocRedockを別のフォルダーへ移したときは、もう一度`install`を実行すると、登録先が新しい場所に更新されます。
- DocRedockを新しい版に置き換えるときは、先にAIツールを終了してください。AIツールがMCPサーバーとして`DocRedock.Cli`を動かしている間は、Windowsではファイルを上書きできません。

AI連携を有効にしても、Office／PDF原本を直接閲覧・描画できます。連携を更新すると、DocRedockが以前登録した制限フックだけを削除し、他のフックは残します。`--enforce`と`--no-enforce`は互換性のため受け付けますが、新しく制限を設けることはありません。

`--auto-approve` は、対応するクライアントでDocRedockツールの呼び出しごとの承認を省きます。変換結果は、出力先を指定しなければ一時フォルダーに保存します。次の設定をクライアントごとに追加します。

| AIツール | 設定内容 |
| --- | --- |
| Claude Code | `~/.claude/settings.json`の`permissions.allow`に`mcp__docredock__*`を追加 |
| Codex | `config.toml`の`[mcp_servers.docredock]`に`default_tools_approval_mode = "approve"`を追加 |
| GitHub Copilot | `~/.copilot/hooks/docredock-approve.json`に`permissionRequest`フックを追加 |

この自動承認フックはCopilot CLI向けです。VS CodeのCopilotでは、DocRedockを呼び出すときに承認を求められる場合があります。

自動承認は`install --no-auto-approve`で外せます。GUIでは自動承認のチェックを外して「設定する」を押します。指定しない場合は既存の自動承認を残します。原本の直接閲覧にはAIツールの通常の権限設定が適用され、連携による追加の承認は不要です。

ほかのオプションは次のとおりです。

- `--dry-run`：書き込まずに、変更内容だけを表示します。
- `--json`：結果をJSON（`docredock.agents.v1`）で返します。

VS Codeで旧来のLocalハーネスを使っている場合、その設定ファイル（`.vscode/mcp.json`、プロファイルの`mcp.json`）には書き込みません。その場合は、下の手順でMCPサーバーを登録してください。

## MCPクライアントから使う

`docredock mcp`は、標準入出力で動くMCPサーバーです（JSON-RPC 2.0、1行に1メッセージ、UTF-8）。クライアントには、たとえば次のように登録します。

```json
{ "mcpServers": { "docredock": { "command": "docredock", "args": ["mcp"] } } }
```

Claude Codeでは`claude mcp add docredock -- docredock mcp`です。7つのツールがあります。どれもCLIと同じ処理をこのコンピューター上で実行し、`--json`の結果を返します。

| ツール | 内容 |
| --- | --- |
| `export_markdown` | 文書を閲覧用Markdownに変換し、結果とMarkdownを返します（`max_chars`、既定40,000文字で切り、ファイル全体の場所を添えます）。 |
| `export_html` | 文書を閲覧用HTML（1つで完結する`.html`）に変換し、書き出したファイルの場所を添えて結果を返します（引数は`max_chars`を除き`export_markdown`と同じ）。 |
| `export_ai_package` | AI向けパッケージを書き出し、そのフォルダーを`package_path`で返します。 |
| `package_review` | パッケージの確認状態と`review.md`を返します。 |
| `package_search` | 文字列を含む分割本文を探します。 |
| `package_read` | 分割本文を1つ返します。 |
| `read_source` | 原本の一部だけを読みます。Excelのシートとセル範囲、PowerPointのスライド、PDFのページ、ノード（`node` にノードIDを指定）、ZIP内のパーツ、画像のいずれかを指定します。`path`だけを渡すと、読めるものの一覧（画像の一覧も含む）を返します。テキストは`max_chars`（既定20,000文字）ずつ、`offset`で区切って返します。 |

確認が必要なパッケージでは、人が`package_review`の内容を確かめて`allow_review`を指定するまで、`package_search`と`package_read`は本文を返しません。

画像は、`image`にIDを指定して読みます。IDは、Markdownの画像の表示（例：「画像: Picture 2（img-0002、テキストのみの出力のため省略）」）や、OCR表のリンクに書かれています。PDFの画像のIDは`pdf-img-0025`のような形です。画像そのものは、MCPの画像コンテンツ（PNG・JPEG・GIF・WebP、5 MBまで）で返します。`ocr: true`を付けると、その画像の文字と位置もOCRで読んで返します。書き出し時の自動OCRでは、PDFから直接抽出した画像を対象から外します。その画像に含まれる文字が必要なときは、`read_source` で画像を指定し、`ocr: true` を付けます。

`read_source`が返すのは、保存されているままの原本です。Markdownでは除いた非表示の文字、コメント、削除された変更履歴が含まれることがあります。CLIでは、`docredock source <ファイル> --sheet <シート名> --range A1:H40`（または`--slide N`、`--page N --allow-pdf`、`--node <id>`、`--part <名前>`、`--image <id> [--ocr]`）で同じように読めます。CLIの`--image`は、画像を`--output`のファイル（省略時は一時フォルダー）に保存し、その場所を返します。

ツールは、クライアントが指定したファイルを、サーバーを起動したユーザーの権限で読みます。`output_dir`を指定しなければ、出力はシステムの一時フォルダーの下に新しく作るフォルダーに書きます。既存のファイルは`overwrite`を指定したときだけ置き換えます。PDFはCLIの`--allow-pdf`と同じく`allow_pdf`が必要です。

## 共有前にプライバシーを確認する

`visible`は認識できる非表示内容（非表示の文字・行・列・シート・スライド、発表者ノート、コメント、Excelのセルのメモ）を除外し、`sanitized`はヘッダー・フッターやメタデータもさらに除外します。`complete`はそれらを含め、非表示だったものには「（非表示）」の印を、コメントには対象の文字を付けます。共有前に確認してください。Wordの変更履歴は既定で、挿入を`<ins>`、削除を`<del>`の印付きで出力します（書式の取り消し線は`~~`）。削除した文字を渡さないときは`--revisions final`、GUIでは結果の「最終版で書き出し直す」を使います。GUIの結果欄と「該当ページを確認」、CLIでは出力された診断と`review.md`を確認します。

## 自動処理に組み込む

CLIで終了コードを確認し、レビューが必要な文書を後続処理へ渡さないようにするには、出力へ`--fail-on-review`を付けます。変換時の確認終了コードは11です。同梱Pythonリーダーで特定パートの表示や検索を行う場合も、レビューが必要なら既定で終了コード11になります（`--require-hidden-review`もCLIと同じ11）。

`--json`の出力は、入力の数や成否に関係なく`{"schema": "docredock.export.v2", "results": [...]}`の形です。各結果は`input`・`output`・`exit_code`・`format`・`mode`・`visual_inference`・`review_required`・`warning_count`・`needs_attention`・`attention`・`diagnostics`（`severity`は`"warning"`などの文字列）・`review`・`error`（失敗時は`code`と`message`）を持ちます。`review.md`を確認した後に本文を表示するには`--allow-review`、従来どおり終了コード3で止めるには`--fail-on-review`を指定します。

終了コード・警告・`review_required`は別々のことを表します。終了コード1は情報以外の診断（例: 保存済みの計算結果がない数式の`XlsxFormulaCachedValueMissing`）があるか、出力が空だったことを示します。`review_required`は原本との照合（ページ、OCR、表の区切り、本文に出なかった画像）か変更履歴の共有確認が必要なことを示します。警告があっても照合が不要な場合があり、OCRの確認だけなら終了コードは0です。自動処理では、これらを自分で組み合わせる代わりに`needs_attention`と`attention`を見てください。`attention`には`warnings`・`source_review`・`revision_sharing`・`hidden_content`・`empty_output`、変換できなかった入力では`failed`が入ります。複数ファイルの一括変換は、最も重い結果（エラー、次に11、次に1）で終了します。

```sh
docredock export input.xlsx --json --ocr off -o input.md | jq '.results[] | {input, needs_attention, attention}'
```

## 用途に合わせて設定を選ぶ

| 用途 | 設定 |
| --- | --- |
| AIへ最終版を共有する | `--content-policy sanitized`（Wordの変更履歴は最終版、非表示内容・コメント・ヘッダー・フッターを除外） |
| 変更履歴を確認する | `--revisions markup --allow-revision-sharing`。共有前に`review.md`を確認 |
| 大きな表の一部を調べる | `--ai-package dir --table-row-blocks --chunk-chars 2000`で書き出し、`package search`で探して`package read --part … --with-sources`で読む |
| 人が読む | 既定の`readable`。往復編集用の制御マーカーは不要 |

`--table-row-blocks`を付けないとExcelの表は行の途中で分けないため、大きなシートは1つのパートになります。パートが目安を超えたときは、CLIの表示と`review.md`でこの指定を案内します。

## 複数ファイルを変換する・標準出力へ送る

```sh
docredock export a.docx b.xlsx --output-dir converted --ocr off
docredock export incoming --output-dir converted --json
docredock export input.docx -o - --ocr off > input.md
```

フォルダー指定は、その直下の対応ファイルを対象にします。`--json`は処理結果をJSONで出し、`--quiet`は通常の要約を消し、`--verbose`は詳細を表示します。`-o -`ではMarkdown本文だけを標準出力へ、要約を標準エラーへ送ります。画像は既定で説明文（代替テキスト）に置き換えます。画像をbase64で埋め込むには`--images embed`を付けます（トークン数が大きく増えます）。同じ名前の`.docx`と`.pdf`を一括変換するときは`--keep-source-extension`で`名前.docx.md`・`名前.pdf.md`に分けます。`--force`でもシンボリックリンクやデバイスなどの特殊ファイルは置き換えません。

## PDF・結合セル・旧形式を扱う

```sh
docredock export report.pdf --allow-pdf --lang en --fail-on-review
docredock export expenses.docx --no-expand-merged-cells --ocr off
```

`--allow-pdf`はPDF変換だけを有効にします。CLIのメッセージは`--lang ja`で日本語になります。パスワード付きのOffice文書や暗号化されたPDFは、その旨を伝えて変換を止めます。解除して保存し直してから変換してください。生成する見出しや注記の言語は`--lang ja|en`で指定できます。PDFの照合画像は、OCR入力より小さなプレビューを作成します。添付しない場合は`--pdf-fallback-images off`を指定し、要確認箇所は原本で確認してください。

`.doc`／`.xls`／`.ppt`は、元のファイルを残し、Word／Excel／PowerPointまたはLibreOfficeでそれぞれ`.docx`／`.xlsx`／`.pptx`として別名保存してから変換します。

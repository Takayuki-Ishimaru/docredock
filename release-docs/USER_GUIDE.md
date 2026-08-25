# DocRedock 利用ガイド

日本語 | [English](USER_GUIDE.en.md)

DocRedock は、DOCX・XLSX・PPTX・PDF をローカルで Markdown に変換し、対応範囲内の編集を元形式へ安全に反映するためのデスクトップアプリケーション／CLI です。

## 1. インストール

GitHub Releases などの公式配布ページから、OS と CPU に合う成果物を取得します。

- Windows: win-x64 または win-arm64
- macOS: osx-x64 または osx-arm64
- Linux: linux-x64 または linux-arm64

一般公開された配布物では、公開ページに記載された SHA-256 チェックサムと一致することを確認してください。自己完結型ビルドには .NET SDK の追加インストールは不要です。

CLI の実行ファイル名は Windows では DocRedock.Cli.exe、macOS/Linux では DocRedock.Cli です。GUI の実行ファイル名は Windows では DocRedock.exe、macOS/Linux では DocRedock です。配布形式ごとの起動方法と署名状態は各リリースノートを確認してください。

## 2. GUI の基本操作

### 閲覧用 Markdown を作る

1. GUI を起動し、DOCX、XLSX、PPTX、PDF のいずれかを選択またはドロップします。
2. Readable Markdown を有効にします。
3. 出力先を選び、変換を実行します。
4. 診断メッセージと生成された Markdown を確認します。

このモードは元文書へ復元できません。埋め込み画像は通常、Markdown と同名の .assets ディレクトリへ出力されます。

### 往復編集する

1. Readable Markdown を無効にして、往復編集用としてエクスポートします。
2. 生成された Markdown と同名の .drmd を常に一緒に保管します。
3. DRMD の制御コメントを変えず、許可された本文だけを編集します。
4. verify と diff で整合性と変更内容を確認します。
5. restore で別名の Office/PDF ファイルへ復元します。
6. 対象アプリケーションで開き、変更箇所とレイアウトを目視確認します。

Markdown のブロックを消しただけでは削除にはなりません。削除は DRMD の明示的な delete マーカーで指示します。詳しくは ../docs/DRMD_AI_EDITING_RULES.md を参照してください。

## 3. CLI の基本操作

以下は macOS/Linux の例です。Windows では ./DocRedock.Cli を .\DocRedock.Cli.exe に読み替えてください。

### プロファイルの選び方

CLI の export では、目的に応じて --profile を指定します。

| 目的 | プロファイル | 復元 | 生成物 |
| --- | --- | --- | --- |
| 読む、検索する、要約する、共有する | readable | 不可 | Markdown。画像があれば .assets ディレクトリ |
| 元の Office 文書を編集して戻す | roundtrip | 対応範囲内で可 | Markdown と隣接する .drmd サイドカー |
| 詳細な監査情報も保持する | audit | 対応範囲内で可 | Markdown、サイドカー、追加の診断情報 |

Readable Markdown は一方向出力です。元文書へ戻す予定が少しでもある場合は roundtrip を指定してください。

### 閲覧用 Markdown

```sh
./DocRedock.Cli export input.xlsx --profile readable --output input.md --ocr off
```

画像を Markdown 内へ埋め込みたい場合は --embed-images を追加します。埋め込まれた画像や OCR テキストも共有対象になるため、機密情報を確認してください。

### 往復編集

```sh
./DocRedock.Cli export input.docx --profile roundtrip --output input.md --ocr auto
# input.md を編集
./DocRedock.Cli verify input.md
./DocRedock.Cli diff input.md
./DocRedock.Cli restore input.md --output restored.docx --strict
```

既定では既存出力を上書きしません。意図的に置き換える場合だけ --force を指定してください。

### サイドカーの持ち運び

```sh
./DocRedock.Cli pack input.md --output input.drmdpkg
./DocRedock.Cli verify input.drmdpkg
```

.drmdpkg には Markdown と復元に必要な元文書情報が含まれます。通常の添付ファイルと同様ではなく、元文書と同じ機密区分で扱ってください。

### 新しい文書を生成する

```sh
./DocRedock.Cli render input.md --format pdf --output rendered.pdf
./DocRedock.Cli render input.md --format docx --template template.docx --output rendered.docx
```

render は新規文書生成、restore は元文書の改版です。用途を混同しないでください。

## 4. OCR と Mermaid

- OCR は任意です。macOS では Apple Vision、Windows では Windows.Media.Ocr を優先し、利用できない場合はローカルに別途導入した Tesseract を使用できます。
- Tesseract 本体と言語モデルは配布物に含まれません。
- スキャン PDF の OCR には PDF ラスタライザーの実装が別途必要です。
- Mermaid 図のレンダリングには、利用者が明示的に指定したローカルの mmdc が必要です。DocRedock は実行時に Mermaid をダウンロードしません。

## 5. 主な制約

- DOCX: 対応する段落、見出し、リスト、同じ形状の表セル、限定的なリッチテキストを編集できます。
- XLSX: 既存セル値と数式の編集が中心です。行列、シート、結合、スタイルなどの構造編集は対象外です。
- PPTX: 既存シェイプのテキスト編集が中心です。ノート、表、画像、シェイプ追加・移動は対象外です。
- PDF: 抽出は保守的です。編集済み PDF の復元は明示的な render fallback となり、元レイアウトの保持を保証しません。
- マクロ、署名、暗号化、保護、危険または未対応のパッケージ構造は拒否される場合があります。

正確な対応範囲は ../docs/FORMAT_CAPABILITY_MATRIX.md と各処理の診断結果を正本とします。

## 6. 問題が起きたとき

1. 元文書とサイドカーを変更せず保管します。
2. verify と diff の出力、DocRedock のバージョン、OS/CPU、実行したコマンドを記録します。
3. 機密文書そのものは公開 Issue へ添付せず、再現用の最小サンプルへ置き換えます。
4. セキュリティ上の問題は SECURITY_AND_PRIVACY.md の報告方針に従います。
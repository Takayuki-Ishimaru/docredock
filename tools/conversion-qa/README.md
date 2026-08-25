# conversion-qa

DRMD の「変換 (export) → 契約チェック (expectations.json) → レンダリング → レポート」を
1 コマンドで回す検証ハーネス。`run.py` は Python 標準ライブラリ + Pillow のみに依存する。

## 使い方

```bash
# 単発実行: 1 ファイルを変換してチェック
python3 tools/conversion-qa/run.py --file tests/DocRedock.Tests/Fixtures/Docx/complex-design-doc.docx

# expectations.json を明示指定 (省略時は <ファイル名>.expectations.json を同じディレクトリから自動探索)
python3 tools/conversion-qa/run.py \
  --file tests/DocRedock.Tests/Fixtures/Docx/complex-design-doc.docx \
  --expectations tests/DocRedock.Tests/Fixtures/Docx/complex-design-doc.expectations.json

# 原本の PNG レンダリングも行う (soffice が PDF 化 → pdftoppm がページ PNG 化)
python3 tools/conversion-qa/run.py --file 経費精算システム_設計書_検証用.xlsx --render

# 出力先を明示指定
python3 tools/conversion-qa/run.py --file <path> --out /tmp/my-check

# 一括実行: tests/DocRedock.Tests/Fixtures/**/*.expectations.json を全て検出して処理し、
# さらにリポジトリ直下の 経費精算システム_設計書_検証用.xlsx を expectations 無しの参考エントリとして処理する
python3 tools/conversion-qa/run.py --all
python3 tools/conversion-qa/run.py --all --render
```

`--file` と `--all` は排他。`--expectations` / `--out` は `--file` 専用 (`--all` と併用するとエラー)。

## 1 target あたりの処理内容

1. **readable export** — `dotnet run --project src/DocRedock.Cli -c Release -- export <src> --profile readable --output <out>/export.md --force --quiet` を実行し、成果ディレクトリに `.md`(と埋め込み画像があれば `export.assets/`)を残す。この `.md` が expectations 評価の対象になる。
2. **roundtrip export のスモーク** — 同様に `--profile roundtrip` で実行し、`<out>/roundtrip/export.md`(+ `.drmd` サイドカー)に出力する。終了コードのみ記録し、export と同じ「exit≥2 なら失敗」判定に使う。
3. **expectations 評価** — `expectations.json` があれば `items[]` を 1 件ずつ判定し、`checks[]` と `guard`/`goal` 別の pass/fail 集計を `report.json`/`report.md` に残す。無ければ「参考エントリ (reference)」として export とレンダリングだけ行う。
4. **レンダリング (`--render` 指定時のみ)** — 原本を `soffice --headless --convert-to pdf` で PDF 化し、`pdftoppm -png -r 150` で全ページ PNG 化して `<out>/render/original-page-NN.png` に保存する。ページ数に応じて桁数は自動調整 (最小 2 桁)。道具が見つからない場合は **失敗にせず警告してスキップ** する。soffice はあるが pdftoppm が無い場合や、soffice 自体が無い場合は `qlmanage`(QuickLook のサムネイル生成)で 1 ページ目だけのサムネイルにフォールバックする(全ページにはならない旨を warning に明記)。
5. **レポート出力** — `report.json`(機械可読)と `report.md`(スコア表・fail 項目表・生成物パス一覧)を書く。

## 出力レイアウト

```
artifacts/conversion-qa/<target名>/        # 既定。--out で上書き可能 (--file のみ)
  export.md                                # readable export (expectations 評価の対象)
  export.assets/                           # 埋め込み画像 (あれば)
  roundtrip/export.md                      # roundtrip スモーク出力 (+ export.drmd/ サイドカー)
  render/original-page-01.png ...          # --render 指定時のみ
  report.json
  report.md
artifacts/conversion-qa/summary.md         # --all 実行時のみ、全 target の集計表
```

`<target名>` は変換対象ファイルのベース名 (拡張子込み)。`artifacts/` はリポジトリの `.gitignore` に含まれる。

## 終了コード

- `0`: 全 target で guard fail 無し、かつ readable/roundtrip export が両方とも exit code 2 未満。
- `1`: いずれかの target で guard fail があるか、readable/roundtrip export のどちらかが exit≥2 で失敗した場合。goal の fail は exit code に影響しない (現状で落ちてよい改善目標のため)。
- `2`: 引数エラー、対象ファイルが見つからない、明示指定した `--expectations` が見つからない、などの起動時エラー。

`--all` は 1 target でも上記の失敗条件に該当すれば全体として `1` を返す。

## 道具の発見順序

各道具は「環境変数 → PATH → 既知パス」の順で探索し、見つかったパスと発見元 (`env:*` / `PATH` / `known:*`) を `report.json` の `tooling` に記録する。

| 道具 | 環境変数 | 既知パスの例 |
| --- | --- | --- |
| dotnet CLI | `DRMD_DOTNET` | `.tmp/dotnet/dotnet` (リポジトリ直下) |
| LibreOffice headless | `DRMD_SOFFICE` | codex ランタイムキャッシュ配下の `LibreOfficeDev.app/Contents/MacOS/soffice`、`/Applications/LibreOffice.app/Contents/MacOS/soffice`、Homebrew (`/opt/homebrew/bin`, `/usr/local/bin`) |
| pdftoppm (poppler) | `DRMD_PDFTOPPM` | codex ランタイムキャッシュ配下の poppler ビルド、Homebrew |
| qlmanage (フォールバック用) | `DRMD_QLMANAGE` | `/usr/bin/qlmanage` (macOS 標準) |

## 道具の恒久化推奨

このリポジトリの codex ランタイムキャッシュ配下の LibreOffice/poppler は開発機のキャッシュに依存しており、
別環境や将来のキャッシュ削除で消える可能性がある。CI や他の開発機でも `--render` を安定して使うには、
Homebrew での恒久インストールを推奨する。

```bash
brew install --cask libreoffice   # soffice (headless 変換で使用)
brew install poppler              # pdftoppm
```

インストール後は `discover_tool` が PATH 経由で自動的に検出する。環境変数 `DRMD_SOFFICE` / `DRMD_PDFTOPPM` /
`DRMD_DOTNET` で明示的にパスを指定することも可能。

## expectations.json 契約

`expectations.json` のスキーマ (`items[].type`: `contains` / `not_contains` / `unique` / `regex` / `count`,
`items[].severity`: `guard` / `goal`)は
[`tests/DocRedock.Tests/Fixtures/COMPLEX_DESIGN_DOC_SPEC.md` の §5](../../tests/DocRedock.Tests/Fixtures/COMPLEX_DESIGN_DOC_SPEC.md#5-expectationsjson-契約)
を正とする。判定の実装はこのハーネス側 (`evaluate_item` in `run.py`) が持ち、fixture 側は宣言のみを行う。

判定の実装メモ:

- `contains` / `not_contains`: `value` を readable export の `.md` 全文に対する単純な部分文字列一致で判定する。
- `unique`: `.md` 全文中に `value` がちょうど 1 回だけ出現するかどうか (`str.count(value) == 1`)。
- `regex`: `value` を正規表現として `re.search(..., re.MULTILINE)` で検索一致判定する (`re.DOTALL` は付けない)。
- `count`: `value` の出現回数 (`str.count`) が `min`/`max` (どちらか一方でも可) の範囲内かどうか。
- `severity: guard` が 1 件でも fail すると、その target は `status: fail` となり終了コードに反映される。
  `severity: goal` の fail はスコアには表れるが終了コードには影響しない。
- `expectations.json` の `profile` フィールドは記録・検証はするが、readable export は常に実行するため
  `profile` が `"readable"` 以外を宣言している場合は warning として `report.md`/`report.json` に残す。

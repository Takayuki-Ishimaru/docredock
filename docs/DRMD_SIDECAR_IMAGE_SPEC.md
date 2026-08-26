# DRMD サイドカー容器と画像表示 — 実装仕様書（Codex 向け）

- 作成日: 2026-08-23
- 対象: `develop`（HEAD `68601ec`）。着手時点でテスト 173 件 green を前提とする。
- 目的:
  1. 出力ファイルの拡張子を `.drmd` / `.drmdpkg` に統一する（DocRedock Markdown）。
  2. `.drmd` を「ディレクトリ形／zip 形の 2 つの物理形をもつ論理容器」として定義し、両形を相互変換・受理できるようにする。
  3. ディレクトリ形を置いたとき、VS Code（組み込みプレビュー）と GitHub（blob/README 描画）で画像が元文書の位置に正しく表示されるよう、抽出と Markdown 投影を修正する。
  4. GUI の出力を上記に合わせる（現状の「一時フォルダーに作って消す」をやめる）。

本仕様は実装者（Codex）が追加の設計判断なしに着手できることを目標に書かれている。迷う点は §8 の「判断が必要になったとき」に従う。

---

## 0. 決定事項（変更不可の前提）

| # | 決定 | 理由 |
| --- | --- | --- |
| D1 | 表示先は VS Code と GitHub。画像参照は相対パスとし、通常は `![alt](<base>.drmd/assets/<file>)`、XLSXの位置・寸法付き画像は `<img src="<base>.drmd/assets/<file>" ...>` を使う。data URI・絶対パスは使わない | GitHub は data URI を除去し、zip 内部を読めない。相対参照は両方で描画でき、HTML `img` は元寸法と同一行配置を表現できる |
| D2 | 配布単位は `<base>.md` と `<base>.drmd` の 2 つ。`<base>.drmd` は論理容器で、物理形は **ディレクトリ形**（表示・commit 時）と **zip 形**（持ち運び時）。Markdown 側の文字列（front matter・画像パス）は両形で完全に同一 | 「置く瞬間はディレクトリ形」をユーザーが受け入れた |
| D3 | 形式識別子を DRMD に、製品・実装識別子を DocRedock に統一する。`<!--drmd:...-->`、`drmd_schema` / `drmd_rules`、`DocRedock.*` 名前空間、CLI 実行ファイル名 `docredock` を使用する | 旧識別子との互換性は提供せず、単一の新契約にする |
| D4 | readable プロファイル（`--profile readable`）の `<base>.assets/` は現状維持（本仕様の対象外） | one-way 出力で `.drmd` を持たない |
| D5 | zip 形は**読み取り専用**。書き込みを伴う操作（restore のレポート保存など）はディレクトリ形で行う。zip 形を与えられた操作は一時展開して処理し、workspace への書き込みは破棄して Information 診断を出す | 単純さ優先。zip 内の部分更新はしない |
| D6 | `.drmd` / `.drmdpkg` のみを受理・生成する | 旧拡張子は対象外 |
| D7 | `.drmdpkg`（バンドル）は残す。内容は `<base>.md` + ディレクトリ形 `<base>.drmd/`。GUI の既定出力からは外す | CLI 経由の単一ファイル配布手段として維持 |

---

## 1. 用語と判別規則

- **サイドカー (sidecar)**: `<base>.drmd`。`RoundTripWorkspace` の実体。
- **ディレクトリ形 (directory form)**: `.drmd/` レイアウト。`manifest.json`, `checksums.json`, `graph/`, `maps/`, `assets/`, `source/`, `derived/`, `reports/`。内部レイアウトと `schema_version` は変更しない。
- **zip 形 (zip form)**: ディレクトリ形の内容を **zip のルート直下**に持つ単一ファイル。ファイル名は `<base>.drmd`（拡張子はディレクトリ形と同じ）。
- **バンドル (`.drmdpkg`)**: `<base>.md` と `<base>.drmd/`（ディレクトリ形）を同梱する zip。
- **判別規則**（`SidecarContainer.Detect`, §3.2.2）:
  1. パスが存在するディレクトリ → ディレクトリ形。
  2. 存在するファイルで先頭 4 バイトが `50 4B 03 04` → zip。zip のルートに `manifest.json` があれば zip 形サイドカー。ルートに `*.md` が 1 つと `*.drmd/` が 1 つあればバンドル。どちらでもなければ `InvalidDataException("Unrecognized DocRedock container.")`。
  3. それ以外 → `InvalidDataException`。拡張子は判別に使わない（人間向けの目印にとどめる）。

---

## 2. スコープ外（本仕様ではやらない）

- 旧識別子との後方互換レイヤー。
- VS Code 拡張（zip 形のまま画像を表示する仕組み）。
- EMF/WMF/TIFF → PNG 変換（画像ライブラリまたは外部ツール依存の判断が別途必要。§3.5-B はプレースホルダ出力まで）。
- readable の `.assets/` 廃止や `.drmd` への統合（D4）。
- `.drmd` 内部レイアウト・manifest `schema_version` の変更。
- XLSX画像のセル重なり、crop、rotation、z-orderまで含む完全な紙面再現。roundtripではアンカー行、同一行の列順、DrawingML表示寸法までを投影する（§3.5-A）。
- GitHub Actions による zip 形→ディレクトリ形の自動展開（§7 に運用メモのみ）。

---

## 3. 仕様

### 3.1 拡張子改名（Phase 1）

対象拡張子（`.drmd` / `.drmdpkg`）、形式識別子（`DRMD` / `drmd:*`）、製品・実装識別子（`DocRedock` / `docredock`）を全レイヤーで統一する:

| ファイル | 箇所 | 変更 |
| --- | --- | --- |
| [src/DocRedock.Cli/CliApplication.cs](../src/DocRedock.Cli/CliApplication.cs) | 208, 240, 288, 324, 353–356 | `.drmdpkg`、`SidecarFor`/`ResolveWorkspace` 既定 `.drmd`、ヘルプ文言 |
| [src/DocRedock.Gui/GuiWorkflowService.cs](../src/DocRedock.Gui/GuiWorkflowService.cs) | 65, 99, 145–146, 239 | 同上（§3.4 の構造変更と同時に行う） |
| [src/DocRedock.Gui/MainWindow.axaml.cs](../src/DocRedock.Gui/MainWindow.axaml.cs) | 72, 320, 331, 340, 499–500 | ピッカー拡張子、競合候補、文言 |
| [src/DocRedock.RoundTrip/RoundTripPackage.cs](../src/DocRedock.RoundTrip/RoundTripPackage.cs) | 18, 107 | 既定ワークスペース名 `.drmd`、バンドル内 `*.drmd` |
| [src/DocRedock.Markdown/DocRedockMarkdown.cs](../src/DocRedock.Markdown/DocRedockMarkdown.cs) | 53 | `RoundTripStore` 既定 `document.drmd` |
| [.gitignore](../.gitignore) | `*.drmd/`, `*.drmdpkg` | 新拡張子を除外 |
| docs / README / tests | §3.7, §5 | |

規則:
- 入出力は新拡張子に統一する。
- UI・CLI 文言の「DRMDパッケージ」「DRMD復元ファイル」は「DocRedock パッケージ（.drmdpkg）」「DocRedock 復元ファイル」に置換。形式名としての「DRMD Markdown」はそのまま。
- `docredock verify` の受理対象: `<file.md> | <file.drmd> | <file.drmdpkg>`。

### 3.2 `.drmd` 論理容器（Phase 3）

#### 3.2.1 zip 形の定義
- entry 名はディレクトリ形の相対パス（`manifest.json`, `graph/index.json`, `assets/img-0001.png`, …）。ディレクトリ entry は作らない。
- entry の並びは相対パスの ordinal 昇順（決定論的出力）。圧縮は Deflate。
- 生成・展開は [RoundTripPackage.cs](../src/DocRedock.RoundTrip/RoundTripPackage.cs) の既存 `AddFileAsync` / `ValidateEntry` / 上限（50,000 entries・展開後 1 GiB・パス脱出検査）を共通化して再利用する。
- zip 形に `<base>.md` は**含めない**（バンドルとの違い）。

#### 3.2.2 API（`DocRedock.RoundTrip` に追加）

```csharp
public enum SidecarForm { Directory, Zip }

public sealed class SidecarLease : IAsyncDisposable
{
    public string OriginalPath { get; }   // 与えられたパス
    public string RootPath { get; }       // RoundTripWorkspace.OpenAsync に渡せるディレクトリ
    public SidecarForm Form { get; }
    public bool IsTemporary { get; }      // zip 形を一時展開した場合 true。Dispose で削除
}

public static class SidecarContainer
{
    public static SidecarForm Detect(string path);                                   // §1 判別規則。バンドルは InvalidDataException
    public static bool IsBundle(string path);                                         // .drmdpkg 判別（Detect と分ける）
    public static Task<SidecarLease> OpenAsync(string path, CancellationToken ct = default);
    public static Task<string> PackInPlaceAsync(string directoryPath, string markdownPath, CancellationToken ct = default);
    public static Task<string> UnpackInPlaceAsync(string zipPath, string markdownPath, CancellationToken ct = default);
    public static Task<string> PackToAsync(string directoryPath, string markdownPath, string outputZipPath, CancellationToken ct = default);
    public static Task<string> UnpackToAsync(string zipPath, string markdownPath, string outputDirectoryPath, CancellationToken ct = default);
}
```

動作:
- `OpenAsync`: ディレクトリ形なら `RootPath = path`。zip 形なら `Path.GetTempPath()/docredock-sidecar/<guid>/<name>` へ展開（検証付き）し `IsTemporary = true`。
- `PackInPlaceAsync(dir, md)`: (1) `RoundTripWorkspace.OpenAsync(dir)` → `VerifyAsync(md, requireUnchangedProjection: false)` が valid であること（invalid なら `WorkspaceIntegrityException`、何も変更しない）。(2) 同じ親ディレクトリに一時名 `.<name>.<guid>.tmp` で zip を作成。(3) 作成した zip を一時展開して `checksums.json` 照合（self-check）。(4) ディレクトリを削除し、zip を `<dir>` と同じパスへ `File.Move`。失敗時は原状維持（一時ファイルは必ず削除）。
- `UnpackInPlaceAsync(zip, md)`: 逆方向。一時ディレクトリへ展開 → `OpenAsync`+`VerifyAsync(md)` → zip を削除 → `Directory.Move`。
- `PackToAsync` / `UnpackToAsync`: 出力先が既に存在すれば `IOException`（本プロジェクトの「出力を上書きしない」規則）。
- いずれも `<base>.md` には一切触れない（projection hash 不変）。

#### 3.2.3 既存 API との関係
- `RoundTripWorkspace.OpenAsync` はディレクトリ専用のまま変更しない。
- CLI / GUI / `DocumentService` の入口（verify, diff, restore, inspect, rebase, pack）は `SidecarContainer.OpenAsync` で lease を取り、`lease.RootPath` を既存 API に渡す。lease の寿命は呼び出し側（コマンド単位）で管理する。
- `RoundTripWorkspace.VerifyAsync(markdownPath: null)` は RootPath の親から Markdown を探す（[RoundTripWorkspace.cs:162–165](../src/DocRedock.RoundTrip/RoundTripWorkspace.cs)）。一時展開時は親が temp になるため、**lease 経由の呼び出しでは必ず `markdownPath` を明示**する（`OriginalPath` の親 + `Manifest.Projection.FileName`）。

#### 3.2.4 zip 形での書き込み（D5）
- restore / diff は一時展開上で実行する。`reports/restore-report.*` 等 workspace 内への書き込みは破棄。
- Information 診断 `SidecarZipFormReadOnly`: 「サイドカーは zip 形のため、workspace 内のレポートは保存されません。`docredock unpack <base>.drmd --in-place` で展開してください。」

#### 3.2.5 Markdown 側の不変条件
- front matter: `roundtrip_store: <base>.drmd`
- 画像: 通常は `![alt](<base>.drmd/assets/img-0001.png)`、XLSXの位置・寸法付き画像は `<img src="<base>.drmd/assets/img-0001.png" alt="..." width="..." height="...">`（§3.5-A/E に従う）
- 上記は物理形に依存しない。pack/unpack/in-place 変換のテストで `.md` のバイト列が不変であることを assert する。

### 3.3 CLI（Phase 1, 3）

| コマンド | 仕様 |
| --- | --- |
| `docredock export <source> [--output file.md] [--profile roundtrip] … [--sidecar dir\|zip]` | 既定 `dir`: `<base>.drmd/` を生成（現状どおり）。`zip`: export と verify の完了後に `PackInPlaceAsync` を適用。標準出力 `Sidecar:  <path> (directory\|zip)` |
| `docredock pack <file.md> [--output file.drmdpkg]` | 既存どおりバンドル生成。サイドカーが zip 形なら一時展開して同梱（バンドル内は常にディレクトリ形） |
| `docredock pack <file.md> --sidecar (--in-place \| --output file.drmd)` | サイドカーだけを zip 形に。どちらも未指定なら exit 2 |
| `docredock unpack <file.drmdpkg> [--output directory]` | 既存どおり |
| `docredock unpack <file.drmd> (--in-place \| --output directory)` | zip 形→ディレクトリ形。入力が zip 形サイドカーであることは内容で判別 |
| `docredock verify <file.md\|file.drmd\|file.drmdpkg>` | 両形受理。`.drmd` 直指定はサイドカー単体検証（Markdown は `Manifest.Projection.FileName` を `OriginalPath` の隣から探す） |
| `docredock restore/diff/inspect/rebase <file.md>` | `roundtrip_store` の参照先を `SidecarContainer.OpenAsync` で開く |

- ヘルプ文言（[CliApplication.cs:345–360](../src/DocRedock.Cli/CliApplication.cs)）を更新。
- `--in-place` は明示指定のみ。暗黙で既存パスを置換しない。
- 終了コードは既存体系を踏襲。

### 3.4 GUI（Phase 1, 3）

Export（roundtrip）:
- 出力先に `<base>.md` と `<base>.drmd/` を**直接**生成する。[GuiWorkflowService.cs:98–129](../src/DocRedock.Gui/GuiWorkflowService.cs) の一時フォルダー生成・`PackAsync`・`finally` での削除を廃止。`.drmdpkg` は既定では生成しない（D7）。
- 新チェックボックス「サイドカーを zip 形式で書き出す（持ち運び用）」。ON のとき export 後に `PackInPlaceAsync`。設定は `GuiSettings` に `ZipSidecar` を追加して永続化（[MainWindow.axaml.cs:536–570](../src/DocRedock.Gui/MainWindow.axaml.cs) の `EmbedReadableImages` と同じ経路）。
- 競合チェック（[MainWindow.axaml.cs:492–505](../src/DocRedock.Gui/MainWindow.axaml.cs), [GuiWorkflowService.cs:232–241](../src/DocRedock.Gui/GuiWorkflowService.cs)）の候補: `<base>.md`, `<base>.drmd`（ファイル／ディレクトリ両方）, `<base>.drmdpkg`。
- 結果表示: `Markdown: <path>` / `サイドカー: <path>（ディレクトリ｜zip）`。「Markdownを開く」は維持。

Restore:
- `_packageFile`（`IStorageFile`）を `_sidecarPath: string?` に一般化する。受理: `.drmdpkg`（バンドル）、`.drmd`（zip 形ファイル、またはドラッグ＆ドロップされたディレクトリ）。
- `.md` 選択時の自動検出（`TrySelectCompanionPackageAsync`）の順序: `<base>.drmd`（ディレクトリ → ファイル）→ `<base>.drmdpkg`。
- ピッカー `Patterns = ["*.md", "*.drmd", "*.drmdpkg"]`。ディレクトリ形はファイルピッカーで選べないため、「サイドカーフォルダーを選択…」ボタン（`OpenFolderPicker`）を追加。
- サイズ上限 `MaxPackageBytes`: zip/pkg はファイルサイズ、ディレクトリ形は配下ファイルの合計。
- `GuiWorkflowService.RestoreAsync`: `.drmdpkg` → `UnpackAsync`（既存）。`.drmd` → `SidecarContainer.OpenAsync`（zip なら一時展開）。Markdown は選択された `.md`。

### 3.5 画像表示の修正（Phase 2）

目的: ディレクトリ形を VS Code / GitHub で開いたとき、画像が **元文書の位置**に **表示可能な形式**で出る。A→C→E→B→D の順に実装する。

#### A. XLSX 画像のアンカー付き抽出
対象: [XlsxAdapter.cs](../src/DocRedock.Formats.OpenXml/Xlsx/XlsxAdapter.cs)（`ReadDrawingShapes` 559–631 は `sp`/`cxnSp` のみ読み、`xdr:pic` を読まない）、[ReadableMarkdownSerializer.cs](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs)（`SerializeWorkbook` 46–82, `RenderPartitionMedia` 195–216）。

XlsxAdapter:
- 新メソッド `ReadPictures(package, worksheetPartUri)`。`ReadDrawingShapes` と同じ drawing part 走査で、`twoCellAnchor` / `oneCellAnchor` 直下および `grpSp` 配下（descendants）の `xdr:pic` を収集する。`absoluteAnchor`（`from` 無し）は位置なしとして収集する（現在の `if (from is null) continue;` は pic には適用しない）。
- レコード: `XlsxPictureRecord(string Id, string Name, string? Description, string RelationshipId, string TargetPartUri, int? Column, int? Row, int? ToColumn, int? ToRow, long WidthEmu, long HeightEmu, string DrawingPartUri)`。`Id`/`Name`/`Description` は `xdr:nvPicPr/xdr:cNvPr` の `id`/`name`/`descr`（`title` があれば `descr` の次に優先）。
- `a:blip` の `r:embed` を `ReadRelationships(package, "xl/drawings/_rels/<drawing>.rels")` で解決し、`TargetPartUri` は先頭 `/` 付き（`/xl/media/image1.png`）に正規化する。`r:embed` が無く `r:link` のみ（外部リンク画像）は収集せず `warnings` に `"{sheet}: linked picture '{name}' was skipped (external image)."`。
- `XlsxWorksheetRecord` に `IReadOnlyList<XlsxPictureRecord>? Pictures` を追加。
- `Extract` で各シートの partition に Image ノードを追加（cell ノードと Diagram ノードの後、`Order` は連番を継続）:
  - `Id = "n_" + Hash($"{sheet.Name}!picture:{DrawingPartUri}:{Id}:{RelationshipId}")[..16]`
  - `Kind = Image`, `Layer = Body`, `Editability = Protected`, `Provenance = [Native]`
  - `Content = ReferenceNodeContent(Reference: TargetPartUri, AltText: Description ?? Name)`
  - `Source = SourceAnchor("xlsx", sheet.PartUri, [("drawing_part", DrawingPartUri), ("image_relationship", RelationshipId)] + Row/Column があれば ("cell_address", "B12"))`
  - `Extensions`: `sheet_name`, `row`, `column`, `to_row`, `to_column`（1 始まり、位置なしは省略）, `address`, `drawing_part`, `image_relationship`, `picture_id`, `picture_name`, `width_emu`, `height_emu`
- 既存 warning「DrawingML shape(s) were retained but not projected as a diagram.」の件数に pic は含めない。
- [DocumentService.cs](../src/DocRedock.Api/DocumentService.cs) の `AttachAssetReferences` / `FindAsset`（690–735）は変更不要で `/xl/media/image1.png` → `img-000N` に解決できる（suffix 一致）。OCR 親ノード解決（`AttachAssetsAndOcr` 643–681）も既存ロジックで一致することをテストで確認する。

ReadableMarkdownSerializer（`SerializeWorkbook`）:
- `ReadImages(partition)` → `(int Row, DocumentNode Node)`（`row` extension を持つ Image のみ、Row 昇順・Id 順）。
- diagrams と images を Row 昇順にマージした「挿入列」を作り、既存ループを拡張する: 行番号 < 挿入位置の行を出力 → 挿入（diagram は `WriteMermaid`、image は `WriteImage` + 直後に `ParentId == image.Id` の ImageText を `> OCR抽出テキスト:` 引用で出力）→ diagram のみ `MinRow..MaxRow` の行をスキップ、image は行をスキップしない。
- `RenderPartitionMedia` は `row` を持たない Image と親無し ImageText だけを「### 埋め込み画像」に出す（見出しは維持）。
- `DocRedockMarkdown`（roundtrip）も`row`付きImageをsheet tableと行昇順でインターリーブする。画像行より前のcellをtableとして出力し、同一行の画像は`column`昇順で同じ物理行へ連続出力する。各画像は独立した`drmd:block` markerを維持する。
- `width_emu` / `height_emu`は96 DPI（9,525 EMU/px）でExcel上の表示寸法をCSS pixelへ丸め、表示可能形式は相対`src`、`alt`、`width`、`height`を持つHTML `img`にする。この値は元ビットマップの自然画素数ではない。`style="max-width:49%;height:auto"`により、広いpreviewではExcel表示寸法を基準とし、狭いpreviewでは縦横比を保って同一行の2画像を縮小する。寸法が無い、範囲外、または表示不可形式の場合は従来のMarkdown画像表現へフォールバックする。

#### B. 表示可能形式のポリシー
対象: [DocumentService.cs:852](../src/DocRedock.Api/DocumentService.cs) `MediaType`、readable rebind（231–272, 344–362）、両 serializer。

- `MediaType` 追加: `.svg` → `image/svg+xml`, `.emf` → `image/emf`, `.wmf` → `image/wmf`（既存: png/jpeg/gif/webp/bmp/tiff、その他 `application/octet-stream`）。
- 新 `DocRedock.Core.Documents.ImageDisplayPolicy.IsMarkdownDisplayable(string mediaType)`: `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/bmp`, `image/svg+xml` → true。`image/tiff`, `image/emf`, `image/wmf`, `application/octet-stream` → false。
- asset 抽出（`ExtractOfficeAssetsAsync`）は `/media/` 配下を形式にかかわらず assets に書く（現状どおり、provenance 目的）。表示不可形式も `.assets/` / `assets/` にコピーする。
- DocumentService は Image ノードを asset に束縛する際、extension `image_media_type` を付与する（serializer が `graph.Assets` を引かずに判定できるようにする）。
- readable: 表示不可の Image は `![]()` を出さず、`> 図: {alt}（{拡張子} 形式は Markdown で表示できません: {相対パス}）` を出力し、診断 `ImageFormatNotDisplayable`（Warning）を追加。`--embed-images` の `HasSafeImageSignature` は変更しない（svg は data URI 化せず省略＋既存診断）。
- roundtrip: 位置・寸法付きXLSX画像は上記HTML `img`、それ以外は`![alt](path)`。どちらも同じ保護Imageノードとしてparser/editorが検証し、表示不可形式には同じ診断 `ImageFormatNotDisplayable` を出す。
- `image/svg+xml` は通常の画像では両 serializer が `![alt](path)` を出す。位置・寸法付きXLSX画像は、他の表示可能形式と同じHTML `img` 規則に従う。

#### C. 同一バイト画像の別名解決
対象: [DocumentService.cs:762–780](../src/DocRedock.Api/DocumentService.cs) `ExtractOfficeAssetsAsync`、`FindAsset`（724–735）、[WorkspaceIntegrity.cs:51](../src/DocRedock.RoundTrip/WorkspaceIntegrity.cs) `WorkspaceAsset`、[RoundTripWorkspace.cs:612](../src/DocRedock.RoundTrip/RoundTripWorkspace.cs) `AssetIndexEntry`。

- 現状: ハッシュ重複の 2 つ目以降の media entry を `SourcePartUri` ごと捨てるため、同じ画像が 2 箇所で使われると 2 箇所目の `![]()` が未解決のまま（生の `media/image2.png`）残る。
- 修正: 重複は引き続き 1 asset にまとめるが、捨てる側の `"/" + entry.FullName` を `WorkspaceAsset.AliasPartUris`（`IReadOnlyList<string>`、既定空）に積む。`FindAsset` は `SourcePartUri` と `AliasPartUris` の全てに対して exact / suffix / filename 一致を試みる。
- `assets/index.json` の entry に省略可の `alias_part_uris`（文字列配列）を追加。`schemas/` に asset index 用スキーマがあれば追記（無ければ追加しない）。`VerifyAssetsAsync` への影響なし。
- 受け入れ: 同一 PNG を `xl/media/image1.png` と `xl/media/image2.png` に持ち、2 つの `xdr:pic` が別々の rId で参照する workbook で、両方の `![]()` が `img-0001.png` を指す。

#### D. alt text
- DOCX（[DocxAdapter.cs:239](../src/DocRedock.Formats.OpenXml/Docx/DocxAdapter.cs)）: `docPr@descr` → `docPr@title` → `docPr@name` の順。
- PPTX: 既存 `shape.Name` の前に `cNvPr@descr` があれば優先。
- XLSX: A のとおり。
- 空なら serializer の既定（readable「図」、roundtrip「image」）。

#### E. リンク先パスのエンコード
対象: [DocRedockMarkdown.cs:356–357, 551](../src/DocRedock.Markdown/DocRedockMarkdown.cs)、[ReadableMarkdownSerializer.cs:218–224](../src/DocRedock.Markdown/ReadableMarkdownSerializer.cs) `WriteImage`、readable rebind。

- 画像リンク先の各パスセグメントに対し、空白→`%20`、`(`→`%28`、`)`→`%29`、`<`→`%3C`、`>`→`%3E`、`%`→`%25` をエンコードする共通関数 `MarkdownPathEncoder.Encode(string relativePath)` を `DocRedock.Markdown` に置く。非 ASCII（日本語）はそのまま（VS Code / GitHub とも UTF-8 相対パスを解決する）。
- readable の `<…>` 包み（`image.Reference.Contains(' ') ? $"<{…}>"`）は廃止し、エンコードに統一。
- roundtrip は `roundtrip_store` 名（`<base>.drmd`）がパス先頭に入るため、日本語・空白を含む `<base>`でMarkdownリンクとHTML `src`の両方を検証する。HTML属性では追加で`&`, `"`, `<`, `>`をescapeする。
- [MarkdownGraphEditor.cs:189](../src/DocRedock.Api/MarkdownGraphEditor.cs) は Image ノードを alt text で照合するため、パス表記の変更で `ProtectedNodeEdit` にならないことをテストで固定する。

### 3.6 互換性方針（Phase 1, 3）

- CLI / GUI の入力は `.drmd`（ディレクトリまたはzip形）、`.drmdpkg` のみを受理する。旧拡張子、旧形式マーカー、旧 front matter key、旧 CLI 名は受理しない。`RoundTripPackage.UnpackAsync` はバンドル内 `*.drmd/` のみを受理する。

### 3.7 ドキュメント（Phase 4）

- [README.md](../README.md)、[docs/DRMD_MARKDOWN_SPEC.md](DRMD_MARKDOWN_SPEC.md)、[docs/DRMD_AI_EDITING_RULES.md](DRMD_AI_EDITING_RULES.md)、[docs/FORMAT_CAPABILITY_MATRIX.md](FORMAT_CAPABILITY_MATRIX.md) の拡張子表記を `.drmd` / `.drmdpkg` に統一する。
- 新節を README に追加: 「サイドカーの 2 つの物理形」「VS Code / GitHub で画像を表示するには」（§7 の内容）。
- [docs/REVIEW_IMPROVEMENTS_2026-08-23.md](REVIEW_IMPROVEMENTS_2026-08-23.md) は履歴として変更しない。

### 3.8 macOS Finder バンドル（Phase 5、任意）

- 現在 [tools/publish-gui.sh](../tools/publish-gui.sh) は単一実行ファイルを出力し `.app` を作らない。`osx-*` では `DocRedock.app/Contents/{MacOS/DocRedock, Resources/DocRedock.icns, Info.plist}` を組み立て、`Info.plist` に `CFBundleDocumentTypes`（`drmd`: `LSTypeIsPackage = true`, `LSHandlerRank = Owner`；`drmdpkg`, `md`: `Alternate`）と `UTExportedTypeDeclarations`（`dev.docredock.drmd` conforms `com.apple.package`；`dev.docredock.drmdpkg` conforms `public.zip-archive`）を宣言する。
- 効果: LaunchServices 登録後、Finder はディレクトリ形 `<base>.drmd` を 1 アイテムとして表示・移動・AirDrop する（`.rtfd` と同じ仕組み）。確認: `mdls -name kMDItemContentTypeTree <base>.drmd`。
- 注意: Windows / Linux / メール添付では通常のフォルダー。持ち運びは zip 形（§3.3 `pack --sidecar`）を案内する。

---

## 4. 実装順序と受け入れ基準

| Phase | 内容 | 完了条件 |
| --- | --- | --- |
| 1 | §3.1 拡張子統一、§3.4 GUI のディレクトリ形直接出力（zip トグルは Phase 3）、§3.5-E パスエンコード | 既存テスト全件 green＋§5 Phase 1 テスト。GUI export の出力先に `<base>.md` と `<base>.drmd/` のみ |
| 2 | §3.5 A → C → E（済）→ B → D | §5 Phase 2 テスト。手動受け入れ 1–4 |
| 3 | §3.2 `SidecarContainer`、§3.3 pack/unpack/`--sidecar`、GUI zip トグルと両形受理、D5 診断 | §5 Phase 3 テスト。手動受け入れ 5–6 |
| 4 | §3.7 docs | docs 内の拡張子表記を `.drmd` / `.drmdpkg` に統一 |
| 5 | §3.8 `.app` バンドル（任意） | `mdls` で `com.apple.package` を含む |

Phase 2 と Phase 3 は独立（並行可）。各 Phase の終わりで次を実行して green を確認する:

```sh
dotnet build DocRedock.sln -c Release --no-restore
dotnet test tests/DocRedock.Tests/DocRedock.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tools/LicenseAudit/LicenseAudit.csproj -c Release -- --root . --output artifacts
```

この Mac には SDK が `~/.cache/docredock-dotnet` にある（`export DOTNET_ROOT=~/.cache/docredock-dotnet; export PATH=$DOTNET_ROOT:$PATH`）。

手動受け入れ（Phase 1+2 完了時）:
1. GUI で画像入り xlsx（例: `outputs/image-validation-20260823/経費精算システム_設計書_画像検証用.xlsx`）を roundtrip export → 出力先に `<base>.md` と `<base>.drmd/` のみ（`.drmdpkg` なし、一時フォルダー残骸なし）。
2. VS Code でプレビュー → 画像が該当シートのアンカー行の位置（見出しの直後）に表示される。
3. `<base>.md` と `<base>.drmd/` を GitHub リポジトリに push → blob 表示で画像が出る。
4. `docredock verify <base>.md` が valid、`docredock restore <base>.md` が F0/F1 で通る。

Phase 3 完了時:
5. `docredock pack <base>.md --sidecar --in-place` → `<base>.drmd` がファイルになり、`docredock verify <base>.md` valid、`docredock restore` 可（`SidecarZipFormReadOnly` 情報が出る）。
6. `docredock unpack <base>.drmd --in-place` → ディレクトリ形に戻り、`checksums.json` 一致、`<base>.md` はバイト単位で不変。

---

## 5. テスト（新規・更新）

Phase 1:
- `Cli/CliApplicationTests`: export が `<base>.drmd` を作る。ヘルプに `.drmd` / `.drmdpkg`。
- `Gui/GuiWorkflowServiceTests`: export 後に `<base>.md` と `<base>.drmd/` が出力先にあり、`.drmdpkg` が無い。競合検出が `.drmd` のファイル／ディレクトリ両方を見る。restore が `.drmd` ディレクトリ・`.drmdpkg` を受理。
- `RoundTrip/RoundTripWorkspaceTests`, `Api/DocumentServiceTests`, `Render/MarkdownRendererTests`, `Api/MarkdownGraphEditorTests`: 拡張子の置換。
- `Markdown/DocRedockMarkdownTests`: 既定 store が `document.drmd`。空白・日本語・括弧を含む store 名の画像パスが §3.5-E の規則でエンコードされる。
- `Api/MarkdownGraphEditorTests`: エンコード済みパスの Image ブロックが `ProtectedNodeEdit` にならない。

Phase 2:
- `Xlsx/XlsxAdapterTests`（`CreateDiagramPackage` を拡張して `xl/drawings/_rels/drawing1.xml.rels` と `xl/media/image1.png` を追加）: `twoCellAnchor` の pic が `row`/`column`/`to_row`/`to_column`/`address` 付きの Image ノードになる。`absoluteAnchor` は位置なし。`grpSp` 配下の pic も拾う。`r:link` のみは skip＋warning。shape warning の件数に pic が含まれない。
- `Markdown/ReadableMarkdownTests`: `row` 付き Image がアンカー行の位置に挿入される（前後の行が正しく分かれる）。位置なし Image は「### 埋め込み画像」に出る。Image 直後に子 ImageText の引用。`image_media_type=image/emf` の Image がプレースホルダ引用になる。
- `Markdown/DocRedockMarkdownTests`, `Api/MarkdownGraphEditorTests`: roundtripでも画像が後続sheet tableより前へ入り、同一行は列順・同一物理行、EMU換算寸法付きHTML `img`になる。無変更HTML imageはprojection-equivalentで、alt・寸法・responsive styleの改変は`ProtectedNodeEdit`になる。
- `Api/DocumentServiceTests`: readable xlsx（アンカー付き pic）で `![…](source.assets/img-0001.png)` がアンカー行の位置に出る。roundtrip で `source.drmd/assets/img-0001.png` が画像の `src` に束縛される。同一バイト 2 参照が同じ asset を指す（C）。`.svg` がリンクされる。`.emf` がプレースホルダ＋`ImageFormatNotDisplayable`。OCR 親解決が新ノードで成立。
- `Docx/DocxAdapterTests`: alt 優先順位（descr → title → name）。

Phase 3:
- `RoundTrip/SidecarContainerTests`（新規）: `Detect`（ディレクトリ／zip 形／バンドル／不正）。`PackInPlace` → `UnpackInPlace` で全ファイルがバイト一致。失敗時（verify 不一致）に原状維持。`OpenAsync` の一時展開が Dispose で消える。entry 脱出・件数超過の拒否（既存 `ValidateEntry` の再利用を確認）。
- `Cli/CliApplicationTests`: `pack --sidecar --in-place`、`unpack --in-place`、`verify file.drmd`（zip 形）、zip 形サイドカーでの `restore` が `SidecarZipFormReadOnly` を出し workspace に書き込まない。`--in-place` と `--output` 未指定で exit 2。
- `Gui/GuiWorkflowServiceTests`: `ZipSidecar` オプションで出力が zip 形になる。restore が zip 形 `.drmd` を受理。

---

## 6. 変更ファイル一覧（目安）

| 領域 | ファイル |
| --- | --- |
| RoundTrip | `RoundTripPackage.cs`（共通化・拡張子）、新 `SidecarContainer.cs`、`WorkspaceIntegrity.cs`（`AliasPartUris`）、`RoundTripWorkspace.cs`（`AssetIndexEntry.alias_part_uris`） |
| Api | `DocumentService.cs`（MediaType / ImageDisplayPolicy 適用 / `image_media_type` / alias 解決 / lease 経由の open） |
| Core | 新 `Documents/ImageDisplayPolicy.cs` |
| Formats | `Xlsx/XlsxAdapter.cs`（`ReadPictures`, `XlsxPictureRecord`, Image ノード）、`Docx/DocxAdapter.cs`・`Pptx/PptxAdapter.cs`（alt） |
| Markdown | `DocRedockMarkdown.cs`（既定 store、パスエンコード）、`ReadableMarkdownSerializer.cs`（画像インターリーブ、プレースホルダ、エンコード）、新 `MarkdownPathEncoder.cs` |
| Cli | `CliApplication.cs`（拡張子、`--sidecar`、pack/unpack 拡張、lease、ヘルプ） |
| Gui | `GuiWorkflowService.cs`、`MainWindow.axaml(.cs)`（直接出力、zip トグル、`_sidecarPath`、フォルダーピッカー、文言） |
| その他 | `.gitignore`、README、docs、schemas（該当があれば）、tests |

---

## 7. 付録: GitHub に置くときの運用メモ（docs に転記する内容）

- 表示目的のリポジトリでは `<base>.drmd/` を **ignore しない**（このリポジトリの `.gitignore` は検証出力を無視しているだけで、利用側が真似する設定ではない）。
- 原本が大きい場合は `.gitattributes` に `**/*.drmd/source/** filter=lfs diff=lfs merge=lfs -text` を追加して `source/` だけ LFS に載せる。
- zip 形を commit した場合、GitHub では画像は表示されない（Markdown 本文は表示される）。表示したいなら commit 前に `docredock unpack <base>.drmd --in-place`。CI で自動展開する運用も可能だが本仕様の範囲外。
- VS Code は `<base>.md` と同じフォルダーに `<base>.drmd/` があれば追加設定なしで画像を表示する。

---

## 8. 判断が必要になったとき

- 本仕様と既存コードの安全規則（出力を上書きしない、DTD 禁止、entry 脱出検査、サイズ上限）が衝突したら、安全規則を優先し、本仕様側の挙動を「明示フラグが無ければ拒否」に倒す。
- 既存テストの期待値変更が必要な場合は、期待値が「拡張子」「画像の出力位置」「パスのエンコード」に起因するものだけを更新し、それ以外の期待値変更が必要になった時点で作業を止めて報告する。
- `drmd:` マーカーや front matter key に手を入れたくなったら止まる（D3）。

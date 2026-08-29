# DocRedock v0.1.6 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.6.en.md)

## 概要

v0.1.6 は visual semantics の silent loss を減らし、DOCX の二重抽出と roundtrip 内部例外を防ぐための品質リリースです。公開用タグから GitHub release workflow が6種類のRID向けself-contained配布物を構築・展開検証し、チェックサム、SBOM、provenance、attestation、署名状態、`RELEASE-EVIDENCE.md`を公開します。

## 利用者への影響

- PPTXのshape／connectorは、native connectionまたは一意なgeometry inferenceで解決できる場合にMermaidへ投影し、曖昧なconnector／labelは具体的diagnosticとして残します。
- DOCXは、native endpointまたは一意に推定できるendpointで有効topologyになる対応connector fragmentだけを条件付きで投影し、それ以外はsource textとdiagnosticを保持します。
- XLSXの既知の`flowChart*` presetを意味のあるMermaid nodeへ対応付け、未知presetはlabelを失わないgeneric nodeとして保持します。
- PDFのnative textを保持し、対応する単純vector pathで有効topologyになる場合はMermaidへ条件付き投影します。partial pathとimage-only pageはdiagnostic／fallbackを保持します。
- 既存の paragraph、heading、list、table、image、hyperlink、家具（header/footer）経路は互換性回帰の対象です。

## 形式別 visual behavior

| 形式 | Projection | Fallback/diagnostic | 既知の範囲 |
|---|---|---|---|
| DOCX | native／一意geometry endpointによる条件付きconnector topologyをMermaidへ投影 | invalid／unresolved topologyはsource textとdiagnosticを保持 | 完全drawing再構成は非目標 |
| PPTX | 解決できたshape／connector topologyをMermaidへ投影 | unresolved connector／labelをdiagnostic化 | SmartArt完全復元は非目標 |
| XLSX | 対応済みshapeと`flowChart*` presetをMermaidへ投影 | 未知presetのlabelをgeneric nodeで保持 | 任意shapeの完全意味復元は非目標 |
| PDF | 単純vector topologyを条件付きでMermaidへ投影 | partial／unresolved pathとimage-onlyをdiagnostic／placeholderで保持 | 任意vector graph完全復元は非目標 |

## Diagnostic 契約

本版で追加・伝播する代表コードは`VisualSemanticProjectionPartial`、`VisualSemanticProjectionUnavailable`、`VisualConnectorUnresolved`、`VisualEdgeLabelUnresolved`、`PdfRasterizerUnavailable`です。形式別のwarningはAPIでstable codeへ昇格し、それ以外は既存のfallback codeを維持します。

## 互換性と制約

Readable Markdown は元 Office 形式へ復元できません。roundtrip/restore は実験的・安全境界付きです。pixel-perfect、全 SmartArt、任意 PDF vector graph の完全復元、OCR engine 新規実装は非目標です。unsupported content を警告なしに捨てないことが契約です。

## 検証状況

- .NET 10.0.400によるローカルsolution test: **359 main + 4 GUI headless、失敗0／skip 0**
- ローカルosx-arm64 self-contained CLI／GUI publishと抽出済みbinary smoke（DOCX／XLSX／PPTX／PDF visual fixtureを含む）: **成功**
- 公開用タグのrelease workflowでは、clean checkoutのlocked restore、全テスト、conversion QA、license audit、6 RID publish、展開後binary smokeを必須gateとします。最終結果はGitHub Releaseに添付された`RELEASE-EVIDENCE.md`、`SHA256SUMS`、SBOM、provenance、attestation、各archive内の署名状態を正本とします。

## 公開証跡

`RELEASE-EVIDENCE.md` は release workflow が生成し、Release Owner が公開ページへの添付と最終承認を担当します。CI/QA Owner は test log、binary smoke、fixture hash、recognized/projected/fallback/omitted accounting を提供します。commit、workflow URL、target RID、artifact SHA-256、diagnostics、known limits を必須項目とします。

## 更新手順

1. 対象 commit と version metadata を固定する。
2. clean clone で restore/build/test と visual fixture/ accounting gate を実行する。
3. 各 RID を publish し、展開後 binary smoke を実行する。
4. RELEASE-EVIDENCE.md を生成し、notes/checklist と数値・known limits を照合する。
5. Release Owner が P0/P1、ハッシュ、証跡を承認してから公開する。

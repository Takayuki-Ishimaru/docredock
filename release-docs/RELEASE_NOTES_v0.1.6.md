# DocRedock v0.1.6 Public Beta

日本語 | [English](RELEASE_NOTES_v0.1.6.en.md)

## 概要

v0.1.6 は、図やフローの情報欠落を減らし、DOCXの重複出力と往復編集時の失敗を防ぐ品質リリースです。

## 利用者への影響

- PPTXのshape／connectorは、native connectionまたは一意なgeometry inferenceで解決できる場合にMermaidへ投影し、曖昧なconnector／labelは具体的diagnosticとして残します。
- DOCXは、native endpointまたは一意に推定できるendpointで有効topologyになる対応connector fragmentだけを条件付きで投影し、それ以外はsource textとdiagnosticを保持します。
- XLSXの既知の`flowChart*` presetを意味のあるMermaid nodeへ対応付け、未知presetはlabelを失わないgeneric nodeとして保持します。
- PDFのnative textを保持し、対応する単純vector pathで有効topologyになる場合はMermaidへ条件付き投影します。partial pathとimage-only pageはdiagnostic／fallbackを保持します。
- 既存の段落、見出し、リスト、表、画像、リンク、ヘッダー／フッターの出力との互換性を維持します。

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

## 配布と更新

Windows、macOS、Linuxの各対応パッケージを提供します。GitHub Releaseでチェックサム、SBOM／provenance、署名状況を確認してください。更新時は元文書を保持し、変換後のMarkdown、診断、画像を共有前に確認してください。

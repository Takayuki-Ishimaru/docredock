# DocRedock v0.1.7 Public Beta リリースノート

## 概要

v0.1.7 は **false topology prevention** を中心にした品質更新です。Mermaid は検証済みの semantic graph だけを出力し、不確実な接続は fallback と stable diagnostic へ退避します。

## 変更点

- source visual item を node、edge、fallback、diagnostic、重複抑止、装飾のいずれかへ accounting。
- DOCX anchor/VML、XLSX directional shape、PDF vector、PPTX textless arrow を保守的に扱う基盤を追加。
- `--no-diagrams` は Warning ではなく、解決済み接続の可読な一覧を出力。
- diagnostic は format、part、partition、source object、type、confidence、fallback を追跡可能。
- release smoke は Mermaid の node/edge/label を構造検証し、JSON evidence を出力。

## 互換性と制約

閲覧用 Markdown は一方向です。図形の pixel-perfect 再現や Readable Markdown からの図形復元は保証しません。Warning と exit code 1 は出力が存在しても確認が必要な部分変換を示します。

## 構造検証

公開前の release workflow は tag 由来の expected version、RID 別 package smoke、semantic evidence を必須にします。詳細は `artifacts/visual-semantics-evidence.json` と release evidence を参照してください。

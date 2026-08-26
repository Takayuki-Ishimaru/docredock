# Technical reference

These documents describe formats and capabilities implemented in the repository. They are for developers and advanced users.

> Implementation status is not release support. For operations users may rely on in v0.1.4, see [English supported features](../en/supported-features.md) or [日本語の対応状況](../ja/supported-features.md).

## Formats and editing contracts

- [DRMD Markdown specification](../DRMD_MARKDOWN_SPEC.md)
- [AI editing rules](../DRMD_AI_EDITING_RULES.md)
- [Implementation capability matrix](../FORMAT_CAPABILITY_MATRIX.md)
- [Valid example](../examples/valid-edit.md)
- [Invalid example](../examples/invalid-edit.md)

## Terminology

| Public term | CLI / internal name |
| --- | --- |
| Readable Markdown / 閲覧用Markdown | readable |
| Round-trip editing / 往復編集 | roundtrip |
| Restoration / 元形式への反映 | restore |
| New-document generation / 新規文書の生成 | render |
| Restoration data / 復元データ | .drmd |
| Portable package / 持ち運び用パッケージ | .drmdpkg |

Round-trip editing, restoration, new-document generation, PDF conversion, .drmd, and .drmdpkg are experimental and unsupported for v0.1.4 user operation; executable paths require `DOCREDOCK_ENABLE_EXPERIMENTAL=1`.

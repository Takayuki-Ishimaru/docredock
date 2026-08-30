# v0.2.0 release evidence correction

## 日本語

初回の `RELEASE-EVIDENCE.md` 生成時、シェルが Markdown のインラインコード記号を評価したため、表示上の3項目が空になりました。正しい値は次のとおりです。

- Product source commit: `76bdaabc83f031b2dfa654b18a9ecbd5857f6aa6`
- Release workflow commit: `76bdaabc83f031b2dfa654b18a9ecbd5857f6aa6`
- Conversion-QA attachment: `CONVERSION-QA-EVIDENCE.md`
- Successful workflow run: https://github.com/Takayuki-Ishimaru/docredock/actions/runs/33319745834

`RELEASE-PROVENANCE.json` と `CONVERSION-QA-EVIDENCE.md` にも同じ commit が独立して記録されています。この不具合は表示用Markdownの生成だけに限定され、配布パッケージ、GitHub asset の SHA-256、`SHA256SUMS`、SBOM、provenance、attestation には影響していません。既存の不変assetは置き換えていません。

## English

During the initial generation of `RELEASE-EVIDENCE.md`, the shell evaluated Markdown inline-code delimiters and omitted three display values. The correct values are:

- Product source commit: `76bdaabc83f031b2dfa654b18a9ecbd5857f6aa6`
- Release workflow commit: `76bdaabc83f031b2dfa654b18a9ecbd5857f6aa6`
- Conversion-QA attachment: `CONVERSION-QA-EVIDENCE.md`
- Successful workflow run: https://github.com/Takayuki-Ishimaru/docredock/actions/runs/33319745834

`RELEASE-PROVENANCE.json` and `CONVERSION-QA-EVIDENCE.md` independently record the same commits. The issue was limited to rendered Markdown: package bytes, GitHub asset SHA-256 digests, `SHA256SUMS`, the SBOM, provenance, and attestations were unaffected. No existing immutable asset was replaced.

# Publication Scope Policy

[日本語](PUBLICATION_SCOPE.md) | English

This document is authoritative for what belongs in the DocRedock public source repository and end-user distributions. Decisions are based on reproducibility, user accountability, licensing, confidentiality, and distribution size.

## Decision

The complete operational-validation set does not need to be public. However, public source should retain the test code, security-boundary tests, and minimum synthetic fixtures required for independent verification.

The policy is therefore:

- Publish: product source, specifications, schemas, unit and integration test code, and small reproducible fixtures with verified redistribution rights.
- Publish conditionally: QA harnesses, fixture generators, and expectations. They are required when the public CI references them.
- Do not publish: real-document corpora, generated conversion results, visual-comparison output, past experiments, local tool settings, and caches.

## Public source repository

### Required

| Path | Reason |
| --- | --- |
| README.md | Product overview, shortest installation path, and key limitations |
| LICENSE | Project license: MIT |
| THIRD-PARTY-NOTICES.txt | Notices for runtime/development dependencies and bundled assets |
| DocRedock.sln, Directory.Build.props, global.json | Reproducible build entry point and SDK requirement |
| src/DocRedock.* | Product source |
| schemas/ | Public contracts for DRMD and reports |
| docs/DRMD_MARKDOWN_SPEC.md | Round-trip editing format |
| docs/DRMD_AI_EDITING_RULES.md | Safe editing contract for humans and AI |
| docs/FORMAT_CAPABILITY_MATRIX.md | Supported and unsupported operations |
| docs/examples/ | Small valid and invalid specification examples |
| release-docs/ | User guidance, publication policy, and release checklist in Japanese and English |
| licenses/ | License allowlist and bundled-font license |
| provenance/ | Technique and third-party code provenance |
| packages.lock.json files | Dependency pinning and reproducible audits |
| Test source under tests/DocRedock.Tests | Independent behavior, security, and regression verification |
| tools/LicenseAudit | Reproducible license checking and SBOM generation |
| tools/publish-cli.* and tools/publish-gui.* | Auditable official build process |
| .github/workflows/ci.yml | Quality gates for the public branch |

### Conditional

| Path or material | Publication condition |
| --- | --- |
| Office/PDF binaries under tests/DocRedock.Tests/Fixtures | Entirely synthetic, free of customer/personal data, all fonts and images redistributable, and essential to the tests |
| Fixture generators and expectations | Reproducible with pinned dependencies and free of absolute paths or local-environment details |
| tools/conversion-qa | Publish when used by public CI. If internal-only, remove it from public CI too |
| docs/BRAND_DESIGN_GUIDELINES.md | Publish after defining third-party name and logo usage |
| assets/brand/docredock | Publish only sizes actually needed by README, the website, and executables; master/social variants may be distributed separately |
| docs/AI_DOCUMENT_FORMAT_TOKEN_BENCHMARK_*.md | Publish only after reviewing input-data rights, methodology, reproducibility, and comparative claims |
| docs/REVIEW_IMPROVEMENTS_*.md | Publish only when intentionally treated as a public roadmap rather than internal review notes |
| Non-CI files under .github | Publish only when contributors need them and they contain no local-product or personal-path dependency |

### Excluded

The following are excluded by default from the public branch, source archives, and binary distributions:

```gitignore
.codex/
.codex-work/
.mcp.json
.tokenlighten/
.playwright-cli/
.vscode/
.idea/
AGENTS.md
CLAUDE.md
.github/copilot-instructions.md

.tmp/
tmp/
bin/
obj/
TestResults/
artifacts/
output/
outputs/

*.drmd/
*.drmd
*.drmdpkg
~$*
.DS_Store
```

Reasons:

- .codex, .mcp.json, .tokenlighten, AGENTS.md, and CLAUDE.md can contain personal environment details, AI/MCP operations, or absolute paths that are unrelated to the product.
- output, outputs, and artifacts contain conversions, visual comparisons, or prerelease builds and can repackage source documents or extracted content.
- .tmp, tmp, bin, obj, and TestResults contain SDKs, caches, temporary data, and build/test output.
- .drmd and .drmdpkg can contain original document binaries and derived data and are excluded unless individually approved as fixtures.
- ~$ files are temporary Microsoft Office lock files.

.gitignore only prevents new untracked files. It does not remove output/ or outputs/ files that are already tracked, so the public branch index must be cleaned separately.

## Test-material policy

### Publish

- Parser, conversion, round-trip, CLI, and GUI-workflow test source
- Security-boundary tests for traversal, DTDs, size limits, suspicious formulas, and similar controls
- Tiny synthetic fixtures or fixtures generated inside tests
- Valid and invalid examples required by the specification

### Do not publish

- Customer, internal, or production-derived documents
- Manually created DOCX/XLSX/PPTX/PDF files without verified redistribution rights
- OCR screenshots, fonts, or images with unclear rights
- Generated restored, previous-export, visual-regression, or comparison-summary output
- Large corpora whose behavior can be reproduced by a small synthetic fixture

When public CI requires a binary fixture, document its origin, generation command, license, SHA-256, and the source of every embedded font or image. Prefer generating it in CI instead of committing the output.

## End-user distributions

Each OS/architecture-specific distribution should contain only:

- The files required to run DocRedock or DocRedock.Cli
- README or QUICKSTART
- LICENSE
- THIRD-PARTY-NOTICES.txt
- sbom.cdx.json
- Version information and release notes

Publish each archive's SHA-256 in SHA256SUMS on the release page. Do not include tests, fixtures, source, generators, brand masters, or conversion results in binary distributions.

## Operating rules

1. Build releases from a clean public commit/tag, never by copying the working directory.
2. Assemble artifacts from an allowlist and fail on every unexpected file.
3. Inspect the complete archive inventory for secrets, absolute paths, personal names, and source-document leakage.
4. When excluding a file referenced by CI, update the CI workflow in the same change.
5. Preserve source and binary manifests per release and bind them to the published checksums and SBOM.
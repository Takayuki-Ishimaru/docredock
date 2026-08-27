# Contributing to DocRedock

Thank you for helping improve DocRedock. This file is for contributors; end-user instructions live in the [English user guide](docs/en/user-guide.md) and [Japanese user guide](docs/ja/user-guide.md).

## Prerequisites

The required .NET SDK version is pinned in global.json. Restore dependencies in locked mode so local and CI builds use the committed lock files.

## Build and test

    dotnet restore DocRedock.sln --locked-mode
    dotnet build DocRedock.sln -c Release --no-restore
    dotnet test tests/DocRedock.Tests/DocRedock.Tests.csproj -c Release --no-build --no-restore
    dotnet test tests/DocRedock.Gui.HeadlessTests/DocRedock.Gui.HeadlessTests.csproj -c Release --no-build --no-restore
    dotnet run --project tools/LicenseAudit -- --root . --output artifacts

The license audit checks locked packages against licenses/allowlist.json and writes the CycloneDX SBOM under artifacts/.

## Run locally

Desktop GUI:

    dotnet run --project src/DocRedock.Gui

CLI, supported readable export example:

    dotnet run --project src/DocRedock.Cli -- export input.xlsx --output input.md --ocr off

The codebase also contains experimental CLI PDF, round-trip, restore, and render paths. Their presence does not make them supported in v0.1.5; CLI access requires `DOCREDOCK_ENABLE_EXPERIMENTAL=1`. The desktop GUI accepts PDF input by default, while PDF OCR still needs configured providers. See the [release support table](docs/en/supported-features.md).

## Project areas

- src/DocRedock.Core: document graph, deterministic serialization, diff, and reports
- src/DocRedock.Markdown and src/DocRedock.RoundTrip: Markdown projections and source-preserving workspaces
- src/DocRedock.Formats.OpenXml and src/DocRedock.Formats.Pdf: format adapters
- src/DocRedock.Gui and src/DocRedock.Cli: user interfaces
- schemas/: public data contracts
- tests/DocRedock.Tests/ and tests/DocRedock.Gui.HeadlessTests/: security, behavior, conversion, and GUI startup regression tests
- tools/: build, audit, publication, and verification utilities

## Documentation rules

Keep each document focused on one audience:

- docs/ja and docs/en: current end-user guidance
- docs/reference and stable format specifications: implementation and format reference
- release-docs: versioned release notes and maintainer publication policy
- historical implementation plans: clearly marked as historical

Do not describe an implemented capability as supported unless it is also listed as supported in the current release support table. Introduce public terms before internal names, for example “Readable Markdown (readable).”

## Pull requests

- Keep changes scoped and include tests for behavior changes.
- Preserve unsupported or unknown Office structure instead of silently flattening it.
- Never commit customer documents, local conversion output, credentials, or generated release artifacts.
- Run the build, test, and license-audit commands relevant to the change.
- Update both Japanese and English user documentation when public behavior changes.

## Releases

Release packaging, checksums, SBOM/provenance, signing status, and publication validation are maintainer responsibilities. See the [publication scope](release-docs/PUBLICATION_SCOPE.en.md) and [release checklist](release-docs/RELEASE_CHECKLIST.en.md). Creating a local build does not publish it.

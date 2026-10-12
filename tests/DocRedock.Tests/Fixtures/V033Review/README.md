# Synthetic package-review fixtures

These project regression inputs come from the synthetic DocRedock v0.3.3 review kit supplied on 2026-10-12. They contain no real user documents. The fixture files included in the public source follow the repository's [MIT License](../../../../LICENSE); no font programs or other embedded binaries are included.

| File | Purpose | SHA-256 |
| --- | --- | --- |
| `spec.docx` | Word revision display, final-content export and sharing-review gates; `SYNTHETIC_AUTHOR` and `DELETED_REVIEW_SECRET` are fabricated test values | `8cdc9e2e0be54d362a383786a7bbe9de04fe06b9bd836b3a4173853bb4c47c46` |
| `sales.xlsx` | Large-table row splitting, normalized search and source-cell lookup; store identifiers and sales values are synthetic | `30bd099b6f757b277a459d952b2ae39bc426d4fa1bd648c81363fd80e8e8edad` |

The inputs are committed so the tests do not depend on downloading the review kit. To reproduce the regression checks:

```sh
dotnet test tests/DocRedock.Tests/DocRedock.Tests.csproj --configuration Release --filter FullyQualifiedName~V033ReviewResponseTests
dotnet test tests/DocRedock.Gui.HeadlessTests/DocRedock.Gui.HeadlessTests.csproj --configuration Release --filter FullyQualifiedName~PackageBrowserTests
```

These fixtures are source-test inputs and are excluded from end-user distribution archives.

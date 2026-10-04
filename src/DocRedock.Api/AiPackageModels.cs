using DocRedock.Core.Documents;

namespace DocRedock.Api;

public enum AiPackageForm { Directory, Zip }

public sealed record AiPackageExportOptions(
    ReadableDocumentExportOptions Document,
    string OutputPath,
    AiPackageForm Form = AiPackageForm.Directory,
    int TargetCharacters = 12_000);

public sealed record AiPackageSourceLocation(
    string NodeId,
    string PartitionId,
    string Label,
    int? PageNumber = null,
    int? SlideNumber = null,
    string? SheetName = null,
    IReadOnlyList<string>? HeadingPath = null);

public sealed record AiPackagePart(
    string Id,
    string Path,
    string Markdown,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<AiPackageSourceLocation> Sources,
    bool ExceedsTarget);

public sealed record AiPackageExportResult(
    string OutputPath,
    AiPackageForm Form,
    DocumentGraph Graph,
    IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> Diagnostics,
    IReadOnlyList<AiPackagePart> Parts,
    ExportSummary Summary,
    ExportReview Review);

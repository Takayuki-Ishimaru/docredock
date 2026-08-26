namespace DocRedock.Core.Reporting;

public enum FidelityLevel { F0, F1, F2, F3, FX }
public enum PackagePreservationLevel { ByteIdentical, PartPayloadIdentical, SlicePreserving, ReSerialized, Unsupported }
public enum DiagnosticSeverity { Information, Warning, Error }
public enum OcrProcessingStatus { Completed, NotRequired, SkippedByPolicy, SkippedByBudget, Unavailable, Failed }

public sealed record Diagnostic(string Code, string Message, DiagnosticSeverity Severity, string? NodeId = null, string? PartUri = null);
public sealed record FidelityReport(
    FidelityLevel Level,
    PackagePreservationLevel PackagePreservation,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string>? ChangedNodeIds = null,
    IReadOnlyList<string>? PreservedPartUris = null)
{
    public bool IsSuccess => Level != FidelityLevel.FX && !Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
public sealed record OcrAttemptSummary(OcrProcessingStatus Status, string? Reason = null, string? ProviderId = null);

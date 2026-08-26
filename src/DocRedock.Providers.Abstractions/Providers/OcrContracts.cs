using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using System.Text.Json.Serialization;

namespace DocRedock.Providers.Abstractions.Providers;

/// <summary>Provider boundary for local OCR. A non-completed status is never interpreted as no text.</summary>
public interface IOcrEngine
{
    ProviderDescriptor Descriptor { get; }
    ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken);
}

public sealed record OcrInput(string AssetId, Stream Image, string MediaType);
public sealed record OcrOptions(IReadOnlyList<string> Languages, TimeSpan? Timeout = null, long? PixelBudget = null);
public sealed record OcrResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("regions")] IReadOnlyList<OcrTextRegion> Regions);
public sealed record OcrTextRegion(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("bounding_box")] Geometry? BoundingBox,
    [property: JsonPropertyName("confidence")] double? Confidence);
public sealed record OcrDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity);
public sealed record OcrAttemptResult(OcrProcessingStatus Status, OcrResult? Result, IReadOnlyList<OcrDiagnostic> Diagnostics)
{
    public bool HasText => Status == OcrProcessingStatus.Completed && Result is not null;
}

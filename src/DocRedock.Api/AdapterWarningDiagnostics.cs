using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Api;

public sealed record DiagnosticDisplaySummary(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    int Count);

/// <summary>Converts adapter warning strings to stable diagnostics when the adapter supplied one.</summary>
public static class AdapterWarningDiagnostics
{
    public static Diagnostic Create(string fallbackCode, string warning, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackCode);
        ArgumentNullException.ThrowIfNull(warning);
        const string externalRelationshipCode = "ExternalRelationshipPresent";
        if (warning.StartsWith(externalRelationshipCode + ":", StringComparison.Ordinal))
            return new Diagnostic(externalRelationshipCode,
                warning[(externalRelationshipCode.Length + 1)..].Trim(), DiagnosticSeverity.Information);

        if (!VisualDiagnostic.TryParseWarning(warning, out var code, out var message))
            return new Diagnostic(fallbackCode, warning, severity);
        return new Diagnostic(code, message, severity);
    }

    /// <summary>Coalesces repeated diagnostics while retaining distinct source locations.</summary>
    public static IReadOnlyList<Diagnostic> Normalize(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var result = new List<Diagnostic>();
        var index = new Dictionary<(string Code, string? PartUri, string? NodeId, string Message, DiagnosticSeverity Severity), int>();
        var counts = new List<int>();

        foreach (var diagnostic in diagnostics)
        {
            var key = (diagnostic.Code, diagnostic.PartUri, diagnostic.NodeId, NormalizeMessage(diagnostic.Message), diagnostic.Severity);
            if (index.TryGetValue(key, out var position))
            {
                counts[position]++;
                continue;
            }
            index.Add(key, result.Count);
            result.Add(diagnostic);
            counts.Add(1);
        }

        for (var indexValue = 0; indexValue < result.Count; indexValue++)
            if (counts[indexValue] > 1)
                result[indexValue] = result[indexValue] with
                {
                    Message = $"{result[indexValue].Message.TrimEnd()} (repeated {counts[indexValue]} times)."
                };
        return result;
    }

    /// <summary>Builds the default one-line-per-code view while preserving full diagnostics for verbose output.</summary>
    public static IReadOnlyList<DiagnosticDisplaySummary> SummarizeForDisplay(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics
            .GroupBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                var severity = (DiagnosticSeverity)group.Max(diagnostic => (int)diagnostic.Severity);
                var representative = group.FirstOrDefault(diagnostic => diagnostic.Severity == severity) ?? group.First();
                return new DiagnosticDisplaySummary(group.Key, representative.Message, severity, group.Count());
            })
            .OrderByDescending(summary => summary.Severity)
            .ThenBy(summary => summary.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeMessage(string message) =>
        string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

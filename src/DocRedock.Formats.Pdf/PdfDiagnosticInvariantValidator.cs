using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Checks public PDF diagnostics against their graph and output evidence.</summary>
public static class PdfDiagnosticInvariantValidator
{
    public static IReadOnlyList<string> Validate(VisualGraph? graph, IEnumerable<string> diagnostics,
        PdfVisualFallbackProjection? fallback = null)
    {
        var messages = diagnostics?.ToArray() ?? [];
        var issues = new List<string>();
        var accounting = graph?.Accounting;
        if (messages.Any(Is("VisualFallbackUsed")) && (accounting is null ||
            (accounting.FallbackPaths == 0 && (fallback?.OmittedFallbackPaths ?? 0) <= 0)))
            issues.Add("INV-01: VisualFallbackUsed requires fallback accounting or a compacted fallback count.");
        if (messages.Any(Is("VisualSemanticProjectionUnavailable")) && graph is not null &&
            graph.Accounting.UnresolvedEdges == 0 && graph.Accounting.FallbackPaths == 0 && graph.Accounting.Diagnostics == 0)
            issues.Add("INV-02: complete visual graph cannot report semantic projection unavailable.");
        var unresolved = messages.Count(Is("VisualConnectorUnresolved"));
        if (graph is not null && unresolved > graph.Accounting.UnresolvedEdges)
            issues.Add("INV-03: unresolved connector diagnostics exceed unresolved edge accounting.");
        if (graph?.SourceItems is { } sourceItems)
        {
            var tableGridPathIds = sourceItems.Where(item => item.Disposition == VisualDisposition.IgnoredDecorative &&
                    item.Reason?.Contains("table/grid", StringComparison.OrdinalIgnoreCase) == true)
                .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var edgePathIds = (graph.Paths ?? []).Where(path => graph.Edges.Any(edge => edge.Path is not null &&
                    path.Points is not null && SamePath(edge.Path, path.Points)))
                .Select(path => path.Id).ToHashSet(StringComparer.Ordinal);
            if (tableGridPathIds.Overlaps(edgePathIds))
                issues.Add("INV-04: a suppressed table-grid path must not remain a connector edge.");
        }
        if (graph is not null && graph.SourceItems is not null && !graph.SourceAccounting.IsConsistent)
            issues.Add("INV-05: every source visual primitive requires one final disposition.");
        return issues;

        static bool SamePath(IReadOnlyList<VisualPathPoint> left, IReadOnlyList<VisualPathPoint> right) =>
            ReferenceEquals(left, right) || left.Count == right.Count && left.Zip(right).All(pair =>
                pair.First.X.Equals(pair.Second.X) && pair.First.Y.Equals(pair.Second.Y));

        static Func<string, bool> Is(string code) => message => message.StartsWith(code + ":", StringComparison.Ordinal);
    }
}

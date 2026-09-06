namespace DocRedock.Formats.Pdf;

/// <summary>Reconciles the native text fragments parsed from a PDF page against the only two places
/// a fragment may legitimately end up: a reconstructed table cell, or a readable flow region.
/// Membership is expressed with stable source ids, never with list positions, because merging
/// fragments into readable lines changes both the length and the order of the region list.</summary>
public static class PdfTextAccounting
{
    public const string UnaccountedDiagnosticCode = "PdfNativeTextUnaccounted";

    public static IReadOnlySet<int> TableSourceIds(IReadOnlyList<PdfTable>? tables) =>
        (tables ?? []).SelectMany(table => table.Rows).SelectMany(row => row.Cells)
            .SelectMany(cell => cell.SourceTextIds).ToHashSet();

    /// <summary>A region belongs to a table only when every fragment it represents was assigned to a
    /// cell. A region that also carries unassigned fragments stays in the flow: removing it would
    /// delete native text that no table cell reproduces.</summary>
    public static bool IsTableOwned(PdfTextRegion region, IReadOnlySet<int>? tableSourceIds)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (tableSourceIds is not { Count: > 0 }) return false;
        var ids = region.SourceTextIds;
        return ids.Count > 0 && ids.All(tableSourceIds.Contains);
    }

    public static PdfTextAccountingResult Reconcile(IReadOnlyCollection<int> parsedSourceIds,
        IReadOnlyList<PdfTextRegion> flowRegions, IReadOnlyList<PdfTable>? tables)
    {
        ArgumentNullException.ThrowIfNull(parsedSourceIds);
        ArgumentNullException.ThrowIfNull(flowRegions);
        var tableIds = TableSourceIds(tables);
        var parsed = parsedSourceIds.ToHashSet();
        var accounted = new HashSet<int>(tableIds);
        foreach (var region in flowRegions)
        {
            if (IsTableOwned(region, tableIds)) continue;
            foreach (var id in region.SourceTextIds) accounted.Add(id);
        }
        return new PdfTextAccountingResult(
            parsed.Where(id => !accounted.Contains(id)).Order().ToArray(),
            tableIds.Where(id => !parsed.Contains(id)).Order().ToArray());
    }

    /// <summary>Reconciles an already-projected page. The parsed population is taken from the page
    /// itself, so this overload detects cells that reference fragments the page never carried.</summary>
    public static PdfTextAccountingResult Reconcile(PdfPageText page, IReadOnlyList<PdfTable>? tables)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Reconcile(page.Regions.SelectMany(region => region.SourceTextIds).ToArray(), page.Regions, tables);
    }
}

public sealed record PdfTextAccountingResult(
    IReadOnlyList<int> UnaccountedSourceIds,
    IReadOnlyList<int> UnknownTableSourceIds)
{
    public bool IsComplete => UnaccountedSourceIds.Count == 0 && UnknownTableSourceIds.Count == 0;

    public IReadOnlyList<string> Describe(int pageNumber)
    {
        var messages = new List<string>();
        if (UnaccountedSourceIds.Count > 0)
            messages.Add($"{PdfTextAccounting.UnaccountedDiagnosticCode}: PDF page {pageNumber}: " +
                $"{UnaccountedSourceIds.Count} native text fragment(s) were represented by neither a table cell nor a flow region; they are kept as flow text.");
        if (UnknownTableSourceIds.Count > 0)
            messages.Add($"{PdfTextAccounting.UnaccountedDiagnosticCode}: PDF page {pageNumber}: " +
                $"{UnknownTableSourceIds.Count} table cell text reference(s) match no native text fragment; native text is kept as flow text.");
        return messages;
    }
}

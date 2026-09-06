namespace DocRedock.Api;

/// <summary>
/// Thrown when a readable XLSX export's <c>--sheets</c> selection names one or more
/// worksheets that do not exist in the workbook. A partial mismatch (some requested
/// names exist, some do not) also fails the whole export: unknown names are never
/// silently dropped, because doing so previously let a typo produce an empty but
/// "successful" export (exit code 0) — dangerous for CI and AI-agent pipelines.
/// </summary>
public sealed class SheetSelectionException : Exception
{
    public SheetSelectionException(
        IReadOnlyList<string> requestedSheets,
        IReadOnlyList<string> unknownSheets,
        IReadOnlyList<string> availableSheets)
        : base(BuildMessage(requestedSheets, unknownSheets, availableSheets))
    {
        RequestedSheets = requestedSheets;
        UnknownSheets = unknownSheets;
        AvailableSheets = availableSheets;
    }

    /// <summary>All sheet names requested via <c>--sheets</c>, in the order given.</summary>
    public IReadOnlyList<string> RequestedSheets { get; }

    /// <summary>The subset of <see cref="RequestedSheets"/> that matched no worksheet.</summary>
    public IReadOnlyList<string> UnknownSheets { get; }

    /// <summary>
    /// Every worksheet actually present in the workbook, including hidden ones
    /// (marked with a " (hidden)" suffix) since they still exist and remain a valid
    /// selection target.
    /// </summary>
    public IReadOnlyList<string> AvailableSheets { get; }

    private static string BuildMessage(
        IReadOnlyList<string> requestedSheets,
        IReadOnlyList<string> unknownSheets,
        IReadOnlyList<string> availableSheets)
    {
        ArgumentNullException.ThrowIfNull(requestedSheets);
        ArgumentNullException.ThrowIfNull(unknownSheets);
        ArgumentNullException.ThrowIfNull(availableSheets);
        var unknownList = string.Join(", ", unknownSheets.Select(sheet => $"'{sheet}'"));
        var availableList = availableSheets.Count == 0
            ? "(workbook has no worksheets)"
            : string.Join(", ", availableSheets);
        var plural = unknownSheets.Count == 1 ? "name" : "names";
        return $"--sheets requests unknown sheet {plural}: {unknownList}. " +
               "Every requested sheet name must exactly match a worksheet (case-insensitive, " +
               "surrounding whitespace ignored); if some requested names exist and some do not, " +
               "the export still fails rather than silently dropping the unknown ones. " +
               $"Available sheets: {availableList}.";
    }
}

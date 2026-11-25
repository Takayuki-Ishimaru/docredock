using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Source selection and content-policy decisions shared by the readable Markdown serializer and the
/// typed readable projection, so both read the same workbook sheets and keep the same nodes.</summary>
internal static class ReadableGraphPreparation
{
    /// <summary>The worksheet name a partition id carries (exact, underscores intact).</summary>
    public static string SheetName(string partitionId)
    {
        var sheet = partitionId;
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (sheet.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return sheet[prefix.Length..];
        return sheet;
    }

    public static bool IsSelectedPartition(string partitionId, IReadOnlyList<string>? includedSheets)
    {
        if (includedSheets is not { Count: > 0 }) return true;
        var sheetName = SheetName(partitionId);
        return includedSheets.Any(sheet => StringComparer.OrdinalIgnoreCase.Equals(sheet.Trim(), sheetName));
    }

    public static IReadOnlyList<DocumentPartition> SelectedPartitions(DocumentGraph graph, ReadableProjectionOptions options) =>
        graph.Partitions
            .Where(partition => graph.Format != DocumentFormatKind.Xlsx || IsSelectedPartition(partition.Id, options.IncludedSheets))
            .OrderBy(partition => partition.Order)
            .ToArray();

    public static bool HasExtension(DocumentNode node, string key) =>
        node.Extensions is not null && node.Extensions.ContainsKey(key);

    public static string? ExtensionText(DocumentNode node, string key) =>
        node.Extensions is not null && node.Extensions.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static int? ExtensionInt(DocumentNode node, string key) =>
        node.Extensions is not null && node.Extensions.TryGetValue(key, out var value) &&
        (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var number) ||
         value.ValueKind is JsonValueKind.String && int.TryParse(value.GetString(), out number))
            ? number : null;

    public static bool ExtensionBool(DocumentNode node, string key) =>
        node.Extensions?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.True;

    public static JsonElement Extension(DocumentNode node, string key) =>
        node.Extensions?.TryGetValue(key, out var value) == true ? value : default;

    public static bool IsReadableOverlay(DocumentNode node) => node.Kind == NodeKind.Shape &&
        HasExtension(node, "sheet_overlay") && ExtensionText(node, "sheet_state") is null or "visible";

    public static (DocumentGraph Graph, IReadOnlyList<MarkdownDiagnostic> Diagnostics) Prepare(
        DocumentGraph graph, ReadableProjectionOptions options, CancellationToken cancellationToken = default)
    {
        var policy = DocumentContentPolicyRules.Parse(options.ContentPolicy);
        var selected = SelectedPartitions(graph, options);
        var excluded = 0; var sensitive = 0;
        var partitions = new List<DocumentPartition>();
        foreach (var partition in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodes = new List<DocumentNode>();
            foreach (var node in partition.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsReadableOverlay(node) && (node.Layer is ContentLayer.Hidden or ContentLayer.Metadata ||
                    node.Kind is NodeKind.Comment or NodeKind.Revision or NodeKind.SpeakerNotes)) sensitive++;
                if (DocumentContentPolicyRules.Includes(node, policy) || IsReadableOverlay(node)) nodes.Add(ReadableRunProjection.Clean(node));
                else excluded++;
            }
            partitions.Add(partition with { Nodes = nodes });
        }
        IReadOnlyList<MarkdownDiagnostic> diagnostics = policy == DocumentContentPolicy.Complete && sensitive > 0
            ? [new("HiddenContentIncluded", $"Complete content policy included {sensitive} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Warning)]
            : excluded > 0 ? [new("HiddenContentExcluded", $"{DocumentContentPolicyRules.Name(policy)} content policy excluded {excluded} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Info)] : [];
        var notes = new List<MarkdownDiagnostic>(diagnostics);
        var extensions = graph.FormatExtensions is null ? null : new Dictionary<string, JsonElement>(graph.FormatExtensions, StringComparer.Ordinal);
        if (extensions?.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var raw) == true &&
            WorkbookPresentationLayoutJson.TryRead(raw, out var layout, null) && layout is not null && policy == DocumentContentPolicy.Sanitized)
            extensions[WorkbookPresentationLayoutJson.ExtensionKey] = WorkbookPresentationLayoutJson.Write(layout with
            { Sheets = layout.Sheets.ToDictionary(kv => kv.Key, kv => kv.Value with { Print = kv.Value.Print is { } print ? print with { HeaderFooter = null, HeaderFooterPresent = false } : null }, StringComparer.Ordinal) });
        notes.AddRange(PrintHeaderFooterDiagnostics(graph, selected, policy));
        return (graph with { Partitions = partitions, FormatExtensions = extensions }, notes);
    }

    // Presence flag only: header/footer text (page codes, confidentiality labels) never reaches the graph,
    // so the warning names the sheet without quoting it. Sanitized strips headers on purpose: no warning.
    private static IEnumerable<MarkdownDiagnostic> PrintHeaderFooterDiagnostics(
        DocumentGraph graph, IReadOnlyList<DocumentPartition> selected, DocumentContentPolicy policy)
    {
        if (graph.Format != DocumentFormatKind.Xlsx || policy == DocumentContentPolicy.Sanitized) yield break;
        if (graph.FormatExtensions is null ||
            !graph.FormatExtensions.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var rawLayout) ||
            !WorkbookPresentationLayoutJson.TryRead(rawLayout, out var layout, null) || layout is null)
            yield break;
        foreach (var partition in selected)
        {
            var sheetName = SheetName(partition.Id);
            if (!layout.Sheets.TryGetValue("sheet-" + sheetName, out var sheet)) continue;
            if (policy == DocumentContentPolicy.Visible && (sheet.State is "hidden" or "veryHidden" ||
                partition.Nodes.Any(node => ExtensionText(node, "sheet_state") is "hidden" or "veryHidden"))) continue;
            if (sheet.Print?.HeaderFooterInvalidCount > 0)
                yield return new("XlsxPrintHeaderFooterInvalid", $"Sheet '{sheetName}' contains an oversized print header or footer. That field was omitted without truncation.", MarkdownDiagnosticSeverity.Warning, partition.Id);
            if (sheet.Print?.HeaderFooterPresent != true || sheet.Print.HeaderFooter is not null) continue;
            yield return new MarkdownDiagnostic("XlsxPrintHeaderFooterNotRendered",
                $"Sheet '{sheetName}' has print headers or footers (for example page numbers or confidentiality labels). " +
                "Excel header and footer content, including dynamic page numbering, cannot be faithfully reproduced in this output.",
                MarkdownDiagnosticSeverity.Warning, partition.Id);
        }
    }
}

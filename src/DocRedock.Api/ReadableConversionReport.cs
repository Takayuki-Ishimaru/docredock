using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

public sealed record ReadableConversionCount(string Kind, int Recognized, int Included, int ExcludedByPolicy);
public sealed record ReadableConversionIssue(string Code, string Severity, string Reason,
    string? SheetName = null, string? Range = null, string? ObjectId = null);
public sealed record ReadableConversionReport(string SchemaVersion, string SourceName, string OutputFormat,
    string ContentPolicy, IReadOnlyList<ReadableConversionCount> Counts,
    IReadOnlyList<ReadableConversionIssue> Issues, IReadOnlyList<string> ImagesNotWritten,
    IReadOnlyList<string> FormulaCellsWithoutCachedValues, IReadOnlyDictionary<string, string> Capabilities)
{
    public string AccountingBasis { get; init; } =
        "Counts describe recognized extracted objects and content-policy selection; they are not proof that all source content was parsed, or that layout was compared with the original. The source_drawing_objects row counts raw DrawingML objects recognized from the source scene and is separate from the semantic drawings row; recognition is not exact rendering fidelity.";
}

public static class ReadableConversionReportBuilder
{
    public static ReadableConversionReport Build(DocumentGraph graph, IEnumerable<Diagnostic> diagnostics,
        ReadableProjectionReport projection, string sourceName, string outputFormat, string contentPolicy,
        IReadOnlyList<string>? sheets = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(projection);
        var policy = DocumentContentPolicyRules.Parse(contentPolicy);
        string Name(string id)
        {
            foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return id[prefix.Length..];
            return id;
        }
        var selected = graph.Partitions.Where(part => graph.Format != DocumentFormatKind.Xlsx || sheets is not { Count: > 0 } ||
            sheets.Any(sheet => sheet.Trim().Equals(Name(part.Id), StringComparison.OrdinalIgnoreCase))).ToArray();
        var scope = selected.SelectMany(part => part.Nodes.Select(node => (Node: node, Part: part))).ToArray();
        var included = scope.Where(item => DocumentContentPolicyRules.Includes(item.Node, policy)).ToArray();
        var includedParts = included.Select(item => item.Part.Id).ToHashSet(StringComparer.Ordinal);
        if (graph.Format == DocumentFormatKind.Xlsx && graph.FormatExtensions is { } extensions)
        {
            WorkbookPresentationLayout? sourceLayout = null;
            if (extensions.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var rawLayout))
                WorkbookPresentationLayoutJson.TryRead(rawLayout, out sourceLayout);
            var scenes = extensions.TryGetValue(WorkbookDrawingSceneJson.ExtensionKey, out var rawScenes)
                ? WorkbookDrawingSceneJson.Read(rawScenes) : new Dictionary<string, WorkbookDrawingScene>();
            foreach (var part in selected)
            {
                var sheet = sourceLayout?.Sheets.GetValueOrDefault(part.Id);
                if (policy != DocumentContentPolicy.Complete && sheet?.State is "hidden" or "veryHidden") continue;
                if (scenes.GetValueOrDefault(part.Id)?.Objects.Any(o => policy == DocumentContentPolicy.Complete || !o.Hidden) == true ||
                    policy != DocumentContentPolicy.Sanitized && sheet?.Print?.HeaderFooter is not null) includedParts.Add(part.Id);
            }
        }
        var counts = new List<ReadableConversionCount>
        {
            new("sheets", selected.Length, selected.Count(part => includedParts.Contains(part.Id)),
                selected.Count(part => !includedParts.Contains(part.Id)))
        };
        void Count(string kind, Func<DocumentNode, bool> predicate)
        {
            var total = scope.Count(item => predicate(item.Node));
            var kept = included.Count(item => predicate(item.Node));
            counts.Add(new(kind, total, kept, total - kept));
        }
        Count("cells", node => node.Kind == NodeKind.Cell);
        Count("tables", node => node.Kind == NodeKind.Table);
        Count("images", node => node.Kind == NodeKind.Image);
        Count("drawings", node => node.Kind is NodeKind.Shape or NodeKind.Connector or NodeKind.Diagram);
        CountSourceDrawingObjects();
        Count("comments", node => node.Kind == NodeKind.Comment);
        Count("formulas", node => Flag(node, "is_formula") || Text(node, "formula") is not null);
        void CountSourceDrawingObjects()
        {
            if (graph.Format != DocumentFormatKind.Xlsx || graph.FormatExtensions is null ||
                !graph.FormatExtensions.TryGetValue(WorkbookDrawingSceneJson.ExtensionKey, out var rawScenes))
                return;
            var scenes = WorkbookDrawingSceneJson.Read(rawScenes);
            var hiddenSheets = new HashSet<string>(StringComparer.Ordinal);
            if (graph.FormatExtensions.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var rawLayout) &&
                WorkbookPresentationLayoutJson.TryRead(rawLayout, out var layout) && layout is not null)
                foreach (var sheet in layout.Sheets)
                    if (sheet.Value.State is "hidden" or "veryHidden") hiddenSheets.Add(sheet.Key);
            var total = 0; var kept = 0;
            foreach (var part in selected)
            {
                var objects = scenes.GetValueOrDefault(part.Id)?.Objects ?? [];
                total += objects.Count;
                // The adapter marks every node of a hidden sheet; the layout extension is authoritative when present.
                var sheetHidden = hiddenSheets.Contains(part.Id) ||
                    part.Nodes.Any(node => Text(node, "sheet_state") is "hidden" or "veryHidden");
                var nodeIds = part.Nodes.Where(node => DocumentContentPolicyRules.Includes(node, policy)).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var obj in objects)
                {
                    var include = policy == DocumentContentPolicy.Complete || (!obj.Hidden && !sheetHidden);
                    // A picture stays an included object only while its source node is present; an intentionally
                    // omitted image keeps its node (image_omitted) and therefore remains counted, never silently missing.
                    if (include && obj.Kind == "picture")
                        include = obj.SourceNodeId is { } sourceId && nodeIds.Contains(sourceId);
                    if (include) kept++;
                }
            }
            counts.Add(new("source_drawing_objects", total, kept, total - kept));
        }
        var notices = diagnostics.ToArray();
        var wanted = notices.Select(item => item.NodeId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var byId = scope.Where(item => wanted.Contains(item.Node.Id)).GroupBy(item => item.Node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var issues = new List<ReadableConversionIssue>();
        foreach (var diagnostic in notices)
        {
            string? sheet = null, range = null, id = diagnostic.NodeId;
            var reason = diagnostic.Message;
            if (id is not null && byId.TryGetValue(id, out var item))
            {
                if (!DocumentContentPolicyRules.Includes(item.Node, policy))
                { reason = "Excluded by the selected content policy."; id = null; }
                else
                {
                    sheet = graph.Format == DocumentFormatKind.Xlsx ? Name(item.Part.Id) : null;
                    range = Text(item.Node, "address") ?? Text(item.Node, "cell_ref") ??
                        item.Node.Source?.Locators.FirstOrDefault(locator => locator.Kind is "cell_address" or "cell_range")?.Value;
                }
            }
            else if (id is not null && selected.Any(part => part.Id == id)) sheet = Name(id);
            issues.Add(new(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), reason, sheet, range, id));
        }
        var missing = included.Where(item => (Flag(item.Node, "is_formula") || Text(item.Node, "formula") is not null) &&
                item.Node.Extensions?.TryGetValue("has_cached_value", out var cached) == true && cached.ValueKind == JsonValueKind.False)
            .Select(item => item.Node.Id).ToArray();
        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["formula_recalculation"] = "Not performed. Saved results are used; missing cached values are labeled.",
            ["input_validation"] = "Source validation rules are not an interactive Excel validation engine.",
            ["outline_groups"] = "Excel outline expansion is not reconstructed.",
            ["excel_editor"] = "Cell selection and an editable formula bar are not provided; this is a document viewer.",
            ["conditional_formatting"] = "Supported saved/static formatting is retained; unevaluated rules are reported.",
            ["print_layout"] = "Worksheet drawing scenes and print headers/footers use fixed source coordinates and planned paper pages, margins, print areas, repeated titles and breaks. Office HTML can use fixed source pages from a configured local Office engine; structured mode uses browser pagination.",
            ["markdown_layout"] = "Markdown reflows page geometry and cannot reproduce all source colors, borders and fonts. Use the native layout companion for comparison.",
            ["source_drawing_details"] = "Native source scenes retain supported geometry, gradients, mixed caption styles, vertical text, transforms and cached chart plots. Saved SmartArt drawings retain their source coordinates; a configured local Office engine provides page views for Word typesetting, 3D and uncached layouts. Engine/font differences remain explicit; raw Office sources are never rewritten.",
        };
        return new("1.0", sourceName, outputFormat, contentPolicy, counts, issues,
            projection.UnwrittenImageIds, missing, capabilities);
    }

    private static bool Flag(DocumentNode node, string key) => node.Extensions?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.True;
    private static string? Text(DocumentNode node, string key) => node.Extensions?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

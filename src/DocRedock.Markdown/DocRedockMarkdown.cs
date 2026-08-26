using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Evidence attached to a projection contribution.</summary>
public enum ProjectionEvidence
{
    Native,
    AltText,
    Ocr,
    UserCorrectedOcr,
    LayoutInferred,
    TableInferred,
    VisionDescribed,
    Generated,
    Unknown
}

public enum ProjectionRole
{
    PrimaryText,
    HeadingLabel,
    TableCell,
    ImageReference,
    OcrText,
    WarningAnnotation,
    IntegrityMarker,
    ContextOnly
}

public sealed record ProjectionContribution(
    string ProjectionId,
    string ProjectionBlockId,
    TextRange MarkdownRange,
    string NodeId,
    ProjectionRole Role,
    TextRange? NodeCharacterRange,
    ProjectionEvidence Evidence);

public sealed record TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed record MarkdownSerializationOptions
{
    public string ProjectionId { get; init; } = "proj_default";
    public string RoundTripStore { get; init; } = "document.drmd";
    public string ContentPolicy { get; init; } = "visible";
    public string SchemaVersion { get; init; } = "1.0";
    public string RulesVersion { get; init; } = "1.0";
    public bool IncludeFrontMatter { get; init; } = true;
}

public sealed record MarkdownProjection(
    string ProjectionId,
    string Markdown,
    IReadOnlyList<ProjectionContribution> Contributions,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics);

public enum MarkdownDiagnosticSeverity { Info, Warning, Error }

public sealed record MarkdownDiagnostic(
    string Code,
    string Message,
    MarkdownDiagnosticSeverity Severity = MarkdownDiagnosticSeverity.Warning,
    string? BlockId = null);

/// <summary>A typed, provider-neutral view of a DRMD Markdown block.</summary>
public sealed record TypedMarkdownBlock(
    string? NodeId,
    string Kind,
    string Text,
    TextRange SourceRange,
    IReadOnlyDictionary<string, string> Attributes,
    bool IsExplicitDelete = false,
    bool IsNew = false,
    string? PartitionId = null);

public sealed record TypedMarkdownDocument(
    string? DocumentId,
    string? SchemaVersion,
    IReadOnlyList<TypedMarkdownBlock> Blocks,
    IReadOnlySet<string> DeclaredNodeIds,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics,
    bool IsComplete,
    string? SourceFormat = null,
    string? RoundTripStore = null,
    string? RulesVersion = null);

public sealed record MarkdownParseOptions
{
    public bool Strict { get; init; } = true;
    public bool RequireFrontMatter { get; init; } = true;
    public bool RequireDocumentEnd { get; init; } = true;
}

/// <summary>
/// Deterministic DRMD projection serializer. The graph argument is intentionally
/// object-shaped so the Markdown provider remains independent of the Core model
/// namespace/version; the adapter reads the stable DocumentGraph contract by name.
/// </summary>
public sealed class DocRedockMarkdownSerializer
{
    private static readonly string[] NodeCollectionNames = ["Nodes", "Children"];

    public MarkdownProjection Serialize(object graph, MarkdownSerializationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new MarkdownSerializationOptions();
        var contentPolicy = NormalizeContentPolicy(options.ContentPolicy);
        var view = ReflectionGraphView.Read(graph);
        var output = new StringBuilder();
        var contributions = new List<ProjectionContribution>();

        var documentId = view.DocumentId ?? "doc_unknown";
        var sourceFormat = view.Format ?? "unknown";
        var partitions = view.Partitions.Count == 0
            ? [new GraphPartitionView("part-0001", 0, view.Nodes)]
            : view.Partitions;

        if (options.IncludeFrontMatter)
        {
            output.AppendLine("---");
            output.Append("drmd_schema: ").AppendLine(options.SchemaVersion);
            output.Append("drmd_rules: ").AppendLine(options.RulesVersion);
            output.Append("document_id: ").AppendLine(EscapeFrontMatter(documentId));
            output.Append("source_format: ").AppendLine(EscapeFrontMatter(sourceFormat));
            output.Append("roundtrip_store: ").AppendLine(EscapeFrontMatter(options.RoundTripStore));
            output.Append("content_policy: ").AppendLine(contentPolicy);
            output.AppendLine("preserve_drmd_comments: true");
            output.AppendLine("---");
        }

        foreach (var partition in partitions.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var nodes = partition.Nodes
                .Where(node => IncludeInPolicy(node, contentPolicy))
                .OrderBy(n => n.Order)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
            output.Append("<!--drmd:partition-begin id=").Append(EscapeAttribute(partition.Id))
                .Append(" baseline_nodes=").Append(nodes.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");
            AddContribution(contributions, options.ProjectionId, "partition-begin", new TextRange(output.Length - 1, 0),
                "", ProjectionRole.IntegrityMarker, ProjectionEvidence.Generated);

            foreach (var node in nodes)
            {
                var blockId = node.Id;
                var kind = NormalizeKind(node.Kind);
                var markerStart = output.Length;
                output.Append("<!--drmd:block id=").Append(EscapeAttribute(blockId)).Append(" kind=")
                    .Append(EscapeAttribute(kind)).AppendLine("-->");
                var textStart = output.Length;
                AppendNodeText(output, node, kind);
                var textLength = Math.Max(0, output.Length - textStart);
                if (output.Length == textStart || output[^1] != '\n') output.AppendLine();
                output.AppendLine();
                var role = kind is "heading" or "title" ? ProjectionRole.HeadingLabel :
                    kind is "image" ? ProjectionRole.ImageReference :
                    kind is "table-cell" or "cell" ? ProjectionRole.TableCell :
                    node.Evidence == ProjectionEvidence.Ocr ? ProjectionRole.OcrText : ProjectionRole.PrimaryText;
                AddContribution(contributions, options.ProjectionId, blockId,
                    new TextRange(textStart, textLength), blockId, role, node.Evidence);
                _ = markerStart;
            }

            output.Append("<!--drmd:partition-end id=").Append(EscapeAttribute(partition.Id))
                .Append(" baseline_nodes=").Append(nodes.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");
        }

        output.Append("<!--drmd:document-end id=").Append(EscapeAttribute(documentId))
            .Append(" partitions=").Append(partitions.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");

        return new MarkdownProjection(options.ProjectionId, output.ToString(), contributions,
            Array.Empty<MarkdownDiagnostic>());
    }

    private static void AppendNodeText(StringBuilder output, GraphNodeView node, string kind)
    {
        var text = node.Text ?? string.Empty;
        switch (kind)
        {
            case "heading":
            case "title":
                output.Append("# ").AppendLine(text);
                break;
            case "quote":
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    output.Append("> ").AppendLine(line);
                break;
            case "code-block":
                output.AppendLine("```").AppendLine(text).AppendLine("```");
                break;
            default:
                output.AppendLine(text);
                break;
        }
    }

    private static void AddContribution(List<ProjectionContribution> list, string projectionId, string blockId,
        TextRange range, string nodeId, ProjectionRole role, ProjectionEvidence evidence) =>
        list.Add(new ProjectionContribution(projectionId, blockId, range, nodeId, role, null, evidence));

    internal static string EscapeAttribute(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeFrontMatter(string value) => value.Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "", StringComparison.Ordinal);

    private static string NormalizeContentPolicy(string value) => value.Trim().ToLowerInvariant() switch
    {
        "visible" => "visible",
        "complete" => "complete",
        "sanitized" => "sanitized",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Content policy must be visible, complete, or sanitized."),
    };

    private static bool IncludeInPolicy(GraphNodeView node, string policy)
    {
        if (policy == "complete") return true;
        if (node.Layer.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
            node.Layer.Equals("metadata", StringComparison.OrdinalIgnoreCase)) return false;
        return !node.Kind.Equals("comment", StringComparison.OrdinalIgnoreCase) &&
            !node.Kind.Equals("revision", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKind(string kind)
    {
        var value = Regex.Replace(kind.Replace("_", "-", StringComparison.Ordinal), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
        return value switch { "paragraph" => "paragraph", "heading" => "heading", "image" => "image", _ => value };
    }

    private sealed record GraphPartitionView(string Id, int Order, IReadOnlyList<GraphNodeView> Nodes);
    private sealed record GraphNodeView(string Id, string Kind, string? Text, int Order, ProjectionEvidence Evidence, string Layer);

    /// <summary>Type-safe entry point for the canonical Core DocumentGraph.</summary>
    public MarkdownProjection Serialize(DocumentGraph graph, MarkdownSerializationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new MarkdownSerializationOptions();
        var policy = NormalizeContentPolicy(options.ContentPolicy);
        var output = new StringBuilder();
        var contributions = new List<ProjectionContribution>();
        var diagnostics = new List<MarkdownDiagnostic>();
        if (options.IncludeFrontMatter)
        {
            output.AppendLine("---");
            output.Append("drmd_schema: ").AppendLine(options.SchemaVersion);
            output.Append("drmd_rules: ").AppendLine(options.RulesVersion);
            output.Append("document_id: ").AppendLine(EscapeFrontMatter(graph.DocumentId));
            output.Append("source_format: ").AppendLine(graph.Format.ToString().ToLowerInvariant());
            output.Append("roundtrip_store: ").AppendLine(EscapeFrontMatter(options.RoundTripStore));
            output.Append("content_policy: ").AppendLine(policy);
            output.AppendLine("preserve_drmd_comments: true");
            output.AppendLine("---");
        }
        var partitions = graph.Partitions.OrderBy(partition => partition.Order).ThenBy(partition => partition.Id, StringComparer.Ordinal).ToArray();
        var assetReferences = graph.Assets?.ToDictionary(
            item => item.Key,
            item => MarkdownPathEncoder.Encode(options.RoundTripStore + "/assets/" + (item.Value.FileName ?? item.Key)),
            StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);

        void AppendNodeMarker(DocumentNode node, bool terminateLine)
        {
            var kind = NormalizeKind(node.Kind.ToString());
            var editPolicy = DocRedockMarkdownEditPolicy.For(graph.Format, node);
            output.Append("<!--drmd:block id=").Append(EscapeAttribute(node.Id))
                .Append(" kind=").Append(EscapeAttribute(kind))
                .Append(" editability=").Append(EscapeAttribute(editPolicy.Editability))
                .Append(" operations=").Append(EscapeAttribute(editPolicy.Operations.Count == 0 ? "none" : string.Join(',', editPolicy.Operations)))
                .Append(" constraints=").Append(EscapeAttribute(string.Join(',', editPolicy.Constraints)));
            AppendSemanticAttributes(output, node);
            if (terminateLine) output.AppendLine("-->");
            else output.Append("-->");
        }

        void AddImageDiagnostic(DocumentNode node)
        {
            if (node.Kind != NodeKind.Image || node.Content is not ReferenceNodeContent image ||
                ImageDisplayPolicy.IsMarkdownDisplayable(ImageMediaType(node, image.Reference))) return;
            diagnostics.Add(new MarkdownDiagnostic("ImageFormatNotDisplayable",
                $"Image '{image.Reference}' uses a format that Markdown cannot display.", MarkdownDiagnosticSeverity.Warning, node.Id));
        }

        void AddNodeContribution(DocumentNode node, int start, int length) =>
            AddContribution(contributions, options.ProjectionId, node.Id, new TextRange(start, length),
                node.Id, RoleFor(node), EvidenceFor(node));

        void AppendNodeBlock(DocumentNode node, bool suppressVisibleContent = false)
        {
            AppendNodeMarker(node, terminateLine: true);
            var start = output.Length;
            AddImageDiagnostic(node);
            if (!suppressVisibleContent)
                AppendCoreNode(output, node, reference => assetReferences.TryGetValue(reference, out var path) ? path : ImageReference(reference, options.RoundTripStore));
            var length = output.Length - start;
            if (length == 0 || output[^1] != '\n') output.AppendLine();
            output.AppendLine();
            AddNodeContribution(node, start, length);
        }

        void AppendPositionedImageRow(IReadOnlyList<DocumentNode> images)
        {
            for (var imageIndex = 0; imageIndex < images.Count; imageIndex++)
            {
                if (imageIndex > 0) output.Append(' ');
                var node = images[imageIndex];
                AppendNodeMarker(node, terminateLine: false);
                var start = output.Length;
                AddImageDiagnostic(node);
                AppendXlsxImage(output, node,
                    reference => assetReferences.TryGetValue(reference, out var path) ? path : ImageReference(reference, options.RoundTripStore));
                AddNodeContribution(node, start, output.Length - start);
            }
            output.AppendLine().AppendLine();
        }

        foreach (var partition in partitions)
        {
            var nodes = partition.Nodes.Where(node => IncludeCorePolicy(node, policy)).OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
            output.Append("<!--drmd:partition-begin id=").Append(EscapeAttribute(partition.Id)).Append(" baseline_nodes=")
                .Append(nodes.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");
            AppendPartitionLabel(output, graph.Format, partition.Id);
            var sheetCells = graph.Format == DocumentFormatKind.Xlsx && TryReadSheetCells(nodes, out var parsedCells)
                ? parsedCells
                : Array.Empty<SpreadsheetCell>();
            var standaloneNodes = nodes.Where(node => sheetCells.All(cell => !StringComparer.Ordinal.Equals(cell.Node.Id, node.Id))).ToArray();
            var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var inlineLinkNodeIds = standaloneNodes.Where(node => IsInlineLinkProjection(node, nodesById))
                .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            var positionedImageRows = graph.Format == DocumentFormatKind.Xlsx
                ? standaloneNodes.Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent && ExtensionLong(node, "row") is > 0)
                    .GroupBy(node => ExtensionLong(node, "row")!.Value)
                    .OrderBy(group => group.Key)
                    .Select(group => new PositionedImageRow(group.Key, group
                        .OrderBy(node => ExtensionLong(node, "column") ?? long.MaxValue)
                        .ThenBy(node => node.Order)
                        .ThenBy(node => node.Id, StringComparer.Ordinal)
                        .ToArray()))
                    .ToArray()
                : Array.Empty<PositionedImageRow>();
            var positionedImageIds = positionedImageRows.SelectMany(row => row.Images).Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            var positionedChildIds = standaloneNodes.Where(node => node.ParentId is not null && positionedImageIds.Contains(node.ParentId))
                .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var diagram in standaloneNodes.Where(IsMermaidDiagram)) AppendNodeBlock(diagram);
            if (sheetCells.Length > 0)
            {
                var cellIndex = 0;
                foreach (var imageRow in positionedImageRows)
                {
                    var nextCellIndex = cellIndex;
                    while (nextCellIndex < sheetCells.Length && sheetCells[nextCellIndex].Row < imageRow.Row) nextCellIndex++;
                    AppendSheetSections(sheetCells[cellIndex..nextCellIndex]);
                    AppendPositionedImageRow(imageRow.Images);
                    foreach (var image in imageRow.Images)
                        foreach (var child in standaloneNodes.Where(node => StringComparer.Ordinal.Equals(node.ParentId, image.Id))
                                     .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
                            AppendNodeBlock(child);
                    cellIndex = nextCellIndex;
                }
                AppendSheetSections(sheetCells[cellIndex..]);
            }
            else
                foreach (var imageRow in positionedImageRows)
                {
                    AppendPositionedImageRow(imageRow.Images);
                    foreach (var image in imageRow.Images)
                        foreach (var child in standaloneNodes.Where(node => StringComparer.Ordinal.Equals(node.ParentId, image.Id))
                                     .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
                            AppendNodeBlock(child);
                }
            foreach (var node in standaloneNodes.Where(node => !IsMermaidDiagram(node) &&
                         !positionedImageIds.Contains(node.Id) && !positionedChildIds.Contains(node.Id)))
                AppendNodeBlock(node, inlineLinkNodeIds.Contains(node.Id));
            output.Append("<!--drmd:partition-end id=").Append(EscapeAttribute(partition.Id)).Append(" baseline_nodes=")
                .Append(nodes.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");

            void AppendSheetSections(IReadOnlyList<SpreadsheetCell> cells)
            {
                foreach (var section in SplitSheetSections(cells))
                {
                    var range = SheetRange(section);
                    var sourceColumns = string.Join(",", section.Select(cell => cell.Column).Distinct().Order().Select(ColumnName));
                    var sourceRows = string.Join(",", section.Select(cell => cell.Row).Distinct().Order());
                    output.Append("<!--drmd:sheet-table range=").Append(range)
                        .Append(" source-columns=").Append(sourceColumns)
                        .Append(" source-rows=").Append(sourceRows)
                        .Append(" baseline_nodes=").Append(section.Count.ToString(CultureInfo.InvariantCulture))
                        .Append(" editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula")
                        .AppendLine("-->");
                    var start = output.Length;
                    AppendSheetTable(output, section);
                    var length = output.Length - start;
                    output.AppendLine();
                    foreach (var cell in section)
                        AddContribution(contributions, options.ProjectionId, "sheet-table:" + partition.Id + ":" + range,
                            new TextRange(start, length), cell.Node.Id, ProjectionRole.TableCell, EvidenceFor(cell.Node));
                }
            }
        }
        output.Append("<!--drmd:document-end id=").Append(EscapeAttribute(graph.DocumentId)).Append(" partitions=")
            .Append(partitions.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("-->");
        return new MarkdownProjection(options.ProjectionId, output.ToString(), contributions, diagnostics);
    }

    private static bool IncludeCorePolicy(DocumentNode node, string policy) =>
        policy == "complete" || (node.Layer is not (ContentLayer.Hidden or ContentLayer.Metadata) && node.Kind is not (NodeKind.Comment or NodeKind.Revision));

    private static void AppendPartitionLabel(StringBuilder output, DocumentFormatKind format, string partitionId)
    {
        var label = format switch
        {
            DocumentFormatKind.Xlsx => "## " + (partitionId.StartsWith("sheet-", StringComparison.OrdinalIgnoreCase) ? partitionId[6..] : partitionId),
            DocumentFormatKind.Pptx => "## Slide " + partitionId.Replace("slide", "", StringComparison.OrdinalIgnoreCase),
            DocumentFormatKind.Pdf => "## Page " + partitionId.Replace("page-", "", StringComparison.OrdinalIgnoreCase).TrimStart('0'),
            _ => string.Empty
        };
        if (label.Length > 0) output.AppendLine(label).AppendLine();
    }

    private static void AppendCoreNode(StringBuilder output, DocumentNode node, Func<string, string> resolveImageReference)
    {
        var text = node.Content is RichTextNodeContent rich
            ? DocRedockInlineMarkdown.Serialize(rich.Runs)
            : NodeText(node) ?? string.Empty;
        switch (node.Kind)
        {
            case NodeKind.Heading:
                output.Append(new string('#', HeadingLevel(node.StyleId))).Append(' ').AppendLine(text);
                break;
            case NodeKind.ListItem:
                var listMarker = StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "list_format"), "ordered")
                    ? (ExtensionLong(node, "list_number") ?? 1).ToString(CultureInfo.InvariantCulture) + ". "
                    : "- ";
                output.Append(listMarker).AppendLine(text);
                break;
            case NodeKind.Table when node.Content is TableNodeContent table:
                AppendTable(output, table.Rows);
                break;
            case NodeKind.Image when node.Content is ReferenceNodeContent image:
                output.Append("![").Append(EscapeLinkText(image.AltText ?? "image")).Append("](").Append(resolveImageReference(image.Reference)).AppendLine(")");
                break;
            case NodeKind.Link when node.Content is ReferenceNodeContent link:
                output.Append("[").Append(EscapeLinkText(link.AltText ?? link.Reference)).Append("](").Append(link.Reference).AppendLine(")");
                break;
            case NodeKind.Cell:
                var address = node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value ?? node.Id;
                output.Append("- **").Append(address).Append(":** ");
                output.Append(ProjectSpreadsheetCell(node));
                output.AppendLine();
                break;
            case NodeKind.Quote:
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) output.Append("> ").AppendLine(line);
                break;
            case NodeKind.CodeBlock:
                var code = NodeText(node) ?? string.Empty;
                output.AppendLine(new string((char)96, 3)).Append(code);
                if (code.Length == 0 || code[^1] != '\n') output.AppendLine();
                output.AppendLine(new string((char)96, 3));
                break;
            case NodeKind.Diagram when IsMermaidDiagram(node):
                output.AppendLine("```mermaid").AppendLine(text).AppendLine("```");
                break;
            case NodeKind.Shape:
                AppendShapeText(output, node, text);
                break;
            default:
                output.AppendLine(text);
                break;
        }
    }

    private static void AppendXlsxImage(StringBuilder output, DocumentNode node, Func<string, string> resolveImageReference)
    {
        if (node.Content is not ReferenceNodeContent image)
        {
            AppendCoreNode(output, node, resolveImageReference);
            return;
        }

        var source = resolveImageReference(image.Reference);
        var alt = image.AltText ?? "image";
        if (ImageDisplayPolicy.IsMarkdownDisplayable(ImageMediaType(node, image.Reference)) &&
            TryImagePixelSize(node, out var width, out var height))
        {
            output.Append("<img src=\"").Append(EscapeHtmlAttribute(source))
                .Append("\" alt=\"").Append(EscapeHtmlAttribute(alt))
                .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture))
                .Append("\" height=\"").Append(height.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"max-width:49%;height:auto\">");
            return;
        }

        output.Append("![").Append(EscapeLinkText(alt)).Append("](").Append(source).Append(')');
    }

    private static void AppendSemanticAttributes(StringBuilder output, DocumentNode node)
    {
        if (node.Content is RichTextNodeContent) output.Append(" rich-text=inline-v1");
        if (node.Kind == NodeKind.Shape && ExtensionString(node, "shape_role") is { Length: > 0 } role)
            output.Append(" role=").Append(EscapeAttribute(role));
        if (IsMermaidDiagram(node))
        {
            output.Append(" language=mermaid");
            if (ExtensionString(node, "diagram_type") is { Length: > 0 } type)
                output.Append(" diagram-type=").Append(EscapeAttribute(type));
        }
    }

    private static void AppendShapeText(StringBuilder output, DocumentNode node, string text)
    {
        var role = ExtensionString(node, "shape_role")?.ToLowerInvariant();
        switch (role)
        {
            case "title":
            case "centered-title":
            case "ctrtitle":
                output.Append("### ").AppendLine(text);
                break;
            case "subtitle":
                output.Append("**Subtitle:** ").AppendLine(text);
                break;
            case "body":
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    output.Append("- ").AppendLine(line);
                break;
            default:
                output.AppendLine(text);
                break;
        }
    }

    private static string? ExtensionString(DocumentNode node, string name) =>
        node.Extensions is not null && node.Extensions.TryGetValue(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ExtensionLong(DocumentNode node, string name) =>
        node.Extensions is not null && node.Extensions.TryGetValue(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool IsMermaidDiagram(DocumentNode node) => node.Kind == NodeKind.Diagram &&
        StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "diagram_language"), "mermaid");

    private static bool IsInlineLinkProjection(DocumentNode node, IReadOnlyDictionary<string, DocumentNode> nodesById)
    {
        if (node.Kind != NodeKind.Link || node.Content is not ReferenceNodeContent link ||
            node.ParentId is null || !nodesById.TryGetValue(node.ParentId, out var parent) ||
            parent.Content is not RichTextNodeContent rich)
            return false;
        return rich.Runs.Any(run => StringComparer.Ordinal.Equals(run.LinkTarget, link.Reference));
    }

    private static bool TryReadSheetCells(IReadOnlyList<DocumentNode> nodes, out SpreadsheetCell[] cells)
    {
        var parsed = new List<SpreadsheetCell>();
        foreach (var node in nodes.Where(node => node.Kind == NodeKind.Cell))
        {
            var address = node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value;
            var match = address is null ? Match.Empty : Regex.Match(address, "^\\$?(?<column>[A-Za-z]+)\\$?(?<row>[1-9][0-9]*)$");
            if (!match.Success || !int.TryParse(match.Groups["row"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var row))
            {
                cells = [];
                return false;
            }
            parsed.Add(new SpreadsheetCell(node, ColumnNumber(match.Groups["column"].Value), row));
        }
        cells = parsed.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray();
        return cells.Length > 0;
    }

    private static void AppendSheetTable(StringBuilder output, IReadOnlyList<SpreadsheetCell> cells)
    {
        var columns = cells.Select(cell => cell.Column).Distinct().Order().ToArray();
        var rows = cells.Select(cell => cell.Row).Distinct().Order().ToArray();
        var byCoordinate = cells.ToDictionary(cell => (cell.Column, cell.Row));

        AppendRow(rows[0]);
        output.Append('|');
        foreach (var _ in columns) output.Append(" --- |");
        output.AppendLine();
        foreach (var row in rows.Skip(1)) AppendRow(row);

        void AppendRow(int row)
        {
            output.Append('|');
            foreach (var column in columns)
            {
                var value = byCoordinate.TryGetValue((column, row), out var cell) ? ProjectSpreadsheetCell(cell.Node) : string.Empty;
                output.Append(' ').Append(EscapeTableCell(value)).Append(" |");
            }
            output.AppendLine();
        }
    }

    private static IReadOnlyList<IReadOnlyList<SpreadsheetCell>> SplitSheetSections(IReadOnlyList<SpreadsheetCell> cells)
    {
        const int maximumRowGapWithinSection = 4;
        const int maximumColumnDistanceFromSection = 2;
        var recurringGaps = cells.GroupBy(cell => cell.Row)
            .SelectMany(row =>
            {
                var ordered = row.OrderBy(cell => cell.Column).ToArray();
                return Enumerable.Range(1, Math.Max(0, ordered.Length - 1))
                    .Where(index => ordered[index].Column - ordered[index - 1].Column >= 2)
                    .Select(index => (Row: row.Key, Right: ordered[index].Column,
                        HasWideSide: index >= 2 || ordered.Length - index >= 2));
            })
            .GroupBy(item => item.Right)
            .Where(group => group.Select(item => item.Row).Distinct().Count() >= 2 && group.Any(item => item.HasWideSide))
            .Select(group => group.Key)
            .ToHashSet();
        var sections = new List<List<SpreadsheetCell>>();

        foreach (var row in cells.GroupBy(cell => cell.Row).OrderBy(group => group.Key))
        {
            var ordered = row.OrderBy(cell => cell.Column).ToArray();
            var fragments = new List<List<SpreadsheetCell>>();
            var fragment = new List<SpreadsheetCell>();

            bool MatchesNearbySection(List<SpreadsheetCell> section, int rangeMinColumn, int rangeMaxColumn) =>
                row.Key - section.Max(item => item.Row) <= maximumRowGapWithinSection &&
                row.Key >= section.Min(item => item.Row) &&
                rangeMinColumn <= section.Max(item => item.Column) + maximumColumnDistanceFromSection &&
                rangeMaxColumn >= section.Min(item => item.Column) - maximumColumnDistanceFromSection;

            bool MatchesAdjacentSection(List<SpreadsheetCell> section, int rangeMinColumn, int rangeMaxColumn) =>
                row.Key - section.Max(item => item.Row) <= 1 &&
                row.Key >= section.Min(item => item.Row) &&
                rangeMinColumn <= section.Max(item => item.Column) + maximumColumnDistanceFromSection &&
                rangeMaxColumn >= section.Min(item => item.Column) - maximumColumnDistanceFromSection;

            for (var index = 0; index < ordered.Length; index++)
            {
                var cell = ordered[index];
                var columnGap = fragment.Count == 0 ? 0 : cell.Column - fragment[^1].Column;
                var hasTwoCellsOnEachSide = fragment.Count >= 2 && ordered.Length - index >= 2;
                var followsRecurringGap = fragment.Count > 0 && recurringGaps.Contains(cell.Column);
                var separatesNearbySections = false;
                if (fragment.Count > 0 && columnGap >= 2)
                {
                    var fragmentMinColumn = fragment.Min(item => item.Column);
                    var fragmentMaxColumn = fragment.Max(item => item.Column);
                    var fragmentMatches = sections
                        .Where(section => MatchesAdjacentSection(section, fragmentMinColumn, fragmentMaxColumn))
                        .ToArray();
                    var cellMatches = sections
                        .Where(section => MatchesAdjacentSection(section, cell.Column, cell.Column))
                        .ToArray();
                    separatesNearbySections = (fragmentMatches.Length > 0 || cellMatches.Length > 0) &&
                        !fragmentMatches.Intersect(cellMatches).Any();
                }
                if ((hasTwoCellsOnEachSide || followsRecurringGap || separatesNearbySections) && columnGap >= 2)
                {
                    fragments.Add(fragment);
                    fragment = [];
                }
                fragment.Add(cell);
            }
            if (fragment.Count > 0) fragments.Add(fragment);

            var sectionsUsedInCurrentRow = new HashSet<List<SpreadsheetCell>>();
            foreach (var rowFragment in fragments)
            {
                var minColumn = rowFragment.Min(cell => cell.Column);
                var maxColumn = rowFragment.Max(cell => cell.Column);
                var isExplicitRecurringGap = rowFragment.Count == 1 && recurringGaps.Contains(minColumn);
                var matching = isExplicitRecurringGap
                    ? null
                    : sections
                        .Where(section => !sectionsUsedInCurrentRow.Contains(section) &&
                            MatchesNearbySection(section, minColumn, maxColumn))
                        .OrderByDescending(section => section.Max(cell => cell.Row))
                        .ThenBy(section => Math.Abs(section.Min(cell => cell.Column) - minColumn))
                        .FirstOrDefault();

                if (matching is null)
                {
                    matching = [];
                    sections.Add(matching);
                }
                sectionsUsedInCurrentRow.Add(matching);
                matching.AddRange(rowFragment);
            }
        }

        return sections
            .OrderBy(section => section.Min(cell => cell.Row))
            .ThenBy(section => section.Min(cell => cell.Column))
            .Select(section => (IReadOnlyList<SpreadsheetCell>)section
                .OrderBy(cell => cell.Row)
                .ThenBy(cell => cell.Column)
                .ToArray())
            .ToArray();
    }

    private static string SheetRange(IReadOnlyList<SpreadsheetCell> cells) =>
        ColumnName(cells.Min(cell => cell.Column)) + cells.Min(cell => cell.Row).ToString(CultureInfo.InvariantCulture) + ":" +
        ColumnName(cells.Max(cell => cell.Column)) + cells.Max(cell => cell.Row).ToString(CultureInfo.InvariantCulture);

    private static int ColumnNumber(string name)
    {
        var result = 0;
        foreach (var character in name.ToUpperInvariant()) result = checked(result * 26 + character - 'A' + 1);
        return result;
    }

    private static string ColumnName(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private sealed record SpreadsheetCell(DocumentNode Node, int Column, int Row);
    private sealed record PositionedImageRow(long Row, IReadOnlyList<DocumentNode> Images);

    private static string ProjectSpreadsheetCell(DocumentNode node)
    {
        if (node.Extensions is not null && node.Extensions.TryGetValue("formula", out var formula) &&
            formula.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var expression = "=" + formula.GetString();
            var calculatedValue = node.Content is TextNodeContent text ? text.Text : string.Empty;
            var calculatedDisplayValue = ExtensionString(node, "display_value");
            var renderedValue = !string.IsNullOrWhiteSpace(calculatedDisplayValue) ? calculatedDisplayValue : calculatedValue;
            return renderedValue.Length == 0
                ? $"`{expression}`"
                : $"`{expression}` → {renderedValue}";
        }

        var value = NodeText(node) ?? string.Empty;
        var displayValue = ExtensionString(node, "display_value");
        if (!string.IsNullOrWhiteSpace(displayValue) &&
            !string.Equals(displayValue, value, StringComparison.Ordinal))
            return $"`{value}` → {displayValue}";
        return value.StartsWith('=') && value.Length > 1 ? $"`{value}`" : value;
    }

    private static int HeadingLevel(string? styleId)
    {
        var match = styleId is null ? Match.Empty : Regex.Match(styleId, "(?<level>[1-6])$");
        return match.Success && int.TryParse(match.Groups["level"].Value, out var level) ? level : 2;
    }
    private static void AppendTable(StringBuilder output, IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        if (rows.Count == 0) return;
        var width = Math.Max(1, rows.Max(row => row.Count));
        WriteRow(rows[0]);
        output.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).AppendLine();
        foreach (var row in rows.Skip(1)) WriteRow(row);
        void WriteRow(IReadOnlyList<TableCell> row)
        {
            output.Append('|');
            for (var index = 0; index < width; index++) output.Append(' ').Append(EscapeTableCell(index < row.Count ? row[index].Text : string.Empty)).Append(" |");
            output.AppendLine();
        }
    }
    private static string ImageReference(string reference, string store) => MarkdownPathEncoder.Encode(
        reference.StartsWith("asset:", StringComparison.Ordinal) ? store + "/assets/" + reference[6..] : reference);
    private static bool TryImagePixelSize(DocumentNode node, out int width, out int height)
    {
        const decimal emuPerPixel = 9525m;
        const int maximumDimension = 32768;
        var widthEmu = ExtensionLong(node, "width_emu");
        var heightEmu = ExtensionLong(node, "height_emu");
        width = 0;
        height = 0;
        if (widthEmu is null or <= 0 || heightEmu is null or <= 0) return false;
        var pixelWidth = decimal.Round(widthEmu.Value / emuPerPixel, 0, MidpointRounding.AwayFromZero);
        var pixelHeight = decimal.Round(heightEmu.Value / emuPerPixel, 0, MidpointRounding.AwayFromZero);
        if (pixelWidth is < 1 or > maximumDimension || pixelHeight is < 1 or > maximumDimension) return false;
        width = decimal.ToInt32(pixelWidth);
        height = decimal.ToInt32(pixelHeight);
        return true;
    }
    private static string? ImageMediaType(DocumentNode node, string reference)
    {
        if (node.Extensions is not null && node.Extensions.TryGetValue("image_media_type", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            return value.GetString();
        return Path.GetExtension(reference).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
            ".bmp" => "image/bmp", ".svg" => "image/svg+xml", ".tif" or ".tiff" => "image/tiff", ".emf" => "image/emf", ".wmf" => "image/wmf",
            _ => "application/octet-stream",
        };
    }
    private static string EscapeTableCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
    private static string EscapeLinkText(string value) => value.Replace("]", "\\]", StringComparison.Ordinal);
    private static string EscapeHtmlAttribute(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
    private static ProjectionRole RoleFor(DocumentNode node) => node.Kind switch { NodeKind.Heading => ProjectionRole.HeadingLabel, NodeKind.Image => ProjectionRole.ImageReference, NodeKind.Table or NodeKind.Cell => ProjectionRole.TableCell, _ when EvidenceFor(node) == ProjectionEvidence.Ocr => ProjectionRole.OcrText, _ => ProjectionRole.PrimaryText };
    private static ProjectionEvidence EvidenceFor(DocumentNode node) => Enum.TryParse<ProjectionEvidence>(node.Provenance?.FirstOrDefault()?.Evidence.ToString(), true, out var evidence) ? evidence : ProjectionEvidence.Native;

    private static string? NodeText(DocumentNode node)
    {
        if (node.Kind == NodeKind.Cell && node.Extensions is not null &&
            node.Extensions.TryGetValue("formula", out var formula) && formula.ValueKind == System.Text.Json.JsonValueKind.String)
            return "=" + formula.GetString();
        return node.Content switch
        {
            TextNodeContent text => text.Text,
            RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
            ReferenceNodeContent reference => reference.AltText is null ? reference.Reference : reference.AltText,
            TableNodeContent table => string.Join("\n", table.Rows.Select(row => string.Join(" | ", row.Select(cell => cell.Text)))),
            _ => null
        };
    }

    private sealed record CoreGraphContract(string DocumentId, string Format, IReadOnlyList<CorePartitionContract> Partitions);
    private sealed record CorePartitionContract(string Id, int Order, IReadOnlyList<CoreNodeContract> Nodes);
    private sealed record CoreNodeContract(string Id, string Kind, string? Text, int Order, string Evidence, string Layer);

    private sealed class ReflectionGraphView
    {
        public string? DocumentId { get; init; }
        public string? Format { get; init; }
        public List<GraphPartitionView> Partitions { get; } = [];
        public List<GraphNodeView> Nodes { get; } = [];

        public static ReflectionGraphView Read(object graph)
        {
            var result = new ReflectionGraphView
            {
                DocumentId = StringProperty(graph, "DocumentId") ?? StringProperty(graph, "Id"),
                Format = StringProperty(graph, "Format")
            };
            var partitions = CollectionProperty(graph, "Partitions");
            if (partitions is not null)
            {
                foreach (var partition in partitions)
                {
                    if (partition is null) continue;
                    var id = StringProperty(partition, "Id") ?? StringProperty(partition, "PartitionId") ?? "part-0001";
                    var nodes = ReadNodes(partition).ToList();
                    result.Partitions.Add(new GraphPartitionView(id, IntProperty(partition, "Order") ?? result.Partitions.Count, nodes));
                }
            }
            result.Nodes.AddRange(ReadNodes(graph));
            return result;
        }

        private static IEnumerable<GraphNodeView> ReadNodes(object value)
        {
            foreach (var name in NodeCollectionNames)
            {
                var collection = CollectionProperty(value, name);
                if (collection is null) continue;
                var order = 0;
                foreach (var node in collection)
                {
                    if (node is null) continue;
                    var id = StringProperty(node, "Id") ?? $"new_{order + 1:00}";
                    var kind = StringProperty(node, "Kind") ?? "paragraph";
                    var content = Property(node, "Content");
                    var text = StringProperty(node, "Text") ?? (content is null ? null : StringProperty(content, "Text") ?? StringProperty(content, "Value"));
                    var evidence = ParseEvidence(node, content);
                    var layer = StringProperty(node, "Layer") ?? nameof(ContentLayer.Body);
                    yield return new GraphNodeView(id, kind, text, IntProperty(node, "Order") ?? order++, evidence, layer);
                }
                yield break;
            }
        }

        private static ProjectionEvidence ParseEvidence(object node, object? content)
        {
            var value = StringProperty(node, "Evidence") ?? StringProperty(node, "Provenance") ?? (content is null ? null : StringProperty(content, "Evidence"));
            return Enum.TryParse<ProjectionEvidence>(value, true, out var evidence) ? evidence : ProjectionEvidence.Native;
        }

        private static object? Property(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        private static string? StringProperty(object value, string name) => Property(value, name)?.ToString();
        private static int? IntProperty(object value, string name) => Property(value, name) is IConvertible x ? x.ToInt32(CultureInfo.InvariantCulture) : null;
        private static IEnumerable? CollectionProperty(object value, string name) => Property(value, name) as IEnumerable;
    }
}

public sealed class DocRedockMarkdownParser
{
    private static readonly Regex FrontMatter = new("^---\\r?\\n(?<body>.*?)\\r?\\n---\\r?\\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Marker = new("<!--drmd:(?<kind>block|delete|new|sheet-table|partition-begin|partition-end|document-end)(?:\\s+(?<attrs>.*?))?-->", RegexOptions.Compiled);

    public TypedMarkdownDocument Parse(string markdown, MarkdownParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new MarkdownParseOptions();
        var diagnostics = new List<MarkdownDiagnostic>();
        var front = FrontMatter.Match(markdown);
        // Front matter is line-oriented.  Do not translate it to marker-style
        // key=value text because unquoted paths may legitimately contain spaces.
        var values = ParseFrontMatter(front.Success ? front.Groups["body"].Value : "");
        if (options.RequireFrontMatter && !front.Success)
            diagnostics.Add(new("DRMD001", "Front matter is missing.", MarkdownDiagnosticSeverity.Error));

        var documentId = Get(values, "document_id");
        var schema = Get(values, "drmd_schema");
        var rulesVersion = Get(values, "drmd_rules");
        var sourceFormat = Get(values, "source_format");
        var store = Get(values, "roundtrip_store");
        if (string.IsNullOrWhiteSpace(documentId))
            diagnostics.Add(new("DRMD011", "Front matter document_id is missing.", MarkdownDiagnosticSeverity.Error));
        if (!StringComparer.Ordinal.Equals(schema, "1.0"))
            diagnostics.Add(new("DRMD012", $"Unsupported DRMD Markdown schema '{schema ?? "(missing)"}'.", MarkdownDiagnosticSeverity.Error));
        if (rulesVersion is not null && !StringComparer.Ordinal.Equals(rulesVersion, "1.0"))
            diagnostics.Add(new("DRMD022", $"Unsupported DRMD AI editing rules version '{rulesVersion}'.", MarkdownDiagnosticSeverity.Error));
        if (string.IsNullOrWhiteSpace(sourceFormat))
            diagnostics.Add(new("DRMD013", "Front matter source_format is missing.", MarkdownDiagnosticSeverity.Error));
        var markers = ReadMarkers(markdown);
        var declaredIds = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new List<TypedMarkdownBlock>();
        string? openPartition = null;
        var partitionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentEnd = default(MarkerToken?);
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var attrs = ParseAttributes(marker.Attributes);
            switch (marker.Kind)
            {
                case "partition-begin":
                    var beginId = Get(attrs, "id");
                    if (openPartition is not null)
                        diagnostics.Add(new("DRMD017", "Partition begin marker is nested inside another partition.", MarkdownDiagnosticSeverity.Error, beginId));
                    if (string.IsNullOrWhiteSpace(beginId))
                        diagnostics.Add(new("DRMD018", "Partition begin marker has no id.", MarkdownDiagnosticSeverity.Error));
                    else if (partitionCounts.ContainsKey(beginId))
                        diagnostics.Add(new("DRMD014", $"Partition '{beginId}' is declared more than once.", MarkdownDiagnosticSeverity.Error, beginId));
                    else { openPartition = beginId; partitionCounts[beginId] = 0; }
                    break;
                case "partition-end":
                    var endId = Get(attrs, "id");
                    if (openPartition is null)
                        diagnostics.Add(new("DRMD010", $"Partition '{endId ?? "(missing)"}' has no matching begin marker.", MarkdownDiagnosticSeverity.Error, endId));
                    else if (!StringComparer.Ordinal.Equals(openPartition, endId))
                    {
                        diagnostics.Add(new("DRMD019", $"Partition end '{endId ?? "(missing)"}' does not match open partition '{openPartition}'.", MarkdownDiagnosticSeverity.Error, endId));
                        openPartition = null;
                    }
                    else
                    {
                        ValidateBaselineCount(attrs, partitionCounts[openPartition], openPartition, diagnostics);
                        openPartition = null;
                    }
                    break;
                case "document-end":
                    if (openPartition is not null)
                        diagnostics.Add(new("DRMD008", $"Partition '{openPartition}' has no matching end marker.", MarkdownDiagnosticSeverity.Error, openPartition));
                    documentEnd = marker;
                    break;
                case "block":
                case "new":
                case "delete":
                case "sheet-table":
                    if (openPartition is null)
                    {
                        diagnostics.Add(new("DRMD020", $"DRMD {marker.Kind} marker must appear inside a partition.", MarkdownDiagnosticSeverity.Error));
                        break;
                    }
                    if (marker.Kind == "sheet-table")
                    {
                        var range = Get(attrs, "range");
                        if (string.IsNullOrWhiteSpace(range))
                            diagnostics.Add(new("DRMD023", "Sheet table marker has no range.", MarkdownDiagnosticSeverity.Error));
                        if (!int.TryParse(Get(attrs, "baseline_nodes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered) || covered < 1)
                        {
                            diagnostics.Add(new("DRMD024", "Sheet table marker has an invalid baseline_nodes count.", MarkdownDiagnosticSeverity.Error));
                            covered = 0;
                        }
                        partitionCounts[openPartition] += covered;
                        var gridStart = marker.Index + marker.Length;
                        var gridEnd = i + 1 < markers.Count ? markers[i + 1].Index : markdown.Length;
                        var gridText = markdown[gridStart..Math.Max(gridStart, gridEnd)].Trim('\r', '\n');
                        blocks.Add(new TypedMarkdownBlock(null, "sheet-table", gridText,
                            new TextRange(gridStart, Math.Max(0, gridEnd - gridStart)), attrs, false, false, openPartition));
                        break;
                    }
                    if (marker.Kind == "delete")
                    {
                        var id = Get(attrs, "id");
                        if (string.IsNullOrWhiteSpace(id)) diagnostics.Add(new("DRMD002", "Delete marker has no id.", MarkdownDiagnosticSeverity.Error));
                        else if (!declaredIds.Add(id)) diagnostics.Add(new("DRMD003", $"Duplicate block id '{id}'.", MarkdownDiagnosticSeverity.Error, id));
                        // A delete marker replaces a baseline block marker in the inventory;
                        // new markers do not belong to the baseline inventory.
                        partitionCounts[openPartition]++;
                        blocks.Add(new TypedMarkdownBlock(id, "delete", string.Empty, new TextRange(marker.Index, marker.Length), attrs, true, false, openPartition));
                        break;
                    }
                    var blockId = marker.Kind == "block" ? Get(attrs, "id") : null;
                    var kind = Get(attrs, "kind") ?? "paragraph";
                    if (marker.Kind == "block")
                    {
                        if (string.IsNullOrWhiteSpace(blockId)) diagnostics.Add(new("DRMD002", "Block marker has no id.", MarkdownDiagnosticSeverity.Error));
                        else if (!declaredIds.Add(blockId)) diagnostics.Add(new("DRMD003", $"Duplicate block id '{blockId}'.", MarkdownDiagnosticSeverity.Error, blockId));
                        partitionCounts[openPartition]++;
                    }
                    var textStart = marker.Index + marker.Length;
                    var textEnd = i + 1 < markers.Count ? markers[i + 1].Index : markdown.Length;
                    var text = markdown[textStart..Math.Max(textStart, textEnd)].Trim('\r', '\n');
                    blocks.Add(new TypedMarkdownBlock(blockId, kind, text, new TextRange(textStart, Math.Max(0, textEnd - textStart)), attrs, false, marker.Kind == "new", openPartition));
                    break;
            }
        }
        if (openPartition is not null)
            diagnostics.Add(new("DRMD008", $"Partition '{openPartition}' has no matching end marker.", MarkdownDiagnosticSeverity.Error, openPartition));

        var complete = documentEnd is not null;
        if (options.RequireDocumentEnd && !complete)
            diagnostics.Add(new("DRMD004", "Document-end marker is missing; input may be truncated.", MarkdownDiagnosticSeverity.Error));
        if (complete)
        {
            if (markers.Count(marker => marker.Kind == "document-end") != 1)
                diagnostics.Add(new("DRMD015", "Document-end marker must appear exactly once.", MarkdownDiagnosticSeverity.Error));
            var endMarker = documentEnd!;
            if (!string.IsNullOrWhiteSpace(markdown[(endMarker.Index + endMarker.Length)..]))
                diagnostics.Add(new("DRMD016", "Content appears after the document-end marker.", MarkdownDiagnosticSeverity.Error));
            var attrs = ParseAttributes(endMarker.Attributes);
            var markerDocumentId = Get(attrs, "id");
            if (documentId is not null && markerDocumentId is not null && !StringComparer.Ordinal.Equals(documentId, markerDocumentId))
                diagnostics.Add(new("DRMD005", "Document ID does not match document-end marker.", MarkdownDiagnosticSeverity.Error));
            if (int.TryParse(Get(attrs, "partitions"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedPartitions) &&
                partitionCounts.Count != expectedPartitions)
                diagnostics.Add(new("DRMD006", "Partition count does not match document-end marker.", MarkdownDiagnosticSeverity.Error));
        }
        var hasError = diagnostics.Any(d => d.Severity == MarkdownDiagnosticSeverity.Error);
        if (options.Strict && hasError)
            return new TypedMarkdownDocument(documentId, schema, blocks, declaredIds, diagnostics, false, sourceFormat, store, rulesVersion);
        return new TypedMarkdownDocument(documentId, schema, blocks, declaredIds, diagnostics, complete && !hasError, sourceFormat, store, rulesVersion);
    }

    /// <summary>Checks baseline inventory and reports missing markers without treating them as deletions.</summary>
    public static IReadOnlyList<MarkdownDiagnostic> FindMissingNodes(TypedMarkdownDocument document, IEnumerable<string> baselineNodeIds)
    {
        var present = document.Blocks.Where(x => x.NodeId is not null && !x.IsNew && !x.IsExplicitDelete)
            .Select(x => x.NodeId!).ToHashSet(StringComparer.Ordinal);
        var deleted = document.Blocks.Where(x => x.IsExplicitDelete).Select(x => x.NodeId!).ToHashSet(StringComparer.Ordinal);
        return baselineNodeIds.Where(id => !present.Contains(id) && !deleted.Contains(id))
            .Select(id => new MarkdownDiagnostic("DRMD007", $"Baseline node '{id}' is missing; it will be preserved, not deleted.", MarkdownDiagnosticSeverity.Warning, id))
            .ToArray();
    }

    public static IReadOnlyDictionary<string, TypedMarkdownBlock> MapEditsToNodes(TypedMarkdownDocument document) =>
        document.Blocks.Where(x => x.NodeId is not null && !x.IsExplicitDelete)
            .GroupBy(x => x.NodeId!, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

    private static void ValidateBaselineCount(IReadOnlyDictionary<string, string> attrs, int actual, string partitionId, List<MarkdownDiagnostic> diagnostics)
    {
        if (int.TryParse(Get(attrs, "baseline_nodes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) && expected != actual)
            diagnostics.Add(new("DRMD021", $"Partition '{partitionId}' baseline_nodes is {expected}, but contains {actual} existing block markers.", MarkdownDiagnosticSeverity.Error, partitionId));
    }

    private static List<MarkerToken> ReadMarkers(string markdown)
    {
        var markers = new List<MarkerToken>();
        var inFence = false;
        foreach (Match line in Regex.Matches(markdown, ".*?(?:\\r?\\n|$)"))
        {
            if (line.Length == 0) continue;
            var lineText = line.Value;
            if (Regex.IsMatch(lineText, "^[ \\t]{0,3}(`{3,}|~{3,})")) { inFence = !inFence; continue; }
            if (inFence) continue;
            foreach (Match match in Marker.Matches(lineText))
                markers.Add(new MarkerToken(match.Groups["kind"].Value, match.Groups["attrs"].Value, line.Index + match.Index, match.Length));
        }
        return markers;
    }

    private sealed record MarkerToken(string Kind, string Attributes, int Index, int Length);

    private static Dictionary<string, string> ParseAttributes(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(input, "(?<key>[A-Za-z0-9_-]+)=(?:\\\"(?<quoted>.*?)\\\"|(?<value>[^\\s]+))"))
            result[match.Groups["key"].Value] = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["value"].Value;
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) result[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    private static Dictionary<string, string> ParseFrontMatter(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) result[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string> map, string key) => map.TryGetValue(key, out var value) ? value : null;
}
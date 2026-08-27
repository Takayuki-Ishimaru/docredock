using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Markdown;

namespace DocRedock.Api;

public sealed record GraphEditResult(
    TypedMarkdownDocument Markdown,
    DocumentGraph EditedGraph,
    DiffResult Diff,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>Maps a DRMD projection back to the canonical graph without using text heuristics for identity.</summary>
public sealed class MarkdownGraphEditor
{
    public GraphEditResult Apply(DocumentGraph baseline, string markdown)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(markdown);
        var parsed = new DocRedockMarkdownParser().Parse(markdown, new MarkdownParseOptions { Strict = true });
        var diagnostics = parsed.Diagnostics.Select(ToCoreDiagnostic).ToList();
        if (parsed.DocumentId is not null && !StringComparer.Ordinal.Equals(parsed.DocumentId, baseline.DocumentId))
            diagnostics.Add(new Diagnostic("DocumentBindingMismatch", "Markdown document_id does not match the baseline graph.", DiagnosticSeverity.Error));
        if (parsed.SourceFormat is not null && !StringComparer.OrdinalIgnoreCase.Equals(parsed.SourceFormat, baseline.Format.ToString()))
            diagnostics.Add(new Diagnostic("FormatBindingMismatch", "Markdown source_format does not match the baseline graph.", DiagnosticSeverity.Error));
        if (!parsed.IsComplete)
        {
            return new GraphEditResult(
                parsed,
                baseline,
                new DocumentGraphDiffEngine().Compare(baseline, baseline),
                diagnostics);
        }

        var baselinePartitions = baseline.Partitions.ToDictionary(partition => partition.Id, StringComparer.Ordinal);
        foreach (var block in parsed.Blocks.Where(block => !block.IsNew && block.NodeId is not null))
        {
            var node = baseline.FindNode(block.NodeId!);
            if (node is null)
            {
                diagnostics.Add(new Diagnostic("UnknownBlockId", $"Markdown references unknown baseline node '{block.NodeId}'.", DiagnosticSeverity.Error, block.NodeId));
                continue;
            }
            if (!block.IsExplicitDelete)
            {
                var expectedKind = MarkerKind(node.Kind);
                if (!StringComparer.Ordinal.Equals(expectedKind, NormalizeMarkerKind(block.Kind)))
                    diagnostics.Add(new Diagnostic("BlockKindMismatch", $"Block '{node.Id}' has kind '{block.Kind}', but baseline kind is '{expectedKind}'.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
                ValidatePolicyAttributes(baseline.Format, node, block, diagnostics);
                ValidateSemanticAttributes(node, block, diagnostics);
            }
            var baselinePartition = baseline.Partitions.FirstOrDefault(partition => partition.Nodes.Any(candidate => candidate.Id == node.Id));
            if (baselinePartition is not null && !StringComparer.Ordinal.Equals(baselinePartition.Id, block.PartitionId))
                diagnostics.Add(new Diagnostic("PartitionBindingMismatch", $"Block '{node.Id}' was moved from partition '{baselinePartition.Id}' to '{block.PartitionId}'.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
        }
        foreach (var addition in parsed.Blocks.Where(block => block.IsNew))
        {
            if (addition.PartitionId is null || !baselinePartitions.ContainsKey(addition.PartitionId))
                diagnostics.Add(new Diagnostic("UnknownAdditionPartition", $"New block targets unknown partition '{addition.PartitionId ?? "(missing)"}'.", DiagnosticSeverity.Error));
            if (!SupportsMarkdownAddition(baseline.Format, addition.Kind))
                diagnostics.Add(new Diagnostic("UnsupportedMarkdownAddition", $"DRMD Markdown cannot safely add kind '{addition.Kind}' to {baseline.Format.ToString().ToLowerInvariant()} with the built-in restore path.", DiagnosticSeverity.Error));
        }
        var contentPolicy = DocumentContentPolicyRules.Parse(parsed.ContentPolicy);
        var sheetGrids = ReadSheetGrids(baseline, parsed, contentPolicy, diagnostics);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return new GraphEditResult(parsed, baseline, new DocumentGraphDiffEngine().Compare(baseline, baseline), diagnostics);

        var edits = DocRedockMarkdownParser.MapEditsToNodes(parsed);
        var deletes = parsed.Blocks.Where(block => block.IsExplicitDelete && block.NodeId is not null)
            .Select(block => block.NodeId!).ToHashSet(StringComparer.Ordinal);
        var sheetCoveredIds = sheetGrids.Values.SelectMany(grid => grid.CellNodeIds).ToHashSet(StringComparer.Ordinal);
        diagnostics.AddRange(DocRedockMarkdownParser.FindMissingNodes(parsed, baseline.Nodes
            .Where(node => DocumentContentPolicyRules.Includes(node, contentPolicy) && !sheetCoveredIds.Contains(node.Id))
            .Select(node => node.Id)).Select(ToCoreDiagnostic));
        var partitions = new List<DocumentPartition>(baseline.Partitions.Count);
        foreach (var partition in baseline.Partitions.OrderBy(partition => partition.Order))
        {
            var nodes = new List<DocumentNode>();
            foreach (var node in partition.Nodes.OrderBy(node => node.Order))
            {
                if (node.Kind == NodeKind.Cell && sheetGrids.TryGetValue(partition.Id, out var sheetGrid) &&
                    CellAddress(node) is { } address && sheetGrid.Values.TryGetValue(address, out var sheetValue))
                {
                    if (node.Editability is NodeEditability.Protected or NodeEditability.Passthrough)
                    {
                        if (!StringComparer.Ordinal.Equals(ProjectNodeText(node), sheetValue))
                            diagnostics.Add(new Diagnostic("ProtectedNodeEdit", "The requested spreadsheet edit targets a protected cell.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
                        nodes.Add(node);
                    }
                    else nodes.Add(ApplyCell(node, sheetValue));
                    continue;
                }
                if (deletes.Contains(node.Id))
                {
                    if (node.Editability is NodeEditability.Protected or NodeEditability.Passthrough)
                    {
                        diagnostics.Add(new Diagnostic("ProtectedNodeDelete", "A protected or passthrough node cannot be deleted.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
                        nodes.Add(node);
                    }
                    continue;
                }
                if (!edits.TryGetValue(node.Id, out var block))
                {
                    nodes.Add(node);
                    continue;
                }

                if (node.Editability is NodeEditability.Protected or NodeEditability.Passthrough)
                {
                    if (!ProtectedContentMatches(baseline, parsed, node, block))
                        diagnostics.Add(new Diagnostic("ProtectedNodeEdit", "The requested edit targets a protected or passthrough node.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
                    nodes.Add(node);
                    continue;
                }
                nodes.Add(ApplyBlock(node, block, diagnostics));
            }
            partitions.Add(partition with { Nodes = nodes });
        }

        var additions = parsed.Blocks.Where(block => block.IsNew).GroupBy(block => block.PartitionId!, StringComparer.Ordinal).ToArray();
        if (additions.Length > 0)
        {
            if (partitions.Count == 0)
                partitions.Add(new DocumentPartition("part-0001", 0, []));
            foreach (var group in additions)
            {
                var targetIndex = partitions.FindIndex(partition => StringComparer.Ordinal.Equals(partition.Id, group.Key));
                if (targetIndex < 0) continue; // Guarded above; preserves compatibility for an empty baseline.
                var target = partitions[targetIndex];
                var nodes = target.Nodes.ToList();
                foreach (var block in group)
                {
                    nodes.Add(new DocumentNode(
                        NodeIdGenerator.CreateNew(),
                        ParseKind(block.Kind),
                        null,
                        nodes.Count == 0 ? 0 : nodes.Max(node => node.Order) + 1,
                        ContentLayer.Body,
                        new TextNodeContent(DecodeBlockText(block)),
                        Editability: NodeEditability.EditableWithConstraints,
                        Provenance: [new ProvenanceItem(EvidenceKind.Generated)]));
                }
                partitions[targetIndex] = target with { Nodes = nodes };
            }
        }

        var edited = baseline with { Partitions = partitions };
        var diff = new DocumentGraphDiffEngine().Compare(baseline, edited, new DiffOptions(deletes));
        diagnostics.AddRange(diff.Diagnostics.Select(diagnostic => new Diagnostic(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity,
            diagnostic.NodeId)));
        return new GraphEditResult(parsed, edited, diff, diagnostics);
    }

    private static DocumentNode ApplyBlock(DocumentNode node, TypedMarkdownBlock block, ICollection<Diagnostic> diagnostics)
    {
        if (node.Content is TableNodeContent)
            return ApplyTable(node, DecodeBlockText(block), diagnostics);
        if (node.Kind == NodeKind.CodeBlock &&
            StringComparer.Ordinal.Equals(ProjectNodeText(node), DecodeBlockText(block)))
            return node;
        if (node.Content is RichTextNodeContent rich)
        {
            var inline = DecodeInlineBlockMarkdown(block);
            if (StringComparer.Ordinal.Equals(DocRedockInlineMarkdown.Serialize(rich.Runs), inline)) return node;
            return node with { Content = DocRedockInlineMarkdown.Parse(inline, rich.Runs) };
        }
        return ApplyText(node, DecodeBlockText(block));
    }

    private static DocumentNode ApplyText(DocumentNode node, string text) => node.Content switch
    {
        _ when node.Kind == NodeKind.Cell => ApplyCell(node, text),
        TextNodeContent current when StringComparer.Ordinal.Equals(current.Text, text) => node,
        TextNodeContent => node with { Content = new TextNodeContent(text) },
        _ => node,
    };

    private static DocumentNode ApplyTable(DocumentNode baselineNode, string markdownText, ICollection<Diagnostic> diagnostics)
    {
        var baseline = (TableNodeContent)baselineNode.Content;
        if (!TableGrid.TryCreate(baseline, out var grid, out var reason))
        {
            diagnostics.Add(new Diagnostic("InvalidTableGrid", reason ?? "The baseline table has invalid spans.",
                DiagnosticSeverity.Error, baselineNode.Id, baselineNode.Source?.PartUri));
            return baselineNode;
        }

        var validatedGrid = grid!;
        var projected = ParseTable(markdownText);
        if (projected.Rows.Count != validatedGrid.RowCount || projected.Rows.Any(row => row.Count != validatedGrid.ColumnCount))
        {
            diagnostics.Add(new Diagnostic("MergedTableShapeChanged",
                "The Markdown table dimensions no longer match the baseline table. Structural table edits are not supported.",
                DiagnosticSeverity.Error, baselineNode.Id, baselineNode.Source?.PartUri));
            return baselineNode;
        }

        var updatedRows = baseline.Rows.Select(row => row.ToArray()).ToArray();
        foreach (var row in validatedGrid.Rows)
        foreach (var slot in row)
        {
            var value = projected.Rows[slot.Row][slot.Column].Text;
            if (slot.IsContinuation)
            {
                if (value.Length != 0 && !StringComparer.Ordinal.Equals(value, slot.Origin.Text))
                {
                    diagnostics.Add(new Diagnostic("MergedTableContinuationEdited",
                        "A merged-cell continuation cannot be edited. Edit the merge origin cell instead.",
                        DiagnosticSeverity.Error, baselineNode.Id, baselineNode.Source?.PartUri));
                    return baselineNode;
                }
                continue;
            }
            updatedRows[slot.OriginRow][slot.OriginCellIndex] =
                updatedRows[slot.OriginRow][slot.OriginCellIndex] with { Text = value };
        }
        return baselineNode with { Content = new TableNodeContent(updatedRows) };
    }

    private static string ProjectNodeText(DocumentNode node) => node.Content switch
    {
        _ when node.Kind == NodeKind.Cell && node.Extensions is not null &&
            node.Extensions.TryGetValue("formula", out var formula) && formula.ValueKind == System.Text.Json.JsonValueKind.String => "=" + formula.GetString(),
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        TableNodeContent table => ProjectTable(table),
        ReferenceNodeContent reference => reference.AltText ?? reference.Reference,
        _ => string.Empty,
    };

    private static bool ProtectedContentMatches(
        DocumentGraph baseline,
        TypedMarkdownDocument markdown,
        DocumentNode node,
        TypedMarkdownBlock block)
    {
        if (node.Content is RichTextNodeContent rich)
            return StringComparer.Ordinal.Equals(DocRedockInlineMarkdown.Serialize(rich.Runs), DecodeInlineBlockMarkdown(block));
        if (node.Kind == NodeKind.Link && string.IsNullOrWhiteSpace(block.Text) &&
            node.Content is ReferenceNodeContent link && node.ParentId is { } parentId &&
            baseline.FindNode(parentId)?.Content is RichTextNodeContent parentRich &&
            parentRich.Runs.Any(run => StringComparer.Ordinal.Equals(run.LinkTarget, link.Reference)))
            return true;
        if (node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent image &&
            TryReadHtmlImage(block.Text, out var attributes))
        {
            if (!attributes.TryGetValue("src", out var source) ||
                !StringComparer.Ordinal.Equals(ExpectedImageReference(baseline, markdown, image.Reference),
                    System.Net.WebUtility.HtmlDecode(source))) return false;
            var expectedAlt = image.AltText ?? "image";
            if (!attributes.TryGetValue("alt", out var alt) ||
                !StringComparer.Ordinal.Equals(expectedAlt, System.Net.WebUtility.HtmlDecode(alt))) return false;
            if (TryImagePixelSize(node, out var expectedWidth, out var expectedHeight) &&
                (!attributes.TryGetValue("width", out var width) ||
                 !attributes.TryGetValue("height", out var height) ||
                 !int.TryParse(width, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var actualWidth) ||
                 !int.TryParse(height, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var actualHeight) ||
                 actualWidth != expectedWidth || actualHeight != expectedHeight)) return false;
            return attributes.TryGetValue("style", out var style) &&
                StringComparer.Ordinal.Equals(style, "max-width:49%;height:auto");
        }
        var decoded = DecodeBlockText(block);
        if (node.Content is TableNodeContent table)
        {
            var projected = ParseTable(decoded);
            return table.Rows.Count == projected.Rows.Count && table.Rows.Zip(projected.Rows).All(pair =>
                pair.First.Count == pair.Second.Count && pair.First.Zip(pair.Second).All(cell =>
                    StringComparer.Ordinal.Equals(cell.First.Text, cell.Second.Text)));
        }
        return StringComparer.Ordinal.Equals(ProjectNodeText(node).TrimEnd(), decoded.TrimEnd());
    }

    private static DocumentNode ApplyCell(DocumentNode node, string text)
    {
        var existingFormula = node.Extensions is not null &&
            node.Extensions.TryGetValue("formula", out var formulaElement) &&
            formulaElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? formulaElement.GetString()
                : null;
        if (text.StartsWith('=') && StringComparer.Ordinal.Equals(existingFormula, text[1..])) return node;
        if (!text.StartsWith('=') && existingFormula is null && node.Content is TextNodeContent current &&
            StringComparer.Ordinal.Equals(current.Text, text)) return node;

        var extensions = node.Extensions is null
            ? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, System.Text.Json.JsonElement>(node.Extensions, StringComparer.Ordinal);
        if (text.Length > 0 && text[0] == '=')
            extensions["formula"] = System.Text.Json.JsonSerializer.SerializeToElement(text[1..]);
        else
            extensions.Remove("formula");
        var content = text.Length > 0 && text[0] == '='
            ? node.Content
            : new TextNodeContent(text);
        return node with { Content = content, Extensions = extensions };
    }

    private static string ProjectTable(TableNodeContent table) =>
        string.Join("\n", table.Rows.Select(row => string.Join(" | ", row.Select(cell => cell.Text))));

    private static TableNodeContent ParseTable(string text) => new(
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Where(line => line.Length > 0)
            .Select(ParseTableRow)
            .Where(row => !row.All(cell => cell.All(character => character is '-' or ':' or ' ')))
            .Select(row => (IReadOnlyList<TableCell>)row.Select(cell => (TableCell)cell).ToArray())
            .ToArray());

    private static IReadOnlyList<string> ParseTableRow(string line)
    {
        var normalized = line.Trim();
        if (normalized.StartsWith('|')) normalized = normalized[1..];
        if (normalized.EndsWith('|')) normalized = normalized[..^1];
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in normalized)
        {
            if (escaped) { current.Append(character); escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (character == '|') { cells.Add(DecodeTableCell(current)); current.Clear(); continue; }
            current.Append(character);
        }
        if (escaped) current.Append('\\');
        cells.Add(DecodeTableCell(current));
        return cells;
    }

    private static string DecodeTableCell(System.Text.StringBuilder value) =>
        value.ToString().Trim().Replace("<br>", "\n", StringComparison.Ordinal);

    private static string DecodeBlockText(TypedMarkdownBlock block)
    {
        var text = block.Text.TrimEnd();
        return block.Kind.ToLowerInvariant() switch
        {
            "heading" or "title" => text.TrimStart().TrimStart('#').TrimStart(),
            "quote" => string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Select(line => line.StartsWith("> ", StringComparison.Ordinal) ? line[2..] : line.TrimStart('>'))),
            "code-block" when text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) =>
                text[3..^3].Trim('\r', '\n'),
            "diagram" => DecodeDiagram(text),
            "list-item" or "listitem" => DecodeListItem(text),
            "cell" => DecodeCellText(text),
            "image" or "link" => DecodeLinkLabel(text),
            "shape" => DecodeShapeText(text, block.Attributes.TryGetValue("role", out var role) ? role : null),
            _ => text,
        };
    }

    private static string DecodeDiagram(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        return firstNewline < 0 ? text : text[(firstNewline + 1)..^3].Trim('\r', '\n');
    }

    private static string DecodeInlineBlockMarkdown(TypedMarkdownBlock block)
    {
        var text = block.Text.TrimEnd('\r', '\n');
        return block.Kind.ToLowerInvariant() switch
        {
            "heading" or "title" => text.TrimStart().TrimStart('#').TrimStart(),
            "list-item" or "listitem" => System.Text.RegularExpressions.Regex.Replace(text, @"^\s*(?:[-+*]|\d+[.)])\s+", string.Empty),
            "shape" => DecodeShapeText(text, block.Attributes.TryGetValue("role", out var role) ? role : null),
            _ => text,
        };
    }

    private static string DecodeShapeText(string text, string? role) => role?.ToLowerInvariant() switch
    {
        "title" or "centered-title" or "ctrtitle" => text.TrimStart().TrimStart('#').TrimStart(),
        "subtitle" => System.Text.RegularExpressions.Regex.Replace(text, @"^\*\*Subtitle:\*\*\s*", string.Empty),
        "body" => string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => line.Length > 0).Select(DecodeListItem)),
        _ => text,
    };

    private static string DecodeCellText(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^- \*\*[^:]+:\*\*\s*(?<value>.*)$");
        var value = match.Success ? match.Groups["value"].Value : text;
        return value.Length >= 2 && value[0] == (char)96 && value[^1] == (char)96 ? value[1..^1] : value;
    }

    private static string DecodeListItem(string text)
    {
        var firstLine = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
        return System.Text.RegularExpressions.Regex.Replace(firstLine, @"^\s*(?:[-+*]|\d+[.)])\s+", string.Empty);
    }

    private static string DecodeLinkLabel(string text)
    {
        if (TryReadHtmlImage(text, out var attributes) && attributes.TryGetValue("alt", out var htmlAlt))
            return System.Net.WebUtility.HtmlDecode(htmlAlt);
        var open = text.IndexOf('[');
        var close = text.IndexOf("](", StringComparison.Ordinal);
        return open >= 0 && close > open ? text[(open + 1)..close].Replace("\\]", "]", StringComparison.Ordinal) : text;
    }

    private static bool TryReadHtmlImage(string text, out IReadOnlyDictionary<string, string> attributes)
    {
        var image = System.Text.RegularExpressions.Regex.Match(text,
            "^\\s*<img\\b(?<attributes>[^>]*)\\s*/?>\\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!image.Success)
        {
            attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attributeText = image.Groups["attributes"].Value;
        var offset = 0;
        var allowed = new HashSet<string>(["src", "alt", "width", "height", "style"], StringComparer.OrdinalIgnoreCase);
        while (offset < attributeText.Length)
        {
            var attribute = System.Text.RegularExpressions.Regex.Match(attributeText[offset..],
                "^\\s+(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s>]+))");
            if (!attribute.Success)
            {
                attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            var name = attribute.Groups["name"].Value;
            if (!allowed.Contains(name) || result.ContainsKey(name))
            {
                attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            var value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value :
                attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value;
            result[name] = value;
            offset += attribute.Length;
        }
        if (result.Count != allowed.Count || result.Values.Any(value => value.Length == 0))
        {
            attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
        attributes = result;
        return true;
    }

    private static string ExpectedImageReference(
        DocumentGraph baseline,
        TypedMarkdownDocument markdown,
        string reference)
    {
        if (baseline.Assets is not null && baseline.Assets.TryGetValue(reference, out var asset))
            return MarkdownPathEncoder.Encode((markdown.RoundTripStore ?? "document.drmd") + "/assets/" + (asset.FileName ?? reference));
        return MarkdownPathEncoder.Encode(reference.StartsWith("asset:", StringComparison.Ordinal)
            ? (markdown.RoundTripStore ?? "document.drmd") + "/assets/" + reference[6..]
            : reference);
    }

    private static bool TryImagePixelSize(DocumentNode node, out int width, out int height)
    {
        const decimal emuPerPixel = 9525m;
        const int maximumDimension = 32768;
        width = 0;
        height = 0;
        if (node.Extensions is null ||
            !node.Extensions.TryGetValue("width_emu", out var widthElement) || widthElement.ValueKind != System.Text.Json.JsonValueKind.Number || !widthElement.TryGetInt64(out var widthEmu) ||
            !node.Extensions.TryGetValue("height_emu", out var heightElement) || heightElement.ValueKind != System.Text.Json.JsonValueKind.Number || !heightElement.TryGetInt64(out var heightEmu) ||
            widthEmu <= 0 || heightEmu <= 0) return false;
        var pixelWidth = decimal.Round(widthEmu / emuPerPixel, 0, MidpointRounding.AwayFromZero);
        var pixelHeight = decimal.Round(heightEmu / emuPerPixel, 0, MidpointRounding.AwayFromZero);
        if (pixelWidth is < 1 or > maximumDimension || pixelHeight is < 1 or > maximumDimension) return false;
        width = decimal.ToInt32(pixelWidth);
        height = decimal.ToInt32(pixelHeight);
        return true;
    }

    private static NodeKind ParseKind(string kind) => kind.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "heading" => NodeKind.Heading,
        "quote" => NodeKind.Quote,
        "codeblock" => NodeKind.CodeBlock,
        "listitem" => NodeKind.ListItem,
        _ => NodeKind.Paragraph,
    };

    private static string MarkerKind(NodeKind kind) => kind.ToString().Replace("_", "-", StringComparison.Ordinal)
        .Select((character, index) => index > 0 && char.IsUpper(character) ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString())
        .Aggregate(string.Empty, (current, value) => current + value);

    private static string NormalizeMarkerKind(string kind) => kind.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();

    private static bool SupportsMarkdownAddition(DocumentFormatKind format, string kind) =>
        format == DocumentFormatKind.Docx && NormalizeMarkerKind(kind) is "paragraph" or "heading" or "list-item";

    private static void ValidatePolicyAttributes(
        DocumentFormatKind format,
        DocumentNode node,
        TypedMarkdownBlock block,
        ICollection<Diagnostic> diagnostics)
    {
        var keys = new[] { "editability", "operations", "constraints" };
        if (!keys.Any(block.Attributes.ContainsKey)) return; // DRMD Markdown 1.0 legacy projection.
        var policy = DocRedockMarkdownEditPolicy.For(format, node);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["editability"] = policy.Editability,
            ["operations"] = policy.Operations.Count == 0 ? "none" : string.Join(',', policy.Operations),
            ["constraints"] = string.Join(',', policy.Constraints),
        };
        if (keys.Any(key => !block.Attributes.TryGetValue(key, out var actual) || !StringComparer.Ordinal.Equals(actual, expected[key])))
            diagnostics.Add(new Diagnostic("BlockPolicyMismatch", $"Block '{node.Id}' editing policy attributes do not match the baseline capabilities.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
    }

    private static void ValidateSemanticAttributes(DocumentNode node, TypedMarkdownBlock block, ICollection<Diagnostic> diagnostics)
    {
        var expectedRich = node.Content is RichTextNodeContent;
        var actualRich = block.Attributes.TryGetValue("rich-text", out var richMode) && StringComparer.Ordinal.Equals(richMode, "inline-v1");
        var expectedRole = node.Kind == NodeKind.Shape ? ExtensionString(node, "shape_role") : null;
        var actualRole = block.Attributes.TryGetValue("role", out var role) ? role : null;
        if (expectedRich != actualRich || !StringComparer.Ordinal.Equals(expectedRole, actualRole))
            diagnostics.Add(new Diagnostic("BlockSemanticMismatch", $"Block '{node.Id}' rich-text or presentation role metadata does not match the baseline node.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
    }

    private static IReadOnlyDictionary<string, SheetGrid> ReadSheetGrids(
        DocumentGraph baseline,
        TypedMarkdownDocument parsed,
        DocumentContentPolicy contentPolicy,
        ICollection<Diagnostic> diagnostics)
    {
        var result = new Dictionary<string, SheetGrid>(StringComparer.Ordinal);
        if (baseline.Format != DocumentFormatKind.Xlsx) return result;
        foreach (var group in parsed.Blocks.Where(block => block.Kind == "sheet-table" && block.PartitionId is not null)
                     .GroupBy(block => block.PartitionId!, StringComparer.Ordinal))
        {
            var errorCountBefore = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var partition = baseline.Partitions.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, group.Key));
            if (partition is null)
            {
                diagnostics.Add(new Diagnostic("UnknownSpreadsheetPartition", $"Sheet table targets unknown partition '{group.Key}'.", DiagnosticSeverity.Error));
                continue;
            }
            var cellNodes = partition.Nodes.Where(node =>
                node.Kind == NodeKind.Cell && DocumentContentPolicyRules.Includes(node, contentPolicy)).ToArray();
            var addresses = new Dictionary<string, DocumentNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in cellNodes)
            {
                var address = CellAddress(node);
                if (address is null || !TryParseAddress(address, out _, out _))
                    diagnostics.Add(new Diagnostic("InvalidCellAnchor", $"Cell node '{node.Id}' has no valid A1 address.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
                else if (!addresses.TryAdd(address.ToUpperInvariant(), node))
                    diagnostics.Add(new Diagnostic("DuplicateCellAnchor", $"Cell address '{address}' is bound to more than one node.", DiagnosticSeverity.Error, node.Id, node.Source?.PartUri));
            }
            if (addresses.Count == 0) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var coveredAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in group)
            {
                if (!block.Attributes.TryGetValue("range", out var actualRange) ||
                    !TryParseRange(actualRange, out var minColumn, out var minRow, out var maxColumn, out var maxRow))
                {
                    diagnostics.Add(new Diagnostic("SpreadsheetRangeMismatch", $"Sheet table range '{actualRange ?? "(missing)"}' is invalid.", DiagnosticSeverity.Error));
                    continue;
                }

                var sectionAddresses = addresses.Keys.Where(address =>
                {
                    _ = TryParseAddress(address, out var column, out var row);
                    return column >= minColumn && column <= maxColumn && row >= minRow && row <= maxRow;
                }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expectedRange = sectionAddresses.Count == 0 ? null : SheetRange(sectionAddresses);
                if (expectedRange is null || !StringComparer.OrdinalIgnoreCase.Equals(actualRange, expectedRange))
                {
                    diagnostics.Add(new Diagnostic("SpreadsheetRangeMismatch", $"Sheet table range '{actualRange}' does not match its baseline cells{(expectedRange is null ? "." : $" ('{expectedRange}').")}", DiagnosticSeverity.Error));
                    continue;
                }
                if (sectionAddresses.Any(address => !coveredAddresses.Add(address)))
                {
                    diagnostics.Add(new Diagnostic("SpreadsheetGridOverlap", $"Sheet table range '{actualRange}' overlaps another table in partition '{group.Key}'.", DiagnosticSeverity.Error));
                    continue;
                }

                var expectedPolicy = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["editability"] = "cell-grid",
                    ["operations"] = "replace-cell",
                    ["constraints"] = "preserve-range,preserve-addresses,no-insert-delete,safe-formula",
                    ["baseline_nodes"] = sectionAddresses.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (expectedPolicy.Any(pair => !block.Attributes.TryGetValue(pair.Key, out var actual) || !StringComparer.Ordinal.Equals(actual, pair.Value)))
                    diagnostics.Add(new Diagnostic("SpreadsheetGridPolicyMismatch", "Sheet table control attributes do not match the baseline spreadsheet capabilities.", DiagnosticSeverity.Error));

                var sectionValues = ParseSheetGrid(block.Text, sectionAddresses, block.Attributes, diagnostics);
                if (sectionValues is not null)
                    foreach (var pair in sectionValues) values[pair.Key] = pair.Value;
            }
            if (!coveredAddresses.SetEquals(addresses.Keys))
                diagnostics.Add(new Diagnostic("SpreadsheetGridCoverageMismatch", $"Sheet tables in partition '{group.Key}' do not cover the complete baseline cell inventory.", DiagnosticSeverity.Error));
            if (diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) == errorCountBefore)
                result[group.Key] = new SheetGrid(values, addresses.Values.Select(node => node.Id).ToHashSet(StringComparer.Ordinal));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string>? ParseSheetGrid(
        string text,
        IReadOnlySet<string> baselineAddresses,
        IReadOnlyDictionary<string, string> attributes,
        ICollection<Diagnostic> diagnostics)
    {
        var coordinates = baselineAddresses.Select(address =>
        {
            _ = TryParseAddress(address, out var column, out var row);
            return (Address: address, Column: column, Row: row);
        }).ToArray();
        var compactColumns = coordinates.Select(item => item.Column).Distinct().Order().ToArray();
        var compactRows = coordinates.Select(item => item.Row).Distinct().Order().ToArray();
        var legacyColumns = Enumerable.Range(compactColumns[0], compactColumns[^1] - compactColumns[0] + 1).ToArray();
        var legacyRows = Enumerable.Range(compactRows[0], compactRows[^1] - compactRows[0] + 1).ToArray();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Where(line => line.Length > 0).ToArray();
        var hasSourceColumns = attributes.TryGetValue("source-columns", out var sourceColumns);
        var hasSourceRows = attributes.TryGetValue("source-rows", out var sourceRows);
        if (hasSourceColumns || hasSourceRows)
        {
            var expectedSourceColumns = string.Join(",", compactColumns.Select(ColumnName));
            var expectedSourceRows = string.Join(",", compactRows);
            if (!hasSourceColumns || !hasSourceRows ||
                !StringComparer.Ordinal.Equals(sourceColumns, expectedSourceColumns) ||
                !StringComparer.Ordinal.Equals(sourceRows, expectedSourceRows))
            {
                diagnostics.Add(new Diagnostic("SpreadsheetCoordinateMetadataMismatch",
                    "Sheet table source row or column metadata does not match the baseline cell addresses.", DiagnosticSeverity.Error));
                return null;
            }

            return ParseMetadataAddressedGrid(lines, compactColumns, compactRows, baselineAddresses, diagnostics);
        }

        if (lines.Length < 3)
        {
            diagnostics.Add(new Diagnostic("SpreadsheetGridShapeMismatch", "Sheet table is missing its header or data rows.", DiagnosticSeverity.Error));
            return null;
        }
        var header = ParseTableRow(lines[0]);
        var separator = ParseTableRow(lines[1]);
        var columns = HeaderMatches(header, compactColumns) && lines.Length == compactRows.Length + 2
            ? compactColumns
            : HeaderMatches(header, legacyColumns) && lines.Length == legacyRows.Length + 2
                ? legacyColumns
                : null;
        var rows = ReferenceEquals(columns, compactColumns) ? compactRows : legacyRows;
        var expectedColumns = (columns?.Length ?? 0) + 1;
        if (columns is null || separator.Count != expectedColumns ||
            !separator.All(cell => cell.All(character => character is '-' or ':' or ' ')))
        {
            diagnostics.Add(new Diagnostic("SpreadsheetGridShapeMismatch", $"Sheet table must keep either its compact {compactRows.Length}-row layout or the legacy coordinate grid.", DiagnosticSeverity.Error));
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var rowOffset = 0; rowOffset < rows.Length; rowOffset++)
        {
            var cells = ParseTableRow(lines[rowOffset + 2]);
            var row = rows[rowOffset];
            if (cells.Count != expectedColumns || !int.TryParse(cells[0], out var actualRow) || actualRow != row)
            {
                diagnostics.Add(new Diagnostic("SpreadsheetGridRowMismatch", "Sheet table row addresses were changed or reordered.", DiagnosticSeverity.Error));
                return null;
            }
            for (var columnOffset = 0; columnOffset < columns.Length; columnOffset++)
            {
                var address = ColumnName(columns[columnOffset]) + row;
                var value = DecodeSpreadsheetCell(cells[columnOffset + 1]);
                if (!baselineAddresses.Contains(address) && value.Length > 0)
                    diagnostics.Add(new Diagnostic("UnsupportedSpreadsheetCellAddition", $"Cell '{address}' is outside the baseline cell inventory; adding cells through the table projection is not supported.", DiagnosticSeverity.Error));
                if (baselineAddresses.Contains(address)) values[address] = value;
            }
        }
        return values;

        static bool HeaderMatches(IReadOnlyList<string> headerCells, IReadOnlyList<int> expected) =>
            headerCells.Count == expected.Count + 1 &&
            StringComparer.OrdinalIgnoreCase.Equals(headerCells[0], "Row") &&
            expected.Select((column, index) => StringComparer.OrdinalIgnoreCase.Equals(headerCells[index + 1], ColumnName(column))).All(matches => matches);
    }

    private static IReadOnlyDictionary<string, string>? ParseMetadataAddressedGrid(
        IReadOnlyList<string> lines,
        IReadOnlyList<int> columns,
        IReadOnlyList<int> rows,
        IReadOnlySet<string> baselineAddresses,
        ICollection<Diagnostic> diagnostics)
    {
        var expectedLineCount = rows.Count + 1;
        if (lines.Count != expectedLineCount)
        {
            diagnostics.Add(new Diagnostic("SpreadsheetGridShapeMismatch",
                $"Sheet table must keep its {columns.Count}-column and {rows.Count}-row layout.", DiagnosticSeverity.Error));
            return null;
        }

        var separator = ParseTableRow(lines[1]);
        if (separator.Count != columns.Count ||
            !separator.All(cell => cell.Length >= 3 && cell.All(character => character is '-' or ':' or ' ')))
        {
            diagnostics.Add(new Diagnostic("SpreadsheetGridShapeMismatch",
                "Sheet table separator or column count was changed.", DiagnosticSeverity.Error));
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var rowOffset = 0; rowOffset < rows.Count; rowOffset++)
        {
            var cells = ParseTableRow(lines[rowOffset == 0 ? 0 : rowOffset + 1]);
            if (cells.Count != columns.Count)
            {
                diagnostics.Add(new Diagnostic("SpreadsheetGridShapeMismatch",
                    "Sheet table column count was changed.", DiagnosticSeverity.Error));
                return null;
            }

            for (var columnOffset = 0; columnOffset < columns.Count; columnOffset++)
            {
                var address = ColumnName(columns[columnOffset]) + rows[rowOffset];
                var value = DecodeSpreadsheetCell(cells[columnOffset]);
                if (!baselineAddresses.Contains(address) && value.Length > 0)
                    diagnostics.Add(new Diagnostic("UnsupportedSpreadsheetCellAddition",
                        $"Cell '{address}' is outside the baseline cell inventory; adding cells through the table projection is not supported.", DiagnosticSeverity.Error));
                if (baselineAddresses.Contains(address)) values[address] = value;
            }
        }
        return values;
    }

    private static bool TryParseRange(string? range, out int minColumn, out int minRow, out int maxColumn, out int maxRow)
    {
        minColumn = minRow = maxColumn = maxRow = 0;
        var endpoints = range?.Split(':');
        return endpoints is { Length: 2 } &&
            TryParseAddress(endpoints[0], out minColumn, out minRow) &&
            TryParseAddress(endpoints[1], out maxColumn, out maxRow) &&
            maxColumn >= minColumn && maxRow >= minRow;
    }

    private static string DecodeSpreadsheetCell(string value)
    {
        if (value.Length < 2 || value[0] != '`') return value;
        var opening = 0;
        while (opening < value.Length && value[opening] == '`') opening++;
        var fence = new string('`', opening);
        var closing = value.IndexOf(fence, opening, StringComparison.Ordinal);
        if (closing < 0) return value;
        var code = value[opening..closing];
        var suffix = value[(closing + opening)..];
        if (suffix.Length == 0 || suffix.StartsWith(" → ", StringComparison.Ordinal))
            return code;
        return value;
    }

    private static string? CellAddress(DocumentNode node) =>
        node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value?.ToUpperInvariant();

    private static string? ExtensionString(DocumentNode node, string name) =>
        node.Extensions is not null && node.Extensions.TryGetValue(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SheetRange(IEnumerable<string> addresses)
    {
        var coordinates = addresses.Select(address =>
        {
            _ = TryParseAddress(address, out var column, out var row);
            return (column, row);
        }).ToArray();
        return ColumnName(coordinates.Min(item => item.column)) + coordinates.Min(item => item.row) + ":" +
            ColumnName(coordinates.Max(item => item.column)) + coordinates.Max(item => item.row);
    }

    private static bool TryParseAddress(string address, out int column, out int row)
    {
        var match = System.Text.RegularExpressions.Regex.Match(address, "^\\$?(?<column>[A-Za-z]+)\\$?(?<row>[1-9][0-9]*)$");
        row = 0;
        if (!match.Success || !int.TryParse(match.Groups["row"].Value, out row)) { column = 0; return false; }
        column = 0;
        foreach (var character in match.Groups["column"].Value.ToUpperInvariant()) column = checked(column * 26 + character - 'A' + 1);
        return true;
    }

    private static string ColumnName(int number)
    {
        var result = new System.Text.StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private sealed record SheetGrid(IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> CellNodeIds);

    private static Diagnostic ToCoreDiagnostic(MarkdownDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            MarkdownDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            MarkdownDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Information,
        },
        diagnostic.BlockId);
}
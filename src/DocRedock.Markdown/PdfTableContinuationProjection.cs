using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Combines conservative page-boundary table continuations only in the readable view, and the parts of a
/// paragraph that a page break cuts (marked by the PDF extractor).</summary>
internal static class PdfTableContinuationProjection
{
    internal static DocumentGraph Project(DocumentGraph graph)
    {
        if (graph.Format != DocumentFormatKind.Pdf || graph.Partitions.Count < 2) return graph;
        graph = JoinParagraphs(graph);
        var source = graph.Partitions.OrderBy(partition => partition.Order).ToArray();
        var projected = source.ToArray();
        var targets = new Dictionary<string, (int Partition, string NodeId)>(StringComparer.Ordinal);
        var partsByHost = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < source.Length; index++)
        {
            if (source[index].Order != source[index - 1].Order + 1) continue;
            var left = Flow(source[index - 1]).LastOrDefault();
            var right = Flow(source[index]).FirstOrDefault();
            if (left is null || right is null || !CanContinue(left, right)) continue;
            var target = targets.GetValueOrDefault(left.Id, (index - 1, left.Id));
            var host = projected[target.Item1].Nodes.First(node => node.Id == target.Item2);
            var hostTable = (TableNodeContent)host.Content;
            var nextTable = (TableNodeContent)right.Content;
            var extensions = new Dictionary<string, JsonElement>(host.Extensions ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
            if (!partsByHost.TryGetValue(host.Id, out var ids))
                partsByHost[host.Id] = ids = [host.Id];
            ids.Add(right.Id);
            extensions["pdf_continuation_tables"] = JsonSerializer.SerializeToElement(ids);
            var combined = host with
            {
                Content = new TableNodeContent(hostTable.Rows.Concat(nextTable.Rows.Skip(1)).ToArray()),
                Extensions = extensions,
                Provenance = (host.Provenance ?? []).Concat(right.Provenance ?? []).ToArray()
            };
            projected[target.Item1] = projected[target.Item1] with
            {
                Nodes = projected[target.Item1].Nodes.Select(node => node.Id == host.Id ? combined : node).ToArray()
            };
            projected[index] = projected[index] with { Nodes = projected[index].Nodes.Where(node => node.Id != right.Id).ToArray() };
            targets[right.Id] = target;
        }
        return graph with { Partitions = projected };
    }

    /// <summary>Puts the rest of a paragraph from the head of a page ("pdf_continues_previous_page") back after its
    /// start on the page before ("pdf_continues_next_page"), so that the paragraph reads whole and what the page
    /// before ends with - a note to compare it with the original, its image - follows the paragraph, not the middle
    /// of a sentence. A paragraph may run over several pages.</summary>
    private static DocumentGraph JoinParagraphs(DocumentGraph graph)
    {
        var partitions = graph.Partitions.OrderBy(partition => partition.Order).ToArray();
        (int Partition, string NodeId)? open = null;
        var changed = false;
        for (var index = 1; index < partitions.Length; index++)
        {
            var carried = open;
            open = null;
            var right = partitions[index].Nodes.FirstOrDefault(node => node.Kind == NodeKind.Paragraph && node.Content is TextNodeContent &&
                Extension(node, "pdf_continues_previous_page") is { ValueKind: JsonValueKind.String });
            if (right is null || partitions[index].Order != partitions[index - 1].Order + 1) continue;
            var target = carried ?? partitions[index - 1].Nodes.Where(node => node.Kind == NodeKind.Paragraph && node.Content is TextNodeContent &&
                Extension(node, "pdf_continues_next_page") is { ValueKind: JsonValueKind.True }).Select(node => ((int, string)?)(index - 1, node.Id)).FirstOrDefault();
            if (target is not { } host) continue;
            var left = partitions[host.Partition].Nodes.First(node => node.Id == host.NodeId);
            var separator = Extension(right, "pdf_continues_previous_page")!.Value.GetString() ?? string.Empty;
            var before = ((TextNodeContent)left.Content).Text.TrimEnd();
            var rightText = ((TextNodeContent)right.Content).Text;
            var after = rightText.TrimStart();
            var offset = before.Length + separator.Length - (rightText.Length - after.Length);
            var extensions = new Dictionary<string, JsonElement>(left.Extensions ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
            foreach (var key in new[] { "strike_ranges", "underline_ranges" })
            {
                var ranges = Ranges(left, key).Where(range => range.Start < before.Length)
                    .Select(range => (range.Start, Math.Min(range.End, before.Length)))
                    .Concat(Ranges(right, key).Select(range => (Math.Max(range.Start + offset, before.Length + separator.Length), range.End + offset))
                        .Where(range => range.Item2 > range.Item1))
                    .Select(range => new[] { range.Item1, range.Item2 }).ToArray();
                if (ranges.Length > 0) extensions[key] = JsonSerializer.SerializeToElement(ranges);
                else extensions.Remove(key);
            }
            var goesOn = Extension(right, "pdf_continues_next_page") is { ValueKind: JsonValueKind.True };
            if (goesOn) extensions["pdf_continues_next_page"] = JsonSerializer.SerializeToElement(true);
            else extensions.Remove("pdf_continues_next_page");
            var combined = left with
            {
                Content = new TextNodeContent(before + separator + after),
                Extensions = extensions,
                Provenance = (left.Provenance ?? []).Concat(right.Provenance ?? []).ToArray(),
            };
            partitions[host.Partition] = partitions[host.Partition] with
            {
                Nodes = partitions[host.Partition].Nodes.Select(node => node.Id == left.Id ? combined : node).ToArray()
            };
            partitions[index] = partitions[index] with { Nodes = partitions[index].Nodes.Where(node => node.Id != right.Id).ToArray() };
            changed = true;
            if (goesOn) open = host;
        }
        return changed ? graph with { Partitions = partitions } : graph;

        static JsonElement? Extension(DocumentNode node, string key) =>
            node.Extensions?.TryGetValue(key, out var value) == true ? value : null;

        static IEnumerable<(int Start, int End)> Ranges(DocumentNode node, string key)
        {
            if (Extension(node, key) is not { ValueKind: JsonValueKind.Array } array) yield break;
            foreach (var range in array.EnumerateArray())
                if (range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2 &&
                    range[0].TryGetInt32(out var start) && range[1].TryGetInt32(out var end))
                    yield return (start, end);
        }
    }

    private static IEnumerable<DocumentNode> Flow(DocumentPartition partition) => partition.Nodes
        .Where(node => node.Layer == ContentLayer.Body && node.Kind != NodeKind.Diagram ||
            node.Kind == NodeKind.Diagram && node.Layer is not (ContentLayer.Hidden or ContentLayer.Metadata) && HasDiagramContent(node))
        .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);

    private static bool HasDiagramContent(DocumentNode node)
    {
        if (node.Extensions?.TryGetValue("visual_graph", out var element) != true) return true;
        try
        {
            return element.Deserialize<VisualGraph>() is not { Nodes: not null, Edges: not null } visual ||
                visual.Nodes.Count > 0 || visual.Edges.Count > 0;
        }
        catch (JsonException) { return true; }
    }

    private static bool CanContinue(DocumentNode left, DocumentNode right)
    {
        if (left.Kind != NodeKind.Table || right.Kind != NodeKind.Table ||
            left.Content is not TableNodeContent { Rows.Count: >= 2 } first ||
            right.Content is not TableNodeContent { Rows.Count: >= 2 } next ||
            left.Geometry is not { } firstBounds || right.Geometry is not { } nextBounds) return false;
        // Cell decorations and overlay coordinates require their own row-index remapping.
        // Keep such tables independent rather than silently moving those annotations.
        string[] annotations = ["table_overlays", "strike_cells", "underline_cells"];
        if (annotations.Any(key => left.Extensions?.ContainsKey(key) == true || right.Extensions?.ContainsKey(key) == true)) return false;
        var header = first.Rows[0];
        var nextHeader = next.Rows[0];
        if (header.Count < 2 || header.Count != nextHeader.Count || header.Count(cell => !string.IsNullOrWhiteSpace(cell.Text)) < 2 ||
            header.Where((cell, column) => cell.RowSpan != 1 || cell != nextHeader[column]).Any()) return false;
        if (left.Extensions?.TryGetValue("pdf_page_rotation", out var firstRotation) == true &&
            (firstRotation.ValueKind != JsonValueKind.Number || !firstRotation.TryGetInt32(out var firstAngle) || firstAngle != 0) ||
            right.Extensions?.TryGetValue("pdf_page_rotation", out var nextRotation) == true &&
            (nextRotation.ValueKind != JsonValueKind.Number || !nextRotation.TryGetInt32(out var nextAngle) || nextAngle != 0)) return false;
        if (!TryColumns(left, out var firstColumns) || !TryColumns(right, out var nextColumns) ||
            firstColumns.Length != nextColumns.Length ||
            firstColumns.Where((value, column) => Math.Abs(value - nextColumns[column]) > 2).Any()) return false;
        if (!TryPageBox(left, out var firstPage) || !TryPageBox(right, out var nextPage)) return false;
        return firstBounds.CoordinateSpace == nextBounds.CoordinateSpace &&
            firstBounds.CoordinateSpace == firstPage.CoordinateSpace && nextBounds.CoordinateSpace == nextPage.CoordinateSpace &&
            firstBounds.RotationDegrees == 0 && nextBounds.RotationDegrees == 0 &&
            firstBounds.Y >= firstPage.Y && firstBounds.Y <= firstPage.Y + firstPage.Height * 0.15 &&
            nextBounds.Y + nextBounds.Height <= nextPage.Y + nextPage.Height + 2 &&
            nextBounds.Y + nextBounds.Height >= nextPage.Y + nextPage.Height * 0.8;
    }

    private static bool TryColumns(DocumentNode node, out double[] columns)
    {
        columns = [];
        if (node.Extensions?.TryGetValue("pdf_table_columns", out var element) != true || element.ValueKind != JsonValueKind.Array) return false;
        var values = new List<double>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            values.Add(number);
        }
        columns = values.ToArray();
        return columns.Length >= 3 && columns.Zip(columns.Skip(1)).All(pair => pair.First < pair.Second);
    }

    private static bool TryPageBox(DocumentNode node, out Geometry page)
    {
        page = null!;
        if (node.Extensions?.TryGetValue("pdf_page_box", out var element) != true || element.ValueKind != JsonValueKind.Object) return false;
        try
        {
            if (element.Deserialize<Geometry>() is not { Height: > 0, Width: > 0 } geometry ||
                !double.IsFinite(geometry.Height) || !double.IsFinite(geometry.Width) ||
                !double.IsFinite(geometry.X) || !double.IsFinite(geometry.Y)) return false;
            page = geometry;
            return true;
        }
        catch (JsonException) { return false; }
    }
}

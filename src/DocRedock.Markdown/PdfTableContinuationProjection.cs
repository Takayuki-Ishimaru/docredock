using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Combines conservative page-boundary table continuations only in the readable view.</summary>
internal static class PdfTableContinuationProjection
{
    internal static DocumentGraph Project(DocumentGraph graph)
    {
        if (graph.Format != DocumentFormatKind.Pdf || graph.Partitions.Count < 2) return graph;
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

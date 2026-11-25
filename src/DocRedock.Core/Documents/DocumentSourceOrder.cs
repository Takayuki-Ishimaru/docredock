using System.Globalization;
using System.Text.Json;

namespace DocRedock.Core.Documents;

/// <summary>The order in which the nodes of one partition are read: by Order, then by the place siblings of an equal
/// Order recorded among each other, then by id. Every output that lists nodes in source order sorts with this, so the
/// readable Markdown and HTML, the AI package and the DRMD agree.
/// <para>A Word paragraph and the nodes made from it (pictures, text boxes, charts, page breaks) share one Order, and
/// their ids do not follow the document, so the adapter records each one's place as the node extension
/// <see cref="SiblingSequenceKey"/>: 0, 1, 2... for the drawings and page breaks after the paragraph's text, in
/// document position, and -2, -3... (below <see cref="NoSequence"/>) for a page break that precedes all of the
/// paragraph's visible text and drawings. The paragraph itself records none and counts as <see cref="NoSequence"/>,
/// which puts it after its leading page breaks and before everything else. A node without the key - every node of
/// the other formats, and of a graph saved before the key existed - sorts by Order and id exactly as it always did.</para></summary>
public static class DocumentSourceOrder
{
    /// <summary>The node extension that holds a node's place among the siblings sharing its Order.</summary>
    public const string SiblingSequenceKey = "sibling_sequence";

    /// <summary>The place of a node that recorded none.</summary>
    public const int NoSequence = -1;

    /// <summary>The recorded place of a node among the siblings sharing its Order, or <see cref="NoSequence"/>.</summary>
    public static int SiblingSequence(DocumentNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Extensions is null || !node.Extensions.TryGetValue(SiblingSequenceKey, out var element)) return NoSequence;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
        return int.TryParse(element.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : NoSequence;
    }

    /// <summary>The nodes in source order.</summary>
    public static IOrderedEnumerable<DocumentNode> InSourceOrder(this IEnumerable<DocumentNode> nodes) =>
        nodes.OrderBy(node => node.Order).ThenBy(SiblingSequence).ThenBy(node => node.Id, StringComparer.Ordinal);

    /// <summary>Source order as the tie-break of an ordering by something else (a chart's cell, say).</summary>
    public static IOrderedEnumerable<DocumentNode> ThenInSourceOrder(this IOrderedEnumerable<DocumentNode> nodes) =>
        nodes.ThenBy(node => node.Order).ThenBy(SiblingSequence).ThenBy(node => node.Id, StringComparer.Ordinal);
}

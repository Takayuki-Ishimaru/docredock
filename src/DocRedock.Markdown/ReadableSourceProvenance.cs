using System.Globalization;
using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

internal static class ReadableSourceProvenance
{
    public static ReadableSourceOrigin ForNode(DocumentNode node, string partitionId, bool workbook)
    {
        var range = ExtensionText(node, "address") ?? ExtensionText(node, "cell_ref") ??
            node.Source?.Locators.FirstOrDefault(l => l.Kind is "cell_range" or "cell_address" or "cell_note")?.Value;
        var row = ExtensionInt(node, "row"); var column = ExtensionInt(node, "column");
        if (row is > 0 && column is > 0)
        {
            var endRow = ExtensionInt(node, "to_row") ?? ExtensionInt(node, "merged_to_row") ?? row.Value;
            var endColumn = ExtensionInt(node, "to_column") ?? ExtensionInt(node, "merged_to_column") ?? column.Value;
            range = Address(column.Value, row.Value) + (endRow > row || endColumn > column
                ? ":" + Address(Math.Max(column.Value, endColumn), Math.Max(row.Value, endRow)) : "");
        }
        if (range is null && ExtensionInt(node, "diagram_min_row") is > 0 and var firstRow)
            range = "rows " + firstRow.ToString(CultureInfo.InvariantCulture) + ":" +
                (ExtensionInt(node, "diagram_max_row") ?? firstRow).ToString(CultureInfo.InvariantCulture);
        return new(partitionId, workbook ? SheetName(partitionId) : null, range, node.Id, [node.Id], node.Source);
    }

    private static string Address(int column, int row)
    {
        if (column is < 1 or > 16384 || row is < 1 or > 1048576) return "?";
        var letters = "";
        while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
}

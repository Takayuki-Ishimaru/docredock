using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocRedock.Core.Documents;

public sealed record WorkbookCellTarget(string SheetName, int Row, int Column);

public static class WorkbookCellLinks
{
    public static string Id(string sheet, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (row is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(row));
        if (column is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(column));
        return "docredock-cell-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sheet))).ToLowerInvariant()[..16] +
            "-" + row.ToString(CultureInfo.InvariantCulture) + "-" + column.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? location, string? currentSheet, out WorkbookCellTarget? target)
    {
        target = null;
        if (location is null || location.Length is 0 or > 4096 || location.Any(char.IsControl)) return false;
        var text = location.AsSpan().Trim();
        if (!text.IsEmpty && text[0] == '#') text = text[1..];
        var sheet = currentSheet;
        if (!text.IsEmpty && text[0] == '\'')
        {
            var name = new StringBuilder(); var end = -1;
            for (var index = 1; index < text.Length; index++)
            {
                if (text[index] != '\'') { name.Append(text[index]); continue; }
                if (index + 1 < text.Length && text[index + 1] == '\'') { name.Append('\''); index++; continue; }
                end = index + 1; break;
            }
            if (end < 0 || end >= text.Length || text[end] != '!') return false;
            sheet = name.ToString(); text = text[(end + 1)..];
        }
        else if (text.LastIndexOf('!') is >= 0 and var split)
        {
            sheet = text[..split].ToString(); text = text[(split + 1)..];
            if (sheet.Contains('\'')) return false;
        }
        if (string.IsNullOrWhiteSpace(sheet) || sheet.Length > 256 || sheet.IndexOfAny(['[', ']']) >= 0) return false;
        var colon = text.IndexOf(':');
        if (!Address(colon < 0 ? text : text[..colon], out var row, out var column)) return false;
        if (colon >= 0 && (!Address(text[(colon + 1)..], out var endRow, out var endColumn) || endRow < row || endColumn < column)) return false;
        target = new(sheet, row, column); return true;
    }

    private static bool Address(ReadOnlySpan<char> address, out int row, out int column)
    {
        row = 0; column = 0;
        if (!address.IsEmpty && address[0] == '$') address = address[1..];
        var index = 0;
        while (index < address.Length && char.ToUpperInvariant(address[index]) is >= 'A' and <= 'Z')
        {
            if (index >= 3) return false;
            column = column * 26 + char.ToUpperInvariant(address[index++]) - 'A' + 1;
        }
        if (index == 0 || column > 16384) return false;
        if (index < address.Length && address[index] == '$') index++;
        return index < address.Length && address[index] != '0' &&
            int.TryParse(address[index..], NumberStyles.None, CultureInfo.InvariantCulture, out row) && row is >= 1 and <= 1048576;
    }
}

using System.Security.Cryptography;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

// A worksheet is read as a stream, once, and a large one is not kept in memory: what its XML says after the cells
// (merged ranges, links) and around them (sizes, hidden rows and columns, the declared range) is read in the same pass.
public sealed partial class XlsxAdapterTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(45_000)]
    public void A_worksheet_is_read_in_one_pass_whether_it_is_kept_or_streamed(int rows)
    {
        var entries = Entries(CreatePackage());
        entries["xl/styles.xml"] = Encoding.UTF8.GetBytes(
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\">" +
            "<font><name val=\"Calibri\"/></font><font><name val=\"Calibri\"/><u/></font></fonts>" +
            "<cellXfs count=\"2\"><xf fontId=\"0\"/><xf fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            $"<dimension ref=\"A1:D{rows}\"/><sheetFormatPr defaultRowHeight=\"18\"/>" +
            "<cols><col min=\"3\" max=\"3\" hidden=\"1\"/><col min=\"4\" max=\"4\" width=\"-1\"/></cols><sheetData>");
        for (var row = 1; row <= rows; row++)
        {
            sheet.Append($"<row r=\"{row}\"{(row == 3 ? " hidden=\"1\"" : "")}>");
            sheet.Append($"<c r=\"A{row}\" t=\"inlineStr\"><is><t>行{row}</t></is></c>");
            sheet.Append($"<c r=\"B{row}\" s=\"{(row is 5 or 7 ? "1" : "00")}\" t=\"inlineStr\"><is><t>link{row}</t></is></c>");
            if (row == 3) sheet.Append("<c r=\"C3\"><v>3</v></c>");
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A1:A2\"/></mergeCells>" +
            "<hyperlinks><hyperlink ref=\"B5:B6\" location=\"Sheet1!A1\"/></hyperlinks></worksheet>");
        var xml = Encoding.UTF8.GetBytes(sheet.ToString());
        // The large sheet is past the size of the parts the package keeps (4 MiB).
        if (rows > 1000) Assert.True(xml.Length > 4 << 20, $"{xml.Length} bytes");
        entries["xl/worksheets/sheet1.xml"] = xml;

        var result = new XlsxAdapter().Extract(new MemoryStream(Pack(entries)));
        var worksheet = Assert.Single(result.Worksheets);
        XlsxCellRecord Cell(string reference) => Assert.Single(worksheet.Cells, cell => cell.CellReference == reference);

        Assert.Equal(2 * rows + 1, worksheet.Cells.Count);
        Assert.Equal($"A1:D{rows}", worksheet.UsedRange);
        Assert.Equal(2, Cell("A1").MergedToRow);
        Assert.True(Cell("A3").IsHiddenRow);
        Assert.True(Cell("C3").IsHiddenColumn);
        Assert.Contains(3, worksheet.HiddenRows!);
        Assert.Contains(3, worksheet.HiddenColumns!);
        Assert.Equal(18, worksheet.Metrics!.DefaultRowHeight);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("XlsxInvalidWorksheetDimension: Sheet1 col@width", StringComparison.Ordinal));
        // The link after the cells takes the underline off the cell it covers, not off the one beside it.
        Assert.Null(Cell("B5").UnderlinedRanges);
        Assert.NotNull(Cell("B7").UnderlinedRanges);
        // A style written "00" is style 0, and is kept as written.
        Assert.Equal("00", Cell("B1").StyleId);
        var node = result.Graph.Nodes.Single(item => item.Source?.Locators[0].Value == "B1");
        Assert.True(((IStringNodeExtensions)node.Extensions!).TryGetString("style_id", out var style));
        Assert.Equal("00", style);
        Assert.Equal("00", node.Extensions!["style_id"].GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(xml)).ToLowerInvariant(), result.PartSha256["xl/worksheets/sheet1.xml"]);
    }
}

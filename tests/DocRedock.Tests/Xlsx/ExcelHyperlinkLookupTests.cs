using System.Reflection;
using System.Text;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// Whether a link covers a cell is asked once for every cell of a sheet (an underline that is only how a link looks is not emphasis), and a
/// sheet can link a range in each of its tens of thousands of rows. The answer is found from the ranges, not by trying the cell against each
/// of them, so that the cost follows the cells and the ranges and not their product.
/// </summary>
public sealed class ExcelHyperlinkLookupTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    // XlsxAdapter.ReadHyperlinkCells(sheet bytes): the function that tells whether a link covers the cell (row, column).
    private static Func<int, int, bool> Read(IEnumerable<string> references)
    {
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/><hyperlinks>");
        foreach (var reference in references) sheet.Append("<hyperlink ref=\"").Append(reference).Append("\" location=\"Sheet1!A1\"/>");
        sheet.Append("</hyperlinks></worksheet>");
        var method = typeof(XlsxAdapter).GetMethod("ReadHyperlinkCells", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Func<int, int, bool>)method.Invoke(null, [Encoding.UTF8.GetBytes(sheet.ToString())])!;
    }

    private static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (; column > 0; column = (column - 1) / 26) name.Insert(0, (char)('A' + (column - 1) % 26));
        return name.ToString();
    }

    [Fact]
    public void A_link_covers_the_cells_of_its_range_and_no_others()
    {
        var covers = Read(["B2", "D4:F6", "H1:H3", "J2:K2", "A9:C10", "B10:E12", "C11:C20", "M5:O5", "M5:M9", "Q1:Q1"]);
        // The cells of the single cell and of the ranges; the ranges that overlap or touch, and ones inside others.
        foreach (var (row, column) in new[] { (2, 2), (4, 4), (6, 6), (5, 5), (1, 8), (3, 8), (2, 10), (2, 11), (9, 1), (10, 3), (10, 5), (12, 2), (12, 5), (20, 3), (5, 13), (5, 15), (9, 13), (1, 17) })
            Assert.True(covers(row, column), $"R{row}C{column}");
        // Next to them, and in a range's row or column but outside the range.
        foreach (var (row, column) in new[] { (1, 2), (2, 1), (2, 3), (3, 2), (7, 4), (3, 4), (4, 7), (4, 8), (4, 9), (4, 10), (2, 12), (8, 1), (9, 4), (11, 1), (13, 2), (13, 5), (21, 3), (6, 14), (5, 16), (10, 13), (1, 16), (2, 17) })
            Assert.False(covers(row, column), $"R{row}C{column}");
    }

    [Fact]
    public void The_lookup_agrees_with_trying_the_cell_against_every_range()
    {
        var random = new Random(20261011);
        var ranges = new List<(int MinRow, int MaxRow, int MinColumn, int MaxColumn)>();
        for (var index = 0; index < 400; index++)
        {
            var (row, column) = (random.Next(1, 70), random.Next(1, 30));
            // Some ranges are single cells, some thin, some tall or wide, one or two reach the limits of the sheet.
            var (height, width) = random.Next(8) switch { 0 => (0, 0), 1 => (random.Next(0, 3), random.Next(0, 12)), 2 => (random.Next(0, 40), random.Next(0, 2)), _ => (random.Next(0, 6), random.Next(0, 6)) };
            ranges.Add((row, row + height, column, column + width));
        }
        ranges.Add((1, 1_048_576, 28, 28));
        ranges.Add((60, 1_048_576, 31, 16_384));
        var covers = Read(ranges.Select(range => $"{ColumnName(range.MinColumn)}{range.MinRow}:{ColumnName(range.MaxColumn)}{range.MaxRow}"));
        bool Brute(int row, int column) => ranges.Any(range => row >= range.MinRow && row <= range.MaxRow && column >= range.MinColumn && column <= range.MaxColumn);
        for (var row = 1; row <= 110; row++)
            for (var column = 1; column <= 40; column++)
                Assert.Equal(Brute(row, column), covers(row, column));
        foreach (var (row, column) in new[] { (1_048_576, 28), (1_000_000, 16_384), (59, 16_384), (60, 16_384), (60, 30), (1_048_576, 29), (1_048_576, 31) })
            Assert.Equal(Brute(row, column), covers(row, column));
        Assert.True(covers(1_048_576, 28));
        Assert.True(covers(1_000_000, 16_384));
        Assert.False(covers(1_048_576, 29));
    }

    [Fact]
    public async Task A_link_to_a_range_in_each_of_a_great_many_rows_costs_time_that_follows_the_rows()
    {
        const int rows = 300_000;
        try
        {
            // The quadratic way tries 600,000 cells against 300,000 ranges: minutes, not the second or two the lookup takes.
            var found = await Task.Run(() =>
            {
                var covers = Read(Enumerable.Range(1, rows).Select(row => $"A{row}:C{row}"));
                var count = 0;
                for (var row = 1; row <= rows; row++)
                {
                    if (covers(row, 2)) count++;
                    if (covers(row, 4)) count--;
                }
                return count;
            }).WaitAsync(Limit);
            Assert.Equal(rows, found);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Asking {2 * rows} cells whether a link covers them was still going after {Limit.TotalSeconds:0} seconds: each cell is being tried against each of the {rows} ranges.");
        }
    }
}

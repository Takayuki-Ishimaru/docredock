using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfEvaluationRegressionTests
{
    private static byte[] Schedule(string overlay)
    {
        var commands = new List<string>();
        for (var y = 60; y <= 220; y += 40) commands.Add($"60 {y} m 460 {y} l S");
        for (var x = 60; x <= 460; x += 100) commands.Add($"{x} 60 m {x} 220 l S");
        foreach (var (y, text) in new[] { (190, "Task"), (150, "DESIGN"), (110, "BUILD"), (70, "TEST") })
            commands.Add($"BT 1 0 0 1 70 {y} Tm ({text}) Tj ET");
        foreach (var (x, text) in new[] { (170, "Jan"), (270, "Feb"), (370, "Mar") })
            commands.Add($"BT 1 0 0 1 {x} 190 Tm ({text}) Tj ET");
        commands.Add(overlay);
        var content = string.Join("\n", commands);
        return Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream\n%%EOF");
    }

    [Theory]
    [InlineData(180, 2)]
    [InlineData(199, 2)]
    [InlineData(200, 2)]
    [InlineData(205, 2)]
    [InlineData(270, 3)]
    public void Bar_width_does_not_change_the_grid(int width, int endColumn)
    {
        var result = PdfTextExtractor.Extract(Schedule($"170 160 {width} 10 re f"));
        var table = AssertSchedule(result);
        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("bar", overlay.Kind);
        Assert.Equal(1, overlay.StartRow);
        Assert.Equal(1, overlay.EndRow);
        Assert.Equal(1, overlay.StartColumn);
        Assert.Equal(endColumn, overlay.EndColumn);
        Assert.DoesNotContain(result.Diagnostics!, d => d.StartsWith("VisualNodeLabelMissing:"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(10.6)]
    [InlineData(10.7)]
    [InlineData(14)]
    public void Connected_triangle_size_does_not_change_rows_or_direction(double size)
    {
        var overlay = FormattableString.Invariant($"170 165 m 420 165 l S\n420 {165-size/2} m {420+size} 165 l 420 {165+size/2} l h f");
        var result = PdfTextExtractor.Extract(Schedule(overlay));
        var table = AssertSchedule(result);
        var arrow = Assert.Single(table.Overlays!);
        Assert.Equal("arrow", arrow.Kind);
        Assert.Equal("right", arrow.Direction);
        Assert.Equal(1, arrow.StartRow);
        Assert.Equal(1, arrow.EndRow);
        Assert.Equal(1, arrow.StartColumn);
        Assert.Equal(3, arrow.EndColumn);
        Assert.DoesNotContain(result.Diagnostics!, d => d.StartsWith("VisualConnectorUnresolved:"));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(450)]
    public void Full_width_filled_bar_cannot_supply_structural_rules(int width)
    {
        var result = PdfTextExtractor.Extract(Schedule($"60 160 {width} 10 re f"));
        var table = AssertSchedule(result);
        var bar = Assert.Single(table.Overlays!);
        Assert.Equal("bar", bar.Kind);
        Assert.Equal(1, bar.StartRow);
        Assert.Equal(1, bar.EndRow);
    }

    [Fact]
    public void Unmatched_interior_shaft_cannot_create_a_row_and_unrelated_warnings_survive()
    {
        var result = PdfTextExtractor.Extract(Schedule("170 165 m 420 165 l S\n500 250 m 570 310 l S"));
        AssertSchedule(result);
        Assert.Contains(result.Diagnostics!, d => d.StartsWith("VisualConnectorUnresolved:"));
    }

    private static PdfTable AssertSchedule(PdfExtractionResult result)
    {
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(4, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(4, row.Cells.Count));
        Assert.Equal(new[] { "Task", "DESIGN", "BUILD", "TEST" }, table.Rows.Select(r => r.Cells[0].Text));
        Assert.Equal(new[] { "Task", "Jan", "Feb", "Mar" }, table.Rows[0].Cells.Select(c => c.Text));
        return table;
    }
}

using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfEvaluationRegressionTests
{
    internal static byte[] Schedule(string overlay, bool frame = false)
    {
        var commands = new List<string>();
        if (frame) commands.Add("60 60 400 160 re S");
        for (var y = frame ? 100 : 60; y <= (frame ? 180 : 220); y += 40) commands.Add($"60 {y} m 460 {y} l S");
        for (var x = frame ? 160 : 60; x <= (frame ? 360 : 460); x += 100) commands.Add($"{x} 60 m {x} 220 l S");
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

    [Fact]
    public void Plain_schedule_retains_grid_without_warnings()
    {
        var result = PdfTextExtractor.Extract(Schedule(""));
        AssertSchedule(result);
        Assert.Empty(result.VisualProjections![1].Graph.Diagnostics!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bidirectional_arrows_are_invariant_to_marker_and_shaft_order(bool vertical)
    {
        foreach (var reverseMarkers in new[] { false, true })
        foreach (var reverseShaft in new[] { false, true })
        {
            var start = vertical ? "210 85" : "170 165";
            var end = vertical ? "210 165" : "420 165";
            var heads = vertical
                ? new[] { "205 85 m 210 75 l 215 85 l h f", "205 165 m 210 175 l 215 165 l h f" }
                : new[] { "170 160 m 160 165 l 170 170 l h f", "420 160 m 430 165 l 420 170 l h f" };
            var shaft = reverseShaft ? $"{end} m {start} l S" : $"{start} m {end} l S";
            var result = PdfTextExtractor.Extract(Schedule(shaft + "\n" + string.Join("\n", reverseMarkers ? heads.Reverse() : heads)));
            var arrow = Assert.Single(AssertSchedule(result).Overlays!);
            Assert.Equal("arrow", arrow.Kind);
            Assert.Equal("both", arrow.Direction);
            Assert.Equal(vertical ? "vertical" : "horizontal", arrow.Axis);
            Assert.Equal(1, arrow.StartRow);
            Assert.Equal(vertical ? 3 : 1, arrow.EndRow);
            Assert.Equal(1, arrow.StartColumn);
            Assert.Equal(vertical ? 1 : 3, arrow.EndColumn);
            Assert.False(result.VisualProjections![1].Graph.IsPartialProjection);
        }
    }

    [Fact]
    public void Diagonal_stroke_is_retained_for_review_instead_of_consumed_as_cell_fill()
    {
        var result = PdfTextExtractor.Extract(Schedule("170 165 m 420 85 l S"));
        Assert.Empty(AssertSchedule(result).Overlays!);
        Assert.True(result.VisualProjections![1].Graph.IsPartialProjection);
        Assert.NotEmpty(result.VisualFallbacks![1].Paths);
    }

    [Fact]
    public void Header_fill_and_rectangular_frame_do_not_leave_edge_label_warnings()
    {
        var result = PdfTextExtractor.Extract(Schedule("60 180 400 40 re f", frame: true));
        AssertSchedule(result);
        Assert.Empty(result.VisualProjections![1].Graph.Diagnostics!);
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

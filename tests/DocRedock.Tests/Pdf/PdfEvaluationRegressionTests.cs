using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfEvaluationRegressionTests
{
    internal static byte[] Schedule(string overlay, bool frame = false, bool title = false, bool headerFill = false)
    {
        var commands = new List<string>();
        // v0.2.9 evaluation: an independent page heading above the table, and a header-row fill
        // painted before the grid, are the two layout features combined with the frame below.
        if (title) commands.Add("BT /F1 16 Tf 1 0 0 1 60 250 Tm (Project Schedule 2026) Tj ET");
        if (headerFill) commands.Add("0.85 g 60 180 400 40 re f 0 g");
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

    // v0.2.9 evaluation, priority 1: a heading outside the table competed for the table's own
    // ruling lines as an edge label whenever the frame was one rectangle path. All eight
    // combinations of heading, header fill, and frame style must stay warning-free.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Heading_fill_and_frame_style_do_not_create_label_warnings(bool title, bool headerFill, bool rectangleFrame)
    {
        var result = PdfTextExtractor.Extract(Schedule("", frame: rectangleFrame, title: title, headerFill: headerFill));
        AssertSchedule(result);
        Assert.Empty(result.VisualProjections![1].Graph.Diagnostics!);
        Assert.False(result.VisualProjections[1].Graph.IsPartialProjection);
        Assert.All(result.Diagnostics!, d => Assert.StartsWith("PdfTableInferred:", d));
        if (title) Assert.Contains(result.Pages[0].Regions, region => region.Text == "Project Schedule 2026");
    }

    // The control case: the same heading and framed table, plus a real diagram whose connector
    // has two competing labels. Only the genuinely ambiguous label keeps its warning, and the
    // warning still names the connector that survives in the readable graph.
    [Fact]
    public void Ambiguous_diagram_label_keeps_its_warning_next_to_a_framed_table()
    {
        var diagram = string.Join("\n",
            "520 150 80 40 re S", "BT 1 0 0 1 535 165 Tm (Start) Tj ET",
            "700 150 80 40 re S", "BT 1 0 0 1 725 165 Tm (End) Tj ET",
            "600 170 m 700 170 l S",
            "BT 1 0 0 1 615 175 Tm (yes) Tj ET",
            "BT 1 0 0 1 660 175 Tm (no) Tj ET");
        var result = PdfTextExtractor.Extract(Schedule(diagram, frame: true, title: true));
        AssertSchedule(result);
        var graph = result.VisualProjections![1].Graph;
        var headingId = "region:" + result.Pages[0].Regions.Single(region => region.Text == "Project Schedule 2026")
            .SourceTextIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        var label = Assert.Single(graph.Diagnostics!, d => d.Code == "VisualEdgeLabelUnresolved");
        Assert.NotEqual(headingId, label.SourceObjectId);
        var candidate = Assert.Single(label.RelatedObjectIds!);
        Assert.Contains(graph.Edges, edge => edge.Id == candidate && edge.SourceId is not null && edge.TargetId is not null);
        Assert.True(graph.IsPartialProjection);
        Assert.Contains(result.Diagnostics!, d => d.StartsWith("VisualEdgeLabelUnresolved:"));
    }

    internal const string BothArrow = "170 165 m 420 165 l S\n170 160 m 160 165 l 170 170 l h f\n420 160 m 430 165 l 420 170 l h f";

    // v0.2.9 evaluation, priority 2: a dashed double arrow produced exactly the same overlay as a
    // solid one. Direction and span stay identical; only the stroke style differs.
    [Theory]
    [InlineData("", null)]
    [InlineData("[4 3] 0 d\n", "dashed")]
    [InlineData("[1 2] 0 d\n", "dotted")]
    [InlineData("2 w [2 2] 0 d\n", "dotted")]
    [InlineData("[4 3 1 3] 0 d\n", "dashed")]
    [InlineData("[] 0 d\n", null)]
    [InlineData("[4 3] 0 d\n[] d\n", null)]
    public void Arrow_stroke_style_is_kept_with_direction_and_span(string strokeState, string? expected)
    {
        var result = PdfTextExtractor.Extract(Schedule(strokeState + BothArrow));
        var arrow = Assert.Single(AssertSchedule(result).Overlays!);
        Assert.Equal(("arrow", "both", 1, 1, 1, 3), (arrow.Kind, arrow.Direction, arrow.StartRow, arrow.EndRow, arrow.StartColumn, arrow.EndColumn));
        Assert.Equal(expected, arrow.LineStyle);
        Assert.False(result.VisualProjections![1].Graph.IsPartialProjection);
    }

    [Fact]
    public void Dash_pattern_follows_graphics_state_save_and_restore()
    {
        // The dash is set inside q..Q for the horizontal shaft only; the vertical arrow drawn after
        // Q (and away from the horizontal one) is solid.
        var overlay = "q [4 3] 0 d 280 165 m 420 165 l S Q\n" +
            "280 160 m 270 165 l 280 170 l h f\n420 160 m 430 165 l 420 170 l h f\n" +
            "210 85 m 210 165 l S\n205 85 m 210 75 l 215 85 l h f";
        var overlays = AssertSchedule(PdfTextExtractor.Extract(Schedule(overlay))).Overlays!;
        Assert.Equal("dashed", Assert.Single(overlays, o => o.Axis == "horizontal").LineStyle);
        Assert.Null(Assert.Single(overlays, o => o.Axis == "vertical").LineStyle);
    }

    [Fact]
    public void Text_arrays_do_not_become_dash_patterns()
    {
        var result = PdfTextExtractor.Extract(Schedule("BT 1 0 0 1 400 30 Tm [(A) -120 (B)] TJ ET\n" + BothArrow));
        Assert.Null(Assert.Single(AssertSchedule(result).Overlays!).LineStyle);
    }

    [Fact]
    public void Dashed_outline_bar_and_unresolved_dashed_stroke_keep_their_style()
    {
        var result = PdfTextExtractor.Extract(Schedule("q [4 3] 0 d 170 70 180 10 re S Q\nq [4 3] 0 d 170 165 m 420 85 l S Q"));
        var bar = Assert.Single(AssertSchedule(result).Overlays!);
        Assert.Equal(("bar", "dashed"), (bar.Kind, bar.LineStyle));
        // The diagonal stays unresolved for review, and its fallback path still says it was dashed.
        var fallback = Assert.Single(result.VisualFallbacks![1].Paths);
        Assert.Equal("dashed", fallback.LineStyle);
    }

    // A filled bar whose outline is painted separately ("re f" then "re S") is one shape: one
    // glyph per cell, carrying the outline's style, instead of a solid and a dashed bar stacked.
    [Theory]
    [InlineData("q 0.8 g 170 160 180 10 re f Q q 170 160 180 10 re S Q", null)]
    [InlineData("q 0.8 g 170 160 180 10 re f Q q [4 3] 0 d 170 160 180 10 re S Q", "dashed")]
    [InlineData("q [4 3] 0 d 170 160 180 10 re S Q q 0.8 g 170 160 180 10 re f Q", "dashed")]
    [InlineData("q 0.8 g [1 2] 0 d 170 160 180 10 re B Q", "dotted")]
    public void Fill_and_outline_of_one_bar_become_one_overlay_with_the_outline_style(string overlay, string? expected)
    {
        var result = PdfTextExtractor.Extract(Schedule(overlay));
        var bar = Assert.Single(AssertSchedule(result).Overlays!);
        Assert.Equal(("bar", 1, 1, 2), (bar.Kind, bar.StartRow, bar.StartColumn, bar.EndColumn));
        Assert.Equal(expected, bar.LineStyle);
        Assert.False(result.VisualProjections![1].Graph.IsPartialProjection);
        Assert.Empty(result.VisualFallbacks![1].Paths);
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

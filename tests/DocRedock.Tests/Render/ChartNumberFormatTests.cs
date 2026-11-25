using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>Category, tick and data labels of a native chart show their Excel number format, as cells do.</summary>
public sealed class ChartNumberFormatTests
{
    private const string Axes = "<c:axId val='1'/><c:axId val='2'/>";

    [Fact]
    public void Date_categories_are_shown_as_dates_instead_of_serial_numbers()
    {
        var result = Read("barChart", Series(Numbers("cat", "m/d/yyyy", 45292, 45323), Numbers("val", "General", 3, 5)) + Axes);
        Assert.Equal(["2024-01-01", "2024-02-01"], Assert.Single(result.Chart!.Series).Categories);
        var categories = Labels(Render(result), "category");
        Assert.Equal(["2024-01-01", "2024-02-01"], categories);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Date_categories_respect_the_1904_date_system_of_the_chart_space()
    {
        var result = Read("lineChart", Series(Numbers("cat", "yyyy/mm/dd", 45292), Numbers("val", "General", 3)) + Axes,
            chartSpace: "<c:date1904 val='1'/>");
        Assert.True(result.Chart!.Date1904);
        Assert.Equal(["2028-01-02"], Assert.Single(result.Chart.Series).Categories);
        Assert.False(Read("lineChart", Series(Numbers("cat", "yyyy/mm/dd", 45292), Numbers("val", "General", 3)) + Axes,
            chartSpace: "<c:date1904 val='0'/>").Chart!.Date1904);
    }

    [Fact]
    public void A_category_point_with_its_own_format_code_uses_it()
    {
        var category = "<c:cat><c:numRef><c:f>S!A2:A3</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val='2'/>" +
            "<c:pt idx='0' formatCode='yyyy/m/d'><c:v>45292</c:v></c:pt><c:pt idx='1'><c:v>7</c:v></c:pt></c:numCache></c:numRef></c:cat>";
        var result = Read("barChart", Series(category, Numbers("val", "General", 3, 5)) + Axes);
        Assert.Equal(["2024-01-01", "7"], Assert.Single(result.Chart!.Series).Categories);
    }

    [Fact]
    public void An_explicit_category_axis_format_shapes_numeric_categories()
    {
        var result = Read("barChart", Series(Numbers("cat", "General", 1500, 2500), Numbers("val", "General", 3, 5)) + Axes,
            "<c:catAx><c:axId val='1'/><c:numFmt formatCode='#,##0' sourceLinked='0'/></c:catAx>");
        Assert.Equal(["1,500", "2,500"], Assert.Single(result.Chart!.Series).Categories);
    }

    [Fact]
    public void A_percent_value_axis_shows_percent_ticks()
    {
        var result = Read("barChart", Series(Text("cat", "A", "B"), Numbers("val", "General", .1, .5)) + Axes,
            ValueAxis("<c:numFmt formatCode='0%' sourceLinked='0'/>", 0, 1));
        Assert.Equal("0%", result.Chart!.ValueAxisFormat);
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], Ticks(Render(result)));
    }

    [Fact]
    public void A_source_linked_value_axis_shows_the_grouping_and_currency_of_the_plotted_values()
    {
        var result = Read("lineChart", Series(Text("cat", "A", "B"), Numbers("val", "\"$\"#,##0.00", 1000, 4000)) + Axes,
            ValueAxis("<c:numFmt formatCode='General' sourceLinked='1'/>", 0, 4000));
        Assert.Equal("\"$\"#,##0.00", result.Chart!.ValueAxisFormat);
        Assert.Equal(["$0.00", "$1,000.00", "$2,000.00", "$3,000.00", "$4,000.00"], Ticks(Render(result)));
    }

    [Fact]
    public void A_horizontal_bar_chart_formats_the_ticks_of_its_value_axis()
    {
        var result = Read("barChart", "<c:barDir val='bar'/>" + Series(Text("cat", "A", "B"), Numbers("val", "General", 1000, 4000)) + Axes,
            ValueAxis("<c:numFmt formatCode='#,##0' sourceLinked='0'/>", 0, 4000));
        Assert.Equal(["0", "1,000", "2,000", "3,000", "4,000"], Ticks(Render(result)));
    }

    [Fact]
    public void A_series_level_label_format_is_applied_and_the_raw_value_is_kept()
    {
        var result = Read("barChart", Series(Text("cat", "A", "B"), Numbers("val", "General", 8, -4),
            "<c:dLbls><c:numFmt formatCode='0.0\" kg\"' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>") +
            "<c:dLbls><c:numFmt formatCode='0.000' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>" + Axes);
        var svg = Render(result);
        Assert.Equal(["8.0 kg", "-4.0 kg"], Labels(svg, "data"));
        Assert.Equal(["8", "-4"], svg.Descendants().Where(e => e.Name.LocalName == "rect" && e.Attribute("data-value") is not null)
            .Select(e => (string)e.Attribute("data-value")!));
        Assert.Equal(new double?[] { 8, -4 }, Assert.Single(result.Chart!.Series).Values);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_chart_level_label_format_is_applied_to_every_series()
    {
        var result = Read("lineChart", Series(Text("cat", "A", "B"), Numbers("val", "0.00", 1234.5, 20000)) +
            "<c:dLbls><c:numFmt formatCode='#,##0' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>" + Axes);
        Assert.Equal(["1,235", "20,000"], Labels(Render(result), "data"));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("<c:dLbls><c:showVal val='1'/></c:dLbls>")]
    [InlineData("<c:dLbls><c:numFmt formatCode='0.000' sourceLinked='1'/><c:showVal val='1'/></c:dLbls>")]
    public void A_source_linked_label_shows_the_format_of_the_plotted_values(string labels)
    {
        var result = Read("barChart", Series(Text("cat", "A", "B"), Numbers("val", "0.00%", .25, .5)) + labels + Axes);
        Assert.Equal(["25.00%", "50.00%"], Labels(Render(result), "data"));
    }

    [Fact]
    public void Pie_labels_apply_their_explicit_format_to_the_value_and_to_the_percentage()
    {
        var percent = Read("pieChart", Series(Text("cat", "A", "B"), Numbers("val", "General", 2, 6)) +
            "<c:dLbls><c:numFmt formatCode='0.0%' sourceLinked='0'/><c:showPercent val='1'/></c:dLbls>");
        Assert.Equal(["25.0%", "75.0%"], Labels(Render(percent), "data"));
        var linked = Read("pieChart", Series(Text("cat", "A", "B"), Numbers("val", "#,##0.0", 2000, 6000)) +
            "<c:dLbls><c:showVal val='1'/><c:showPercent val='1'/></c:dLbls>");
        Assert.Equal(["2,000.0 (25%)", "6,000.0 (75%)"], Labels(Render(linked), "data"));
    }

    [Fact]
    public void Scatter_axes_format_their_ticks_with_the_explicit_or_source_linked_format()
    {
        var result = Read("scatterChart", "<c:ser><c:idx val='0'/><c:order val='0'/>" + Numbers("xVal", "yyyy-mm-dd", 45292, 45296) +
            Numbers("yVal", "General", .2, .8) + "</c:ser>" + Axes,
            "<c:valAx><c:axId val='1'/><c:scaling><c:min val='45292'/><c:max val='45296'/></c:scaling><c:axPos val='b'/><c:numFmt formatCode='General' sourceLinked='1'/></c:valAx>" +
            "<c:valAx><c:axId val='2'/><c:scaling><c:min val='0'/><c:max val='1'/></c:scaling><c:axPos val='l'/><c:numFmt formatCode='0.0%' sourceLinked='0'/></c:valAx>");
        var svg = Render(result);
        Assert.Equal(["2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05"], Labels(svg, "x-tick"));
        Assert.Equal(["0.0%", "25.0%", "50.0%", "75.0%", "100.0%"], Ticks(svg));
        Assert.Contains(svg.Descendants(), e => (string?)e.Attribute("data-x-value") == "45292");
    }

    [Fact]
    public void General_and_missing_formats_keep_the_plain_numbers()
    {
        var general = Read("barChart", Series(Numbers("cat", "General", 2023, 2024), Numbers("val", "General", .1, .5),
            "<c:dLbls><c:numFmt formatCode='General' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>") + Axes,
            ValueAxis("<c:numFmt formatCode='General' sourceLinked='1'/>", 0, 1));
        var plain = Read("barChart", Series(Numbers("cat", null, 2023, 2024), Numbers("val", null, .1, .5),
            "<c:dLbls><c:showVal val='1'/></c:dLbls>") + Axes, ValueAxis("", 0, 1));
        Assert.Null(general.Chart!.ValueAxisFormat);
        Assert.Null(Assert.Single(general.Chart.Series).LabelFormat);
        Assert.Empty(general.Warnings);
        var svg = Render(general);
        Assert.Equal(["0", "0.25", "0.5", "0.75", "1"], Ticks(svg));
        Assert.Equal(["0.1", "0.5"], Labels(svg, "data"));
        Assert.Equal(["2023", "2024"], Labels(svg, "category"));
        Assert.Equal(DrawingChartSvg.Render(plain.Chart!, 480, 320), DrawingChartSvg.Render(general.Chart, 480, 320));
    }

    [Fact]
    public void A_percent_stacked_axis_keeps_its_percent_ticks_when_linked_to_the_source_format()
    {
        var result = Read("barChart", "<c:grouping val='percentStacked'/>" +
            Series(Text("cat", "A", "B"), Numbers("val", "#,##0.00", 1000, 3000)) + Axes,
            "<c:valAx><c:axId val='2'/><c:numFmt formatCode='0%' sourceLinked='1'/></c:valAx>");
        Assert.Null(result.Chart!.ValueAxisFormat);
        Assert.Equal(["0%", "25%", "50%", "75%", "100%"], Ticks(Render(result)));
    }

    [Fact]
    public void An_oversized_format_code_falls_back_to_the_plain_number_with_a_diagnostic()
    {
        var oversized = "0." + new string('0', 300);
        var result = Read("barChart", Series(Numbers("cat", oversized, 45292, 45323), Numbers("val", "General", .1, .5),
            "<c:dLbls><c:numFmt formatCode='" + oversized + "' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>") + Axes,
            ValueAxis("<c:numFmt formatCode='" + oversized + "' sourceLinked='0'/>", 0, 1));
        Assert.Null(result.Chart!.ValueAxisFormat);
        Assert.Null(Assert.Single(result.Chart.Series).LabelFormat);
        Assert.Contains(result.Warnings, w => w.Contains("value axis number format exceeds", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("data label number format exceeds", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("category number format exceeds", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains(oversized, StringComparison.Ordinal));
        var svg = Render(result);
        Assert.Equal(["0", "0.25", "0.5", "0.75", "1"], Ticks(svg));
        Assert.Equal(["0.1", "0.5"], Labels(svg, "data"));
        Assert.Equal(["45292", "45323"], Labels(svg, "category"));
    }

    [Fact]
    public void A_format_code_that_cannot_be_applied_falls_back_to_the_plain_number_with_a_diagnostic()
    {
        var result = Read("lineChart", Series(Text("cat", "A", "B"), Numbers("val", "General", .1, .5),
            "<c:dLbls><c:numFmt formatCode='@' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>") + Axes,
            ValueAxis("<c:numFmt formatCode='@' sourceLinked='0'/>", 0, 1));
        Assert.Null(result.Chart!.ValueAxisFormat);
        Assert.Null(Assert.Single(result.Chart.Series).LabelFormat);
        Assert.Contains("The value axis number format \"@\" cannot be applied; the plain number is shown.", result.Warnings);
        Assert.Contains("The data label number format \"@\" cannot be applied; the plain number is shown.", result.Warnings);
        var svg = Render(result);
        Assert.Equal(["0", "0.25", "0.5", "0.75", "1"], Ticks(svg));
        Assert.Equal(["0.1", "0.5"], Labels(svg, "data"));
    }

    [Fact]
    public void A_format_that_reaches_the_renderer_unchecked_never_breaks_the_chart()
    {
        var chart = Read("barChart", Series(Text("cat", "A", "B"), Numbers("val", "General", .1, .5),
            "<c:dLbls><c:showVal val='1'/></c:dLbls>") + Axes, ValueAxis("", 0, 1)).Chart!;
        var plain = DrawingChartSvg.Render(chart, 480, 320);
        var broken = chart with { ValueAxisFormat = new string('0', 5000), Series = [chart.Series[0] with { LabelFormat = "@" }] };
        Assert.Equal(plain, DrawingChartSvg.Render(broken, 480, 320));
    }

    private static XDocument Render(DrawingChartReadResult result) =>
        XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(Assert.IsType<DocRedock.Core.Documents.DrawingChart>(result.Chart), 480, 320)));

    private static string[] Labels(XDocument svg, string kind) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == kind).Select(e => e.Value).ToArray();

    /// <summary>Tick labels of the value axis: the only text without a label kind.</summary>
    private static string[] Ticks(XDocument svg) => svg.Descendants()
        .Where(e => e.Name.LocalName == "text" && e.Attribute("data-chart-label") is null).Select(e => e.Value).ToArray();

    private static string ValueAxis(string numberFormat, double minimum, double maximum) =>
        "<c:valAx><c:axId val='2'/><c:scaling><c:min val='" + Invariant(minimum) + "'/><c:max val='" + Invariant(maximum) +
        "'/></c:scaling><c:axPos val='l'/>" + numberFormat + "</c:valAx>";

    private static string Series(string categories, string values, string labels = "") =>
        "<c:ser><c:idx val='0'/><c:order val='0'/><c:tx><c:v>Source</c:v></c:tx>" + categories + values + labels + "</c:ser>";

    private static string Text(string element, params string[] values) =>
        "<c:" + element + "><c:strRef><c:f>S!A2:A9</c:f><c:strCache><c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + value + "</c:v></c:pt>")) +
        "</c:strCache></c:strRef></c:" + element + ">";

    private static string Numbers(string element, string? formatCode, params double[] values) =>
        "<c:" + element + "><c:numRef><c:f>S!B2:B9</c:f><c:numCache>" +
        (formatCode is null ? "" : "<c:formatCode>" + System.Security.SecurityElement.Escape(formatCode) + "</c:formatCode>") +
        "<c:ptCount val='" + values.Length + "'/>" +
        string.Concat(values.Select((value, index) => "<c:pt idx='" + index + "'><c:v>" + Invariant(value) + "</c:v></c:pt>")) +
        "</c:numCache></c:numRef></c:" + element + ">";

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static DrawingChartReadResult Read(string type, string plot, string axes = "", string chartSpace = "") =>
        DrawingChartReader.Read(Encoding.UTF8.GetBytes(
            "<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'>" +
            chartSpace + "<c:chart><c:plotArea><c:" + type + ">" + plot + "</c:" + type + ">" + axes + "</c:plotArea></c:chart></c:chartSpace>"));
}

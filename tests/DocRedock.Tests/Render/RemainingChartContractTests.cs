using System.Text;
using System.Xml.Linq;
using DocRedock.Formats.OpenXml.Drawing;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class RemainingChartContractTests
{
    [Fact]
    public void Explicit_value_labels_are_visible_without_changing_the_cached_bar_values()
    {
        var result = Read("barChart", "<c:dLbls><c:showVal val='1'/></c:dLbls>");
        Assert.NotNull(result.Chart);
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320)));
        var labels = svg.Descendants().Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == "data").Select(e => e.Value).ToArray();
        Assert.Equal(2, labels.Length);
        Assert.Contains("8", labels);
        Assert.Contains("-4", labels);
        Assert.Equal(new double?[] { 8, -4 }, Assert.Single(result.Chart.Series).Values);
    }

    [Fact]
    public void Pie_percentage_labels_use_the_positive_source_total()
    {
        var result = Read("pieChart", "<c:dLbls><c:showPercent val='1'/></c:dLbls>", values: [2, 6]);
        Assert.NotNull(result.Chart);
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320)));
        var labels = svg.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray();
        Assert.Contains("25%", labels); Assert.Contains("75%", labels);
        Assert.Equal(new double?[] { 2, 6 }, Assert.Single(result.Chart.Series).Values);
    }

    [Fact]
    public void Explicit_false_does_not_create_data_labels()
    {
        var result = Read("barChart", "<c:dLbls><c:showVal val='0'/></c:dLbls>");
        Assert.NotNull(result.Chart);
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart, 480, 320)));
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "text" && (string?)e.Attribute("data-chart-label") == "data");
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("span")]
    public void Explicit_blank_display_is_applied_or_diagnosed_instead_of_silently_becoming_a_gap(string mode)
    {
        var result = Read("lineChart", "", chartSetting: "<c:dispBlanksAs val='" + mode + "'/>", values: [2, null, 6]);
        Assert.True(result.Warnings.Any(w => w.Contains("blank", StringComparison.OrdinalIgnoreCase)) ||
            result.Chart is {} chart && DrawingChartSvg.Render(chart, 480, 320) is {} svg &&
            (mode == "zero" ? svg.Contains("data-value=\"0\"", StringComparison.Ordinal) :
                XDocument.Parse(svg).Descendants().Any(e => e.Name.LocalName == "polyline" && ((string?)e.Attribute("points"))?.Contains(' ') == true)));
    }

    [Fact]
    public void Axis_units_are_rendered_or_explicitly_diagnosed()
    {
        var result = Read("barChart", "", axis: "<c:valAx><c:axId val='2'/><c:title><c:tx><c:rich><a:p><a:r><a:t>Pressure (MPa)</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx>");
        Assert.True(result.Warnings.Any(w => w.Contains("axis", StringComparison.OrdinalIgnoreCase) && w.Contains("title", StringComparison.OrdinalIgnoreCase)) ||
            result.Chart is {} chart && DrawingChartSvg.Render(chart, 480, 320)?.Contains("Pressure (MPa)", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Custom_label_format_is_applied_or_diagnosed_without_losing_its_units()
    {
        var result = Read("barChart", "<c:dLbls><c:numFmt formatCode='0.0 kg' sourceLinked='0'/><c:showVal val='1'/></c:dLbls>");
        Assert.True(result.Warnings.Any(w => w.Contains("label", StringComparison.OrdinalIgnoreCase) || w.Contains("format", StringComparison.OrdinalIgnoreCase)) ||
            result.Chart is {} chart && DrawingChartSvg.Render(chart, 480, 320)?.Contains("kg", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("barChart")]
    [InlineData("lineChart")]
    [InlineData("areaChart")]
    public void Value_labels_are_emitted_outside_the_clipped_plot_and_default_boolean_means_true(string type)
    {
        var chart = Assert.IsType<DocRedock.Core.Documents.DrawingChart>(Read(type, "<c:dLbls><c:showVal/></c:dLbls>").Chart);
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(chart, 480, 320)));
        var labels = svg.Descendants().Where(e => (string?)e.Attribute("data-chart-label") == "data").ToArray();
        Assert.Equal(2, labels.Length);
        Assert.All(labels, label => Assert.Same(svg.Root, label.Parent));
    }

    [Fact]
    public void Series_false_overrides_chart_true_without_hiding_other_series_values()
    {
        var source = Read("barChart", "<c:dLbls><c:showVal val='1'/></c:dLbls>").Chart!;
        var first = source.Series[0] with { ShowValueLabels = false };
        var second = first with { ShowValueLabels = true, Name = "Other" };
        var chart = source with { Series = [first, second] };
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(chart, 480, 320)));
        Assert.Equal(2, svg.Descendants().Count(e => (string?)e.Attribute("data-chart-label") == "data"));
    }

    [Fact]
    public void Reader_applies_series_label_suppression_before_chart_level_inheritance()
    {
        var chart = Read("barChart", "<c:dLbls><c:showVal val='1'/></c:dLbls>",
            seriesLabels: "<c:dLbls><c:showVal val='0'/></c:dLbls>").Chart!;
        Assert.False(Assert.Single(chart.Series).ShowValueLabels);
        Assert.DoesNotContain("data-chart-label=\"data\"", DrawingChartSvg.Render(chart, 480, 320));
    }

    [Fact]
    public void Scatter_value_labels_and_both_pie_label_flags_do_not_silently_drop_content()
    {
        var source = Read("pieChart", "<c:dLbls><c:showVal/><c:showPercent/></c:dLbls>", values: [2, 6]).Chart!;
        var pie = DrawingChartSvg.Render(source, 480, 320)!;
        Assert.Contains(">2 (25%)<", pie);
        Assert.Contains(">6 (75%)<", pie);
        var scatter = source with { Type = "scatter", Series = [source.Series[0] with { ShowPercentLabels = false, XValues = [1, 2] }] };
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(scatter, 480, 320)));
        Assert.Equal(2, svg.Descendants().Count(e => (string?)e.Attribute("data-chart-label") == "data"));
    }

    [Fact]
    public void Excess_label_count_requests_an_explicit_renderer_fallback_instead_of_silently_truncating()
    {
        var chart = Read("lineChart", "<c:dLbls><c:showVal/></c:dLbls>").Chart!;
        chart = chart with { Series = [chart.Series[0] with { Categories = [], Values = Enumerable.Repeat<double?>(1, 513).ToArray() }] };
        Assert.Null(DrawingChartSvg.Render(chart, 4800, 320));
    }

    [Fact]
    public void Rotated_axis_title_attributes_are_diagnosed_and_title_text_stays_safe()
    {
        var result = Read("barChart", "", axis: "<c:valAx><c:axId val='2'/><c:title><c:tx><c:rich><a:bodyPr rot='5400000'/><a:p><a:r><a:t>&lt;script&gt;Pressure&lt;/script&gt;</a:t></a:r></a:p><a:p><a:r><a:t>MPa</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx>");
        Assert.Contains(result.Warnings, w => w.Contains("axis title", StringComparison.OrdinalIgnoreCase) && w.Contains("rotation", StringComparison.OrdinalIgnoreCase));
        var svg = XDocument.Parse(Assert.IsType<string>(DrawingChartSvg.Render(result.Chart!, 480, 320)));
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "script");
        Assert.Contains(svg.Descendants(), e => (string?)e.Attribute("data-chart-label") == "axis-title" && e.Value == "MPa");
    }

    private static DrawingChartReadResult Read(string type, string labels, string chartSetting = "", double?[]? values = null, string axis = "", string seriesLabels = "")
    {
        values ??= [8, -4];
        var points = string.Concat(values.Select((v, i) => v is null ? "" :
            "<c:pt idx='" + i + "'><c:v>" + v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</c:v></c:pt>"));
        var xml = "<c:chartSpace xmlns:c='http://schemas.openxmlformats.org/drawingml/2006/chart' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><c:chart><c:plotArea><c:" + type + ">" +
            "<c:ser><c:idx val='0'/><c:order val='0'/><c:tx><c:v>Source</c:v></c:tx><c:cat><c:strLit><c:ptCount val='2'/><c:pt idx='0'><c:v>Alpha</c:v></c:pt><c:pt idx='1'><c:v>Beta</c:v></c:pt></c:strLit></c:cat>" +
            "<c:val><c:numLit><c:ptCount val='" + values.Length + "'/>" + points + "</c:numLit></c:val>" + seriesLabels + "</c:ser>" + labels + "<c:axId val='2'/></c:" + type + ">" + axis + "</c:plotArea>" + chartSetting + "</c:chart></c:chartSpace>";
        return DrawingChartReader.Read(Encoding.UTF8.GetBytes(xml));
    }
}

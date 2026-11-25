using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

public sealed class ComplexDrawingGeometryTests
{
    private static XmlElement Xml(string body)
    { var document = new XmlDocument { XmlResolver = null }; document.LoadXml(body); return document.DocumentElement!; }

    [Theory]
    [InlineData("*/ 12 3 2", 18)]
    [InlineData("+- 12 3 2", 13)]
    [InlineData("+/ 12 3 2", 7.5)]
    [InlineData("?: -1 2 3", 3)]
    [InlineData("?: 1 2 3", 2)]
    [InlineData("abs -4", 4)]
    [InlineData("at2 0 1", 5400000)]
    [InlineData("cat2 10 3 4", 6)]
    [InlineData("cos 10 3600000", 5)]
    [InlineData("min 4 2", 2)]
    [InlineData("max 4 2", 4)]
    [InlineData("mod 2 3 6", 7)]
    [InlineData("pin 2 7 5", 5)]
    [InlineData("sat2 10 3 4", 8)]
    [InlineData("sin 10 1800000", 5)]
    [InlineData("sqrt 81", 9)]
    [InlineData("tan 10 2700000", 10)]
    [InlineData("val -42", -42)]
    public void All_guide_operations_have_independent_numeric_expectations(string formula, double expected)
    {
        Assert.True(DrawingMlGeometryReader.TryEvaluateFormula(formula, new Dictionary<string, double>(), out var actual));
        Assert.Equal(expected, actual, 7);
    }

    [Theory]
    [InlineData("*/ 1 2 0")]
    [InlineData("sqrt -1")]
    [InlineData("val unknown")]
    [InlineData("val NaN")]
    [InlineData("val 1e100")]
    [InlineData("bad 1 2")]
    [InlineData("val 1 2")]
    public void Unresolved_or_invalid_formulas_are_not_silently_coordinates(string formula) =>
        Assert.False(DrawingMlGeometryReader.TryEvaluateFormula(formula, new Dictionary<string, double>(), out _));

    [Fact(Timeout = 10000)]
    public async Task All_187_standard_presets_resolve_to_finite_paths_without_fallback()
    {
        await Task.Yield();
        var presets = DrawingMlGeometryReader.PresetNames;
        Assert.Equal(187, presets.Count);
        foreach (var preset in presets)
        {
            var result = DrawingMlGeometryReader.Read(Xml($"<prstGeom prst='{preset}'/>"), 240, 160);
            Assert.True(result.Warnings.Count == 0, preset + ": " + string.Join(";", result.Warnings));
            Assert.NotEmpty(result.Paths);
            Assert.All(result.Paths, path => Assert.All(path.Commands, command => Assert.All(command.Values, value => Assert.True(double.IsFinite(value)))));
        }
    }

    [Fact]
    public void Custom_guides_resolve_curve_and_arc_and_keep_separate_fill_and_stroke_paths()
    {
        var result = DrawingMlGeometryReader.Read(Xml("""
            <custGeom><avLst><gd name="quarter" fmla="*/ w 1 4"/></avLst>
            <gdLst><gd name="halfH" fmla="*/ h 1 2"/></gdLst><rect l="quarter" t="0" r="w" b="h"/>
            <pathLst><path fill="lighten" stroke="false"><moveTo><pt x="quarter" y="0"/></moveTo>
            <cubicBezTo><pt x="quarter" y="h"/><pt x="w" y="h"/><pt x="w" y="halfH"/></cubicBezTo><close/></path>
            <path fill="none"><moveTo><pt x="w" y="halfH"/></moveTo><arcTo wR="quarter" hR="halfH" stAng="0" swAng="cd2"/></path></pathLst></custGeom>
            """), 240, 160);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Paths.Count);
        var first = result.Paths[0];
        Assert.True(first.Fill); Assert.False(first.Stroke); Assert.Equal("lighten", first.FillMode);
        Assert.Equal(new double[] { 60, 0 }, first.Commands[0].Values);
        Assert.Equal(new double[] { 60, 160, 240, 160, 240, 80 }, first.Commands[1].Values);
        Assert.False(result.Paths[1].Fill); Assert.True(result.Paths[1].Stroke);
        Assert.Equal(new double[] { 60, 80, 0, 0, 1, 120, 80 }, result.Paths[1].Commands[1].Values.Select(v => Math.Round(v, 7)));
        Assert.Equal(60, result.TextBounds!.X); Assert.Equal(180, result.TextBounds.Width);
    }

    [Fact]
    public void Arc_with_one_zero_radius_becomes_a_line_to_the_arc_end_and_moves_the_current_point()
    {
        var result = DrawingMlGeometryReader.Read(Xml("""
            <custGeom><pathLst><path w="100" h="100"><moveTo><pt x="0" y="0"/></moveTo>
            <arcTo wR="0" hR="50" stAng="-5400000" swAng="10800000"/><lnTo><pt x="30" y="100"/></lnTo></path></pathLst></custGeom>
            """), 100, 100);
        Assert.Empty(result.Warnings);
        var commands = result.Paths[0].Commands;
        Assert.Equal(new[] { "M", "L", "L" }, commands.Select(c => c.Kind));
        // The degenerate ellipse is the vertical segment from (0,-50) to (0,50) about the center (0,50).
        Assert.Equal(0, commands[1].Values[0], 6);
        Assert.Equal(100, commands[1].Values[1], 6);
        Assert.Equal(30, commands[2].Values[0], 6);
        Assert.Equal(100, commands[2].Values[1], 6);
    }

    [Fact]
    public void Non_circular_arc_uses_visual_angles_and_full_circle_never_disappears()
    {
        var result = DrawingMlGeometryReader.Read(Xml("""
            <custGeom><pathLst><path w="200" h="100"><moveTo><pt x="100" y="0"/></moveTo>
            <arcTo wR="100" hR="50" stAng="2700000" swAng="21600000"/></path></pathLst></custGeom>
            """), 200, 100);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Paths[0].Commands.Count);
        var midpoint = result.Paths[0].Commands[1];
        // At visual 45 degrees, both radial components equal 44.72135955.
        Assert.Equal(100 - 2 * Math.Sqrt(2000), midpoint.Values[5], 6);
        Assert.Equal(-2 * Math.Sqrt(2000), midpoint.Values[6], 6);
        Assert.Equal(100, result.Paths[0].Commands[2].Values[5], 6);
        Assert.Equal(0, result.Paths[0].Commands[2].Values[6], 6);
    }

    [Fact]
    public void Fixed_path_coordinates_scale_independently_and_adjustments_override_preset_defaults()
    {
        var result = DrawingMlGeometryReader.Read(Xml("<custGeom><pathLst><path w='10' h='20'><moveTo><pt x='0' y='0'/></moveTo><lnTo><pt x='10' y='20'/></lnTo></path></pathLst></custGeom>"), 300, 100);
        Assert.Empty(result.Warnings);
        Assert.Equal(new double[] { 300, 100 }, result.Paths[0].Commands[1].Values);
        var arrow = DrawingMlGeometryReader.Read(Xml("<prstGeom prst='rightArrow'><avLst><gd name='adj1' fmla='val 20000'/></avLst></prstGeom>"), 200, 100);
        Assert.Empty(arrow.Warnings);
        Assert.Equal(40, arrow.Paths[0].Commands[0].Values[1], 6);
    }

    [Fact]
    public void Raw_EMU_paths_and_text_rectangles_use_shape_units_independent_of_fixed_path_units()
    {
        var result = DrawingMlGeometryReader.Read(Xml("""
            <custGeom><gdLst><gd name="quarter" fmla="*/ w 1 4"/></gdLst>
            <rect l="quarter" t="95250" r="1905000" b="952500"/>
            <pathLst><path><moveTo><pt x="95250" y="190500"/></moveTo><lnTo><pt x="w" y="h"/></lnTo></path>
            <path w="100000" h="100000"><moveTo><pt x="quarter" y="0"/></moveTo><lnTo><pt x="100000" y="100000"/></lnTo></path></pathLst></custGeom>
            """), 240, 160);
        Assert.Empty(result.Warnings);
        Assert.Equal(new double[] { 10, 20 }, result.Paths[0].Commands[0].Values);
        Assert.Equal(new double[] { 240, 160 }, result.Paths[0].Commands[1].Values);
        Assert.Equal(new double[] { 60, 0 }, result.Paths[1].Commands[0].Values.Select(v => Math.Round(v, 8)));
        Assert.Equal(new double[] { 240, 160 }, result.Paths[1].Commands[1].Values.Select(v => Math.Round(v, 8)));
        Assert.Equal(60, result.TextBounds!.X);
        Assert.Equal(10, result.TextBounds.Y);
        Assert.Equal(140, result.TextBounds.Width);
        Assert.Equal(90, result.TextBounds.Height);
        var fixedRect = DrawingMlGeometryReader.Read(Xml("<custGeom><rect l='0' t='0' r='100000' b='100000'/><pathLst><path w='100000' h='100000'><moveTo><pt x='0' y='0'/></moveTo><lnTo><pt x='100000' y='100000'/></lnTo></path></pathLst></custGeom>"), 240, 160);
        Assert.Equal(100000d / 9525, fixedRect.TextBounds!.Width, 8);
        Assert.InRange(fixedRect.TextBounds.Height, 10, 11);
    }

    [Fact(Timeout = 5000)]
    public async Task Invalid_guides_and_excessive_source_complexity_return_located_warning_material()
    {
        await Task.Yield();
        foreach (var body in new[] {
            "<custGeom><gdLst><gd name='cycle' fmla='val cycle'/></gdLst><pathLst><path><moveTo><pt x='cycle' y='0'/></moveTo></path></pathLst></custGeom>",
            "<custGeom><pathLst><path><unsupported/></path></pathLst></custGeom>",
            "<custGeom><gdLst>"+string.Concat(Enumerable.Range(0,8193).Select(i=>$"<gd name='g{i}' fmla='val 1'/>"))+"</gdLst></custGeom>" })
        {
            var result = DrawingMlGeometryReader.Read(Xml(body), 200, 100);
            Assert.Empty(result.Paths); Assert.NotEmpty(result.Warnings);
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// Independent source-coordinate checks for DrawingML extraction. Expected positions below are
/// fixed numbers, never derived using the production metrics or matrix composition helpers.
/// </summary>
public sealed class WorkbookDrawingSceneTests
{
    [Fact]
    public void Three_anchor_kinds_use_stored_column_widths_offsets_and_nonuniform_row_heights()
    {
        var result = Extract(
            OneCell(1, 1, 10, 6, 120, 50, Shape(1, "ONE", width: 120, height: 50)) +
            TwoCell(2, 2, 5, 4, 4, 4, 7, 8, Shape(2, "TWO", width: 121, height: 54)) +
            Absolute(300, 20, 80, 40, Shape(3, "ABSOLUTE", width: 80, height: 40)));
        var scene = Scene(result);

        Assert.Equal("Scene", scene.SheetName);
        Assert.Equal(3, scene.Objects.Count);
        // Stored widths 10,20,5,12 map to 70,140,35,84px, without a second +5px padding.
        Pose(scene.Objects[0], 120, 50, 1, 0, 0, 1, 80, 26);
        Pose(scene.Objects[1], 121, 54, 1, 0, 0, 1, 215, 64);
        Pose(scene.Objects[2], 80, 40, 1, 0, 0, 1, 300, 20);
        Assert.True(scene.Objects[0].MoveWithCells);
        Assert.False(scene.Objects[0].SizeWithCells);
        Assert.True(scene.Objects[1].MoveWithCells);
        Assert.True(scene.Objects[1].SizeWithCells);
        Assert.False(scene.Objects[2].MoveWithCells);
        Assert.False(scene.Objects[2].SizeWithCells);
        Assert.All(scene.Objects, item =>
        {
            Assert.NotNull(item.Source);
            Assert.EndsWith("xl/worksheets/sheet1.xml", item.Source.PartUri, StringComparison.Ordinal);
            Assert.Contains(item.Source.Locators, locator =>
                locator.Kind == "drawing_part" && locator.Value == "/xl/drawings/drawing1.xml");
        });
        Assert.Equal(new[] { "1", "2", "3" }, scene.Objects.Select(ShapeId));
    }

    [Theory]
    [InlineData("", true, true)]
    [InlineData("oneCell", true, false)]
    [InlineData("absolute", false, false)]
    public void Two_cell_edit_as_preserves_behavior_without_changing_the_stored_frame(
        string editAs, bool moves, bool sizes)
    {
        var item = Assert.Single(Scene(Extract(
            TwoCell(2, 2, 5, 4, 4, 4, 7, 8, Shape(1, "EDIT_AS", width: 121, height: 54), editAs))).Objects);

        Pose(item, 121, 54, 1, 0, 0, 1, 215, 64);
        Assert.Equal(moves, item.MoveWithCells);
        Assert.Equal(sizes, item.SizeWithCells);
    }

    [Fact]
    public void Rotation_is_about_the_object_center_instead_of_its_top_left_corner()
    {
        var item = Assert.Single(Scene(Extract(
            Absolute(100, 50, 40, 20, Shape(1, "ROTATED", width: 40, height: 20,
                transformAttributes: "rot=\"5400000\"")))).Objects);

        // The four transformed corners are (130,40), (130,80), (110,40), (110,80).
        Pose(item, 40, 20, 0, 1, -1, 0, 130, 40);
        Point(item, 0, 0, 130, 40);
        Point(item, 40, 20, 110, 80);
    }

    [Fact]
    public void A_sideways_shape_anchored_by_its_quarter_turned_cells_keeps_its_own_width_and_height()
    {
        // Excel anchors a shape rotated 90 degrees by the cells its turned box covers (A1:A4 is 70x110px),
        // while a:ext keeps the unrotated 110x70. The drawn box must stay inside A1:A4.
        var item = Assert.Single(Scene(Extract(
            TwoCell(0, 0, 0, 0, 1, 4, 0, 0, Shape(1, "SIDEWAYS", x: -20, y: 20, width: 110, height: 70,
                transformAttributes: "rot=\"5400000\"")))).Objects);

        Pose(item, 110, 70, 0, 1, -1, 0, 70, 0);
        Point(item, 0, 0, 70, 0);
        Point(item, 110, 70, 0, 110);
    }

    [Fact]
    public void A_flipped_zero_height_connector_keeps_both_endpoints_and_both_arrowheads()
    {
        var line = $"""
            <xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id="7" name="EDGE"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>
            <xdr:spPr><a:xfrm flipH="1"><a:off x="0" y="0"/><a:ext cx="{Emu(100)}" cy="0"/></a:xfrm>
            <a:prstGeom prst="straightConnector1"><a:avLst/></a:prstGeom>
            <a:ln w="{Emu(2)}"><a:solidFill><a:srgbClr val="123456"/></a:solidFill>
            <a:prstDash val="dash"/><a:headEnd type="triangle"/><a:tailEnd type="diamond"/></a:ln></xdr:spPr></xdr:cxnSp>
            """;
        var item = Assert.Single(Scene(Extract(Absolute(10, 30, 100, 0, line))).Objects);

        Pose(item, 100, 0, -1, 0, 0, 1, 110, 30);
        Point(item, 0, 0, 110, 30);
        Point(item, 100, 0, 10, 30);
        Assert.Equal("straightConnector1", item.Preset);
        Assert.Equal("triangle", item.HeadArrow);
        Assert.Equal("diamond", item.TailArrow);
        Assert.Equal("dash", item.Dash);
        Assert.Equal("#123456", item.Stroke);
        Near(2, item.StrokeWidth);
    }

    [Fact]
    public void Nested_group_transforms_and_mixed_picture_z_order_keep_image_source_identity()
    {
        var nested = Group(101, 20, 30, 40, 20, 5, 10, 20, 10,
            Picture(11, "NESTED_IMAGE", 10, 15, 5, 4));
        var group = Group(100, 200, 100, 240, 120, 10, 20, 120, 60,
            Shape(10, "OUTER", 20, 30, 40, 15) + nested +
            Shape(12, "AFTER_NESTED", 50, 30, 10, 10));
        var result = Extract(
            Absolute(0, 0, 500, 200, Shape(1, "BACK", width: 500, height: 200)) +
            Absolute(200, 100, 240, 120, group) +
            Absolute(400, 10, 40, 40, Shape(2, "FRONT", width: 40, height: 40)));
        var objects = Scene(result).Objects;

        Assert.Equal(new[] { "BACK", "OUTER", "NESTED_IMAGE", "AFTER_NESTED", "FRONT" },
            objects.Select(item => item.Text));
        Assert.Equal(new[] { "shape", "shape", "picture", "shape", "shape" },
            objects.Select(item => item.Kind));
        Assert.Equal(objects.Count, objects.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        // A group's scale resizes the child frames; it is not a zoom of captions and line weights.
        Pose(objects[1], 80, 30, 1, 0, 0, 1, 220, 120);
        Pose(objects[2], 20, 16, 1, 0, 0, 1, 240, 140);
        Point(objects[2], 20, 16, 260, 156);
        var picture = objects[2];
        Assert.Equal("/xl/media/image1.png", picture.ImageReference);
        Near(.1, picture.CropLeft);
        Near(.2, picture.CropTop);
        Near(.05, picture.CropRight);
        Near(0, picture.CropBottom);
        Assert.Equal("11", ShapeId(picture));
        Assert.NotNull(picture.SourceNodeId);
        var imageNode = Assert.Single(result.Graph.Nodes, node =>
            node.Kind == NodeKind.Image && node.Id == picture.SourceNodeId);
        Assert.Equal(picture.ImageReference, Assert.IsType<ReferenceNodeContent>(imageNode.Content).Reference);
    }

    [Fact]
    public void Hidden_dimensions_do_not_compress_the_extracted_source_scene_and_hidden_groups_propagate()
    {
        var drawing =
            OneCell(1, 1, 10, 6, 120, 50, Shape(1, "VISIBLE", width: 120, height: 50)) +
            Absolute(200, 100, 240, 120,
                Group(100, 200, 100, 240, 120, 10, 20, 120, 60,
                    Shape(2, "HIDDEN_BY_GROUP", 20, 30, 40, 15), hidden: true)) +
            Absolute(300, 20, 80, 40, Shape(3, "NOT_PRINTABLE", width: 80, height: 40, hidden: true),
                printable: false);
        var baseline = Scene(Extract(drawing));
        var result = Extract(drawing, hideDimensions: true);
        var hidden = Scene(result);

        Assert.Equal(3, hidden.Objects.Count);
        for (var index = 0; index < baseline.Objects.Count; index++)
        {
            Assert.Equal(baseline.Objects[index].Width, hidden.Objects[index].Width);
            Assert.Equal(baseline.Objects[index].Height, hidden.Objects[index].Height);
            Assert.Equal(baseline.Objects[index].Transform, hidden.Objects[index].Transform);
        }
        Pose(hidden.Objects[0], 120, 50, 1, 0, 0, 1, 80, 26);
        Assert.False(hidden.Objects[0].Hidden);
        Assert.True(hidden.Objects[1].Hidden);
        Assert.True(hidden.Objects[1].Printable);
        Assert.True(hidden.Objects[2].Hidden);
        Assert.False(hidden.Objects[2].Printable);
        var hiddenCell = Assert.Single(result.Graph.Nodes, node =>
            node.Kind == NodeKind.Cell && node.Source!.Locators.Any(locator =>
                locator.Kind == "cell_address" && locator.Value == "B2"));
        Assert.True(hiddenCell.Extensions!["hidden_column"].GetBoolean());
        Assert.True(hiddenCell.Extensions["hidden_row"].GetBoolean());
    }

    [Fact]
    public void Extracting_a_scene_does_not_change_source_cell_text_or_formula_values()
    {
        var baseline = Extract(null);
        var annotated = Extract(
            OneCell(0, 0, 0, 0, 300, 120, Shape(1, "ANNOTATION_ONLY", width: 300, height: 120)) +
            Absolute(10, 30, 100, 30, Shape(2, "DRAWING_ONLY", width: 100, height: 30)));

        static object[] Cells(DocumentGraph graph) => graph.Nodes.Where(node => node.Kind == NodeKind.Cell)
            .OrderBy(node => node.Source!.Locators.First(locator => locator.Kind == "cell_address").Value,
                StringComparer.Ordinal)
            .Select(node => (object)(node.Id, Assert.IsType<TextNodeContent>(node.Content).Text,
                node.Extensions!.TryGetValue("display_value", out var display) ? display.GetString() : null,
                node.Extensions.TryGetValue("formula", out var formula) ? formula.GetString() : null))
            .ToArray();
        Assert.Equal(Cells(baseline.Graph), Cells(annotated.Graph));
        Assert.Equal(new[] { "ANNOTATION_ONLY", "DRAWING_ONLY" },
            Scene(annotated).Objects.Select(item => item.Text));
    }

    [Fact]
    public void Theme_luminance_modifier_is_preserved_or_gets_a_located_unsupported_presentation_warning()
    {
        var shape = Shape(1, "TINTED_CAPTION", width: 100, height: 40).Replace("<a:noFill/>",
            "<a:solidFill><a:schemeClr val=\"accent1\"><a:lumMod val=\"50000\"/></a:schemeClr></a:solidFill>", StringComparison.Ordinal);
        var result = Extract(Absolute(10, 20, 100, 40, shape), theme: VisualContractTheme);
        var item = Assert.Single(Scene(result).Objects);
        Assert.Equal("TINTED_CAPTION", item.Text);
        Pose(item, 100, 40, 1, 0, 0, 1, 10, 20);
        // Red has HSL luminance 1/2; multiplying it by 1/2 produces dark red, not unchanged red.
        if (!string.Equals("#800000", item.Fill, StringComparison.OrdinalIgnoreCase))
            VisualContractWarning(result, item, "color");
    }

    [Fact]
    public void Theme_fill_reference_is_preserved_or_gets_a_located_unsupported_presentation_warning()
    {
        var shape = Shape(1, "STYLE_CAPTION", width: 100, height: 40).Replace("<a:noFill/>", "", StringComparison.Ordinal)
            .Replace("</xdr:spPr>", """
                </xdr:spPr><xdr:style><a:lnRef idx="1"><a:schemeClr val="accent2"/></a:lnRef>
                <a:fillRef idx="1"><a:schemeClr val="accent2"/></a:fillRef>
                <a:effectRef idx="0"><a:schemeClr val="accent2"/></a:effectRef>
                <a:fontRef idx="minor"><a:schemeClr val="dk1"/></a:fontRef></xdr:style>
                """, StringComparison.Ordinal);
        var result = Extract(Absolute(10, 20, 100, 40, shape), theme: VisualContractTheme);
        var item = Assert.Single(Scene(result).Objects);
        Assert.Equal("STYLE_CAPTION", item.Text);
        Pose(item, 100, 40, 1, 0, 0, 1, 10, 20);
        if (!string.Equals("#008844", item.Fill, StringComparison.OrdinalIgnoreCase))
            VisualContractWarning(result, item, "style");
    }

    [Fact]
    public void Guide_based_custom_path_is_resolved_or_retained_as_an_explicit_caption_fallback()
    {
        var shape = Shape(1, "GUIDE_CAPTION", width: 100, height: 40)
            .Replace("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>", """
                <a:custGeom><a:avLst/><a:gdLst><a:gd name="mid" fmla="*/ w 1 2"/></a:gdLst><a:ahLst/><a:cxnLst/>
                <a:rect l="0" t="0" r="w" b="h"/><a:pathLst><a:path w="952500" h="381000" fill="none">
                <a:moveTo><a:pt x="0" y="0"/></a:moveTo><a:lnTo><a:pt x="mid" y="h"/></a:lnTo>
                <a:lnTo><a:pt x="w" y="0"/></a:lnTo></a:path></a:pathLst></a:custGeom>
                """, StringComparison.Ordinal);
        var result = Extract(Absolute(10, 20, 100, 40, shape));
        var item = Assert.Single(Scene(result).Objects);
        Assert.Equal("GUIDE_CAPTION", item.Text);
        Pose(item, 100, 40, 1, 0, 0, 1, 10, 20);
        var resolved = item.Paths.Count == 1 && item.Paths[0].Commands.Count == 3 &&
            item.Paths[0].Commands[0].Values.SequenceEqual(new double[] { 0, 0 }) &&
            item.Paths[0].Commands[1].Values.SequenceEqual(new double[] { 50, 40 }) &&
            item.Paths[0].Commands[2].Values.SequenceEqual(new double[] { 100, 0 });
        if (!resolved)
        {
            // Zeroing guide tokens into a plausible but incorrect path is not a source-preserving fallback.
            Assert.Empty(item.Paths);
            VisualContractWarning(result, item, "guide");
        }
    }

    [Fact]
    public void Mixed_rich_caption_style_keeps_each_runs_color_and_emphasis_without_an_unsupported_warning()
    {
        var shape = Shape(1, "RED_BLUE", width: 100, height: 40).Replace("<a:r><a:t>RED_BLUE</a:t></a:r>", """
            <a:r><a:rPr b="1"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></a:rPr><a:t>RED_</a:t></a:r>
            <a:r><a:rPr i="1"><a:solidFill><a:srgbClr val="0000FF"/></a:solidFill></a:rPr><a:t>BLUE</a:t></a:r>
            """, StringComparison.Ordinal);
        var result = Extract(Absolute(10, 20, 100, 40, shape));
        var item = Assert.Single(Scene(result).Objects);
        Assert.Equal("RED_BLUE", item.Text);
        Pose(item, 100, 40, 1, 0, 0, 1, 10, 20);
        var runs = Assert.Single(item.Paragraphs).Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal("RED_", runs[0].Text); Assert.Equal("#FF0000", runs[0].Color); Assert.True(runs[0].Bold); Assert.False(runs[0].Italic);
        Assert.Equal("BLUE", runs[1].Text); Assert.Equal("#0000FF", runs[1].Color); Assert.False(runs[1].Bold); Assert.True(runs[1].Italic);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("XlsxDrawingPresentationUnsupported:", StringComparison.Ordinal) &&
            warning.Contains(item.Id, StringComparison.Ordinal) && warning.Contains("rich", StringComparison.OrdinalIgnoreCase));
    }

    private static void VisualContractWarning(XlsxExtractionResult result, WorkbookDrawingObject item, string feature)
    {
        Assert.Contains(result.Warnings, warning =>
            warning.StartsWith("XlsxDrawingPresentationUnsupported:", StringComparison.Ordinal) &&
            warning.Contains(item.Id, StringComparison.Ordinal) &&
            warning.Contains(feature, StringComparison.OrdinalIgnoreCase));
    }

    private const string VisualContractTheme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Contract"><a:themeElements>
        <a:clrScheme name="Contract"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>
        <a:dk2><a:srgbClr val="222222"/></a:dk2><a:lt2><a:srgbClr val="EEEEEE"/></a:lt2>
        <a:accent1><a:srgbClr val="FF0000"/></a:accent1><a:accent2><a:srgbClr val="008844"/></a:accent2>
        <a:accent3><a:srgbClr val="333333"/></a:accent3><a:accent4><a:srgbClr val="444444"/></a:accent4>
        <a:accent5><a:srgbClr val="555555"/></a:accent5><a:accent6><a:srgbClr val="666666"/></a:accent6>
        <a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
        <a:fontScheme name="Contract"><a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
        <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
        <a:fmtScheme name="Contract"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
        <a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
        <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
        <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>
        </a:themeElements></a:theme>
        """;
    private static WorkbookDrawingScene Scene(XlsxExtractionResult extraction)
    {
        Assert.NotNull(extraction.Graph.FormatExtensions);
        Assert.True(extraction.Graph.FormatExtensions.TryGetValue(WorkbookDrawingSceneJson.ExtensionKey, out var json));
        var scenes = WorkbookDrawingSceneJson.Read(json);
        Assert.Equal("sheet-Scene", Assert.Single(scenes).Key);
        return scenes["sheet-Scene"];
    }

    private static string ShapeId(WorkbookDrawingObject item) =>
        Assert.Single(item.Source!.Locators, locator => locator.Kind == "shape_id").Value;

    private static void Pose(WorkbookDrawingObject item, double width, double height,
        double a, double b, double c, double d, double e, double f)
    {
        Near(width, item.Width); Near(height, item.Height);
        Near(a, item.Transform.A); Near(b, item.Transform.B);
        Near(c, item.Transform.C); Near(d, item.Transform.D);
        Near(e, item.Transform.E); Near(f, item.Transform.F);
    }

    private static void Point(WorkbookDrawingObject item, double x, double y, double expectedX, double expectedY)
    {
        var point = item.Transform.Point(x, y);
        Near(expectedX, point.X); Near(expectedY, point.Y);
    }

    private static void Near(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, .000001);

    private static string Emu(double pixels) =>
        Math.Round(pixels * 9525).ToString(CultureInfo.InvariantCulture);

    private static string Marker(string name, int column, int row, double x, double y) =>
        $"<xdr:{name}><xdr:col>{column}</xdr:col><xdr:colOff>{Emu(x)}</xdr:colOff>" +
        $"<xdr:row>{row}</xdr:row><xdr:rowOff>{Emu(y)}</xdr:rowOff></xdr:{name}>";

    private static string OneCell(int column, int row, double x, double y, double width, double height, string content) =>
        "<xdr:oneCellAnchor>" + Marker("from", column, row, x, y) +
        $"<xdr:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/>{content}<xdr:clientData/></xdr:oneCellAnchor>";

    private static string TwoCell(int column, int row, double x, double y,
        int endColumn, int endRow, double endX, double endY, string content, string editAs = "") =>
        $"<xdr:twoCellAnchor{(editAs.Length == 0 ? "" : " editAs=\"" + editAs + "\"")}>" +
        Marker("from", column, row, x, y) + Marker("to", endColumn, endRow, endX, endY) +
        content + "<xdr:clientData/></xdr:twoCellAnchor>";

    private static string Absolute(double x, double y, double width, double height, string content, bool printable = true) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><xdr:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/>" +
        content + $"<xdr:clientData fPrintsWithSheet=\"{(printable ? "1" : "0")}\"/></xdr:absoluteAnchor>";

    private static string Transform(double x, double y, double width, double height, string attributes = "") =>
        $"<a:xfrm {attributes}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></a:xfrm>";

    private static string Shape(int id, string text, double x = 0, double y = 0,
        double width = 40, double height = 20, string transformAttributes = "", bool hidden = false) => $"""
        <xdr:sp><xdr:nvSpPr><xdr:cNvPr id="{id}" name="SHAPE_{id}" hidden="{(hidden ? "1" : "0")}"/><xdr:cNvSpPr/></xdr:nvSpPr>
        <xdr:spPr>{Transform(x, y, width, height, transformAttributes)}<a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
        <a:noFill/><a:ln><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:ln></xdr:spPr>
        <xdr:txBody><a:bodyPr/><a:p><a:r><a:t>{SecurityElement.Escape(text)}</a:t></a:r></a:p></xdr:txBody></xdr:sp>
        """;

    private static string Group(int id, double x, double y, double width, double height,
        double childX, double childY, double childWidth, double childHeight, string content, bool hidden = false) => $"""
        <xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id="{id}" name="GROUP_{id}" hidden="{(hidden ? "1" : "0")}"/><xdr:cNvGrpSpPr/></xdr:nvGrpSpPr>
        <xdr:grpSpPr><a:xfrm><a:off x="{Emu(x)}" y="{Emu(y)}"/><a:ext cx="{Emu(width)}" cy="{Emu(height)}"/>
        <a:chOff x="{Emu(childX)}" y="{Emu(childY)}"/><a:chExt cx="{Emu(childWidth)}" cy="{Emu(childHeight)}"/></a:xfrm></xdr:grpSpPr>
        {content}</xdr:grpSp>
        """;

    private static string Picture(int id, string description, double x, double y, double width, double height) => $"""
        <xdr:pic><xdr:nvPicPr><xdr:cNvPr id="{id}" name="PICTURE_{id}" descr="{SecurityElement.Escape(description)}"/><xdr:cNvPicPr/></xdr:nvPicPr>
        <xdr:blipFill><a:blip r:embed="rImage"/><a:srcRect l="10000" t="20000" r="5000" b="0"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>
        <xdr:spPr>{Transform(x, y, width, height)}<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>
        """;

    private static XlsxExtractionResult Extract(string? drawing, bool hideDimensions = false, string? theme = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string path, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Write("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="png" ContentType="image/png"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                </Types>
                """);
            Write("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rWorkbook"
                Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Write("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Scene" sheetId="1" r:id="rSheet"/></sheets></workbook>
                """);
            Write("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rSheet"
                Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            Write("xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><cellXfs count="1"><xf fontId="0"/></cellXfs></styleSheet>
                """);
            if (theme is not null) Write("xl/theme/theme1.xml", theme);
            var hidden = hideDimensions ? " hidden=\"1\"" : "";
            Write("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheetFormatPr defaultColWidth="8.43" defaultRowHeight="15"/>
                <cols><col min="1" max="1" width="10" customWidth="1"/><col min="2" max="2" width="20" customWidth="1"{hidden}/>
                <col min="3" max="3" width="5" customWidth="1"/><col min="4" max="4" width="12" customWidth="1"/></cols>
                <sheetData><row r="1" ht="15" customHeight="1"><c r="A1" t="inlineStr"><is><t>SOURCE_A</t></is></c></row>
                <row r="2" ht="30" customHeight="1"{hidden}><c r="B2" t="inlineStr"><is><t>SOURCE_B</t></is></c></row>
                <row r="3" ht="22.5" customHeight="1"><c r="C3"><f>1+6</f><v>7</v></c></row>
                <row r="4" ht="15" customHeight="1"><c r="D4" t="inlineStr"><is><t>SOURCE_D</t></is></c></row></sheetData>
                {(drawing is null ? "" : "<drawing r:id=\"rDrawing\"/>")}</worksheet>
                """);
            if (drawing is not null)
            {
                Write("xl/worksheets/_rels/sheet1.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDrawing"
                    Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/></Relationships>
                    """);
                Write("xl/drawings/drawing1.xml", $"""
                    <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing"
                    xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                    {drawing}</xdr:wsDr>
                    """);
                Write("xl/drawings/_rels/drawing1.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rImage"
                    Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/></Relationships>
                    """);
                using var image = archive.CreateEntry("xl/media/image1.png").Open();
                image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1u8AAAAASUVORK5CYII="));
            }
        }
        stream.Position = 0;
        return new XlsxAdapter().Extract(stream);
    }
}

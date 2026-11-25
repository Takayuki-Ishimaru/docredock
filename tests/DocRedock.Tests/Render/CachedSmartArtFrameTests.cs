using System.Text;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

/// <summary>Expected frames are fixed numbers worked out by hand, not with the production matrix helpers.</summary>
public sealed class CachedSmartArtFrameTests
{
    private const string D = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DS = "http://schemas.microsoft.com/office/drawing/2008/diagram";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Fact]
    public void A_frame_twice_as_wide_as_the_saved_drawing_resizes_shape_frames_without_stretching_them()
    {
        // Saved drawing 200x100 px; the frame is 400x100 at (100,100): twice as wide, same height.
        var objects = Expand(Frame(400, 100), Drawing("<dsp:grpSpPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/></a:xfrm></dsp:grpSpPr>"));

        Assert.Equal(new[] { "PLAIN", "TILTED" }, objects.Select(o => o.Text));
        // Centre (60,35) -> (120,35) -> page (220,135): twice as wide, same height, no change of orientation.
        Pose(objects[0], 200, 50, 1, 0, 0, 1, 120, 110);
        // The corner radius stays circular (16.667% of the 50 px height), the caption and the line keep their size.
        var corner = objects[0].Paths[0].Commands.First(c => c.Kind == "A");
        Near(8.3335, corner.Values[0]); Near(8.3335, corner.Values[1]);
        Near(24 * 96 / 72d, objects[0].FontSize); Near(2, objects[0].StrokeWidth);
        // Centre (130,30) -> (260,30) -> page (360,130); a shape turned 45 degrees takes the scales on its swapped
        // axes (60x20 -> 60x40) and its turn stays rigid, with no shear.
        Pose(objects[1], 60, 40, .7071067812, .7071067812, -.7071067812, .7071067812, 352.9289321881, 94.6446609407);
        Centre(objects[1], 360, 130);
    }

    [Fact]
    public void A_frame_of_the_saved_size_keeps_the_saved_geometry()
    {
        var objects = Expand(Frame(200, 100), Drawing("<dsp:grpSpPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='1905000' cy='952500'/></a:xfrm></dsp:grpSpPr>"));

        Pose(objects[0], 100, 50, 1, 0, 0, 1, 110, 110);
        Near(8.3335, objects[0].Paths[0].Commands.First(c => c.Kind == "A").Values[0]);
        Near(24 * 96 / 72d, objects[0].FontSize); Near(2, objects[0].StrokeWidth);
        Pose(objects[1], 60, 20, .7071067812, .7071067812, -.7071067812, .7071067812, 215.8578643763, 101.7157287525);
        Centre(objects[1], 230, 130);
    }

    [Fact]
    public void A_drawing_without_a_saved_extent_is_measured_from_its_shapes_and_resized_the_same_way()
    {
        // Without an extent, the 100x50 shape at (10,10) alone spans 100 px: a 200 px wide frame doubles it.
        var drawing = Drawing("", tilted: false);
        var objects = Expand(Frame(200, 50), drawing);
        Pose(objects[0], 200, 50, 1, 0, 0, 1, 100, 100);
        Near(24 * 96 / 72d, objects[0].FontSize);
    }

    private static WorkbookDrawingObject Frame(double width, double height) => new("frame", "shape", width, height, new(E: 100, F: 100));

    private static IReadOnlyList<WorkbookDrawingObject> Expand(WorkbookDrawingObject frame, string drawing)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["/ppt/slides/_rels/slide1.xml.rels"] = Bytes(Rels("rData", "../diagrams/data1.xml", R + "/diagramData")),
            ["/ppt/diagrams/data1.xml"] = Bytes("<dgm:dataModel xmlns:dgm='" + D + "' xmlns:dsp='" + DS + "'><dgm:extLst><dsp:dataModelExt relId='rDraw'/></dgm:extLst></dgm:dataModel>"),
            ["/ppt/diagrams/_rels/data1.xml.rels"] = Bytes(Rels("rDraw", "drawing1.xml", "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing")),
            ["/ppt/diagrams/drawing1.xml"] = Bytes(drawing),
        };
        var ids = new XmlDocument { XmlResolver = null };
        ids.LoadXml("<dgm:relIds xmlns:dgm='" + D + "' xmlns:r='" + R + "' r:dm='rData'/>");
        var warnings = new List<string>();
        var result = CachedSmartArtScene.Expand(ids.DocumentElement!, frame, "/ppt/slides/slide1.xml", "pptx",
            uri => files.GetValueOrDefault(uri), null, warnings, CancellationToken.None);
        Assert.Empty(warnings);
        return result!;
    }

    private static string Drawing(string rootProperties, bool tilted = true) =>
        "<dsp:drawing xmlns:dsp='" + DS + "' xmlns:a='" + A + "'><dsp:spTree>" + rootProperties +
        Shape(3, "PLAIN", "<a:xfrm><a:off x='95250' y='95250'/><a:ext cx='952500' cy='476250'/></a:xfrm>", "roundRect") +
        (tilted ? Shape(4, "TILTED", "<a:xfrm rot='2700000'><a:off x='952500' y='190500'/><a:ext cx='571500' cy='190500'/></a:xfrm>", "rect") : "") +
        "</dsp:spTree></dsp:drawing>";
    private static string Shape(int id, string text, string frame, string preset) =>
        $"<dsp:sp><dsp:nvSpPr><dsp:cNvPr id='{id}' name='s'/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr>{frame}<a:prstGeom prst='{preset}'><a:avLst/></a:prstGeom>" +
        "<a:solidFill><a:srgbClr val='4472C4'/></a:solidFill><a:ln w='19050'><a:solidFill><a:srgbClr val='000000'/></a:solidFill></a:ln></dsp:spPr>" +
        $"<dsp:txBody><a:bodyPr/><a:p><a:r><a:rPr sz='2400'/><a:t>{text}</a:t></a:r></a:p></dsp:txBody></dsp:sp>";
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Rels(string id, string target, string type) =>
        "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='" + id + "' Type='" + type + "' Target='" + target + "'/></Relationships>";
    private static void Pose(WorkbookDrawingObject item, double width, double height, double a, double b, double c, double d, double e, double f)
    {
        Near(width, item.Width); Near(height, item.Height);
        Near(a, item.Transform.A); Near(b, item.Transform.B); Near(c, item.Transform.C); Near(d, item.Transform.D);
        Near(e, item.Transform.E); Near(f, item.Transform.F);
    }
    private static void Centre(WorkbookDrawingObject item, double x, double y)
    {
        var point = item.Transform.Point(item.Width / 2, item.Height / 2);
        Near(x, point.X); Near(y, point.Y);
    }
    private static void Near(double expected, double actual, double tolerance = .0001) => Assert.InRange(Math.Abs(expected - actual), 0, tolerance);
}

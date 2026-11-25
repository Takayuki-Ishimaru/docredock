using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

/// <summary>Expected frames are fixed numbers worked out by hand, not with the production matrix helpers.</summary>
public sealed class GroupedDrawingFrameTests
{
    private const string Ns = "xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'";

    [Fact]
    public void A_resized_group_resizes_child_frames_without_stretching_captions_lines_or_rotated_outlines()
    {
        // Group 400x100 at (100,100) over a 200x100 child space: twice as wide, same height.
        var objects = Read(Group(Frame(100, 100, 400, 100, "", "<a:chOff x='0' y='0'/><a:chExt cx='1905000' cy='952500'/>"),
            Shape(2, "PLAIN", Frame(10, 10, 100, 50), "roundRect") +
            Shape(3, "TILTED", Frame(100, 20, 60, 20, "rot='1800000'")) +
            Shape(4, "SIDEWAYS", Frame(100, 20, 60, 20, "rot='5400000'"))));

        Assert.Equal(new[] { "PLAIN", "TILTED", "SIDEWAYS" }, objects.Select(o => o.Text));
        Pose(objects[0], 200, 50, 1, 0, 0, 1, 120, 110);
        // The preset is resolved for the resized frame: the corner radius stays circular (16.667% of 50).
        var corner = objects[0].Paths[0].Commands.First(c => c.Kind == "A");
        Near(8.3335, corner.Values[0]); Near(8.3335, corner.Values[1]);
        // Centre (130,30) -> (260,30) -> page (360,130); the rotation stays rigid, with no shear.
        Pose(objects[1], 120, 20, .8660254038, .5, -.5, .8660254038, 313.0384757729, 91.3397459622);
        Centre(objects[1], 360, 130);
        // A child on its side takes the horizontal scale on its own height.
        Pose(objects[2], 60, 40, 0, 1, -1, 0, 380, 100);
        Centre(objects[2], 360, 130);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Flat_preset_shapes_keep_a_collapsed_outline_instead_of_losing_their_geometry(double width, double height)
    {
        foreach (var preset in DrawingMlGeometryReader.PresetNames)
        {
            var document = new XmlDocument { XmlResolver = null }; document.LoadXml($"<prstGeom prst='{preset}'/>");
            var result = DrawingMlGeometryReader.Read(document.DocumentElement!, width, height);
            Assert.True(result.Warnings.Count == 0, preset + ": " + string.Join(";", result.Warnings));
            Assert.NotEmpty(result.Paths);
            Assert.All(result.Paths.SelectMany(p => p.Commands).Where(c => c.Kind is "M" or "L"), command =>
                Assert.Equal(0d, width == 0 ? command.Values[0] : command.Values[1], 6));
        }
    }

    private static IReadOnlyList<WorkbookDrawingObject> Read(string content)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml($"<p:spTree {Ns}>{content}</p:spTree>");
        var warnings = new List<string>();
        var objects = DrawingMlSceneReader.Read(document.DocumentElement!, "pptx", "/ppt/slides/slide1.xml", null, warnings: warnings);
        Assert.Empty(warnings);
        return objects;
    }
    private static string Emu(double pixels) => Math.Round(pixels * 9525).ToString(CultureInfo.InvariantCulture);
    private static string Frame(double x, double y, double width, double height, string attributes = "", string child = "") =>
        $"<a:xfrm {attributes}><a:off x='{Emu(x)}' y='{Emu(y)}'/><a:ext cx='{Emu(width)}' cy='{Emu(height)}'/>{child}</a:xfrm>";
    private static string Shape(int id, string text, string frame, string preset = "rect") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id='{id}' name='s'/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>{frame}<a:prstGeom prst='{preset}'><a:avLst/></a:prstGeom></p:spPr>" +
        $"<p:txBody><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";
    private static string Group(string frame, string content) =>
        $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id='1' name='g'/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>{frame}</p:grpSpPr>{content}</p:grpSp>";
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
    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, .0001);
}

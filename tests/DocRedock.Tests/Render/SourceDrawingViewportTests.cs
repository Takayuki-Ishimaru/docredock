using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class SourceDrawingViewportTests
{
    [Fact]
    public void Negative_source_extent_roundtrips_without_rewriting_object_coordinates_and_is_visible()
    {
        var drawing = new WorkbookDrawingObject("p", "shape", 100, 50, new(0, -1, -1, 0, 75, 75));
        var source = new SourceDrawingScene("word", 100, 100, [drawing]) { OriginY = -25, CoordinateSpace = "inline" };
        var json = SourceDrawingSceneJson.Write(new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["body"] = [source] });
        var restored = Assert.Single(SourceDrawingSceneJson.Read(json)["body"]);
        Assert.Equal(-25, restored.OriginY);
        Assert.Equal(drawing.Transform, Assert.Single(restored.Objects).Transform);
        var html = ReadableHtmlWriter.WriteDocument(new("d", "en", DocumentFormatKind.Docx, [new ReadableSourceDrawingScene(restored)])).Html;
        Assert.Contains("data-source-origin-y=\"-25\"", html);
        Assert.Matches(@"transform:translate\(-?0px,25px\)", html);
        Assert.Contains("transform:matrix(0,-1,-1,0,75,75)", html);
    }

    [Theory]
    [InlineData(-100000001)]
    [InlineData(100000001)]
    public void Unbounded_viewport_origins_are_rejected_with_a_notice(double origin)
    {
        var scene = new SourceDrawingScene("bad", 100, 100, []) { OriginY = origin };
        var json = SourceDrawingSceneJson.Write(new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["body"] = [scene] });
        var warnings = new List<string>();
        Assert.Empty(SourceDrawingSceneJson.Read(json, warnings)["body"]);
        Assert.NotEmpty(warnings);
    }
}

using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

/// <summary>A scene's background and table styles are read from the graph's JSON under the same bounds as its objects.</summary>
public sealed class SourceDrawingSceneDecorationJsonTests
{
    private static readonly WorkbookDrawingObject[] Objects = [new("ppt/slides/slide1.xml:1:2", "shape", 10, 10, WorkbookDrawingTransform.Identity)];

    [Fact]
    public void Background_and_table_styles_survive_the_json_round_trip()
    {
        var scene = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        {
            Background = new DrawingPaint { Kind = "linear", Stops = [new(0, "#C00000"), new(1, "#FFFFFF")], AngleDegrees = 90 },
            BackgroundImageReference = "/ppt/media/image1.png",
            TableStyles = new Dictionary<string, SourceDrawingTableStyle>
            {
                ["ppt/slides/slide1.xml:2:5"] = new([[new() { Fill = "#1F4E79", Color = "#FFFFFF", FontSize = 18, Bold = true, Bottom = new(4, "#000000") }, null]])
                { ColumnWidths = [200, 300], RowHeights = [40] },
            },
        }, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal("linear", scene.Background?.Kind);
        Assert.Equal("#C00000", scene.Background?.Stops[0].Color);
        Assert.Equal("/ppt/media/image1.png", scene.BackgroundImageReference);
        var table = Assert.Single(scene.TableStyles!).Value;
        Assert.Equal([200d, 300d], table.ColumnWidths);
        Assert.Equal("#1F4E79", table.Cells[0][0]?.Fill);
        Assert.True(table.Cells[0][0]?.Bold);
        Assert.Equal(new SourceDrawingTableBorder(4, "#000000"), table.Cells[0][0]?.Bottom);
        Assert.Null(table.Cells[0][1]);
    }

    [Fact]
    public void An_invalid_background_or_table_style_is_dropped_with_a_warning_and_the_scene_stays()
    {
        var background = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        { Background = new DrawingPaint { Kind = "solid", Color = "#FFFFFF", Opacity = 7 }, BackgroundImageReference = "/ppt/media/image1.png" }, out var warnings);
        Assert.Null(background.Background);
        Assert.Null(background.BackgroundImageReference);
        Assert.Contains(warnings, warning => warning.StartsWith("Invalid source scene background", StringComparison.Ordinal));
        Assert.Single(background.Objects);

        var styles = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        {
            TableStyles = new Dictionary<string, SourceDrawingTableStyle> { ["table"] = new([[new() { FontSize = -1, TextAlign = "sideways" }]]) },
        }, out warnings);
        Assert.Null(styles.TableStyles);
        Assert.Contains(warnings, warning => warning.StartsWith("Invalid source table styles", StringComparison.Ordinal));
        Assert.Single(styles.Objects);
    }

    [Fact]
    public void The_crop_and_place_of_a_background_picture_and_the_paragraphs_of_a_cell_survive_the_json_round_trip()
    {
        var scene = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        {
            BackgroundImageReference = "/ppt/media/image1.png", BackgroundImageCrop = new(.25, 0, .25, 0), BackgroundImageStretch = new(0, -.1, 0, .1),
            TableStyles = new Dictionary<string, SourceDrawingTableStyle>
            {
                ["table"] = new([[new()
                {
                    FillPaint = new DrawingPaint { Kind = "pattern", Pattern = "pct50", Color = "#FF0000", BackColor = "#FFFF00" }, TextAlign = "center",
                    ParagraphStyles = [new() { TextAlign = "center" }, new() { TextAlign = "right", MarginLeft = 36, Indent = 10 }],
                }]]),
            },
        }, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(new DrawingInsets(.25, 0, .25, 0), scene.BackgroundImageCrop);
        Assert.Equal(new DrawingInsets(0, -.1, 0, .1), scene.BackgroundImageStretch);
        var cell = scene.TableStyles!["table"].Cells[0][0]!;
        Assert.Equal("pct50", cell.FillPaint?.Pattern);
        Assert.Equal([new SourceDrawingTableParagraph { TextAlign = "center" }, new SourceDrawingTableParagraph { TextAlign = "right", MarginLeft = 36, Indent = 10 }], cell.ParagraphStyles);

        // A scene written before these values existed reads as before.
        var earlier = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects) { BackgroundImageReference = "/ppt/media/image1.png" }, out warnings);
        Assert.Empty(warnings);
        Assert.Null(earlier.BackgroundImageCrop);
        Assert.Null(earlier.BackgroundImageStretch);

        var crop = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        { BackgroundImageReference = "/ppt/media/image1.png", BackgroundImageCrop = new(2000, 0, 0, 0) }, out warnings);
        Assert.Null(crop.BackgroundImageReference);
        Assert.Null(crop.BackgroundImageCrop);
        Assert.Contains(warnings, warning => warning.StartsWith("Invalid source scene background", StringComparison.Ordinal));

        var paragraphs = Read(new SourceDrawingScene("ppt/slides/slide1.xml", 960, 720, Objects)
        {
            TableStyles = new Dictionary<string, SourceDrawingTableStyle> { ["table"] = new([[new() { ParagraphStyles = [new() { TextAlign = "sideways" }] }]]) },
        }, out warnings);
        Assert.Null(paragraphs.TableStyles);
        Assert.Contains(warnings, warning => warning.StartsWith("Invalid source table styles", StringComparison.Ordinal));
    }

    private static SourceDrawingScene Read(SourceDrawingScene scene, out List<string> warnings)
    {
        warnings = [];
        var json = SourceDrawingSceneJson.Write(new Dictionary<string, IReadOnlyList<SourceDrawingScene>> { ["slide1"] = [scene] });
        return Assert.Single(Assert.Single(SourceDrawingSceneJson.Read(json, warnings)).Value);
    }
}

using System.Globalization;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class WorkbookDrawingPrimitiveTests
{
    private static readonly string[] LineFamily =
    [
        "line", "straightConnector1", "bentConnector2", "bentConnector3", "bentConnector4", "bentConnector5",
        "curvedConnector2", "curvedConnector3", "curvedConnector4", "curvedConnector5",
    ];

    private static readonly string[] ShapeFamily =
    [
        "rect", "roundRect", "ellipse", "triangle", "rtTriangle", "diamond", "parallelogram", "trapezoid",
        "hexagon", "octagon", "chevron", "pentagon", "flowChartProcess", "flowChartDecision",
        "flowChartTerminator", "flowChartInputOutput", "rightArrow", "leftArrow", "upArrow", "downArrow",
        "leftRightArrow", "upDownArrow",
    ];

    private static WorkbookDrawingObject Obj(string preset, double w = 120, double h = 80,
        IReadOnlyList<WorkbookDrawingPath>? paths = null, IReadOnlyDictionary<string, double>? adj = null)
        => new("d1", "shape", w, h, WorkbookDrawingTransform.Identity)
        {
            Preset = preset,
            Paths = paths ?? [],
            Adjustments = adj ?? new Dictionary<string, double>(),
        };

    private static WorkbookDrawingPath Cmds(params WorkbookDrawingCommand[] commands) => new(commands);
    private static WorkbookDrawingCommand Cmd(string kind, params double[] values) => new(kind, values);

    private static void AssertValidPath(string? path)
    {
        Assert.NotNull(path);
        Assert.Matches(@"^M -?\d+(\.\d{1,4})?( -?\d+(\.\d{1,4})?)?( (L( -?\d+(\.\d{1,4})?){2}|Q( -?\d+(\.\d{1,4})?){4}|C( -?\d+(\.\d{1,4})?){6}|A( -?\d+(\.\d{1,4})?){2} -?\d+(\.\d{1,4})? [01] [01]( -?\d+(\.\d{1,4})?){2}|Z))*$", path!);
    }

    [Theory]
    [MemberData(nameof(AllPresetsData))]
    public void SupportedPreset_EmitsValidPathInLocalBox(string preset)
    {
        var path = WorkbookDrawingPrimitives.Path(Obj(preset));
        AssertValidPath(path);
        foreach (var (x, y) in Coords(path!))
        {
            Assert.InRange(x, -0.0001, 120.0001);
            Assert.InRange(y, -0.0001, 80.0001);
        }
    }

    public static TheoryData<string> AllPresetsData { get; } =
        new(ShapeFamily.Concat(LineFamily).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(AllPresetsData))]
    public void ClosedShapesEndWithZ_ConnectorsStayOpen(string preset)
    {
        var path = WorkbookDrawingPrimitives.Path(Obj(preset))!;
        if (LineFamily.Contains(preset))
        {
            Assert.DoesNotContain("Z", path, StringComparison.Ordinal);
            Assert.DoesNotContain("z", path, StringComparison.Ordinal);
        }
        else
        {
            Assert.EndsWith(" Z", path, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("line")]
    [InlineData("straightConnector1")]
    [InlineData("bentConnector2")]
    [InlineData("bentConnector3")]
    [InlineData("bentConnector4")]
    [InlineData("bentConnector5")]
    [InlineData("curvedConnector2")]
    [InlineData("curvedConnector3")]
    [InlineData("curvedConnector4")]
    [InlineData("curvedConnector5")]
    public void Connector_IsOpenWithOriginalEndpointsForArrowHeadsAndTails(string preset)
    {
        var path = WorkbookDrawingPrimitives.Path(Obj(preset))!;
        Assert.DoesNotContain("Z", path, StringComparison.Ordinal);
        var coords = Coords(path);
        Assert.Equal((0d, 0d), coords[0]); // tail anchor stays at the original start point
        Assert.Equal((120d, 80d), coords[^1]); // head anchor stays at the original end point
    }

    [Theory]
    [InlineData("line")]
    [InlineData("straightConnector1")]
    [InlineData("bentConnector2")]
    [InlineData("bentConnector3")]
    [InlineData("curvedConnector2")]
    [InlineData("curvedConnector3")]
    [InlineData("curvedConnector4")]
    [InlineData("curvedConnector5")]
    public void Connector_WithOneZeroDimension_StillSupported(string preset)
    {
        Assert.NotNull(WorkbookDrawingPrimitives.Path(Obj(preset, 0, 40)));
        Assert.NotNull(WorkbookDrawingPrimitives.Path(Obj(preset, 30, 0)));
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj(preset, 0, 0)));
    }

    [Theory]
    [InlineData("rect")]
    [InlineData("roundRect")]
    [InlineData("ellipse")]
    [InlineData("rightArrow")]
    [InlineData("flowChartTerminator")]
    public void ShapeWithZeroDimension_ReturnsNull(string preset)
    {
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj(preset, 0, 50)));
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj(preset, 50, 0)));
    }

    [Theory]
    [InlineData("bentConnector3", "M 0 0 L 60 0 L 60 80 L 120 80")]
    [InlineData("bentConnector2", "M 0 0 L 120 0 L 120 80")]
    [InlineData("straightConnector1", "M 0 0 L 120 80")]
    [InlineData("line", "M 0 0 L 120 80")]
    [InlineData("curvedConnector2", "M 0 0 C 60 0 120 40 120 80")]
    [InlineData("curvedConnector3", "M 0 0 C 30 0 60 20 60 40 C 60 60 90 80 120 80")]
    public void Connector_ExactOpenGeometry(string preset, string expected)
        => Assert.Equal(expected, WorkbookDrawingPrimitives.Path(Obj(preset)));

    [Theory]
    [InlineData("rect", "M 0 0 L 120 0 L 120 80 L 0 80 Z")]
    [InlineData("flowChartProcess", "M 0 0 L 120 0 L 120 80 L 0 80 Z")]
    [InlineData("triangle", "M 60 0 L 120 80 L 0 80 Z")]
    [InlineData("rtTriangle", "M 0 0 L 0 80 L 120 80 Z")]
    [InlineData("diamond", "M 60 0 L 120 40 L 60 80 L 0 40 Z")]
    [InlineData("flowChartDecision", "M 60 0 L 120 40 L 60 80 L 0 40 Z")]
    [InlineData("ellipse", "M 0 40 A 60 40 0 1 1 120 40 A 60 40 0 1 1 0 40 Z")]
    [InlineData("pentagon", "M 0.0001 30.5572 L 60 0 L 119.9999 30.5572 L 97.082 79.9998 L 22.918 79.9998 Z")]
    [InlineData("rightArrow", "M 0 20 L 80 20 L 80 0 L 120 40 L 80 80 L 80 60 L 0 60 Z")]
    [InlineData("leftArrow", "M 40 0 L 40 20 L 120 20 L 120 60 L 40 60 L 40 80 L 0 40 Z")]
    [InlineData("upArrow", "M 60 0 L 120 40 L 90 40 L 90 80 L 30 80 L 30 40 L 0 40 Z")]
    [InlineData("downArrow", "M 60 80 L 0 40 L 30 40 L 30 0 L 90 0 L 90 40 L 120 40 Z")]
    public void PresetGeometry_Exact(string preset, string expected)
        => Assert.Equal(expected, WorkbookDrawingPrimitives.Path(Obj(preset)));

    [Fact]
    public void RightArrow_DefaultShaftHasHalfHeightThickness()
    {
        var path = WorkbookDrawingPrimitives.Path(Obj("rightArrow"))!;
        var coords = Coords(path);
        Assert.Equal((0d, 20d), coords[0]); // shaft top edge at (h - h/2)/2, not collapsed to h/2
        Assert.Equal((0d, 60d), coords[^1]);
    }

    [Theory]
    [InlineData("roundRect", "adj", 100000, "M 40 0 L 80 0 A 40 40 0 0 1 120 40 L 120 40 A 40 40 0 0 1 80 80 L 40 80 A 40 40 0 0 1 0 40 L 0 40 A 40 40 0 0 1 40 0 Z")]
    [InlineData("roundRect", "adj", 0, "M 0 0 L 120 0 L 120 80 L 0 80 Z")]
    [InlineData("chevron", "adj", 20000, "M 0 0 L 104 0 L 120 40 L 104 80 L 0 80 L 16 40 Z")]
    [InlineData("rightArrow", "adj1", 25000, "M 0 30 L 80 30 L 80 0 L 120 40 L 80 80 L 80 50 L 0 50 Z")]
    [InlineData("rightArrow", "adj2", 25000, "M 0 20 L 100 20 L 100 0 L 120 40 L 100 80 L 100 60 L 0 60 Z")]
    public void AdjustmentVal_ChangesGeometry(string preset, string key, double val, string expected)
        => Assert.Equal(expected, WorkbookDrawingPrimitives.Path(Obj(preset, adj: new Dictionary<string, double> { [key] = val })));

    [Fact]
    public void RoundRect_DefaultRadiusIsOneSixthOfMinDimension()
    {
        var path = WorkbookDrawingPrimitives.Path(Obj("roundRect"))!;
        Assert.StartsWith("M 13.3336 0 L 106.6664 0 A 13.3336 13.3336 0 0 1 120 13.3336", path, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomCommands_CombinePreservingClose()
    {
        var item = Obj("rect", paths: [Cmds(Cmd("M", 1, 2), Cmd("L", 3, 4), Cmd("Q", 5, 6, 7, 8),
            Cmd("C", 1, 2, 3, 4, 5, 6), Cmd("A", 7, 8, 9, 0, 1, 10, 11), Cmd("Z"))]);
        Assert.Equal("M 1 2 L 3 4 Q 5 6 7 8 C 1 2 3 4 5 6 A 7 8 9 0 1 10 11 Z", WorkbookDrawingPrimitives.Path(item));
    }

    [Theory]
    [InlineData("M", "1")]
    [InlineData("L", "1,2,3")]
    [InlineData("Q", "1,2,3")]
    [InlineData("C", "1,2,3,4,5")]
    [InlineData("A", "1,2,3,0,1,4")]
    [InlineData("Z", "1,2")]
    [InlineData("S", "1,2")]
    [InlineData("m", "1,2")]
    public void CustomCommand_InvalidKindOrArity_ReturnsNull(string kind, string values)
    {
        var parsed = values.Length == 0 ? Array.Empty<double>() : values.Split(',').Select(double.Parse).ToArray();
        var item = Obj("rect", paths: [Cmds(new WorkbookDrawingCommand(kind, parsed))]);
        Assert.Null(WorkbookDrawingPrimitives.Path(item));
    }

    [Fact]
    public void CustomCommand_NonFiniteValue_ReturnsNull()
    {
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj("rect", paths: [Cmds(Cmd("M", 1, 2), Cmd("L", double.NaN, 3))])));
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj("rect", paths: [Cmds(Cmd("M", double.PositiveInfinity, 2))])));
    }

    [Fact]
    public void PathFormatting_IsLocaleIndependent()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            Assert.Equal("M 0.5 0 L 1.25 0.75 Z",
                WorkbookDrawingPrimitives.Path(Obj("rect", paths: [Cmds(Cmd("M", 0.5, 0), Cmd("L", 1.25, 0.75), Cmd("Z"))])));
            Assert.Equal("M 0 0 L 33.3333 0", WorkbookDrawingPrimitives.Path(Obj("line", 100.0 / 3, 0)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("bogusPreset")]
    [InlineData("")]
    [InlineData("rect3")]
    [InlineData("RIGHTARROW")]
    [InlineData("curvedConnector6")]
    public void UnknownPreset_ReturnsNull(string preset)
        => Assert.Null(WorkbookDrawingPrimitives.Path(Obj(preset)));

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.NaN)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(10, double.NegativeInfinity)]
    [InlineData(-1, 10)]
    [InlineData(10, -0.5)]
    [InlineData(1e8 + 1, 10)]
    [InlineData(10, 1e8 + 1)]
    public void InvalidDimensions_ReturnNull(double w, double h)
    {
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj("rect", w, h)));
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj("line", w, h)));
        Assert.Null(WorkbookDrawingPrimitives.Path(Obj("rect", w, h, paths: [Cmds(Cmd("M", 0, 0), Cmd("Z"))])));
    }

    [Fact]
    public void Path_ContainsNoUnsafeMarkup()
    {
        foreach (var preset in ShapeFamily.Concat(LineFamily))
        {
            var path = WorkbookDrawingPrimitives.Path(Obj(preset))!;
            Assert.DoesNotContain("<", path, StringComparison.Ordinal);
            Assert.DoesNotContain("\"", path, StringComparison.Ordinal);
            Assert.DoesNotContain("'", path, StringComparison.Ordinal);
            Assert.DoesNotContain("&", path, StringComparison.Ordinal);
        }
    }

    private static List<(double X, double Y)> Coords(string path)
    {
        var result = new List<(double, double)>();
        string? command = null;
        var numbers = new List<double>();
        foreach (var token in path.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
                continue;
            }
            Flush();
            command = token;
        }
        Flush();
        return result;

        void Flush()
        {
            if (command is null) return;
            var pairs = command == "A"
                ? new[] { (numbers[^2], numbers[^1]) }
                : numbers.Chunk(2).Where(c => c.Length == 2).Select(c => (c[0], c[1])).ToArray();
            result.AddRange(pairs);
            numbers.Clear();
        }
    }
}

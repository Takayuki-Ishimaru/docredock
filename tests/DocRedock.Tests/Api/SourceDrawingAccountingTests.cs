using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

/// <summary>Photo-audit accounting: the raw source drawing scene (xlsx_drawing_scenes) is counted in its
/// own source_drawing_objects row, separately from the semantic drawings row, with hidden-sheet,
/// hidden-object, selected-sheet and omitted-image policies applied the way the projection applies them.</summary>
public sealed class SourceDrawingAccountingTests
{
    [Fact]
    public async Task Hidden_drawing_only_sheet_is_excluded_from_the_published_review_metadata()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "private-drawing.xlsx");
        var drawing = SixObjectDrawing();
        var shapes = System.Text.RegularExpressions.Regex.Replace(drawing.Xml, @"<xdr:pic>.*?</xdr:pic>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        WriteWorkbook(source, new SheetSpec("Public", $"<row r=\"1\">{Cell("A", 1, "PUBLIC")}</row>"),
            new SheetSpec("PRIVATE_EMPTY_DRAWING", "") { State = "hidden", Drawing = (shapes, [], []) });
        var target = await ExportAsync(source, Path.Combine(root, "out"));
        foreach (var path in new[] { "manifest.json", "source-index.json", "README.md", "layout.html" })
            Assert.DoesNotContain("PRIVATE_EMPTY_DRAWING", File.ReadAllText(Path.Combine(target, path)), StringComparison.Ordinal);
    }

    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task RealXlsx_SixRawObjects_ReportedEvenThoughSemanticDrawingsAreZero()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "photo.xlsx");
        WriteWorkbook(source, new SheetSpec("Photo", $"<row r=\"1\">{Cell("A", 1, "P1")}</row>")
        { Drawing = SixObjectDrawing() });
        var target = await ExportAsync(source, Path.Combine(root, "out"));
        var (recognized, included, excluded) = CountOf(target, "source_drawing_objects");
        Assert.Equal((6, 5, 1), (recognized, included, excluded));
        // The discrepancy the audit found: the old semantic row sees none of these raw objects.
        Assert.Equal(0, CountOf(target, "drawings").recognized);
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "source-index.json"))).RootElement;
        var sourceObjects = index.GetProperty("sourceDrawingObjects").EnumerateArray().ToArray();
        Assert.Equal(5, sourceObjects.Length);
        Assert.All(sourceObjects, item => Assert.Equal("Photo", item.GetProperty("sheetName").GetString()));
        Assert.All(sourceObjects, item => Assert.NotEmpty(item.GetProperty("objectId").GetString()!));
    }

    [Fact]
    public async Task RealXlsx_CompletePolicy_IncludesHiddenObject()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "photo.xlsx");
        WriteWorkbook(source, new SheetSpec("Photo", $"<row r=\"1\">{Cell("A", 1, "P1")}</row>")
        { Drawing = SixObjectDrawing() });
        var target = await ExportAsync(source, Path.Combine(root, "out"), "complete");
        var (recognized, included, excluded) = CountOf(target, "source_drawing_objects");
        Assert.Equal((6, 6, 0), (recognized, included, excluded));
    }

    [Fact]
    public async Task RealXlsx_SelectedSheetScope_CountsOnlyThatSheetsScene()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "photo.xlsx");
        WriteWorkbook(source,
            new SheetSpec("Photo", $"<row r=\"1\">{Cell("A", 1, "P1")}</row>") { Drawing = SixObjectDrawing() },
            new SheetSpec("Notes", $"<row r=\"1\">{Cell("A", 1, "N1")}</row>"));
        var target = await ExportAsync(source, Path.Combine(root, "out"), sheets: ["Notes"]);
        var (recognized, included, excluded) = CountOf(target, "source_drawing_objects");
        Assert.Equal((0, 0, 0), (recognized, included, excluded));
    }

    [Fact]
    public void HiddenSheetAndHiddenObject_ExcludedByVisiblePolicy()
    {
        var graph = Graph(("Visible", [CellNode("n_v1")],
                [Shape("s1"), Shape("s2", hidden: true)]),
            ("Secret", [CellNode("n_s1", hiddenSheet: true)], [Shape("s3")]));
        var counts = ReadableConversionReportBuilder.Build(graph, [], ReadableProjectionReport.Empty,
            "g.xlsx", "markdown-review", "visible").Counts;
        var row = counts.Single(c => c.Kind == "source_drawing_objects");
        Assert.Equal((3, 1, 2), (row.Recognized, row.Included, row.ExcludedByPolicy));
    }

    [Fact]
    public void CompletePolicy_IncludesHiddenSheetAndHiddenObjects()
    {
        var graph = Graph(("Visible", [CellNode("n_v1")],
                [Shape("s1"), Shape("s2", hidden: true)]),
            ("Secret", [CellNode("n_s1", hiddenSheet: true)], [Shape("s3")]));
        var row = ReadableConversionReportBuilder.Build(graph, [], ReadableProjectionReport.Empty,
            "g.xlsx", "markdown-review", "complete").Counts.Single(c => c.Kind == "source_drawing_objects");
        Assert.Equal((3, 3, 0), (row.Recognized, row.Included, row.ExcludedByPolicy));
    }

    [Fact]
    public void SelectedSheets_ScopeLimitsSceneObjects()
    {
        var graph = Graph(("A", [CellNode("n_a1")], [Shape("a1"), Shape("a2")]),
            ("B", [CellNode("n_b1")], [Shape("b1")]));
        var row = ReadableConversionReportBuilder.Build(graph, [], ReadableProjectionReport.Empty,
            "g.xlsx", "markdown-review", "visible", ["A"]).Counts.Single(c => c.Kind == "source_drawing_objects");
        Assert.Equal((2, 2, 0), (row.Recognized, row.Included, row.ExcludedByPolicy));
    }

    [Fact]
    public void OmittedImage_StaysRecognizedAndIncluded_DanglingPictureIsExcludedNotSilent()
    {
        var imageNode = new DocumentNode("n_img", NodeKind.Image, null, 0, ContentLayer.Body,
            new ReferenceNodeContent("assets/a.png"), Extensions: new Dictionary<string, JsonElement>
            { ["image_omitted"] = JsonSerializer.SerializeToElement(true) });
        var graph = Graph(("Photo", [imageNode],
            [Picture("p_omitted", "n_img"), Picture("p_dangling", "n_missing")]));
        var row = ReadableConversionReportBuilder.Build(graph, [], ReadableProjectionReport.Empty,
            "g.xlsx", "markdown-review", "visible").Counts.Single(c => c.Kind == "source_drawing_objects");
        Assert.Equal((2, 1, 1), (row.Recognized, row.Included, row.ExcludedByPolicy));
    }

    [Fact]
    public void NonXlsxGraph_KeepsOldRowsWithoutSourceDrawingRow()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_t", DocumentFormatKind.Docx,
            [new DocumentPartition("section-0", 0, [CellNode("n1")])]);
        var counts = ReadableConversionReportBuilder.Build(graph, [], ReadableProjectionReport.Empty,
            "d.docx", "markdown", "visible").Counts;
        Assert.DoesNotContain(counts, c => c.Kind == "source_drawing_objects");
        Assert.Contains(counts, c => c.Kind == "drawings");
    }

    private static (int recognized, int included, int excluded) CountOf(string target, string kind)
    {
        var counts = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "conversion-report.json")))
            .RootElement.GetProperty("counts").EnumerateArray()
            .Where(c => c.GetProperty("kind").GetString() == kind).Single();
        return (counts.GetProperty("recognized").GetInt32(), counts.GetProperty("included").GetInt32(),
            counts.GetProperty("excludedByPolicy").GetInt32());
    }

    private static DocumentGraph Graph(params (string Sheet, DocumentNode[] Nodes, WorkbookDrawingObject[] Objects)[] sheets)
    {
        var scenes = sheets.ToDictionary(s => "sheet-" + s.Sheet,
            s => new WorkbookDrawingScene(s.Sheet, s.Objects), StringComparer.Ordinal);
        return new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_t", DocumentFormatKind.Xlsx,
            sheets.Select((s, i) => new DocumentPartition("sheet-" + s.Sheet, i, s.Nodes)).ToArray(),
            FormatExtensions: new Dictionary<string, JsonElement>
            { [WorkbookDrawingSceneJson.ExtensionKey] = WorkbookDrawingSceneJson.Write(scenes) });
    }

    private static DocumentNode CellNode(string id, bool hiddenSheet = false) => new(id, NodeKind.Cell, null, 0,
        hiddenSheet ? ContentLayer.Hidden : ContentLayer.Body, new TextNodeContent("v"),
        Extensions: hiddenSheet ? new Dictionary<string, JsonElement> { ["sheet_state"] = JsonSerializer.SerializeToElement("hidden") } : null);

    private static WorkbookDrawingObject Shape(string id, bool hidden = false) =>
        new(id, "shape", 10, 10, WorkbookDrawingTransform.Identity) { Hidden = hidden };

    private static WorkbookDrawingObject Picture(string id, string sourceNodeId) =>
        new(id, "picture", 10, 10, WorkbookDrawingTransform.Identity) { SourceNodeId = sourceNodeId };

    private static (string Xml, (string Id, string Target)[] Relationships, (string Name, byte[] Bytes)[] Assets)
        SixObjectDrawing()
    {
        string Rect(int id, bool hidden = false) =>
            "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"" + id + "\" name=\"r" + id + "\"" + (hidden ? " hidden=\"1\"" : "") +
            "/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:xfrm><a:off x=\"" + (id * 200000) +
            "\" y=\"100000\"/><a:ext cx=\"150000\" cy=\"150000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:sp>";
        var pic = "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"99\" name=\"photo\"/><xdr:cNvPicPr/></xdr:nvPicPr>" +
            "<xdr:blipFill><a:blip r:embed=\"rId1\"/></xdr:blipFill><xdr:spPr/></xdr:pic>";
        var anchors = string.Concat(Enumerable.Range(1, 5).Select(id =>
            $"<xdr:absoluteAnchor><xdr:pos x=\"{id * 200000}\" y=\"100000\"/><xdr:ext cx=\"150000\" cy=\"150000\"/>" +
            Rect(id, hidden: id == 5) + "<xdr:clientData/></xdr:absoluteAnchor>")) +
            "<xdr:absoluteAnchor><xdr:pos x=\"900000\" y=\"100000\"/><xdr:ext cx=\"150000\" cy=\"150000\"/>" +
            pic + "<xdr:clientData/></xdr:absoluteAnchor>";
        var xml = $"<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" + anchors + "</xdr:wsDr>";
        return (xml, [("rId1", "../media/p1.png")],
            [("p1.png", Convert.FromBase64String(PngBase64))]);
    }

    private static string TempDirectory() => Directory.CreateTempSubdirectory("docredock-source-drawing-").FullName;
    private static string Cell(string column, int row, string text) =>
        $"<c r=\"{column}{row}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c>";

    private static async Task<string> ExportAsync(string source, string target, string policy = "visible",
        IReadOnlyList<string>? sheets = null)
    {
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportWorkbookReviewAsync(
            new(new(source, Path.Combine(Path.GetDirectoryName(source)!, "unused.md"),
                EnableOcr: false, ContentPolicy: policy, Sheets: sheets), target));
        return target;
    }

    private sealed record SheetSpec(string Name, string Rows)
    {
        public string? State { get; init; }
        public (string Xml, (string Id, string Target)[] Relationships, (string Name, byte[] Bytes)[] Assets)? Drawing { get; init; }
    }

    private static void WriteWorkbook(string source, params SheetSpec[] sheets)
    {
        const string n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string p = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var zip = new ZipArchive(File.Create(source), ZipArchiveMode.Create);
        void Write(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
        string Rel(string id, string type, string target) => $"<Relationship Id=\"{id}\" Type=\"{r}/{type}\" Target=\"{target}\"/>";
        Write("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>");
        Write("_rels/.rels", $"<Relationships xmlns=\"{p}\">{Rel("workbook", "officeDocument", "xl/workbook.xml")}</Relationships>");
        Write("xl/workbook.xml", $"<workbook xmlns=\"{n}\" xmlns:r=\"{r}\"><sheets>" + string.Concat(sheets.Select((s, i) =>
            $"<sheet name=\"{System.Security.SecurityElement.Escape(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"s{i + 1}\"" +
            (s.State is null ? "" : $" state=\"{s.State}\"") + "/>")) + "</sheets></workbook>");
        Write("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{p}\">" + string.Concat(sheets.Select((s, i) =>
            Rel("s" + (i + 1), "worksheet", $"worksheets/sheet{i + 1}.xml"))) + "</Relationships>");
        for (var i = 0; i < sheets.Length; i++)
        {
            var s = sheets[i]; var tail = ""; var rels = "";
            if (s.Drawing is { } drawing)
            {
                tail += "<drawing r:id=\"drawing\"/>"; rels += Rel("drawing", "drawing", $"../drawings/drawing{i + 1}.xml");
                Write($"xl/drawings/drawing{i + 1}.xml", drawing.Xml);
                Write($"xl/drawings/_rels/drawing{i + 1}.xml.rels", $"<Relationships xmlns=\"{p}\">" +
                    string.Concat(drawing.Relationships.Select(rel => Rel(rel.Id, "image", rel.Target))) + "</Relationships>");
                foreach (var asset in drawing.Assets)
                { using var stream = zip.CreateEntry("xl/media/" + asset.Name).Open(); stream.Write(asset.Bytes); }
            }
            Write($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet xmlns=\"{n}\" xmlns:r=\"{r}\"><sheetData>{s.Rows}</sheetData>{tail}</worksheet>");
            if (rels.Length > 0) Write($"xl/worksheets/_rels/sheet{i + 1}.xml.rels", $"<Relationships xmlns=\"{p}\">{rels}</Relationships>");
        }
    }
}

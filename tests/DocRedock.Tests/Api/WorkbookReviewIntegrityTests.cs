using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>Integrity regressions for the workbook review bundle: merged/wide records,
/// source-index grounding, exact change-history names, image dedup, comment policy.</summary>
public sealed class WorkbookReviewIntegrityTests
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task BlankColumn_VerticallyMergedCondition_SeventeenNamedColumns_KeepEveryValue_NoRawHtml()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "wide.xlsx");
    // Column A is blank (no header, no values); B1..R1 name 17 columns; B2:B4 is one
    // vertically merged condition whose value must read whole in every covered row.
    var header = Cell("A", 1, null) + string.Concat(Enumerable.Range(1, 17)
        .Select(i => Cell(ColumnName(i + 1), 1, $"Col{i:D2}")));
    var rows = new StringBuilder($"<row r=\"1\">{header}</row>");
    for (var r = 2; r <= 4; r++)
        rows.Append($"<row r=\"{r}\">")
            .Append(Cell("A", r, null))
            .Append(Cell("B", r, r == 2 ? "MERGEMARK" : null))
            .Append(string.Concat(Enumerable.Range(3, 16)
                .Select(c => Cell(ColumnName(c), r, $"V{r}c{c}"))))
            .Append("</row>");
    WriteWorkbook(source, new SheetSpec("Wide", rows.ToString())
    { MergedRanges = ["B2:B4"] });
    var target = await ExportAsync(source, Path.Combine(root, "out"));
    var markdown = File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single());
    for (var i = 1; i <= 17; i++) Assert.Contains($"Col{i:D2}", markdown);
    for (var r = 2; r <= 4; r++)
        for (var c = 3; c <= 18; c++) Assert.Contains($"V{r}c{c}", markdown);
    Assert.Equal(3, CountOccurrences(markdown, "MERGEMARK"));
    foreach (var tag in new[] { "<table", "<br", "<div", "<img", "<span" })
        Assert.DoesNotContain(tag, markdown, StringComparison.OrdinalIgnoreCase);
}

    [Fact]
    public async Task SourceIndex_NodesCarryOriginalSheetName_AndRealCellOrMergedRange()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "grounded.xlsx");
    WriteWorkbook(source, new SheetSpec("Data",
        $"<row r=\"1\">{Cell("A", 1, "H1")}</row>"
        + $"<row r=\"2\">{Cell("B", 2, "M2V")}</row>"
        + $"<row r=\"5\">{Cell("C", 5, "S5V")}</row>")
    { MergedRanges = ["B2:B4"] });
    var target = await ExportAsync(source, Path.Combine(root, "out"));
    var index = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(target, "source-index.json"))).RootElement.GetProperty("nodes");
    var manifestNames = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")))
        .RootElement.GetProperty("sheets").EnumerateArray()
        .Select(sheet => sheet.GetProperty("originalSheetName").GetString()!).ToHashSet(StringComparer.Ordinal);
    var ranges = new HashSet<string>(StringComparer.Ordinal);
    foreach (var node in index.EnumerateArray())
    {
        var sheetName = node.GetProperty("sheetName").GetString()!;
        var nodeId = node.GetProperty("nodeId").GetString()!;
        // A node-id-derived sheet name ("n_..." or the id itself) is invented, never original.
        Assert.True(manifestNames.Contains(sheetName), $"invented sheet name '{sheetName}' for node '{nodeId}'");
        Assert.NotEqual(nodeId, sheetName);
        Assert.False(nodeId.StartsWith("sheet-", StringComparison.Ordinal) && sheetName == nodeId);
        if (node.TryGetProperty("cellRange", out var range) && range.ValueKind == JsonValueKind.String)
            ranges.Add(range.GetString()!);
    }
    Assert.Contains("B2:B4", ranges); // merged origin reports its whole range, not just B2
    Assert.Contains("C5", ranges);
    Assert.Contains("A1", ranges);
}

    [Fact]
    public async Task SubstringHistoryName_IsNotHistory_NoDanglingChangelogLinks()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "nearmiss.xlsx");
        WriteWorkbook(source,
            new SheetSpec("Data", $"<row r=\"1\">{Cell("A", 1, "D1V")}</row>"),
            new SheetSpec("変更履歴表", $"<row r=\"1\">{Cell("A", 1, "SUBSTR-ONLY-91")}</row>"));
        var target = await ExportAsync(source, Path.Combine(root, "out"));
        Assert.False(File.Exists(Path.Combine(target, "CHANGELOG.md")));
        var readme = File.ReadAllText(Path.Combine(target, "README.md"));
        Assert.DoesNotContain("CHANGELOG", readme, StringComparison.Ordinal);
        foreach (var sheet in Directory.GetFiles(Path.Combine(target, "sheets")))
            Assert.DoesNotContain("../CHANGELOG.md", File.ReadAllText(sheet), StringComparison.Ordinal);
        Assert.Contains("SUBSTR-ONLY-91",
            File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single(p => p.Contains("002_"))));
        AssertAllLinksResolve(target);
    }

    [Fact]
    public async Task ExactHistoryName_DifferentCase_IsHistory_LinksResolve()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "chg.xlsx");
        WriteWorkbook(source,
            new SheetSpec("Data", $"<row r=\"1\">{Cell("A", 1, "D1V")}</row>"),
            new SheetSpec("Changelog", $"<row r=\"1\">{Cell("A", 1, "CHG-ROW-7")}</row>"));
        var target = await ExportAsync(source, Path.Combine(root, "out"));
        var changeLog = File.ReadAllText(Path.Combine(target, "CHANGELOG.md"));
        Assert.Contains("CHG-ROW-7", changeLog);
        Assert.Contains("CHANGELOG.md", File.ReadAllText(Path.Combine(target, "README.md")));
        Assert.Contains("../CHANGELOG.md",
            File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single(p => p.Contains("001_"))));
        AssertAllLinksResolve(target);
    }

    [Fact]
    public async Task TwoImageNodes_IdenticalPngBytes_OneAsset_BothReferencesResolve()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "pics.xlsx");
        var png = Convert.FromBase64String(PngBase64);
        WriteWorkbook(source, new SheetSpec("Pic", $"<row r=\"1\">{Cell("A", 1, "P1V")}</row>")
        {
            Drawing = (TwoAnchorDrawingXml,
                [("rId1", "../media/image1.png"), ("rId2", "../media/image2.png")],
                [("image1.png", png), ("image2.png", png)]),
        });
        var target = await ExportAsync(source, Path.Combine(root, "out"));
        var assets = Directory.GetFiles(Path.Combine(target, "assets"));
        Assert.Single(assets);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() + ".png",
            Path.GetFileName(assets[0]));
        var markdown = File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single());
        var references = Regex.Matches(markdown, @"\]\((\.\./assets/[^)\s]+)\)")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(2, references.Length); // both image nodes stay visible
        foreach (var reference in references)
            Assert.True(File.Exists(Path.Combine(target, "sheets", reference)), $"missing {reference}");
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "conversion-report.json")))
            .RootElement.GetProperty("imagesNotWritten").EnumerateArray());
        var html = File.ReadAllText(Path.Combine(target, "layout.html"));
        var screen = Regex.Match(html, "<div class=\"source-screen-layout\">([\\s\\S]*?)<details data-kind=\"worksheet-data\"").Groups[1].Value;
        Assert.Equal(2, Regex.Matches(screen, "<img\\b").Count);
    }

    [Fact]
    public async Task CellComments_Authors_ExcludedInVisibleAndSanitized_IncludedInComplete()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "notes.xlsx");
        const string commentText = "ZQXV-COMMENT-7731";
        const string authorName = "ZQXV-AUTHOR-42";
        var rows = $"<row r=\"1\">{Cell("A", 1, "K1V")}</row><row r=\"2\">{Cell("B", 2, "V2V")}</row>";
        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var perPolicy = Path.Combine(root, policy);
            WriteWorkbook(source, new SheetSpec("Notes", rows)
            {
                Comments = $"<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + $"<authors><author>{authorName}</author></authors><commentList>"
                    + $"<comment ref=\"B2\" authorId=\"0\"><text><r><t>{commentText}</t></r></text></comment>"
                    + "</commentList></comments>",
            });
            var target = await ExportAsync(source, Path.Combine(perPolicy, "out"), policy);
            var sheetMarkdown = File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single());
            var layoutHtml = File.ReadAllText(Path.Combine(target, "layout.html"));
            var sourceIndex = File.ReadAllText(Path.Combine(target, "source-index.json"));
            var report = File.ReadAllText(Path.Combine(target, "conversion-report.json"));
            if (policy == "complete")
            {
                Assert.Contains(commentText, sheetMarkdown);
                Assert.Contains(authorName, sheetMarkdown);
                Assert.Contains(commentText, layoutHtml);
                Assert.Contains("Comment", sourceIndex);
                var count = JsonDocument.Parse(report).RootElement.GetProperty("counts").EnumerateArray()
                    .Single(item => item.GetProperty("kind").GetString() == "comments");
                Assert.Equal(1, count.GetProperty("included").GetInt32());
            }
            else
            {
                foreach (var (name, text) in new[] { ("sheet md", sheetMarkdown), ("layout.html", layoutHtml),
                         ("source-index", sourceIndex), ("conversion-report", report) })
                {
                    Assert.False(text.Contains(commentText, StringComparison.Ordinal),
                        $"{policy} leaked comment text into {name}");
                    Assert.False(text.Contains(authorName, StringComparison.Ordinal),
                        $"{policy} leaked author into {name}");
                }
            }
        }
    }

    [Fact]
    public async Task Omitted_images_keep_descriptions_without_assets_or_broken_image_links()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "omit.xlsx");
        var png = Convert.FromBase64String(PngBase64);
        WriteWorkbook(source, new SheetSpec("Pic", $"<row r=\"1\">{Cell("A", 1, "source")}</row>")
        { Drawing = (TwoAnchorDrawingXml, [("rId1", "../media/image1.png"), ("rId2", "../media/image2.png")], [("image1.png", png), ("image2.png", png)]) });
        var result = await new DocumentService(null, null, discoverPdfRasterizer: false).ExportWorkbookReviewAsync(
            new(new(source, Path.Combine(root, "unused.md"), EnableOcr: false, OmitImages: true), Path.Combine(root, "out")));
        Assert.Empty(Directory.GetFiles(Path.Combine(result.DirectoryPath, "assets")));
        var markdown = File.ReadAllText(Directory.GetFiles(Path.Combine(result.DirectoryPath, "sheets")).Single());
        Assert.DoesNotContain("![", markdown, StringComparison.Ordinal);
        Assert.Contains("first", markdown, StringComparison.Ordinal);
        Assert.Contains("second", markdown, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Code == "ImageOmitted");
    }

    [Fact]
    public async Task Real_worksheet_print_area_excludes_outside_anchored_images()
    {
        var root = TempDirectory(); var source = Path.Combine(root, "print-images.xlsx");
        var png = Convert.FromBase64String(PngBase64);
        WriteWorkbook(source, new SheetSpec("Pic", $"<row r=\"1\">{Cell("A", 1, "print this cell")}</row>")
        { PrintArea = "A1:D1", Drawing = (TwoAnchorDrawingXml, [("rId1", "../media/image1.png"), ("rId2", "../media/image2.png")], [("image1.png", png), ("image2.png", png)]) });
        var directory = await ExportAsync(source, Path.Combine(root, "out"));
        var html = File.ReadAllText(Path.Combine(directory, "layout.html"));
        var screen = Regex.Match(html, "<div class=\"source-screen-layout\">([\\s\\S]*?)<details data-kind=\"worksheet-data\"").Groups[1].Value;
        Assert.Equal(2, Regex.Matches(screen, "<img\\b").Count);
        var printed = html[html.IndexOf("<div class=\"source-print-pages\">", StringComparison.Ordinal)..];
        Assert.DoesNotContain("<img", printed, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "<img\\b").Count);
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("sanitized", false)]
    [InlineData("complete", true)]
    public async Task Excluded_hidden_sheets_do_not_create_named_files_or_manifest_entries(string policy, bool included)
    {
        var root = TempDirectory(); var source = Path.Combine(root, "hidden.xlsx");
        WriteWorkbook(source, new SheetSpec("Public", $"<row r=\"1\">{Cell("A", 1, "PUBLIC")}</row>"),
            new SheetSpec("HIDDEN-SHEET-SENTINEL", $"<row r=\"1\">{Cell("A", 1, "HIDDEN-CONTENT-SENTINEL")}</row>") { State = "hidden" });
        var directory = await ExportAsync(source, Path.Combine(root, "out"), policy);
        Assert.Equal(included ? 2 : 1, Directory.GetFiles(Path.Combine(directory, "sheets")).Length);
        foreach (var name in new[] { "README.md", "manifest.json", "source-index.json", "layout.html" })
            Assert.Equal(included, File.ReadAllText(Path.Combine(directory, name)).Contains("HIDDEN-SHEET-SENTINEL", StringComparison.Ordinal));
    }

    private static string TempDirectory() => Directory.CreateTempSubdirectory("docredock-review-integrity-").FullName;
    private static int CountOccurrences(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
    private static string ColumnName(int column)
    {
        var name = "";
        for (; column > 0; column = (column - 1) / 26) name = (char)('A' + (column - 1) % 26) + name;
        return name;
    }
    private static string Cell(string column, int row, string? text) => text is null ? $"<c r=\"{column}{row}\"/>" :
        $"<c r=\"{column}{row}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c>";
    private static async Task<string> ExportAsync(string source, string target, string policy = "visible")
    {
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportWorkbookReviewAsync(
            new(new(source, Path.Combine(Path.GetDirectoryName(source)!, "unused.md"), EnableOcr: false, ContentPolicy: policy), target));
        return target;
    }
    private static void AssertAllLinksResolve(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories))
            foreach (Match link in Regex.Matches(File.ReadAllText(file), @"\]\(([^)]+)\)"))
            {
                var destination = link.Groups[1].Value;
                if (destination.StartsWith('#') || Uri.TryCreate(destination, UriKind.Absolute, out _)) continue;
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(destination))), destination);
            }
    }
    private sealed record SheetSpec(string Name, string Rows)
    {
        public string[] MergedRanges { get; init; } = [];
        public string? Comments { get; init; }
        public string? PrintArea { get; init; }
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
        Write("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>
            """);
        Write("_rels/.rels", $"<Relationships xmlns=\"{p}\">{Rel("workbook", "officeDocument", "xl/workbook.xml")}</Relationships>");
        var definitions = string.Concat(sheets.Select((s,i) => s.PrintArea is null ? "" :
            $"<definedName name=\"_xlnm.Print_Area\" localSheetId=\"{i}\">'{System.Security.SecurityElement.Escape(s.Name)}'!{s.PrintArea}</definedName>"));
        Write("xl/workbook.xml", $"<workbook xmlns=\"{n}\" xmlns:r=\"{r}\"><sheets>" + string.Concat(sheets.Select((s,i) =>
            $"<sheet name=\"{System.Security.SecurityElement.Escape(s.Name)}\" sheetId=\"{i+1}\" r:id=\"s{i+1}\"" + (s.State is null ? "" : $" state=\"{s.State}\"") + "/>")) + "</sheets><definedNames>" + definitions + "</definedNames></workbook>");
        Write("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{p}\">" + string.Concat(sheets.Select((s,i) => Rel("s"+(i+1), "worksheet", $"worksheets/sheet{i+1}.xml"))) + "</Relationships>");
        for (var i = 0; i < sheets.Length; i++)
        {
            var s = sheets[i]; var tail = ""; var rels = "";
            if (s.MergedRanges.Length > 0) tail += "<mergeCells>" + string.Concat(s.MergedRanges.Select(m => $"<mergeCell ref=\"{m}\"/>")) + "</mergeCells>";
            if (s.Comments is { } comments)
            { rels += Rel("comments", "comments", $"../comments{i+1}.xml"); Write($"xl/comments{i+1}.xml", comments); }
            if (s.Drawing is { } drawing)
            {
                tail += "<drawing r:id=\"drawing\"/>"; rels += Rel("drawing", "drawing", $"../drawings/drawing{i+1}.xml");
                Write($"xl/drawings/drawing{i+1}.xml", drawing.Xml);
                Write($"xl/drawings/_rels/drawing{i+1}.xml.rels", $"<Relationships xmlns=\"{p}\">" +
                    string.Concat(drawing.Relationships.Select(rel => Rel(rel.Id, "image", rel.Target))) + "</Relationships>");
                foreach (var asset in drawing.Assets)
                { using var stream = zip.CreateEntry("xl/media/" + asset.Name).Open(); stream.Write(asset.Bytes); }
            }
            Write($"xl/worksheets/sheet{i+1}.xml", $"<worksheet xmlns=\"{n}\" xmlns:r=\"{r}\"><sheetData>{s.Rows}</sheetData>{tail}</worksheet>");
            if (rels.Length > 0) Write($"xl/worksheets/_rels/sheet{i+1}.xml.rels", $"<Relationships xmlns=\"{p}\">{rels}</Relationships>");
        }
    }
    private const string TwoAnchorDrawingXml = """
        <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="100000" cy="100000"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="1" name="first"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId1"/></xdr:blipFill><xdr:spPr/></xdr:pic><xdr:clientData/></xdr:oneCellAnchor>
        <xdr:oneCellAnchor><xdr:from><xdr:col>2</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="100000" cy="100000"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="2" name="second"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId2"/></xdr:blipFill><xdr:spPr/></xdr:pic><xdr:clientData/></xdr:oneCellAnchor>
        </xdr:wsDr>
        """;
}

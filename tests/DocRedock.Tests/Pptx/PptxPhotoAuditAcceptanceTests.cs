using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Pptx;

/// <summary>Real-package acceptance cases for the photo audit. The human-readable API keeps
/// cell values separate from drawings, does not disclose hidden derived diagrams, and retains
/// explicit links and body text. Existing explicitly selected legacy overlay modes may remain.</summary>
public sealed class PptxPhotoAuditAcceptanceTests
{
    [Theory]
    [InlineData(ReadableOutputFormat.Markdown)]
    [InlineData(ReadableOutputFormat.Html)]
    public async Task A_hyperlink_inside_a_real_table_cell_keeps_its_label_and_target(ReadableOutputFormat format)
    {
        using var temp = new Scratch();
        var table = NativeTable().Replace("<a:t>" + OriginalCell + "</a:t>",
            "<a:rPr><a:hlinkClick r:id=\"rLink\"/></a:rPr><a:t>" + OriginalCell + "</a:t>", StringComparison.Ordinal);
        var relationships = "<Relationship Id=\"rLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"" +
            WebUtility.HtmlEncode(Url) + "\" TargetMode=\"External\"/>";
        var source = temp.Write("table-link.pptx", Package(table, relationships: relationships));
        var result = await Export(source, temp.PathFor("result" + Extension(format)), format);
        var text = await File.ReadAllTextAsync(result.MarkdownPath);
        var content = Assert.IsType<TableNodeContent>(Assert.Single(result.Graph.Nodes, n => n.Kind == NodeKind.Table).Content);
        Assert.Equal(Url, Assert.Single(content.Rows[1][1].Runs!).LinkTarget);
        Assert.Contains(Url, WebUtility.HtmlDecode(text), StringComparison.Ordinal);
        Assert.Contains(OriginalCell, text, StringComparison.Ordinal);
    }

    private const string Annotation = "OverlayLabelFseven";
    private const string OriginalCell = "ActualCellValue";
    private const string PrivateFrom = "PrivateHiddenFlowFrom";
    private const string PrivateTo = "PrivateHiddenFlowTo";
    private const string BusinessConstraint = "RepeatedBusinessConstraint";
    private const string Url = "https://example.org/source?id=7#part";

    [Theory]
    [InlineData(ReadableOutputFormat.Markdown)]
    [InlineData(ReadableOutputFormat.Html)]
    public async Task Readable_API_keeps_real_table_cells_and_drawing_annotation_separate(ReadableOutputFormat format)
    {
        using var temp = new Scratch();
        var source = temp.Write("overlays.pptx", Package(NativeTable() + Arrow()));
        var result = await Export(source, temp.PathFor("result" + Extension(format)), format);
        var text = await File.ReadAllTextAsync(result.MarkdownPath);
        var table = Assert.Single(result.Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal(OriginalCell, Assert.IsType<TableNodeContent>(table.Content).Rows[1][1].Text);
        Assert.True(table.Extensions?.ContainsKey("table_overlays") == true, "The real native-table drawing must be detected.");

        string outside;
        if (format == ReadableOutputFormat.Markdown)
        {
            var tableLines = text.Split('\n').Where(line => line.TrimStart().StartsWith('|')).ToArray();
            Assert.NotEmpty(tableLines);
            Assert.Contains(OriginalCell, string.Join("\n", tableLines), StringComparison.Ordinal);
            Assert.DoesNotContain(Annotation, string.Join("\n", tableLines), StringComparison.Ordinal);
            outside = string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith('|')));
        }
        else
        {
            var tables = Regex.Matches(text, @"<table\b[^>]*>.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            Assert.NotEmpty(tables);
            var cells = string.Join("\n", tables.Select(match => match.Value));
            Assert.Contains(OriginalCell, BodyText(cells), StringComparison.Ordinal);
            Assert.DoesNotContain(Annotation, BodyText(cells), StringComparison.Ordinal);
            outside = BodyText(Regex.Replace(text, @"<table\b[^>]*>.*?</table>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase));
        }
        Assert.Equal(1, Occurrences(outside, Annotation));
        Assert.Contains("B2:D2", outside, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("visible", false, ReadableOutputFormat.Markdown)]
    [InlineData("complete", true, ReadableOutputFormat.Markdown)]
    [InlineData("sanitized", false, ReadableOutputFormat.Markdown)]
    [InlineData("visible", false, ReadableOutputFormat.Html)]
    [InlineData("complete", true, ReadableOutputFormat.Html)]
    [InlineData("sanitized", false, ReadableOutputFormat.Html)]
    public async Task Hidden_slide_derived_diagram_obeys_the_API_content_policy(
        string policy, bool included, ReadableOutputFormat format)
    {
        using var temp = new Scratch();
        var source = temp.Write("hidden-flow.pptx", Package(Flow(), hiddenFirst: true));
        var result = await Export(source, temp.PathFor("result" + Extension(format)), format, policy);
        Assert.Contains(result.Graph.Nodes, node => node.Kind == NodeKind.Diagram &&
            node.Extensions?.ContainsKey("visual_graph") == true); // This must exercise a derived flow, not a text-only slide.
        var text = await File.ReadAllTextAsync(result.MarkdownPath);
        if (included)
        {
            Assert.Contains(PrivateFrom, text, StringComparison.Ordinal);
            Assert.Contains(PrivateTo, text, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(PrivateFrom, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PrivateTo, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ReadableOutputFormat.Markdown)]
    [InlineData(ReadableOutputFormat.Html)]
    public async Task Explicit_run_hyperlink_keeps_its_relationship_target_while_plain_url_stays_text(ReadableOutputFormat format)
    {
        using var temp = new Scratch();
        var link = Box(2, 0, 0, "FollowSourceSpec",
            "<a:hlinkClick r:id=\"rLink\"/>");
        var plain = Box(3, 0, 1500000, "https://example.org/plain");
        var relationships = "<Relationship Id=\"rLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"" +
            WebUtility.HtmlEncode(Url) + "\" TargetMode=\"External\"/>";
        var source = temp.Write("links.pptx", Package(link + plain, relationships: relationships));
        var result = await Export(source, temp.PathFor("result" + Extension(format)), format);
        var text = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Contains("FollowSourceSpec", text, StringComparison.Ordinal);
        if (format == ReadableOutputFormat.Markdown)
        {
            Assert.Contains(Url, WebUtility.HtmlDecode(text), StringComparison.Ordinal);
            Assert.DoesNotContain("](https://example.org/plain)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("](<https://example.org/plain>)", text, StringComparison.Ordinal);
        }
        else
        {
            var targets = Regex.Matches(text, "href=\"([^\"]*)\"").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
            Assert.Contains(Url, targets);
            Assert.DoesNotContain("https://example.org/plain", targets);
        }
    }

    [Theory]
    [InlineData(ReadableOutputFormat.Markdown)]
    [InlineData(ReadableOutputFormat.Html)]
    public async Task Repeated_bottom_body_text_is_not_discarded_as_an_inferred_slide_footer(ReadableOutputFormat format)
    {
        using var temp = new Scratch();
        var body = Box(2, 0, 5000000, BusinessConstraint);
        var source = temp.Write("body-constraint.pptx", Package(body, secondSlide: body));
        var result = await Export(source, temp.PathFor("result" + Extension(format)), format);
        var text = await File.ReadAllTextAsync(result.MarkdownPath);
        Assert.Equal(2, result.Graph.Nodes.Count(node => node.Layer == ContentLayer.Body &&
            Plain(node).Contains(BusinessConstraint, StringComparison.Ordinal)));
        Assert.Equal(2, Occurrences(format == ReadableOutputFormat.Html ? BodyText(text) : text, BusinessConstraint));
    }

    [Theory]
    [InlineData("schedule-shape-grid.pptx")]
    [InlineData("schedule-shape-grid.powerpoint-saved.pptx")]
    public async Task Native_HTML_keeps_original_grid_shapes_once_and_collapses_the_synthesized_table(string fileName)
    {
        using var temp = new Scratch();
        var source = Fixture(fileName);
        // First use the public Markdown conversion, then inspect only its extracted first slide.
        var result = await Export(source, temp.PathFor("converted.md"), ReadableOutputFormat.Markdown);
        var slide = result.Graph.Partitions[0];
        Assert.Contains(slide.Nodes, node => node.Extensions?.ContainsKey("table_grid_member_host") == true);
        var graph = result.Graph with { Partitions = [slide] };
        var document = ReadableProjectionBuilder.Build(graph, new(Language: "ja")).Document;
        var section = Assert.Single(document.Blocks.OfType<ReadableSection>());
        var sourceScene = Assert.Single(section.Blocks.OfType<ReadableSourceDrawingScene>());
        Assert.DoesNotContain(section.Blocks, block => block is ReadableTable);
        var description = Assert.Single(section.Blocks.OfType<ReadableDetails>(), block => block.Kind == "source-diagram-description");
        Assert.False(description.InitiallyOpen);
        Assert.NotEmpty(description.Blocks.OfType<ReadableTable>());
        Assert.Single(sourceScene.Scene.Objects, drawing => drawing.Text == "山田");
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        var outside = BodyText(Regex.Replace(html, @"<table\b[^>]*>.*?</table>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase));
        Assert.Equal(1, Occurrences(outside, "山田"));
    }

    private static Task<ReadableDocumentExportResult> Export(string source, string path,
        ReadableOutputFormat format, string policy = "visible") =>
        new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(
            source, path, Title: "PPTX photo audit", ContentPolicy: policy, Language: "en", OutputFormat: format));

    private static string Extension(ReadableOutputFormat format) => format == ReadableOutputFormat.Html ? ".html" : ".md";
    private static string Plain(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => "",
    };
    private static int Occurrences(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;
    private static string BodyText(string text)
    {
        text = Regex.Replace(text, @"<(style|script|svg|pre)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " "));
    }
    private static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = System.IO.Path.Combine(directory.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("PPTX fixture not found.", name);
    }

    private static string NativeTable()
    {
        string Cell(string value) => "<a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>" + value + "</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>";
        var rows = new[]
        {
            new[] { "Task", "DayOne", "DayTwo", "DayThree", "DayFour", "DayFive" },
            new[] { "Work", OriginalCell, "", "", "", "" },
            new[] { "Review", "", "", "", "", "" },
        };
        return "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"NativeSchedule\"/></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"7200000\" cy=\"1200000\"/></p:xfrm>" +
            "<a:graphic><a:graphicData><a:tbl><a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:gridCol w=\"1200000\"/>", 6)) +
            "</a:tblGrid>" + string.Concat(rows.Select(row => "<a:tr h=\"400000\">" + string.Concat(row.Select(Cell)) + "</a:tr>")) +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
    }
    private static string Arrow() =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"10\" name=\"DrawingAnnotation\"/></p:nvSpPr><p:spPr>" +
        "<a:xfrm><a:off x=\"1200000\" y=\"400000\"/><a:ext cx=\"3600000\" cy=\"400000\"/></a:xfrm>" +
        "<a:prstGeom prst=\"rightArrow\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:p><a:r><a:t>" +
        Annotation + "</a:t></a:r></a:p></p:txBody></p:sp>";
    private static string Box(int id, int x, int y, string text, string properties = "") =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"BusinessBody\"/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"" + x +
        "\" y=\"" + y + "\"/><a:ext cx=\"1200000\" cy=\"600000\"/></a:xfrm><a:prstGeom prst=\"rect\"/></p:spPr>" +
        "<p:txBody><a:bodyPr/><a:p><a:r><a:rPr>" + properties + "</a:rPr><a:t>" + WebUtility.HtmlEncode(text) +
        "</a:t></a:r></a:p></p:txBody></p:sp>";
    private static string Flow() => Box(2, 0, 0, PrivateFrom) + Box(3, 3600000, 0, PrivateTo) +
        "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"4\" name=\"PrivateConnection\"/><p:cNvCxnSpPr>" +
        "<a:stCxn id=\"2\" idx=\"3\"/><a:endCxn id=\"3\" idx=\"1\"/></p:cNvCxnSpPr></p:nvCxnSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"1200000\" y=\"300000\"/><a:ext cx=\"2400000\" cy=\"0\"/></a:xfrm>" +
        "<a:prstGeom prst=\"line\"/><a:ln><a:tailEnd type=\"triangle\"/></a:ln></p:spPr></p:cxnSp>";

    private static byte[] Package(string shapes, bool hiddenFirst = false, string relationships = "", string? secondSlide = null)
    {
        const string relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        string Slide(string body, bool hidden) => "<p:sld" + (hidden ? " show=\"0\"" : "") +
            " xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:cSld><p:spTree>" +
            body + "</p:spTree></p:cSld></p:sld>";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rOffice\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
            ["ppt/presentation.xml"] = "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/>" +
                (secondSlide is null ? "" : "<p:sldId id=\"257\" r:id=\"rId2\"/>") + "</p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rId1\" Type=\"slide\" Target=\"slides/slide1.xml\"/>" +
                (secondSlide is null ? "" : "<Relationship Id=\"rId2\" Type=\"slide\" Target=\"slides/slide2.xml\"/>") + "</Relationships>",
            ["ppt/slides/slide1.xml"] = Slide(shapes, hiddenFirst),
            ["ppt/slides/_rels/slide1.xml.rels"] = "<Relationships xmlns=\"" + relNs + "\">" + relationships + "</Relationships>",
        };
        if (secondSlide is not null) parts["ppt/slides/slide2.xml"] = Slide(secondSlide, false);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
                writer.Write(part.Value);
            }
        return output.ToArray();
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docredock-pptx-photo-audit-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(root);
        public string PathFor(string name) => System.IO.Path.Combine(root, name);
        public string Write(string name, byte[] bytes) { var path = PathFor(name); File.WriteAllBytes(path, bytes); return path; }
        public void Dispose()
        {
            var safeRoot = System.IO.Path.GetFullPath(root);
            var tempRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (safeRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(safeRoot))
                Directory.Delete(safeRoot, recursive: true);
        }
    }
}

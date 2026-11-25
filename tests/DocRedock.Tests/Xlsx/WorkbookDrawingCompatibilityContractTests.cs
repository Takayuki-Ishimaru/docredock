using System.IO.Compression;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;

namespace DocRedock.Tests.Xlsx;

/// <summary>Independent regression contracts for ordinary Office compatibility markup and drawing-only sheets.</summary>
public sealed class WorkbookDrawingCompatibilityContractTests
{
    [Fact]
    public void Cover_title_version_date_identifier_and_decoration_keep_their_source_cell_relationships()
    {
        var result = Extract(Anchor(Shape("")), true, """
            <sheetData><row r="2"><c r="B2" t="inlineStr"><is><t>EXAMPLE DOCUMENT</t></is></c></row>
            <row r="5"><c r="B5" t="inlineStr"><is><t>Version</t></is></c><c r="C5" t="inlineStr"><is><t>2.0</t></is></c></row>
            <row r="7"><c r="B7" t="inlineStr"><is><t>Created</t></is></c><c r="C7" t="inlineStr"><is><t>2026-11-18</t></is></c></row>
            <row r="9"><c r="B9" t="inlineStr"><is><t>Document ID</t></is></c><c r="C9" t="inlineStr"><is><t>EXAMPLE-001</t></is></c></row></sheetData>
            <mergeCells count="1"><mergeCell ref="B2:D3"/></mergeCells>
            """);
        var document = ReadableProjectionBuilder.Build(result.Graph).Document;
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        foreach (var text in new[] { "EXAMPLE DOCUMENT", "Version", "2.0", "2026-11-18", "EXAMPLE-001" })
            Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.Contains("data-source-range=\"B2:D3\"", html, StringComparison.Ordinal);
        Assert.Contains("data-source-row=\"9\" data-source-column=\"3\"", html, StringComparison.Ordinal);
        Assert.Contains("data-drawing-kind=\"shape\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_formula_display_reaches_the_source_canvas_and_opens_the_full_cell_list()
    {
        var result = Extract(Anchor(Shape("SOURCE_CAPTION")), true,
            "<sheetData><row r=\"2\"><c r=\"B2\"><f>1+6</f><v>7</v></c></row></sheetData>");
        var document = ReadableProjectionBuilder.Build(result.Graph).Document;
        var shown = ReadableHtmlWriter.WriteDocument(document, new(ShowFormulas: true)).Html;
        var canvas = shown[shown.IndexOf("source-screen-layout", StringComparison.Ordinal)..shown.IndexOf("<details data-kind=\"worksheet-data\"", StringComparison.Ordinal)];
        Assert.Contains("source-cell-formula\">=1+6</code>", canvas, StringComparison.Ordinal);
        Assert.Contains("<details data-kind=\"worksheet-data\" open>", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("=1+6", ReadableHtmlWriter.WriteDocument(document).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sparse_record_fallback_keeps_every_source_cell_on_the_drawing_canvas()
    {
        static string Column(int column) { var value = ""; while (column > 0) { column--; value = (char)('A' + column % 26) + value; column /= 26; } return value; }
        var data = "<sheetData>" + string.Concat(Enumerable.Range(1, 4001).Select(row =>
            $"<row r=\"{row}\"><c r=\"{Column((row - 1) % 1000 + 1)}{row}\" t=\"inlineStr\"><is><t>SPARSE_{row}</t></is></c></row>")) + "</sheetData>";
        var result = Extract(Anchor(Shape("SOURCE_CAPTION")), true, data);
        var document = ReadableProjectionBuilder.Build(result.Graph).Document;
        var section = Assert.IsType<ReadableSection>(Assert.Single(document.Blocks));
        var scene = Assert.Single(section.Blocks.OfType<ReadableWorksheetScene>());
        var grid = Assert.IsType<ReadableTable>(scene.Grid);
        Assert.NotNull(grid.SourceGrid);
        Assert.Equal(4001, grid.SourceGrid.Rows.Sum(row => row.Cells.Count));
        Assert.Equal(1000, grid.SourceGrid.Rows[999].Cells[0].SourceColumn);
        var html = ReadableHtmlWriter.WriteDocument(document).Html;
        var canvas = html[html.IndexOf("source-screen-layout", StringComparison.Ordinal)..html.IndexOf("<details data-kind=\"worksheet-data\"", StringComparison.Ordinal)];
        Assert.Contains("SPARSE_4001", canvas, StringComparison.Ordinal);
        Assert.Equal(4001, System.Text.RegularExpressions.Regex.Matches(canvas, "class=\"source-grid-cell").Count);
        var unbounded = ReadableHtmlWriter.WriteDocument(document with { WorkbookLayout = null });
        Assert.Contains(unbounded.Diagnostics, d => d.Code == "HtmlSourcePaginationLimit");
        var fallback = unbounded.Html[unbounded.Html.IndexOf("source-print-fallback", StringComparison.Ordinal)..];
        Assert.Contains("SPARSE_4001", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_footer_tint_uses_the_retained_source_palette()
    {
        var colors = new string?[] { null, null, null, null, "#FF0000" };
        var positive = WorkbookHeaderFooterParser.Parse("&C&K04+50CONFIDENTIAL", colors).SelectMany(s => s.Runs);
        var negative = WorkbookHeaderFooterParser.Parse("&C&K04-25CONFIDENTIAL", colors).SelectMany(s => s.Runs);
        Assert.Equal("#FF8080", Assert.Single(positive).Color);
        Assert.Equal("#BF0000", Assert.Single(negative).Color);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Alternate_content_picture_is_extracted_once_and_has_a_live_source_image_node(bool wrapperAroundAnchor)
    {
        var anchor = Anchor(wrapperAroundAnchor ? Picture : Alternate(Picture));
        var result = Extract(wrapperAroundAnchor ? Alternate(anchor) : anchor, cells: true);
        var scene = Assert.Single(WorkbookDrawingSceneJson.Read(
            result.Graph.FormatExtensions![WorkbookDrawingSceneJson.ExtensionKey])).Value;
        var picture = Assert.Single(scene.Objects);
        Assert.Equal("picture", picture.Kind);
        Assert.Equal("SOURCE_PICTURE", picture.Text);
        Assert.Equal("/xl/media/image1.png", picture.ImageReference);
        Assert.NotNull(picture.SourceNodeId);
        Assert.Single(result.Graph.Nodes, node => node.Kind == NodeKind.Image && node.Id == picture.SourceNodeId);

        var section = Assert.IsType<ReadableSection>(Assert.Single(ReadableProjectionBuilder.Build(result.Graph).Document.Blocks));
        var projected = Assert.Single(section.Blocks.OfType<ReadableWorksheetScene>());
        var projectedPicture = Assert.Single(projected.Scene.Objects);
        Assert.Equal(picture.Id, projectedPicture.Id);
        Assert.Equal(picture.ImageReference, projectedPicture.ImageReference);
    }

    [Fact]
    public void Alternate_content_shape_inside_an_anchor_retains_its_caption_and_source_position()
    {
        var result = Extract(Anchor(Alternate(Shape("SOURCE_CAPTION"))), cells: true);
        var scene = Assert.Single(WorkbookDrawingSceneJson.Read(
            result.Graph.FormatExtensions![WorkbookDrawingSceneJson.ExtensionKey])).Value;
        var item = Assert.Single(scene.Objects);
        Assert.Equal("SOURCE_CAPTION", item.Text);
        Assert.Equal(10, item.Transform.E);
        Assert.Equal(20, item.Transform.F);
        Assert.Equal(100, item.Width);
        Assert.Equal(40, item.Height);
    }

    [Fact]
    public void Decorative_drawing_only_sheet_keeps_source_paper_print_area_and_footer_after_projection()
    {
        var result = Extract(Anchor(Shape("")), cells: false);
        Assert.DoesNotContain(result.Graph.Nodes, node => node.Kind == NodeKind.Cell);
        var projection = ReadableProjectionBuilder.Build(result.Graph);
        var section = Assert.IsType<ReadableSection>(Assert.Single(projection.Document.Blocks));
        Assert.Single(section.Blocks.OfType<ReadableWorksheetScene>());
        Assert.True(projection.Document.WorkbookLayout.HasValue);
        Assert.True(WorkbookPresentationLayoutJson.TryRead(projection.Document.WorkbookLayout.Value, out var layout));
        var sheet = Assert.Single(layout!.Sheets).Value;
        Assert.Equal("Scene", sheet.Name);
        var print = Assert.IsType<WorkbookPrintSettings>(sheet.Print);
        Assert.Equal(9, print.PaperSize);
        Assert.Equal("landscape", print.Orientation);
        Assert.Contains("$A$1:$D$8", print.Areas!);
        Assert.Equal("&CCONFIDENTIAL &P / &N", print.HeaderFooter!.OddFooter);
        Assert.Contains("CONFIDENTIAL", new ReadableMarkdownSerializer().Serialize(result.Graph), StringComparison.Ordinal);
        var counts = DocRedock.Api.ReadableConversionReportBuilder.Build(result.Graph, [], ReadableProjectionReport.Empty,
            "drawing-only.xlsx", "html", "visible").Counts;
        Assert.Equal(1, Assert.Single(counts, c => c.Kind == "sheets").Included);
    }

    [Theory]
    [InlineData("&C&K04+50CONFIDENTIAL")]
    [InlineData("&C&K04-25CONFIDENTIAL")]
    public void Valid_theme_color_control_is_consumed_and_never_leaks_into_the_printed_footer(string source)
    {
        // RGB resolution may use the source theme; a recognized color control is never printed as text.
        // ECMA-376 Part 1 18.3.1.39 and the SDK EvenHeader remarks specify theme colors as TTSNN.
        var runs = WorkbookHeaderFooterParser.Parse(source).SelectMany(section => section.Runs).ToArray();
        Assert.Equal("CONFIDENTIAL", string.Concat(runs.Select(run => run.Text)));
        Assert.All(runs, run => Assert.Equal('\0', run.Field));
    }

    private static string Alternate(string content) =>
        "<mc:AlternateContent><mc:Choice Requires=\"a\">" + content + "</mc:Choice><mc:Fallback/></mc:AlternateContent>";

    private static string Anchor(string content) =>
        "<xdr:absoluteAnchor><xdr:pos x=\"95250\" y=\"190500\"/><xdr:ext cx=\"952500\" cy=\"381000\"/>" +
        content + "<xdr:clientData/></xdr:absoluteAnchor>";

    private static string Shape(string caption) => """
        <xdr:sp><xdr:nvSpPr><xdr:cNvPr id="1" name="SOURCE_SHAPE"/><xdr:cNvSpPr/></xdr:nvSpPr>
        <xdr:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="952500" cy="381000"/></a:xfrm>
        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="EEEEEE"/></a:solidFill></xdr:spPr>
        """ + (caption.Length == 0 ? "" : "<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>" + caption + "</a:t></a:r></a:p></xdr:txBody>") + "</xdr:sp>";

    private const string Picture = """
        <xdr:pic><xdr:nvPicPr><xdr:cNvPr id="2" name="PICTURE" descr="SOURCE_PICTURE"/><xdr:cNvPicPr/></xdr:nvPicPr>
        <xdr:blipFill><a:blip r:embed="rImage"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>
        <xdr:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="952500" cy="381000"/></a:xfrm>
        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>
        """;

    private static XlsxExtractionResult Extract(string drawing, bool cells, string? sheetData = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Write("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/>
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="png" ContentType="image/png"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>
                """);
            Write("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rWorkbook" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Write("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Scene" sheetId="1" r:id="rSheet"/></sheets><definedNames><definedName name="_xlnm.Print_Area" localSheetId="0">Scene!$A$1:$D$8</definedName></definedNames></workbook>
                """);
            Write("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rSheet" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            var data = cells ? "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>BODY</t></is></c></row>" : "";
            Write("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                {sheetData ?? ("<sheetData>" + data + "</sheetData>")}<pageSetup paperSize="9" orientation="landscape"/><headerFooter><oddFooter>&amp;CCONFIDENTIAL &amp;P / &amp;N</oddFooter></headerFooter><drawing r:id="rDrawing"/></worksheet>
                """);
            Write("xl/worksheets/_rels/sheet1.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDrawing" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/></Relationships>
                """);
            Write("xl/drawings/drawing1.xml", $"""
                <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">{drawing}</xdr:wsDr>
                """);
            Write("xl/drawings/_rels/drawing1.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/></Relationships>
                """);
            using var image = zip.CreateEntry("xl/media/image1.png").Open();
            image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1u8AAAAASUVORK5CYII="));
        }
        stream.Position = 0;
        return new XlsxAdapter().Extract(stream);
    }
}

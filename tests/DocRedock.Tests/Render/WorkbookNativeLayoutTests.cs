using System.Text.RegularExpressions;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Tests.Xlsx;

namespace DocRedock.Tests.Render;

public sealed class WorkbookNativeLayoutTests
{
    [Fact]
    public void Worksheet_overlay_annotations_stay_separate_from_values_even_without_a_formula_cache()
    {
        const string label = "**RAW_OVERLAY** [label](https://example.org)";
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Task</t></is></c><c r="B1" t="inlineStr"><is><t>Day 1</t></is></c><c r="C1" t="inlineStr"><is><t>Day 2</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>Design</t></is></c><c r="B2"><f>ROW()</f></c></row></sheetData>
            """).Graph;
        var overlay = new DocumentNode("source-overlay", NodeKind.Shape, null, 99, ContentLayer.Hidden, new EmptyNodeContent(),
            Extensions: new Dictionary<string, JsonElement> { ["sheet_overlay"] = JsonSerializer.SerializeToElement(new
            { ShapeId = "arrow", Text = label, Kind = "arrow", Direction = "right", Axis = "horizontal", StartRow = 2, EndRow = 2, StartColumn = 2, EndColumn = 3 }) });
        graph = graph with { Partitions = [graph.Partitions[0] with { Nodes = graph.Partitions[0].Nodes.Append(overlay).ToArray() }] };
        var projected = ReadableProjectionBuilder.Build(graph, new(Language: "ja"));
        var document = projected.Document;
        var table = Assert.Single(Assert.Single(document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableTable>());
        var formula = Assert.Single(table.Rows[1].Cells, c => c.SourceColumn == 2);
        Assert.False(formula.Formula!.HasCachedValue);
        Assert.Empty(formula.Children);
        Assert.DoesNotContain(table.Rows.SelectMany(r => r.Cells), c => c.Content.PlainText.Contains(label, StringComparison.Ordinal));
        var notes = Assert.Single(Assert.Single(document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableDetails>(), d => d.Kind == "cell-overlays");
        var note = Assert.Single(notes.Blocks.OfType<ReadableParagraph>());
        Assert.Contains(label, note.Content.PlainText, StringComparison.Ordinal);
        Assert.Equal("B2:C2", note.Origin!.Range);
        Assert.Contains(projected.Diagnostics, d => d.Code == "ReadableOverlayLayout");
        var html = DocRedock.Render.ReadableHtmlWriter.WriteDocument(document).Html;
        Assert.Contains("保存済み計算値なし", html, StringComparison.Ordinal);
        Assert.Contains("**RAW_OVERLAY**", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>RAW_OVERLAY</strong>", html, StringComparison.Ordinal);
        var serializer = new ReadableMarkdownSerializer(new(Language: "ja", SeparateWorksheetAnnotations: true));
        var markdown = serializer.Serialize(graph);
        var annotationStart = markdown.IndexOf("### 図形注記", StringComparison.Ordinal);
        Assert.True(annotationStart >= 0);
        Assert.DoesNotContain("RAW_OVERLAY", markdown[..annotationStart], StringComparison.Ordinal);
        Assert.Contains("RAW\\_OVERLAY", markdown[annotationStart..], StringComparison.Ordinal);
        Assert.Contains("B2:C2", markdown[annotationStart..], StringComparison.Ordinal);
        Assert.Contains(serializer.Diagnostics, d => d.Code == "ReadableOverlayLayout");
    }

    [Theory]
    [InlineData("visible", 2, false)]
    [InlineData("complete", 3, true)]
    public void Hidden_columns_keep_their_original_coordinates_without_reserving_a_visible_blank_column(string policy, int columns, bool hiddenText)
    {
        var graph = WorkbookPresentationTests.Extract("""
            <cols><col min="2" max="2" hidden="1" width="30"/></cols>
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>First</t></is></c><c r="B1" t="inlineStr"><is><t>HIDDEN_COLUMN_VALUE</t></is></c><c r="C1" t="inlineStr"><is><t>Last</t></is></c></row></sheetData>
            """).Graph;
        var result = ReadableProjectionBuilder.Build(graph, new(ContentPolicy: policy));
        var section = Assert.Single(result.Document.Blocks.OfType<ReadableSection>());
        var table = Assert.Single(section.Blocks.OfType<ReadableTable>());
        Assert.Equal(columns, table.Columns.Count);
        Assert.Equal(hiddenText, table.Rows.SelectMany(r => r.Cells).Any(c => c.Content.PlainText == "HIDDEN_COLUMN_VALUE"));
        Assert.Equal(3, table.Columns[^1].SourceIndex);
    }

    [Fact]
    public void Numeric_cells_use_excel_general_alignment_while_explicit_alignment_still_wins()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1"><v>12</v></c><c r="B1" s="1"><v>34</v></c><c r="C1" t="inlineStr"><is><t>56</t></is></c></row></sheetData>
            """).Graph;
        var document = ReadableProjectionBuilder.Build(graph).Document;
        var table = Assert.Single(Assert.Single(document.Blocks.OfType<ReadableSection>()).Blocks.OfType<ReadableTable>());
        Assert.True(table.Rows[0].Cells[0].IsNumeric);
        Assert.True(table.Rows[0].Cells[1].IsNumeric);
        Assert.False(table.Rows[0].Cells[2].IsNumeric);
        Assert.Equal("right", graph.Styles![table.Rows[0].Cells[1].StyleId!].Properties!["text-align"]);
        Assert.Contains("numeric-cell", DocRedock.Render.ReadableHtmlWriter.WriteDocument(document).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Printing_uses_source_paper_ranges_and_titles_without_reusing_screen_freeze_as_print_titles()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetViews><sheetView workbookViewId="0"><pane state="frozen" ySplit="2"/></sheetView></sheetViews>
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>PRINT_HEADER</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>FIRST_RECORD</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>SECOND_RECORD</t></is></c></row></sheetData>
            <pageSetup paperSize="9" orientation="landscape"/>
            """).Graph;
        var document = ReadableProjectionBuilder.Build(graph).Document;
        var html = DocRedock.Render.ReadableHtmlWriter.WriteDocument(document).Html;
        Assert.Contains("size:A4 landscape;", html, StringComparison.Ordinal);
        var printed = Regex.Match(html, "<div class=\"print-copy\">(.*?)</table>", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(printed);
        Assert.DoesNotContain("<thead>", printed, StringComparison.Ordinal);
        Assert.Contains("FIRST_RECORD", printed, StringComparison.Ordinal);
        Assert.Contains("SECOND_RECORD", printed, StringComparison.Ordinal);
    }
}

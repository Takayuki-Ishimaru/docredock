using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Tests.Markdown;
using Markdig.Syntax;
using GfmTable = Markdig.Extensions.Tables.Table;

namespace DocRedock.Tests.Xlsx;

// Regression: worksheet data rows and in-body metadata/caption rows must not be turned into
// headings or reordered by the heading-inference heuristics of ReadableMarkdownSerializer.
public sealed class WorksheetHeadingInferenceRegressionTests
{
    [Fact]
    public void Header_like_words_in_plain_data_rows_do_not_extend_a_styled_header()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "キー", " s=\"1\"")}{Text("B1", "設定値", " s=\"1\"")}</row>" +
            $"<row r=\"2\">{Text("A2", "method")}{Text("B2", "path")}</row>" +
            $"<row r=\"3\">{Text("A3", "token")}{Text("B3", "auth")}</row></sheetData>";
        var markdown = Serialize(WorkbookPresentationTests.Extract(sheet, styles: Styles).Graph, out var serializer);
        Assert.Equal("| キー | 設定値 |", HeaderLine(markdown, serializer));
        var table = Assert.Single(Assert.Single(serializer.WorkbookLayout!.Sheets).Segments, s => s.Table is not null).Table!;
        Assert.Equal(new[] { 1 }, table.HeaderRows);
        Assert.Equal(new[] { 2, 3 }, table.Rows.Select(r => r.Row));
    }

    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    // s=1: bold + solid fill (emphasized header); s=2: centered only (unemphasized data style).
    private const string Styles =
        $"<styleSheet xmlns=\"{Ns}\"><fonts count=\"2\"><font/><font><b/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/></patternFill></fill></fills>" +
        "<cellXfs count=\"3\"><xf fontId=\"0\" fillId=\"0\"/><xf fontId=\"1\" fillId=\"2\"/>" +
        "<xf fontId=\"0\" fillId=\"0\"><alignment horizontal=\"center\"/></xf></cellXfs></styleSheet>";

    private static string Text(string address, string value, string style = "") =>
        $"<c r=\"{address}\"{style} t=\"inlineStr\"><is><t>{value}</t></is></c>";
    private static string Num(string address, double value, string style = "") =>
        $"<c r=\"{address}\"{style}><v>{value}</v></c>";

    private static string Serialize(DocumentGraph graph, out ReadableMarkdownSerializer serializer)
    {
        serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions()) { RecordWorkbookLayout = true };
        return serializer.Serialize(graph);
    }

    private static string HeaderLine(string markdown, ReadableMarkdownSerializer serializer)
    {
        var layout = Assert.Single(serializer.WorkbookLayout!.Sheets);
        var segment = Assert.Single(layout.Segments, item => item.Table is not null);
        return markdown[segment.Start..segment.Table!.HeaderEnd].Split('\n')[0].Trim();
    }

    [Fact]
    public void Emphasized_single_numeric_rows_stay_data_below_their_header()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "Item", " s=\"1\"")}{Text("B1", "Type", " s=\"1\"")}{Text("C1", "Length", " s=\"1\"")}</row>" +
            $"<row r=\"2\">{Text("A2", "ItemA")}{Text("B2", "alpha", " s=\"2\"")}{Num("C2", 8, " s=\"2\"")}</row>" +
            $"<row r=\"3\">{Text("A3", "ItemB")}{Text("B3", "beta", " s=\"2\"")}{Num("C3", 16, " s=\"2\"")}</row>" +
            $"<row r=\"4\">{Text("A4", "ItemC")}{Text("B4", "gamma", " s=\"2\"")}{Num("C4", 32, " s=\"2\"")}</row>" +
            "<row r=\"5\">" + Num("A5", 1) + Num("B5", 100) + Num("C5", 10) + "</row>" +
            "<row r=\"6\">" + Num("A6", 2) + Num("B6", 200) + Num("C6", 20) + "</row>" +
            "</sheetData>";
        var extraction = WorkbookPresentationTests.Extract(sheet, styles: Styles);
        var markdown = Serialize(extraction.Graph, out var serializer);

        Assert.Single(MarkdownStructure.Parse(markdown).Descendants<GfmTable>());
        Assert.Equal("| Item | Type | Length |", HeaderLine(markdown, serializer));
        Assert.DoesNotContain("/", markdown, StringComparison.Ordinal);
        Assert.Contains("| ItemA |", markdown, StringComparison.Ordinal);
        Assert.Contains("| ItemB |", markdown, StringComparison.Ordinal);
        Assert.Contains("| ItemC |", markdown, StringComparison.Ordinal);

        var layout = Assert.Single(serializer.WorkbookLayout!.Sheets);
        var recorded = Assert.Single(layout.Segments, segment => segment.Table is not null).Table!;
        Assert.Equal(new[] { 1 }, recorded.HeaderRows);
        var id = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in extraction.Graph.Nodes)
            if (node.Extensions is not null && node.Extensions.TryGetValue("address", out var address) &&
                address.ValueKind == JsonValueKind.String)
                id[address.GetString()!] = node.Id;
        Assert.Equal(new[] { id["A1"], id["B1"], id["C1"] }, recorded.HeaderNodeIds);
        Assert.DoesNotContain(recorded.HeaderNodeIds, nodeId =>
            new[] { "A2", "B2", "C2", "A3", "B3", "C3", "A4", "B4", "C4" }.Select(address => id[address]).Contains(nodeId));
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, recorded.Rows.Select(row => row.Row).ToArray());
        foreach (var (row, first) in recorded.Rows.Zip(new[] { 2, 3, 4, 5, 6 }))
            Assert.All(new[] { $"A{first}", $"B{first}", $"C{first}" },
                address => Assert.Contains(id[address], row.NodeIds));
    }

    [Fact]
    public void All_bold_text_records_do_not_become_a_multirow_header()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "項目名", " s=\"1\"")}{Text("B1", "属性", " s=\"1\"")}{Text("C1", "桁数", " s=\"1\"")}</row>" +
            string.Concat(Enumerable.Range(2, 3).Select(r => $"<row r=\"{r}\">{Text("A" + r, "項目" + r, " s=\"1\"")}{Text("B" + r, "文字", " s=\"1\"")}{Text("C" + r, (r * 4).ToString(), " s=\"1\"")}</row>")) +
            "</sheetData>";
        var markdown = Serialize(WorkbookPresentationTests.Extract(sheet, styles: Styles).Graph, out var serializer);
        Assert.Equal("| 項目名 | 属性 | 桁数 |", HeaderLine(markdown, serializer));
        var table = Assert.Single(Assert.Single(serializer.WorkbookLayout!.Sheets).Segments, s => s.Table is not null).Table!;
        Assert.Equal(new[] { 1 }, table.HeaderRows);
        Assert.Equal(new[] { 2, 3, 4 }, table.Rows.Select(r => r.Row));
    }

    [Fact]
    public void Metadata_and_instruction_rows_keep_source_order_around_body()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "Status")}{Text("B1", "Operating")}</row>" +
            $"<row r=\"2\">{Text("A2", "1. Verify")}</row>" +
            $"<row r=\"3\">{Text("A3", "Check the seal before use.")}{Text("B3", "Wear gloves.")}</row>" +
            $"<row r=\"4\">{Text("A4", "更新日")}{Text("B4", "2026-11-18")}</row>" +
            $"<row r=\"5\">{Text("A5", "2. Store")}</row>" +
            $"<row r=\"6\">{Text("A6", "Keep dry and cool.")}{Text("B6", "Away from light.")}</row>" +
            "</sheetData>";
        var extraction = WorkbookPresentationTests.Extract(sheet, styles: Styles);
        var markdown = Serialize(extraction.Graph, out _);
        var displayed = MarkdownStructure.DisplayedText(MarkdownStructure.Parse(markdown));

        MarkdownStructure.AssertInOrder(displayed, "Status", "Operating", "1. Verify",
            "Check the seal before use.", "更新日", "2026-11-18", "2. Store", "Keep dry and cool.");
        Assert.Equal(1, displayed.Split("Status", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, displayed.Split("更新日", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Merged_group_header_over_named_columns_is_a_genuine_two_level_header()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "Item", " s=\"1\"")}{Text("B1", "Constraint", " s=\"1\"")}</row>" +
            $"<row r=\"2\">{Text("B2", "Type", " s=\"1\"")}{Text("C2", "Length", " s=\"1\"")}</row>" +
            $"<row r=\"3\">{Text("A3", "ItemA")}{Text("B3", "alpha")}{Num("C3", 8)}</row>" +
            $"<row r=\"4\">{Text("A4", "ItemB")}{Text("B4", "beta")}{Num("C4", 4)}</row>" +
            "</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A1:A2\"/><mergeCell ref=\"B1:C1\"/></mergeCells>";
        var extraction = WorkbookPresentationTests.Extract(sheet, styles: Styles);
        var markdown = Serialize(extraction.Graph, out var serializer);

        Assert.Single(MarkdownStructure.Parse(markdown).Descendants<GfmTable>());
        var header = HeaderLine(markdown, serializer);
        Assert.Contains("Item", header, StringComparison.Ordinal);
        Assert.Contains("Constraint / Type", header, StringComparison.Ordinal);
        Assert.Contains("Constraint / Length", header, StringComparison.Ordinal);
        Assert.Contains("| ItemA |", markdown, StringComparison.Ordinal);
        Assert.Contains("| ItemB |", markdown, StringComparison.Ordinal);
        var layout = Assert.Single(serializer.WorkbookLayout!.Sheets);
        var recorded = Assert.Single(layout.Segments, segment => segment.Table is not null).Table!;
        Assert.Equal(new[] { 1, 2 }, recorded.HeaderRows);
        Assert.Equal(new[] { 3, 4 }, recorded.Rows.Select(row => row.Row).ToArray());
    }

    [Fact]
    public void A_three_level_header_retains_parent_merges_spanning_the_previous_header_row()
    {
        var sheet = "<sheetData>" +
            $"<row r=\"1\">{Text("A1", "項目", " s=\"1\"")}{Text("B1", "売上", " s=\"1\"")}{Text("E1", "利益", " s=\"1\"")}</row>" +
            $"<row r=\"2\">{Text("B2", "国内")}{Text("D2", "海外")}</row>" +
            $"<row r=\"3\">{Text("B3", "Q1")}{Text("C3", "Q2")}{Text("E3", "営業")}{Text("F3", "経常")}</row>" +
            $"<row r=\"4\">{Text("A4", "ItemA")}{Num("B4", 1)}{Num("C4", 2)}{Num("D4", 3)}{Num("E4", 4)}{Num("F4", 5)}</row>" +
            $"<row r=\"5\">{Text("A5", "ItemB")}{Num("B5", 6)}{Num("C5", 7)}{Num("D5", 8)}{Num("E5", 9)}{Num("F5", 10)}</row>" +
            "</sheetData><mergeCells count=\"5\"><mergeCell ref=\"A1:A3\"/><mergeCell ref=\"B1:D1\"/>" +
            "<mergeCell ref=\"E1:F2\"/><mergeCell ref=\"B2:C2\"/><mergeCell ref=\"D2:D3\"/></mergeCells>";
        var extraction = WorkbookPresentationTests.Extract(sheet, styles: Styles);
        var markdown = Serialize(extraction.Graph, out var serializer);

        Assert.Equal("| 項目 | 売上 / 国内 / Q1 | 売上 / 国内 / Q2 | 売上 / 海外 | 利益 / 営業 | 利益 / 経常 |", HeaderLine(markdown, serializer));
        var recorded = Assert.Single(Assert.Single(serializer.WorkbookLayout!.Sheets).Segments, segment => segment.Table is not null).Table!;
        Assert.Equal(new[] { 1, 2, 3 }, recorded.HeaderRows);
        Assert.Equal(new[] { 4, 5 }, recorded.Rows.Select(row => row.Row).ToArray());
    }
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Tests.Docx;

/// <summary>A page break in a paragraph of a table cell gets the same marker as one in a body paragraph. A pipe table cell
/// holds no block, so the Markdown writes it as a note after the table (as it does a cell's picture) and the HTML puts a
/// marker element into the cell; no output writes a separator line inside the table.</summary>
public sealed partial class DocxParagraphDrawingOrderTests
{
    private const string CellMark = "[CELL BREAK]";

    public static TheoryData<string, string, string[]> CellBreakCases => new()
    {
        { "between-runs", $"<w:p><w:r><w:t>first half</w:t></w:r>{Break}<w:r><w:t> second half</w:t></w:r></w:p>", ["first half second half", CellMark, "After table"] },
        { "alone", $"<w:p><w:r><w:t>Cell text</w:t></w:r></w:p><w:p>{Break}</w:p>", ["Cell text", CellMark, "After table"] },
        { "with-picture", "<w:p><w:r><w:t>Cell text</w:t></w:r>" + Frame(false, 51, "PICTURE A", PictureUri, Picture(1)) + Break +
            Frame(false, 52, "PICTURE B", PictureUri, Picture(2)) + "</w:p>", ["Cell text", "PICTURE A", CellMark, "PICTURE B", "After table"] },
    };

    private static string CellTable(string cell) =>
        "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Head</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cells</w:t></w:r></w:p></w:tc></w:tr>" +
        $"<w:tr><w:tc><w:p><w:r><w:t>Row</w:t></w:r></w:p></w:tc><w:tc>{cell}</w:tc></w:tr></w:tbl><w:p><w:r><w:t>After table</w:t></w:r></w:p>";

    private static string[] ReadCell(string output, string[] expected)
    {
        var marked = Regex.Replace(output.ReplaceLineEndings("\n"), "(?m)^> Page break in table: .*$|<hr class=\"page-break in-cell\">", CellMark);
        var found = Regex.Matches(marked, string.Join("|", expected.Distinct().OrderByDescending(text => text.Length).Select(Regex.Escape))).Select(match => match.Value).ToArray();
        return [.. found.Where((text, index) => text == CellMark || index == 0 || found[index - 1] != text)];
    }

    [Theory(Timeout = 30000)]
    [MemberData(nameof(CellBreakCases))]
    public async Task A_page_break_in_a_table_cell_paragraph_is_a_marker_in_the_graph_the_markdown_and_the_html(string name, string cell, string[] expected)
    {
        var output = await Export(WriteDocx("cell-break-" + name, CellTable(cell)));
        var marker = Assert.Single(Nodes(output.Graph), node => node.Kind == NodeKind.PageBreak);
        var table = Assert.Single(Nodes(output.Graph), node => node.Id == marker.ParentId);
        Assert.Equal(NodeKind.Table, table.Kind);
        Assert.Equal(table.Order, marker.Order);
        Assert.Equal("1:1", marker.Source!.Locators.Single(locator => locator.Kind == "table_cell").Value);
        Assert.Equal("0", marker.Source.Locators.Single(locator => locator.Kind == "page_break").Value);
        Assert.Equal(expected, ReadCell(output.Markdown, expected));
        Assert.Equal(expected, ReadCell(output.Html, expected));
        // No separator line in or next to the pipe table, and no body-level marker in the HTML.
        Assert.DoesNotContain(output.Markdown.ReplaceLineEndings("\n").Split('\n'), line => line == "---");
        Assert.DoesNotContain("<hr class=\"page-break\">", output.Html);
        Assert.Contains("| Row |", output.Markdown);
    }

    [Fact(Timeout = 30000)]
    public async Task A_page_break_alone_in_the_only_paragraph_of_a_cell_is_read_before_the_cells_text_and_keeps_the_table()
    {
        var output = await Export(WriteDocx("cell-break-lone", CellTable($"<w:p>{Break}</w:p>")));
        var marker = Assert.Single(Nodes(output.Graph), node => node.Kind == NodeKind.PageBreak);
        Assert.Equal(-2, DocumentSourceOrder.SiblingSequence(marker));
        Assert.Single(Regex.Matches(output.Markdown, "(?m)^> Page break in table: "));
        Assert.Single(Regex.Matches(output.Html, "<hr class=\"page-break in-cell\">"));
        Assert.Contains("| Row |", output.Markdown);
        Assert.Contains("After table", output.Html);
    }

    [Fact(Timeout = 30000)]
    public async Task The_marker_of_a_cell_page_break_changes_no_node_id_and_orders_with_the_cells_pictures()
    {
        static string Pic(string label, int id) => Frame(false, id, label, PictureUri, Picture(id));
        var with = await Export(WriteDocx("cell-ids-with", CellTable("<w:p><w:r><w:t>Cell text</w:t></w:r>" + Pic("PICTURE A", 51) + Break + Pic("PICTURE B", 52) + "</w:p>")));
        var without = await Export(WriteDocx("cell-ids-without", CellTable("<w:p><w:r><w:t>Cell text</w:t></w:r>" + Pic("PICTURE A", 51) + Pic("PICTURE B", 52) + "</w:p>")));
        Assert.Equal(Nodes(without.Graph).Select(node => node.Id).Order(StringComparer.Ordinal),
            Nodes(with.Graph).Where(node => node.Kind != NodeKind.PageBreak).Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["PICTURE A", "break", "PICTURE B"], Nodes(with.Graph).Where(node => node.Kind is NodeKind.Image or NodeKind.PageBreak)
            .InSourceOrder().Select(node => node.Content is ReferenceNodeContent image ? image.AltText! : "break"));
    }

    [Theory(Timeout = 30000)]
    [MemberData(nameof(CellBreakCases))]
    public async Task The_ai_package_lists_a_cell_page_break_with_its_table(string name, string cell, string[] expected)
    {
        var source = WriteDocx("cell-package-" + name, CellTable(cell));
        var target = Path.Combine(root, "cell-package-out-" + name);
        var result = await service.ExportAiPackageAsync(new AiPackageExportOptions(
            new ReadableDocumentExportOptions(source, "unused.md", ContentPolicy: "visible", Language: "en"), target));
        var markdown = string.Concat(result.Parts.Select(part => part.Markdown));
        Assert.Equal(expected, ReadCell(markdown, expected));
        var marker = Assert.Single(Nodes(result.Graph), node => node.Kind == NodeKind.PageBreak);
        Assert.Contains(marker.Id, result.Parts.SelectMany(part => part.NodeIds));
        var manifest = await AiPackageManifestReader.ReadAsync(target);
        var index = await AiPackageManifestReader.ReadSourceIndexAsync(target, manifest);
        Assert.Contains(marker.Id, manifest.Parts.SelectMany(part => index[part.Id]).SelectMany(group => group.NodeIds));
    }

    [Theory(Timeout = 30000)]
    [MemberData(nameof(CellBreakCases))]
    public async Task A_document_with_a_cell_page_break_round_trips_through_the_drmd_byte_identically(string name, string cell, string[] expected)
    {
        Assert.NotEmpty(expected);
        var source = WriteDocx("cell-drmd-" + name, CellTable(cell));
        var markdown = Path.Combine(root, "cell-drmd-" + name + ".md");
        var workspace = Path.Combine(root, "cell-drmd-" + name + ".drmd");
        var restoredPath = Path.Combine(root, "cell-drmd-" + name + "-restored.docx");
        var exported = await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var marker = Assert.Single(Nodes(exported.Graph), node => node.Kind == NodeKind.PageBreak);
        Assert.Contains($"id={marker.Id} ", await File.ReadAllTextAsync(markdown));
        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, restoredPath, markdown));
        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F0, restored.Fidelity);
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(source)), SHA256.HashData(await File.ReadAllBytesAsync(restoredPath)));
    }
}

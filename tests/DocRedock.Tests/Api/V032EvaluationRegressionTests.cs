using DocRedock.Api;

namespace DocRedock.Tests.Api;

/// <summary>
/// Follow-ups to the v0.3.2 evaluation, through the real XLSX reader. A title with unit and
/// condition notes directly above a table stays text above it: it is not the table's header and
/// does not turn a two-column table into label/value pairs. When an AI package cuts that table into
/// row blocks, every later block repeats the notes before the header, so a block read alone still
/// says what its numbers mean, and names the cells they came from.
/// </summary>
public sealed class V032EvaluationRegressionTests : IDisposable
{
    private const string Unit = "売上実績（単位: 千円、税抜）";
    private const string Condition = "対象: 国内全店";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v032-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("captioned_sales.xlsx", "| 商品 | 売上 | 数量 |")]
    [InlineData("captioned_two_columns.xlsx", "| 商品 | 売上 |")]
    public async Task Notes_above_a_table_stay_text_and_its_own_header_stays_the_header(string name, string header)
    {
        var output = Path.Combine(root, name + ".md");
        var result = await service.ExportReadableAsync(new(V031EvaluationRegressionTests.Fixture(name, "V032"), output));
        var markdown = (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains($"\n\n{Unit}\n\n{Condition}\n\n{header}\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| 商品001 | 137 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| " + Unit, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**: ", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("文書情報", markdown, StringComparison.Ordinal);
        Assert.Equal(1, result.Projection.RenderedTables);
    }

    [Theory]
    [InlineData("captioned_sales.xlsx", "A5:C5")]
    [InlineData("captioned_two_columns.xlsx", "A5:B5")]
    public async Task Every_row_block_of_a_cut_table_starts_with_its_unit_and_condition_notes(string name, string headerRange)
    {
        var result = await service.ExportAiPackageAsync(new(new(V031EvaluationRegressionTests.Fixture(name, "V032"), "unused.md"),
            Path.Combine(root, name + ".ai-package"), AiPackageForm.Directory, TargetCharacters: 900, TableRowBlocks: true));
        Assert.True(result.Parts.Count >= 2, result.Parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var rows = new List<string>();
        foreach (var part in result.Parts)
        {
            var block = part.TableBlock!;
            Assert.Equal(("A3:A4", headerRange), (block.ContextRange, block.HeaderRange));
            var markdown = part.Markdown;
            Assert.Equal(markdown.IndexOf(Unit, StringComparison.Ordinal), markdown.LastIndexOf(Unit, StringComparison.Ordinal));
            Assert.True(markdown.IndexOf(Unit, StringComparison.Ordinal) < markdown.IndexOf(Condition, StringComparison.Ordinal));
            Assert.True(markdown.IndexOf(Condition, StringComparison.Ordinal) < markdown.IndexOf("| 商品 | 売上", StringComparison.Ordinal));
            Assert.Contains($"; context A3:A4{(block.Index > 1 ? " (repeated)" : string.Empty)}\n", markdown, StringComparison.Ordinal);
            Assert.Contains(part.Sources, source => source.CellAddress == "A3");
            Assert.Contains(part.Sources, source => source.CellAddress == "A4");
            rows.AddRange(markdown.Split('\n').Where(line => line.StartsWith("| 商品0", StringComparison.Ordinal)));
        }
        // Repeating the notes never repeats, drops or reorders a data row.
        Assert.Equal(Enumerable.Range(1, 80).Select(index => $"商品{index:D3}"),
            rows.Select(line => line.Split(" | ")[0].TrimStart('|', ' ')));
        var manifest = await AiPackageManifestReader.ReadAsync(result.OutputPath);
        Assert.Equal("2.2", manifest.SchemaVersion);
        Assert.Equal(result.Parts.Select(part => part.TableBlock), manifest.Parts.Select(part => part.TableBlock));
        Assert.Empty(await AiPackageManifestReader.VerifyFilesAsync(result.OutputPath, manifest));
    }
}

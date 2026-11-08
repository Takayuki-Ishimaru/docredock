using System.Text;
using DocRedock.Api;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

public sealed class AiPackageSearchMetadataTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-search-metadata-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Search_excludes_generated_source_and_section_metadata_but_finds_literal_body_and_keeps_line_numbers()
    {
        var source = Path.Combine(root, "input.docx");
        await new MarkdownRenderer().RenderAsync("# Report\n\n## Marker heading\n\nsection_path: real body sentinel\n", RenderFormat.Docx, source);
        var target = Path.Combine(root, "package");
        await service.ExportAiPackageAsync(new(new(source, "unused.md", EnableOcr: false), target));
        var package = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        Assert.Contains(package.Parts, part => part.Markdown.Contains("section_path: ", StringComparison.Ordinal));
        Assert.Contains(package.Parts, part => part.Markdown.Contains("ソース: input.docx", StringComparison.Ordinal));
        Assert.Empty(AiPackageSearch.Find(package, "ソース:"));

        var matches = AiPackageSearch.Find(package, "section_path:");
        Assert.NotEmpty(matches);
        foreach (var match in matches)
        {
            var part = Assert.Single(package.Parts, candidate => candidate.Part.Id == match.PartId);
            var lines = part.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            Assert.All(match.Lines, line =>
            {
                Assert.Contains("real body sentinel", line.Text, StringComparison.Ordinal);
                Assert.Equal(lines[line.Number - 1], line.Text);
                Assert.True(line.Number > 2);
            });
        }

        Assert.NotEmpty(AiPackageSearch.Find(package, "real  \n\t body sentinel"));
        Assert.NotEmpty(AiPackageSearch.Find(package, "real<br><br>body sentinel"));
        foreach (var separator in new[] { "\n\n", "<br><br>" })
        {
            var splitText = package with
            {
                Parts = package.Parts.Select(part => part with
                {
                    Markdown = part.Markdown.Replace("real body sentinel", "real" + separator + "body sentinel", StringComparison.Ordinal),
                }).ToArray(),
            };
            Assert.NotEmpty(AiPackageSearch.Find(splitText, "real body sentinel"));
        }
    }

    [Fact]
    public async Task Search_excludes_row_block_metadata_from_verified_export()
    {
        var source = Path.Combine(root, "sheet.xlsx");
        var markdown = new StringBuilder("| Item | Sales | Cost |\n| --- | --- | --- |\n");
        for (var i = 1; i <= 200; i++) markdown.AppendLine($"| item {i} | {i * 10} | {i * 5} |");
        await new MarkdownRenderer().RenderAsync(markdown.ToString(), RenderFormat.Xlsx, source);
        var target = Path.Combine(root, "table-package");
        await service.ExportAiPackageAsync(new(new(source, "unused.md", EnableOcr: false), target,
            AiPackageForm.Directory, 512, TableRowBlocks: true));
        var package = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);
        Assert.Contains(package.Parts, part => part.Markdown.Contains("table_block: ", StringComparison.Ordinal));
        Assert.Empty(AiPackageSearch.Find(package, "table_block:"));
        Assert.NotEmpty(AiPackageSearch.Find(package, "item 100"));
    }

    [Fact]
    public async Task Search_heading_hit_reports_its_own_heading_once()
    {
        var source = Path.Combine(root, "heading.docx");
        await new MarkdownRenderer().RenderAsync("# Report\n\n## Marker heading\n\nBody text\n", RenderFormat.Docx, source);
        var target = Path.Combine(root, "heading-package");
        await service.ExportAiPackageAsync(new(new(source, "unused.md", EnableOcr: false), target));
        var package = await AiPackageManifestReader.ReadVerifiedPackageAsync(target);

        var heading = Assert.Single(AiPackageSearch.Find(package, "Marker heading"));
        var hit = Assert.Single(heading.Lines);
        Assert.Equal("Marker heading", hit.Heading);
        Assert.Contains("Marker heading", hit.Text, StringComparison.Ordinal);
    }
}

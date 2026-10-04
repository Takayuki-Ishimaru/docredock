using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;

namespace DocRedock.Tests.Api;

public sealed class V030EvaluationRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v030-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    private static string Fixture(string name)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Evaluation", "V030", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Evaluation fixture not found.", name);
    }

    [Theory]
    [InlineData("workbook.xlsx", "visible")]
    [InlineData("workbook.xlsx", "complete")]
    [InlineData("workbook_base.xlsx", "visible")]
    public async Task F1_independent_candidate_list_never_belongs_to_revenue_table(string name, string policy)
    {
        var output = Path.Combine(root, "book.md");
        await service.ExportReadableAsync(new(Fixture(name), output, ContentPolicy: policy));
        var markdown = (await File.ReadAllTextAsync(output)).Replace("\\", "", StringComparison.Ordinal);
        Assert.Contains("Revenue", markdown);
        Assert.Contains("CANDIDATE_ONE", markdown);
        Assert.Contains("CANDIDATE_TWO", markdown);
        Assert.DoesNotContain(markdown.Split('\n'), line => line.Contains("Revenue") && line.Contains("CANDIDATE"));
        Assert.DoesNotContain(markdown.Split("\n\n"), block => block.Contains("Revenue") && block.Contains("CANDIDATE"));
        Assert.Contains("Candidates", markdown);
        Assert.Contains("| Item | Q1 | Q2 |", markdown);
        Assert.DoesNotContain("| Financial summary |", markdown);
    }

    [Theory]
    [InlineData("twocol.pdf", 2)]
    [InlineData("onecol.pdf", 1)]
    public async Task F2_columns_finish_the_revenue_policy_before_starting_refund_policy(string name, int columns)
    {
        var result = PdfTextExtractor.Extract(File.ReadAllBytes(Fixture(name)));
        Assert.Equal(columns, Assert.Single(result.Pages).ColumnCount);
        var expected = new[] { "Revenue policy.", "Revenue is recognized", "when delivery is complete.",
            "Refund policy.", "Refunds are accepted", "within thirty days." };
        Assert.Equal(expected, result.Pages[0].Regions.Select(region => region.Text));
        var ids = result.Pages[0].Regions.SelectMany(region => region.SourceTextIds).ToArray();
        Assert.Equal(6, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var output = Path.Combine(root, "policies.md");
        await service.ExportReadableAsync(new(Fixture(name), output));
        var markdown = await File.ReadAllTextAsync(output);
        var previous = -1;
        foreach (var text in expected)
        {
            var index = markdown.IndexOf(text, StringComparison.Ordinal);
            Assert.True(index > previous, markdown);
            previous = index;
        }
        Assert.DoesNotContain("Revenue is recognized Refunds are accepted", markdown);
    }

    [Theory]
    [InlineData("image_space.docx", "Caption: ")]
    [InlineData("image_nospace.docx", "Caption:")]
    public async Task F3_unedited_image_paragraph_has_no_diff_and_allows_a_separate_paragraph_edit(string name, string caption)
    {
        var workspace = Path.Combine(root, "image.drmd");
        var markdown = Path.Combine(root, "image.md");
        var source = Fixture(name);
        await service.ExportAsync(new(source, workspace, markdown));
        var unedited = await service.DiffAsync(workspace, markdown);
        Assert.True(unedited.Edit.IsValid);
        Assert.Empty(unedited.Edit.Diff.PatchSet.Operations);
        var noOp = Path.Combine(root, "noop.docx");
        await service.RestoreAsync(new(workspace, noOp, markdown));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(noOp));
        await File.WriteAllTextAsync(markdown, (await File.ReadAllTextAsync(markdown)).Replace("Editable OLD", "Editable NEW"));
        var edited = await service.DiffAsync(workspace, markdown);
        Assert.True(edited.Edit.IsValid);
        var change = Assert.Single(edited.Edit.Diff.PatchSet.Operations);
        Assert.Equal("Editable OLD", Assert.IsType<TextNodeContent>(change.Before!.Content).Text);
        var restored = Path.Combine(root, "edited.docx");
        Assert.True((await service.RestoreAsync(new(workspace, restored, markdown))).Succeeded);
        using var original = ZipFile.OpenRead(source);
        using var final = ZipFile.OpenRead(restored);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = final.GetEntry("word/document.xml")!.Open();
        var document = XDocument.Load(stream);
        var paragraphs = document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).ToArray();
        Assert.Equal(new[] { caption, "Editable NEW" }, paragraphs);
        Assert.Single(document.Descendants(w + "drawing"));
        foreach (var entry in original.Entries.Where(entry => entry.FullName.StartsWith("word/media/")))
        {
            using var before = entry.Open();
            using var after = final.GetEntry(entry.FullName)!.Open();
            using var beforeBytes = new MemoryStream();
            using var afterBytes = new MemoryStream();
            before.CopyTo(beforeBytes);
            after.CopyTo(afterBytes);
            Assert.Equal(beforeBytes.ToArray(), afterBytes.ToArray());
        }
    }

    [Theory]
    [InlineData("Caption: ")]
    [InlineData("  leading and trailing  ")]
    [InlineData("Text\t")]
    [InlineData("Text\u00a0")]
    public void F3_plain_source_whitespace_is_preserved_and_deliberate_removal_is_an_edit(string text)
    {
        var node = new DocumentNode("n", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent(text));
        var graph = new DocumentGraph("1.1", "spaces", DocumentFormatKind.Docx, [new("p", 0, [node])]);
        var projection = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;
        Assert.Empty(new MarkdownGraphEditor().Apply(graph, projection).Diff.PatchSet.Operations);
        var changed = projection.Replace(text, text.TrimEnd(), StringComparison.Ordinal);
        Assert.Single(new MarkdownGraphEditor().Apply(graph, changed).Diff.PatchSet.Operations);
    }

    [Fact]
    public async Task F4_compact_manifest_and_hashed_source_index_retain_every_node_and_cell()
    {
        var result = await service.ExportAiPackageAsync(new(new(Fixture("scale500.xlsx"), "unused.md"), Path.Combine(root, "scale")));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(result.OutputPath, "manifest.json"));
        var documentBytes = new FileInfo(Path.Combine(result.OutputPath, "document.md")).Length;
        Assert.True(bytes.Length < documentBytes, $"Manifest {bytes.Length} bytes, document {documentBytes} bytes");
        using var manifest = JsonDocument.Parse(bytes);
        var json = manifest.RootElement;
        Assert.Equal("2.0", json.GetProperty("schema_version").GetString());
        var locationIds = json.GetProperty("locations").EnumerateArray().Select(location => location.GetProperty("id").GetString()).ToArray();
        Assert.Single(locationIds);
        var part = Assert.Single(json.GetProperty("parts").EnumerateArray());
        Assert.Equal(result.Parts[0].NodeIds.Count, part.GetProperty("node_count").GetInt32());
        Assert.False(part.TryGetProperty("node_ids", out _));
        Assert.Equal(locationIds, part.GetProperty("source_ids").EnumerateArray().Select(id => id.GetString()));
        var indexPath = json.GetProperty("source_index").GetString()!;
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.OutputPath, indexPath)));
        var group = Assert.Single(Assert.Single(index.RootElement.GetProperty("parts").EnumerateArray()).GetProperty("sources").EnumerateArray());
        Assert.Equal(locationIds[0], group.GetProperty("source_id").GetString());
        var nodeIds = group.GetProperty("node_ids").EnumerateArray().Select(id => id.GetString()).ToArray();
        var addresses = group.GetProperty("cell_addresses").EnumerateArray().Select(address => address.GetString()).ToArray();
        Assert.Equal(result.Parts[0].NodeIds.Cast<string?>(), nodeIds);
        Assert.Equal(nodeIds.Length, addresses.Length);
        Assert.Equal(result.Parts[0].Sources.Select(sourceLocation => sourceLocation.CellAddress), addresses);
        Assert.Contains("A1", addresses);
        Assert.Contains("F501", addresses);
        Assert.DoesNotContain(addresses, string.IsNullOrEmpty);
        var indexedFile = Assert.Single(json.GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == indexPath);
        var indexBytes = await File.ReadAllBytesAsync(Path.Combine(result.OutputPath, indexPath));
        Assert.Equal(indexBytes.Length, indexedFile.GetProperty("bytes").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(indexBytes)), indexedFile.GetProperty("sha256").GetString());
        Assert.Equal(0, result.Summary.NativeTables);
        Assert.Equal(1, result.Summary.RenderedTables);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.OutputPath, "report.json")));
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("native_tables").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("summary").GetProperty("rendered_tables").GetInt32());
    }

    [Fact]
    public async Task F5_table_and_list_parts_carry_their_parent_section_as_metadata()
    {
        var result = await service.ExportAiPackageAsync(new(new(Fixture("chunking.docx"), "unused.md"), Path.Combine(root, "chunking"), TargetCharacters: 128));
        var checkedParts = 0;
        foreach (var part in result.Parts)
        {
            var nodes = part.NodeIds.Select(id => result.Graph.FindNode(id)!).ToArray();
            if (nodes.Any(node => node.Kind == NodeKind.Heading) || !nodes.Any(node => node.Kind is NodeKind.Table or NodeKind.ListItem)) continue;
            var paths = part.Sources.Where(source => source.HeadingPath is { Count: > 0 })
                .Select(source => string.Join(" > ", source.HeadingPath!)).Distinct().ToArray();
            Assert.NotEmpty(paths);
            foreach (var path in paths) Assert.Contains("section_path: " + path, part.Markdown);
            checkedParts++;
        }
        Assert.True(checkedParts >= 2, "Must check both table-only and list-only parts.");
    }

    [Fact]
    public void F6_fenced_table_examples_are_not_counted_as_rendered_tables()
    {
        var graph = new DocumentGraph("1.1", "code", DocumentFormatKind.Docx, [new("p", 0,
        [new("code", NodeKind.CodeBlock, null, 0, ContentLayer.Body, new TextNodeContent("| A | B |\n| --- | --- |\n| 1 | 2 |")),
         new("table", NodeKind.Table, null, 1, ContentLayer.Body, new TableNodeContent([new TableCell[] { "A", "B" }, new TableCell[] { "1", "2" }]))])]);
        var serializer = new ReadableMarkdownSerializer();
        serializer.Serialize(graph);
        Assert.Equal(1, serializer.RenderedTables);
        serializer.Serialize(graph with { Partitions = [] });
        Assert.Equal(0, serializer.RenderedTables);
    }
}

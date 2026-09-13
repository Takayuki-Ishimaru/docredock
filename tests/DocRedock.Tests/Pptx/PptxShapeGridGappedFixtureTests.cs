using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// End-to-end coverage for the "hand-authored-looking" shape-grid-table fixture
/// (schedule-shape-grid-gapped.pptx): the same schedule content as schedule-shape-grid.pptx Slide
/// 1, but drawn with the small sizing/positioning sloppiness a real author leaves behind -- 2pt
/// gaps between every cell, a 工程 label column 20% wider than its header, header cells that stay
/// full height while body rows are trimmed, and a 担当 column 1pt shorter than its row slot --
/// plus two more slides that are negative cases for the "one decorative line/row of non-rectangle
/// shapes near a row of boxes synthesizes a bogus table" bug (G1). Confirms that G1-G9's
/// containment/coverage/adjacency/row-derivation fixes make PptxAdapter.DetectShapeGridTables
/// produce EXACTLY the same table as schedule-shape-grid.pptx Slide 1 despite every one of these
/// imperfections (README.md "schedule-shape-grid-gapped corpus"), that Slides 2 and 3 are correctly
/// NOT synthesized into tables, and that the PowerPoint-saved twin renders identically.
/// </summary>
public sealed class PptxShapeGridGappedFixtureTests
{
    [Fact]
    public async Task Gapped_fixture_slide1_renders_the_identical_table_to_the_flush_fixture()
    {
        var fixture = FindFixture();
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-shape-grid-gapped", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-shape-grid-gapped.md");
            var service = new DocumentService();
            var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(fixture, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Pptx, readable.Graph.Format);
            Assert.Equal(3, readable.Graph.Partitions.Count);

            // --- Slide 1: 隙間のある図形格子 -- 2pt gaps everywhere, a 20%-wider 工程 label
            // column, and a 1pt-shorter 担当 column must still resolve to EXACTLY the same table
            // as schedule-shape-grid.pptx's own flush Slide 1 (no phantom empty row from the
            // header/label seam gap -- G3 -- and no label rendered as a bar via "━" -- G2).
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |\n" +
                "| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  | ▼ |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // No phantom empty row above 要件定義 (the header/first-label-row 2pt seam gap must
            // not produce a zero-content row -- G1 fix 1/G3). The exact-match block above already
            // pins every 工程/担当 label as plain leading-cell text (never a "label ━"-style bar
            // suffix), proving G2's containment + coverage test resolved each one to a genuine
            // grid member instead of letting it fall through to the overlay pipeline.
            Assert.DoesNotContain("|  |  |  |  |  |  |  |  |\n", markdown, StringComparison.Ordinal);

            var slide1 = readable.Graph.Partitions[0];
            var gridTable = Assert.Single(slide1.Nodes, node => node.Kind == NodeKind.Table && node.Extensions?.ContainsKey("shape_grid_table") == true);
            Assert.Equal(6, Assert.IsType<TableNodeContent>(gridTable.Content).Rows.Count);

            // --- Slide 2: 飾り線つきカード（表ではない） -- a single row of 4 KPI cards, a thin
            // decorative line, and an unrelated arrow further below must NOT become a table (G1:
            // a label-less grid now needs >= 2 derived body rows, and neither the line's own
            // degenerate cluster nor a cluster too far from its neighbour can supply the second).
            var slide2 = readable.Graph.Partitions[1];
            Assert.DoesNotContain(slide2.Nodes, node => node.Kind == NodeKind.Table && node.Extensions?.ContainsKey("shape_grid_table") == true);
            Assert.Contains("売上", markdown, StringComparison.Ordinal);
            Assert.Contains("次のステップ", markdown, StringComparison.Ordinal);

            // --- Slide 3: チェブロンのアジェンダ -- 4 chevrons (a valid header row on their own)
            // plus a single trailing arrow must NOT become a table either (G1 fix 2: only 1
            // derivable body row, below the required minimum of 2).
            var slide3 = readable.Graph.Partitions[2];
            Assert.DoesNotContain(slide3.Nodes, node => node.Kind == NodeKind.Table && node.Extensions?.ContainsKey("shape_grid_table") == true);
            Assert.Contains("STEP1", markdown, StringComparison.Ordinal);
            Assert.Contains("次へ", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PowerPoint_saved_variant_produces_identical_readable_markdown()
    {
        var generated = FindFixture();
        var powerPointSaved = FindFixture("schedule-shape-grid-gapped.powerpoint-saved.pptx");
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-shape-grid-gapped", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new DocumentService();
            var generatedPath = Path.Combine(root, "generated.md");
            var savedPath = Path.Combine(root, "powerpoint-saved.md");
            _ = await service.ExportReadableAsync(new ReadableDocumentExportOptions(generated, generatedPath));
            var saved = await service.ExportReadableAsync(new ReadableDocumentExportOptions(powerPointSaved, savedPath));
            var generatedMarkdown = await File.ReadAllTextAsync(generatedPath);
            var savedMarkdown = await File.ReadAllTextAsync(savedPath);

            var gridTables = saved.Graph.Partitions.SelectMany(partition => partition.Nodes)
                .Where(node => node.Kind == NodeKind.Table && node.Extensions?.ContainsKey("shape_grid_table") == true).ToArray();
            Assert.Single(gridTables);
            Assert.All(gridTables, table => Assert.True(table.Geometry is { Width: > 0, Height: > 0 },
                "the PowerPoint-saved deck's grid geometry must resolve to a non-degenerate frame"));
            Assert.Equal(generatedMarkdown, savedMarkdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindFixture(string fileName = "schedule-shape-grid-gapped.pptx")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Pptx", fileName));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Pptx/generate_shape_grid_gapped_pptx.py.");
        return working;
    }
}

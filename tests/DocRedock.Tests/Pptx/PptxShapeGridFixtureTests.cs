using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// End-to-end coverage for the "shape-grid table" feature (schedule-shape-grid.pptx): a Japanese
/// project-schedule deck whose "table" is built from adjacent rectangle shapes (a header row of
/// date rectangles, a label column of process/owner rectangles, an otherwise empty or rect-tiled
/// body) instead of a native a:tbl, with arrow/bar/marker/line/label overlays drawn on top exactly
/// like schedule-arrows.pptx's native-table case. Confirms PptxAdapter.DetectShapeGridTables
/// synthesizes the exact table shape-grid-table-spec.md and schedule-shape-grid.expected.md
/// describe, that ReadableMarkdownSerializer folds its overlays the same way it already does for a
/// native table, that member shapes no longer print as stray bold paragraphs once their host grid
/// table renders, and that Slide 3's negative case (a card layout with no overlays, plus a genuine
/// unrelated flow) is handled correctly.
/// </summary>
public sealed class PptxShapeGridFixtureTests
{
    [Fact]
    public async Task Shape_grid_fixture_renders_the_expected_table_markers_and_suppresses_member_shapes()
    {
        var fixture = FindFixture();
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-shape-grid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-shape-grid.md");
            var service = new DocumentService();
            var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(fixture, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Pptx, readable.Graph.Format);
            Assert.Equal(4, readable.Graph.Partitions.Count);

            // --- Slide 1: 図形で組んだスケジュール -- identical content to schedule-arrows.pptx
            // Slide 1 (same schedule, authored as adjacent rectangles instead of a native table),
            // so once the grid's column/row boundaries are resolved from the header/label
            // rectangles the resulting table is byte-identical to schedule-arrows.expected.md's
            // Slide 1 table, today-line │/▼ markers included (schedule-shape-grid.expected.md).
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |\n" +
                "| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  | ▼ |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // --- Slide 2: 本体セルあり -- same header/labels, a full 5x6 grid of (mostly empty)
            // body rectangles under the date columns, two of which carry real text (済/予定), and
            // the same overlay set as Slide 1 EXCEPT the today-line connector.
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ |  |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | ━━ | ━━ | ━━ | 済 |\n" +
                "| テスト | 田中 |  |  |  | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  |  |  | 予定 | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // --- Slide 3: カード型（表ではない） -- negative case: a 2x3 card layout with zero
            // overlays must NOT be synthesized into a table (guard rail 8b), and the genuine
            // 開始→完了 flow below the cards must still render as a real connector-based diagram
            // (grid/overlay detection on this slide must not consume it).
            var slide3 = readable.Graph.Partitions[2];
            Assert.DoesNotContain(slide3.Nodes, node => node.Kind == NodeKind.Table);
            Assert.Contains("開始", markdown, StringComparison.Ordinal);
            Assert.Contains("完了", markdown, StringComparison.Ordinal);
            Assert.Contains(slide3.Nodes, node => node.Kind == NodeKind.Diagram &&
                node.Extensions != null && node.Extensions.ContainsKey("visual_graph"));

            // --- Slide 4: ラベル列なし -- header row of 6 date rectangles only (no 工程/担当, no
            // label column); rows are derived purely from clustering the overlays' own Y centres.
            Assert.Contains(
                "| 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 ━━ | ━━▶ |  |  |  |  |\n" +
                "|  | 設計 ━━ | ━━ | ━━▶ |  |  |\n" +
                "|  |  | 実装 ━━ | ━━ | ━━▶ |  |\n" +
                "|  |  |  |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // Header/label texts absorbed into the synthesized tables no longer print as their own
            // standalone bold paragraphs (WritePptxParagraphs' bold-title fallback never runs for
            // them) -- each of these strings appears exactly as many times as it legitimately
            // occurs inside a GFM table cell above, never as an extra "**text**" line.
            foreach (var absorbed in new[] { "工程", "担当", "要件定義", "設計", "実装", "テスト", "リリース", "山田", "佐藤", "鈴木", "田中", "全員" })
                Assert.DoesNotContain("**" + absorbed + "**", markdown, StringComparison.Ordinal);
            // The date headers only ever appear inside a table row (never as a standalone heading
            // or bold paragraph); "9/1" alone is enough to prove the header rectangles were
            // absorbed rather than left as independent shape text.
            Assert.DoesNotContain("**9/1**", markdown, StringComparison.Ordinal);
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
        var powerPointSaved = FindFixture("schedule-shape-grid.powerpoint-saved.pptx");
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-shape-grid", Guid.NewGuid().ToString("N"));
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

            var tables = saved.Graph.Partitions.SelectMany(partition => partition.Nodes)
                .Where(node => node.Kind == NodeKind.Table).ToArray();
            Assert.Equal(3, tables.Length);
            Assert.All(tables, table => Assert.True(table.Geometry is { Width: > 0, Height: > 0 },
                "the PowerPoint-saved deck's grid geometry must resolve to a non-degenerate frame"));
            Assert.Equal(generatedMarkdown, savedMarkdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindFixture(string fileName = "schedule-shape-grid.pptx")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Pptx", fileName));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Pptx/generate_shape_grid_pptx.py.");
        return working;
    }
}

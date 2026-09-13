using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;

namespace DocRedock.Tests.Docx;

/// <summary>
/// End-to-end coverage for the "DOCX table overlay" feature (schedule-arrows.docx): a Japanese
/// project-schedule document whose date table has arrow/bar/marker/label DrawingML/VML shapes
/// drawn on top of it. Confirms DocxAdapter's table_overlays/table_overlay_host extensions and
/// the shared ReadableMarkdownSerializer.ApplyTableOverlays together produce the exact cell
/// markers documented in Fixtures/Docx/README.md's "schedule-arrows.docx" section, that the
/// page-anchored today-line and the 30%-overlapping note box are NOT folded into the table (both
/// keep rendering as their own paragraph text, unchanged), that the unrelated 開始/完了 flow still
/// renders, and that the table's own cell text (TableNodeContent, the DRMD/roundtrip source of
/// truth) never gains a glyph -- only the readable projection folds overlays in.
/// </summary>
public sealed class DocxScheduleOverlayFixtureTests
{
    [Fact]
    public async Task Schedule_overlay_fixture_renders_the_expected_table_markers()
    {
        var fixture = FindFixture();
        var root = Path.Combine(Path.GetTempPath(), "docredock-docx-schedule-overlay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-arrows.md");
            var service = new DocumentService();
            var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(fixture, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Docx, readable.Graph.Format);

            // --- Page 1: 開発スケジュール -- every anchoring/preset combination the fixture covers
            // (cell-anchored rightArrow/leftRightArrow/rect/diamond, a txBox label, and a
            // page-anchored connector -- the today-line -- that must NOT show up as a marker at
            // all). See Fixtures/Docx/README.md's "Page 1" table for the shape inventory.
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ |  |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | ━━ | ━━ | ━━ |  |\n" +
                "| テスト | 田中 |  |  |  | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  |  |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // --- Page 2: 回転した矢印 -- rot="10800000" (180 degrees) turns a rightArrow into a
            // left-pointing one, cell-anchored over 9/2-9/3 of 差し戻し.
            Assert.Contains(
                "| 工程 | 9/1 | 9/2 | 9/3 | 9/4 |\n" +
                "| --- | --- | --- | --- | --- |\n" +
                "| 差し戻し |  | 戻し ◀━━ | ━━ |  |\n",
                markdown, StringComparison.Ordinal);

            // The page-anchored note box overlapping only ~30% of page 2's table row band must NOT
            // be folded into a cell -- it keeps rendering as its own standalone text.
            Assert.Contains("※ 実績は毎週更新", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("実績は毎週更新 ", markdown, StringComparison.Ordinal); // never became a cell-marker prefix.

            // The unrelated, non-table-anchored "実データフロー" flow diagram (開始/完了, connected
            // by an unconnected straightConnector1 -- see README's "library limitations") is
            // structurally untouched by table-overlay detection (neither shape sits in a table
            // cell, nor is either the paragraph immediately before a table): both labels still
            // render exactly as they did before this feature existed.
            Assert.Contains("開始", markdown, StringComparison.Ordinal);
            Assert.Contains("完了", markdown, StringComparison.Ordinal);

            // No absorbed cell shape's text doubles as a stray paragraph after the table -- the
            // labels the table now carries (要件定義/設計/テスト/戻し/▲レビュー) never had a
            // paragraph node of their own to begin with (CellText/RelevantDescendants already
            // treated w:txbxContent as opaque before this feature), so there is nothing new to
            // suppress there; this only confirms that stayed true.
            Assert.DoesNotContain("\n要件定義\n", "\n" + markdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Schedule_overlay_fixture_table_cell_text_never_gains_a_glyph()
    {
        // DRMD/roundtrip (DocRedockMarkdown.cs) is untouched by this feature: ApplyTableOverlays
        // only rewrites a COPY of the rows inside ReadableMarkdownSerializer's own output, never
        // the DocumentNode/TableNodeContent the extraction graph carries. Assert that directly
        // instead of a git-stash before/after diff: the actual cell text this adapter produced
        // must contain none of the glyph characters ApplyTableOverlays can ever emit.
        var fixture = FindFixture();
        var export = await new DocxAdapter().ExtractAsync(fixture);
        var glyphs = new[] { '━', '▶', '◀', '─', '│', '▼', '▲', '◆', '●' };

        foreach (var table in export.Graph.Nodes.Where(node => node.Kind == NodeKind.Table))
        {
            var content = Assert.IsType<TableNodeContent>(table.Content);
            foreach (var row in content.Rows)
            foreach (var cell in row)
                Assert.DoesNotContain(cell.Text, character => glyphs.Contains(character));
        }

        // Both tables were actually detected as overlay hosts (otherwise this assertion would be
        // vacuous -- there would be nothing for ApplyTableOverlays to ever have folded in).
        var overlayHosts = export.Graph.Nodes.Count(node => node.Kind == NodeKind.Table &&
            node.Extensions is not null && node.Extensions.ContainsKey("table_overlays"));
        Assert.Equal(2, overlayHosts);
    }

    private static string FindFixture(string fileName = "schedule-arrows.docx")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Docx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Docx", fileName));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Docx/generate_schedule_docx.py.");
        return working;
    }
}

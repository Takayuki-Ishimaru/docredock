using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// End-to-end coverage for the "PPTX table overlay" feature (schedule-arrows.pptx): a Japanese
/// project-schedule deck whose date table has arrow/bar/marker/line/label shapes drawn on top of
/// it. Confirms PptxAdapter's table_overlays/table_overlay_host extensions and
/// ReadableMarkdownSerializer.ApplyTableOverlays together produce the exact cell markers
/// documented in schedule-arrows.expected.md, and that the absorbed overlay shapes no longer
/// print as stray paragraphs (with their rotation annotations) once their host table renders.
/// </summary>
public sealed class PptxScheduleOverlayFixtureTests
{
    [Fact]
    public async Task Schedule_overlay_fixture_renders_the_expected_table_markers_and_suppresses_absorbed_shapes()
    {
        var fixture = FindFixture();
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-schedule-overlay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-arrows.md");
            var service = new DocumentService();
            var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(fixture, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Pptx, readable.Graph.Format);
            Assert.Equal(3, readable.Graph.Partitions.Count);

            // --- Slide 1: 開発スケジュール -- every Kind/Direction/Axis combination the spec
            // defines, plus a today-line connector that spans every row (including the header) and
            // stacks a second marker onto cells the row overlays also cover. See
            // schedule-arrows.expected.md's "Per-cell derivation notes" for why each cell is what
            // it is (in particular the two stacked-marker cells at (row2,9/3) and (row3,9/3)).
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |\n" +
                "| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  | ▼ |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // The absorbed overlay shapes (the 要件定義/設計/テスト arrow textboxes, the ▲レビュー
            // label) no longer print as their own bold paragraphs -- their text now lives only
            // inside the table cells above.
            Assert.DoesNotContain("**要件定義**", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("**設計**", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("**テスト**", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("**▲レビュー**", markdown, StringComparison.Ordinal);

            // --- Slide 2: グループ化されたスケジュール -- the two RIGHT_ARROW overlays live inside a
            // <p:grpSp> with a deliberately non-identity chOff/chExt transform; PptxAdapter must
            // resolve their absolute position through that transform to land on the very same grid
            // cells the ungrouped arrows occupy on slide 1 (compare the two rows below against
            // slide 1's 要件定義/設計 rows -- identical except slide 2 has no today-line connector,
            // so its 9/3 column stays plain). A genuine, natively-connected flow diagram
            // (開始 -> 完了, via begin_connect/end_connect) sits below the table and must still
            // render as a real mermaid flowchart, proving overlay detection does not swallow an
            // unrelated connected diagram that merely shares the slide with a table.
            Assert.Contains("```mermaid\nflowchart", markdown, StringComparison.Ordinal);
            Assert.Contains("開始", markdown, StringComparison.Ordinal);
            Assert.Contains("完了", markdown, StringComparison.Ordinal);
            // F9: a mermaid node id (e.g. "v_7") is an internal rendering detail that can shift
            // with unrelated changes elsewhere on the slide. Resolve the ids ProjectVisualGraph
            // actually assigned to the "開始"/"完了" node-definition lines, then assert an edge
            // line connects them with an arrow, instead of pinning a specific id.
            var mermaidStart = markdown.IndexOf("```mermaid\nflowchart", StringComparison.Ordinal);
            var mermaidEnd = markdown.IndexOf("```", mermaidStart + 3, StringComparison.Ordinal);
            var mermaidLines = markdown[mermaidStart..mermaidEnd].Split('\n');
            static string NodeId(string line)
            {
                var trimmed = line.TrimStart();
                var index = trimmed.IndexOfAny(['[', '(', '{']);
                return index < 0 ? trimmed : trimmed[..index];
            }
            var startNodeId = NodeId(Assert.Single(mermaidLines, line => line.Contains("開始", StringComparison.Ordinal)));
            var endNodeId = NodeId(Assert.Single(mermaidLines, line => line.Contains("完了", StringComparison.Ordinal)));
            Assert.Contains(mermaidLines, line =>
                line.Contains(startNodeId + " -->", StringComparison.Ordinal) && line.TrimEnd().EndsWith(endNodeId, StringComparison.Ordinal));
            Assert.Contains("| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ |  |  |  |  |\n", markdown, StringComparison.Ordinal);
            Assert.Contains("| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ |  |  |\n", markdown, StringComparison.Ordinal);

            // --- Slide 3: 回転した矢印 -- rotation-aware AABB computation.
            // rotation=180 RIGHT_ARROW ("戻し") becomes Direction=left: visible as ◀━━ then ━━.
            Assert.Contains("| 差し戻し |  | 戻し ◀━━ | ━━ | │ |\n", markdown, StringComparison.Ordinal);
            // rotation=90 RIGHT_ARROW becomes a vertical down-arrow spanning rows 1-3 of column
            // 9/4 (its pre-rotation width/height were pre-swapped so the AABB swap lands exactly
            // there): body │ on rows 1-2, head ▼ on row 3 (the last covered row).
            Assert.Contains("| 差し戻し |  | 戻し ◀━━ | ━━ | │ |\n" +
                             "| 設計 | 共通 ━━ | ━━ |  | │ |\n" +
                             "| 実装 | ━━ | ━━ |  | ▼ |",
                markdown, StringComparison.Ordinal);
            // A genuine multi-row x multi-column bar ("共通", rows 2-3 x columns 9/1-9/2): the
            // horizontal-rule glyph applies to every covered row (both the 設計 and 実装 rows
            // above), and the label appears only once, on the first covered row -- already pinned
            // down by the three-row block asserted above ("共通 ━━" on 設計, plain "━━" on 実装).
            Assert.Equal(1, markdown.Split("共通").Length - 1);

            // No rotated shape's own paragraph survives with a "回転…°" annotation once its host
            // table absorbs it -- WritePptxParagraphs is the only place that comment is written,
            // and this fixture's suppression must skip that call entirely for every
            // table_overlay_host shape (slide 3's two rotated arrows included).
            Assert.DoesNotContain("<!-- 回転", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // schedule-arrows.powerpoint-saved.pptx is the generated deck re-saved by Microsoft PowerPoint
    // for Mac (16.x). PowerPoint decorates every a:gridCol / a:tr of a table with an
    // <a:extLst><a:ext uri="…"><a16:colId|rowId …/></a:ext></a:extLst> block *after* the frame's
    // p:xfrm, and every p:cNvPr with an a16:creationId extension. ReadShapes' "ext" handler once
    // matched those uri-only a:ext elements as size elements and zeroed the table's width/height,
    // which silently disabled overlay detection on any deck PowerPoint itself had saved while every
    // synthetic fixture (python-pptx never writes a16:*) kept passing. This pins the real-world XML.
    [Fact]
    public async Task PowerPoint_saved_variant_with_a16_row_and_column_ids_renders_the_same_table_markers()
    {
        var generated = FindFixture();
        var powerPointSaved = FindFixture("schedule-arrows.powerpoint-saved.pptx");
        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-schedule-overlay", Guid.NewGuid().ToString("N"));
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

            // Guard against a vacuous pass: the re-saved deck must itself carry the a16 decoys.
            using var package = System.IO.Compression.ZipFile.OpenRead(powerPointSaved);
            using var slide = new StreamReader(package.GetEntry("ppt/slides/slide1.xml")!.Open());
            var slideXml = await slide.ReadToEndAsync();
            Assert.Contains("a16:colId", slideXml, StringComparison.Ordinal);
            Assert.Contains("a16:rowId", slideXml, StringComparison.Ordinal);

            var table = saved.Graph.Partitions[0].Nodes.Single(node => node.Kind == NodeKind.Table);
            Assert.True(table.Geometry is { Width: > 0, Height: > 0 }, "a16 extLst must not zero the table frame");
            Assert.Contains("| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |\n", savedMarkdown, StringComparison.Ordinal);
            Assert.Equal(generatedMarkdown, savedMarkdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindFixture(string fileName = "schedule-arrows.pptx")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Pptx", fileName));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Pptx/generate_schedule_pptx.py.");
        return working;
    }
}

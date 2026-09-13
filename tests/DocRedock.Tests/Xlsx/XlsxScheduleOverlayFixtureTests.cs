using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// End-to-end coverage for the "XLSX table overlay" feature (schedule-arrows.xlsx): the
/// SpreadsheetML twin of schedule-arrows.pptx -- a Japanese project-schedule table with
/// arrow/bar/marker/today-line DrawingML shapes drawn on top of it. Confirms
/// XlsxAdapter.DetectSheetOverlays's "sheet_overlay" Shape nodes and
/// ReadableMarkdownSerializer.ApplySheetOverlays together produce the same cell markers the PPTX
/// port does (see PptxScheduleOverlayFixtureTests), that a sheet where XlsxMermaidProjection
/// already produced a diagram still folds in the shapes that diagram did NOT consume (P-Overlay
/// F-B), and that absolute/rotated anchors resolve correctly.
/// </summary>
public sealed class XlsxScheduleOverlayFixtureTests
{
    [Fact]
    public async Task Schedule_overlay_fixture_renders_the_expected_table_markers()
    {
        var fixture = FindFixture();
        var root = Path.Combine(Path.GetTempPath(), "docredock-xlsx-schedule-overlay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-arrows.md");
            var service = new DocumentService();
            var readable = await service.ExportReadableAsync(new ReadableDocumentExportOptions(fixture, markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Xlsx, readable.Graph.Format);
            Assert.Equal(3, readable.Graph.Partitions.Count);

            // --- Sheet 1: スケジュール -- every Kind/Direction/Axis combination the spec defines,
            // plus a today-line connector that spans every row (including the header) and stacks a
            // second marker onto cells the row overlays also cover (9/3 on the 設計/実装 rows).
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3<br>│ | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ | │ |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | │<br>━━ | ━━▶ | ▲レビュー |  |\n" +
                "| 実装 | 鈴木 |  |  | │<br>━━ | ━━ | ━━ |  |\n" +
                "| テスト | 田中 |  |  | │ | テスト ◀━━ | ━━ | ━━▶ |\n" +
                "| リリース | 全員 |  |  | ▼ |  |  | ◆ |\n",
                markdown, StringComparison.Ordinal);

            // The absorbed overlay shapes carry no visible text of their own once folded into the
            // table -- the workbook rendering path never prints a Shape node (see
            // ReadableMarkdownSerializer.SerializeWorkbook), so there is no separate paragraph to
            // suppress here the way the PPTX port needs table_overlay_host suppression for.

            // --- Sheet 2: グループ -- XlsxMermaidProjection produced a diagram for this sheet (the
            // real 開始/完了 flow below the table, rows 12-13), but that diagram only consumes the
            // shapes it actually turned into nodes/edges (P-Overlay F-B: XlsxMermaidProjection's
            // "visual_graph_member_shape_ids" convention) -- the grouped 要件定義/設計 arrows over
            // the table (D3:E3 / E4:G4, resolved through a one-level grpSp's non-identity
            // chOff/chExt transform, see F-A) have nothing to do with that flow and must still fold
            // into the table exactly like sheet 1's ungrouped arrows do (minus the today-line/
            // review-note overlays sheet 2 does not have).
            Assert.Contains(
                "| 工程 | 担当 | 9/1 | 9/2 | 9/3 | 9/4 | 9/5 | 9/8 |\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
                "| 要件定義 | 山田 | 要件定義 ━━ | ━━▶ |  |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ |  |  |\n" +
                "| 実装 | 鈴木 |  |  |  |  |  |  |\n" +
                "| テスト | 田中 |  |  |  |  |  |  |\n" +
                "| リリース | 全員 |  |  |  |  |  |  |\n",
                markdown, StringComparison.Ordinal);
            Assert.Contains("```mermaid\nflowchart", markdown, StringComparison.Ordinal);
            Assert.Contains("開始", markdown, StringComparison.Ordinal);
            Assert.Contains("完了", markdown, StringComparison.Ordinal);
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
            // The fixture's a:stCxn/a:endCxn now attach start-to-開始/end-to-完了 (fixed generator
            // bug -- see generate_schedule_xlsx.py build_drawing2), so the rendered edge must read
            // forward (開始 --> 完了), not the reversed 完了 --> 開始 this used to render as.
            Assert.Contains(mermaidLines, line =>
                line.Contains(startNodeId + " -->", StringComparison.Ordinal) && line.TrimEnd().EndsWith(endNodeId, StringComparison.Ordinal));

            // --- Sheet 3: 絶対配置 -- an absoluteAnchor arrow (xdr:pos/xdr:ext in raw EMU, not
            // cell-relative) and a rot="10800000" (180 degree) arrow.
            Assert.Contains(
                "| 要件定義 | 山田 | 戻し ◀━━ | ━━ |  |  |  |  |\n" +
                "| 設計 | 佐藤 |  | 設計 ━━ | ━━ | ━━▶ |  |  |\n",
                markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Schedule_overlay_fixture_does_not_change_drmd_roundtrip_output()
    {
        // P-Overlay (XLSX): the new "sheet_overlay" Shape nodes must be invisible to the generic
        // DRMD/roundtrip projection (see the ContentLayer.Hidden comment on
        // XlsxAdapter.ToSheetOverlayNode and the IsAlwaysReadableSheetOverlay bypass comment on
        // ReadableMarkdownSerializer.Serialize) -- confirm no such node's shape_id/marker/kind
        // leaks into the DRMD markdown text.
        var fixture = FindFixture();
        using var stream = File.OpenRead(fixture);
        var extraction = new DocRedock.Formats.OpenXml.Xlsx.XlsxAdapter().Extract(stream);
        var overlayNodes = extraction.Graph.Nodes.Where(node => node.Kind == NodeKind.Shape &&
            node.Extensions is not null && node.Extensions.ContainsKey("sheet_overlay")).ToArray();
        Assert.NotEmpty(overlayNodes);

        var markdown = new DocRedock.Markdown.DocRedockMarkdownSerializer().Serialize(extraction.Graph).Markdown;
        foreach (var node in overlayNodes)
        {
            Assert.DoesNotContain(node.Id, markdown, StringComparison.Ordinal);
            var shapeId = node.Extensions!["shape_id"].GetString();
            if (!string.IsNullOrEmpty(shapeId)) Assert.DoesNotContain($"shape_id={shapeId}", markdown, StringComparison.Ordinal);
        }
        // Test-quality: the id/shape_id checks above only prove no overlay NODE's own identifiers
        // leak out -- they would not notice a bug that leaked an overlay's rendered MARKER TEXT
        // (the readable-only "▲レビュー" note-marker from the 設計/実装 rows, asserted present in the
        // readable export above) into this generic DRMD projection instead. Assert directly that
        // it does not.
        Assert.DoesNotContain("▲レビュー", markdown, StringComparison.Ordinal);
    }

    private static string FindFixture(string fileName = "schedule-arrows.xlsx")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Xlsx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Xlsx", fileName));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Xlsx/generate_schedule_xlsx.py.");
        return working;
    }
}

using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>
/// End-to-end coverage for the "PDF table overlay" feature (schedule-arrows.pdf), modeled on
/// PptxScheduleOverlayFixtureTests: a Japanese project-schedule document (page 1) plus a small
/// rotated-arrow table (page 2) and an unrelated flow diagram, all reconstructed purely from PDF
/// vector geometry (see PdfTableOverlayDetector / table-overlay-spec-xlsx-docx-pdf.md section 3).
///
/// NOTE: schedule-arrows.pdf's Japanese text uses reportlab's predefined (non-embedded) CID font
/// "HeiseiKakuGo-W5"; PdfTextExtractor's font-map reader does not resolve that font's character
/// codes to Unicode (a pre-existing, unrelated limitation -- confirmed present identically with
/// and without every P-Overlay change in this file's history, and with complex-layout.pdf's own
/// *embedded* TrueType font extracting Japanese text correctly). Every Japanese label in this
/// fixture therefore extracts as mojibake. This file verifies row/column geometry and the actual
/// overlay records directly (immune to that bug), plus the readable Markdown's glyph structure and
/// ASCII content (date headers, "<br>" separators, arrow/marker symbols), never a specific
/// Japanese string.
/// </summary>
public sealed class PdfScheduleOverlayFixtureTests
{
    [Fact]
    public void Page_one_table_infers_six_by_eight_with_the_six_documented_overlays()
    {
        var extraction = PdfTextExtractor.Extract(FixturePath());

        var table = Assert.Single(extraction.Tables![1]);
        Assert.Equal(6, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(8, row.Cells.Count));

        // See generate_schedule_pdf.py --verify for the ground-truth row/column assignment of
        // every overlay on this page (re-derived independently there from the same Grid object
        // used to draw the page, via the identical 50%-overlap rule PdfTableOverlayDetector uses).
        var overlays = table.Overlays!.OrderBy(overlay => overlay.StartRow).ThenBy(overlay => overlay.StartColumn).ToArray();
        Assert.Equal(6, overlays.Length);

        // 本日線 (today line): a full-height down arrow at column 4 (9/3), rows 0-5 (header included).
        var todayLine = Assert.Single(overlays, overlay => overlay.StartRow == 0 && overlay.EndRow == 5);
        Assert.Equal("arrow", todayLine.Kind);
        Assert.Equal("down", todayLine.Direction);
        Assert.Equal("vertical", todayLine.Axis);
        Assert.Equal(4, todayLine.StartColumn);
        Assert.Equal(4, todayLine.EndColumn);

        // 要件定義 rightArrow: row 1, columns 2-3.
        var requirementArrow = Assert.Single(overlays, overlay => overlay.StartRow == 1);
        Assert.Equal("arrow", requirementArrow.Kind);
        Assert.Equal("right", requirementArrow.Direction);
        Assert.Equal(2, requirementArrow.StartColumn);
        Assert.Equal(3, requirementArrow.EndColumn);

        // 設計 rightArrow: row 2, columns 3-5.
        var designArrow = Assert.Single(overlays, overlay => overlay.StartRow == 2);
        Assert.Equal("arrow", designArrow.Kind);
        Assert.Equal("right", designArrow.Direction);
        Assert.Equal(3, designArrow.StartColumn);
        Assert.Equal(5, designArrow.EndColumn);

        // 実装バー: a plain filled bar, row 3, columns 4-6, no direction.
        var implementationBar = Assert.Single(overlays, overlay => overlay.StartRow == 3);
        Assert.Equal("bar", implementationBar.Kind);
        Assert.Equal("none", implementationBar.Direction);
        Assert.Equal(4, implementationBar.StartColumn);
        Assert.Equal(6, implementationBar.EndColumn);

        // テスト leftRightArrow: row 4, columns 5-7, both-direction.
        var testArrow = Assert.Single(overlays, overlay => overlay.StartRow == 4);
        Assert.Equal("arrow", testArrow.Kind);
        Assert.Equal("both", testArrow.Direction);
        Assert.Equal(5, testArrow.StartColumn);
        Assert.Equal(7, testArrow.EndColumn);

        // リリース diamond: row 5, column 7 only.
        var releaseDiamond = Assert.Single(overlays, overlay => overlay.StartRow == 5);
        Assert.Equal("marker", releaseDiamond.Kind);
        Assert.Equal("diamond", releaseDiamond.ShapePreset);
        Assert.Equal(7, releaseDiamond.StartColumn);
        Assert.Equal(7, releaseDiamond.EndColumn);

        // Every overlay's Text is empty: a PDF overlay's label is native page text already
        // assigned to the cell by table-cell text extraction, never carried by the overlay itself.
        Assert.All(overlays, overlay => Assert.Equal(string.Empty, overlay.Text));
    }

    [Fact]
    public void Page_two_table_infers_four_by_five_with_the_rotated_left_arrow_overlay()
    {
        var extraction = PdfTextExtractor.Extract(FixturePath());

        var table = Assert.Single(extraction.Tables![2]);
        Assert.Equal(4, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(5, row.Cells.Count));

        // 戻し: a left-pointing polygon (drawn as a 180-degree-rotated right arrow's own mirrored
        // points -- PDF has no shape-rotation metadata, so this is recognized purely from the
        // resulting geometry), row 1, columns 1-2.
        var overlay = Assert.Single(table.Overlays!);
        Assert.Equal("arrow", overlay.Kind);
        Assert.Equal("left", overlay.Direction);
        Assert.Equal("horizontal", overlay.Axis);
        Assert.Equal(1, overlay.StartRow);
        Assert.Equal(1, overlay.EndRow);
        Assert.Equal(1, overlay.StartColumn);
        Assert.Equal(2, overlay.EndColumn);
    }

    [Fact]
    public void Both_pages_visual_graphs_stay_source_accounting_consistent()
    {
        var extraction = PdfTextExtractor.Extract(FixturePath());

        Assert.True(extraction.VisualProjections![1].Graph.SourceAccounting.IsConsistent);
        Assert.True(extraction.VisualProjections![2].Graph.SourceAccounting.IsConsistent);
    }

    [Fact]
    public async Task Readable_markdown_shows_table_glyphs_the_today_line_and_the_page_two_left_arrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-schedule-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var markdownPath = Path.Combine(root, "schedule-arrows.md");
            var exported = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(FixturePath(), markdownPath));
            var markdown = await File.ReadAllTextAsync(markdownPath);

            Assert.Equal(DocumentFormatKind.Pdf, exported.Graph.Format);

            // Page 1's header row: the 9/3 column (today line, header included) carries "│" beside
            // its date; every other header cell is plain. (The date text itself extracts with a
            // stray NUL byte per character -- e.g. "\09\0/\03" -- from this fixture's predefined
            // CID font; see the class comment. "<br>│ |" is immune to that: it never depends on
            // the preceding date text's own bytes.) Both GFM tables render (6x8 and 4x5).
            Assert.Contains("<br>│ |", markdown, StringComparison.Ordinal);
            Assert.Contains("| --- | --- | --- | --- | --- | --- | --- | --- |", markdown, StringComparison.Ordinal);
            Assert.Contains("| --- | --- | --- | --- | --- |", markdown, StringComparison.Ordinal);

            // The today-line's own column: body "│" on every row it merely passes through, "▼"
            // (arrowhead) on its last covered row (5, リリース's row) -- see the Kind/Direction
            // assertions in Page_one_table_infers_six_by_eight_with_the_six_documented_overlays.
            Assert.Contains("│ |", markdown, StringComparison.Ordinal);
            Assert.Contains("▼", markdown, StringComparison.Ordinal);
            // リリース's diamond marker and the 要件定義/設計 arrowheads/bodies.
            Assert.Contains("◆", markdown, StringComparison.Ordinal);
            Assert.Contains("━━▶", markdown, StringComparison.Ordinal);
            Assert.Contains("◀━━", markdown, StringComparison.Ordinal); // テスト's both-direction arrow head and 戻し's left-arrow head

            // Page 2's 戻し left arrow: "◀━━" on its first covered column, "━━" (no further
            // arrowhead) on its last -- the same left-arrow glyph rule as the unit tests.
            var page2TableStart = markdown.IndexOf("| --- | --- | --- | --- | --- |", StringComparison.Ordinal);
            Assert.True(page2TableStart >= 0);
            var page2Region = markdown[page2TableStart..];
            Assert.Contains("◀━━", page2Region, StringComparison.Ordinal);

            // No overlay member (either page's arrows/bar/diamond/today-line) survives as its own
            // "Visual flow" fallback entry.
            Assert.DoesNotContain("ノード: ", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("パス: ", markdown, StringComparison.Ordinal);

            // Page 2's flow-diagram boxes (開始/完了), positioned well away from the table, still
            // render as a genuine two-node, one-edge Mermaid flow -- proving overlay detection does
            // not swallow an unrelated, actually-connected diagram that merely shares the page with
            // a table (see generate_schedule_pdf.py's box1/box2 flow-diagram boxes). Both node
            // labels extract as mojibake (see the class comment), so this checks the Mermaid
            // structure itself: two node-definition lines and one "-->" edge between them.
            Assert.Contains("```mermaid\nflowchart", markdown, StringComparison.Ordinal);
            var mermaidStart = markdown.IndexOf("```mermaid\nflowchart", StringComparison.Ordinal);
            var mermaidEnd = markdown.IndexOf("```", mermaidStart + 3, StringComparison.Ordinal);
            var mermaidBlock = markdown[mermaidStart..mermaidEnd];
            var nodeLines = mermaidBlock.Split('\n').Where(line => line.Contains('[', StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, nodeLines.Length);
            Assert.Contains("-->", mermaidBlock, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FixturePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", "schedule-arrows.pdf");
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath(Path.Combine("tests/DocRedock.Tests/Fixtures/Pdf", "schedule-arrows.pdf"));
        Assert.True(File.Exists(working), "Generate the fixture with tests/DocRedock.Tests/Fixtures/Pdf/generate_schedule_pdf.py.");
        return working;
    }
}

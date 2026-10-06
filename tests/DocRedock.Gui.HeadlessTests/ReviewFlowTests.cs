using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Gui;
using DocRedock.Render;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

/// <summary>v0.2.9 evaluation, priority 3: from the result panel a person reaches the page to
/// check in one step, sees what is unresolved in short Japanese with page and element counts,
/// and can compare the source image (with the unresolved elements framed) against the Markdown
/// of that page. OCR review is shown separately from warnings.</summary>
public sealed class ReviewFlowTests
{
    private static GuiReviewItem Item(string root, int page, string? imagePath, bool unavailable = false,
        DocumentFormatKind format = DocumentFormatKind.Pdf, params ReviewRegion?[] regions)
    {
        var reviewPage = new ReviewPage(page, $"page-{page:D4}", format,
            regions.Select((region, index) => new ReviewElement(ReviewElementKind.TableDiagonalLine, $"path{index}", region)).ToArray(),
            imagePath is null ? null : "diagonal.assets/page.png", unavailable, HasTables: true);
        return new GuiReviewItem("diagonal.pdf", Path.Combine(root, "diagonal.md"), reviewPage, ExportReviewText.Location(reviewPage),
            ExportReviewText.DescribeJapanese(reviewPage), imagePath, "| Task | Jan |\n| --- | --- |\n| DESIGN |  |\n");
    }

    private static string WritePng(string root, int width = 200, int height = 100)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, (byte)255);
        var path = Path.Combine(root, "page.png");
        File.WriteAllBytes(path, PngRasterImage.Encode(width, height, rgb));
        return path;
    }

    [AvaloniaFact]
    public void Review_window_shows_page_image_with_framed_elements_next_to_the_page_markdown()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-window-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "diagonal.md"), "# doc\n");
            var image = WritePng(root);
            var items = new[]
            {
                Item(root, 1, image, regions: [new ReviewRegion(.25, .5, .4, .2), new ReviewRegion(.1, .1, .05, .05)]),
                Item(root, 3, null, unavailable: true, regions: [null]),
                Item(root, 2, null, format: DocumentFormatKind.Pptx, regions: [null]),
            };
            var window = new ReviewWindow(items);
            try
            {
                Assert.Same(items[0], CurrentItem(window));
                Assert.Equal(2, (int)Property(window, "HighlightCount")!);
                Assert.True(Get<ScrollViewer>(window, "ImageScrollViewer").IsVisible);
                Assert.False(Get<TextBlock>(window, "NoImageText").IsVisible);
                Assert.Equal(items[0].Description, Get<TextBlock>(window, "PageDescriptionText").Text);
                Assert.Contains("| DESIGN |", Get<TextBox>(window, "PageMarkdownTextBox").Text);
                // The rendered view is shown first; the source is one toggle away.
                Assert.True(Get<ScrollViewer>(window, "MarkdownPreviewScroll").IsVisible);
                Assert.False(Get<TextBox>(window, "PageMarkdownTextBox").IsVisible);
                Assert.Contains("赤い線と枠", Get<TextBlock>(window, "LegendText").Text);
                Assert.True(Get<Button>(window, "OpenImageButton").IsVisible);
                Assert.Equal(new[] { "1ページ目", "3ページ目", "スライド2" }, Get<ComboBox>(window, "PageSelector").ItemsSource!.Cast<string>());

                Get<ComboBox>(window, "PageSelector").SelectedIndex = 1;
                Assert.Same(items[1], CurrentItem(window));
                Assert.False(Get<ScrollViewer>(window, "ImageScrollViewer").IsVisible);
                Assert.Contains("原本PDFの3ページ目を開いて確認してください", Get<TextBlock>(window, "NoImageText").Text);
                Assert.False(Get<Button>(window, "OpenImageButton").IsVisible);

                Get<ComboBox>(window, "PageSelector").SelectedIndex = 2;
                Assert.Contains("この形式ではページ画像を作成しません", Get<TextBlock>(window, "NoImageText").Text);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Review_window_loads_self_contained_data_uri_images()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-window-").FullName;
        try
        {
            var dataUri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(WritePng(root, 40, 20)));
            var window = new ReviewWindow([Item(root, 1, dataUri, regions: [new ReviewRegion(0, 0, .5, .5)])]);
            try
            {
                Assert.True(Get<ScrollViewer>(window, "ImageScrollViewer").IsVisible);
                Assert.Equal(1, (int)Property(window, "HighlightCount")!);
                Assert.False(Get<Button>(window, "OpenImageButton").IsVisible);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Result_panel_leads_with_counts_short_explanation_and_one_step_to_the_page()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-panel-").FullName;
        var window = new MainWindow();
        try
        {
            var item = Item(root, 1, WritePng(root), regions: [new ReviewRegion(.25, .5, .4, .2)]);
            SetField(window, "_latestReviewItems", (IReadOnlyList<GuiReviewItem>)[item]);
            SetField(window, "_latestOcrReviewItems", 2);
            var warnings = new[]
            {
                new Diagnostic("VisualConnectorUnresolved", "PDF page 1: Edge endpoint is ambiguous.", DiagnosticSeverity.Warning),
                new Diagnostic("VisualSemanticProjectionUnavailable", "PDF page 1: 1 of 1 connector unresolved", DiagnosticSeverity.Warning),
                new Diagnostic("VisualSemanticProjectionPartial", "Visual metadata did not contain a valid semantic topology (source_object=pdf_p1_e1)", DiagnosticSeverity.Warning),
            };
            Invoke(window, "ShowResult", true, "書き出しが完了しました", "Markdown: x.md", null, warnings);

            Assert.Equal("COMPLETED WITH WARNINGS", Get<TextBlock>(window, "ResultKickerText").Text);
            Assert.Equal("要確認 1ページ／照合画像 1ページ添付／未解決の図形 1件／OCR確認 2件", Get<TextBlock>(window, "ResultCountsText").Text);
            var review = Get<TextBlock>(window, "ResultReviewText").Text!;
            Assert.Equal(item.Description, review);
            Assert.DoesNotContain("source_object", review);
            Assert.DoesNotContain("Visual", review);
            Assert.True(Get<TextBlock>(window, "ResultOcrText").IsVisible);
            Assert.Contains("2件", Get<TextBlock>(window, "ResultOcrText").Text);
            Assert.True(Get<Button>(window, "OpenReviewButton").IsVisible);
            // One cause, one entry: the projection consequences are folded into a single related line.
            var details = Get<TextBox>(window, "DiagnosticsTextBox").Text!;
            Assert.Contains("警告 VisualConnectorUnresolved: PDFの線や矢印", details);
            Assert.Contains("関連 VisualSemanticProjectionPartial／VisualSemanticProjectionUnavailable（記録2件）", details);
            Assert.DoesNotContain("コネクタ端点を図形へ接続", details);

            ReviewWindow? opened = null;
            var presented = 0;
            typeof(MainWindow).GetProperty("ReviewWindowPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, (Action<ReviewWindow>)(shown => { opened = shown; presented++; shown.Show(); }));
            Invoke(window, "OnOpenReview", null, new RoutedEventArgs());
            Assert.NotNull(opened);
            Assert.Same(item, CurrentItem(opened!));
            // A second click brings the same window forward instead of stacking another one.
            Invoke(window, "OnOpenReview", null, new RoutedEventArgs());
            Assert.Equal(1, presented);
            // Starting another export or a restore closes the review of the previous result.
            Invoke(window, "ClearLatestReview");
            Assert.False(opened!.IsVisible);
            Assert.Null(typeof(MainWindow).GetField("_reviewWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Ocr_review_without_warnings_keeps_a_clean_completion()
    {
        var window = new MainWindow();
        try
        {
            SetField(window, "_latestReviewItems", (IReadOnlyList<GuiReviewItem>)[]);
            SetField(window, "_latestOcrReviewItems", 3);
            Invoke(window, "ShowResult", true, "書き出しが完了しました", "Markdown: x.md", null, Array.Empty<Diagnostic>());
            Assert.Equal("COMPLETE", Get<TextBlock>(window, "ResultKickerText").Text);
            Assert.Equal("書き出しが完了しました", Get<TextBlock>(window, "ResultTitleText").Text);
            Assert.False(Get<TextBlock>(window, "ResultReviewText").IsVisible);
            Assert.True(Get<TextBlock>(window, "ResultOcrText").IsVisible);
            Assert.Equal("OCR確認 3件", Get<TextBlock>(window, "ResultCountsText").Text);
            Assert.False(Get<Button>(window, "OpenReviewButton").IsVisible);

            // A failure never advertises review items from a previous run.
            Invoke(window, "ShowResult", false, "書き出しできませんでした", "error", null, Array.Empty<Diagnostic>());
            Assert.False(Get<TextBlock>(window, "ResultOcrText").IsVisible);
            Assert.False(Get<TextBlock>(window, "ResultCountsText").IsVisible);
        }
        finally { window.Close(); }
    }

    // v0.3.1 evaluation, priority 3: the panel names what was detected and how reading order and
    // tables were obtained, and a worksheet table boundary to compare is a hint like OCR, not a warning.
    [AvaloniaFact]
    public void Table_boundaries_and_the_evaluation_line_are_shown_without_turning_the_result_into_a_warning()
    {
        var window = new MainWindow();
        try
        {
            SetField(window, "_latestReviewItems", (IReadOnlyList<GuiReviewItem>)[]);
            SetField(window, "_latestOcrReviewItems", 0);
            SetField(window, "_latestTableBoundaries", (IReadOnlyList<DocRedock.Markdown.ReadableTableBoundaryReview>)
                [new DocRedock.Markdown.ReadableTableBoundaryReview("sheet-Data", "Data", "C1:C3", "A1:B3", "D1:D3")]);
            var summary = new ExportSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RenderedTables: 2, TableBoundaryReviewItems: 1,
                ReadingOrder: StructureBasis.Inferred, TableStructure: StructureBasis.NeedsComparison);
            SetField(window, "_latestEvaluation", (IReadOnlyList<string>)[ExportSummaryText.EvaluationJapanese(summary)]);
            Invoke(window, "ShowResult", true, "書き出しが完了しました", "Markdown: x.md", null, Array.Empty<Diagnostic>());
            Assert.Equal("COMPLETE", Get<TextBlock>(window, "ResultKickerText").Text);
            Assert.Equal("書き出しが完了しました", Get<TextBlock>(window, "ResultTitleText").Text);
            Assert.Equal("表の区切りの確認 1件", Get<TextBlock>(window, "ResultCountsText").Text);
            Assert.True(Get<TextBlock>(window, "ResultTableText").IsVisible);
            Assert.Contains("シート「Data」：C列の空白の左右（A1:B3／D1:D3）を別の表として出力しました。", Get<TextBlock>(window, "ResultTableText").Text);
            Assert.Equal("検出された要確認事項: あり（上記）／読み順: 配置から推定（原本とは未照合）／表構造: 配置から推定・1か所要照合",
                Get<TextBlock>(window, "ResultEvaluationText").Text);
            Assert.False(Get<Button>(window, "OpenReviewButton").IsVisible);

            // Nothing detected still says how reading order and tables were obtained; it never says
            // that no review is needed.
            SetField(window, "_latestTableBoundaries", (IReadOnlyList<DocRedock.Markdown.ReadableTableBoundaryReview>)[]);
            SetField(window, "_latestEvaluation", (IReadOnlyList<string>)[ExportSummaryText.EvaluationJapanese(summary with
                { TableBoundaryReviewItems = 0, TableStructure = StructureBasis.Inferred, ReadingOrder = StructureBasis.Source })]);
            Invoke(window, "ShowResult", true, "書き出しが完了しました", "Markdown: x.md", null, Array.Empty<Diagnostic>());
            Assert.False(Get<TextBlock>(window, "ResultTableText").IsVisible);
            Assert.False(Get<TextBlock>(window, "ResultCountsText").IsVisible);
            Assert.Equal("検出された要確認事項: なし／読み順: 原本の順序／表構造: 配置から推定（原本とは未照合）",
                Get<TextBlock>(window, "ResultEvaluationText").Text);

            Invoke(window, "ShowResult", false, "書き出しできませんでした", "error", null, Array.Empty<Diagnostic>());
            Assert.False(Get<TextBlock>(window, "ResultTableText").IsVisible);
            Assert.False(Get<TextBlock>(window, "ResultEvaluationText").IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Review_image_paths_stay_next_to_the_markdown_or_inside_the_sidecar()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-path-").FullName;
        try
        {
            var markdown = Path.Combine(root, "doc.md");
            Directory.CreateDirectory(Path.Combine(root, "doc.assets"));
            File.WriteAllBytes(Path.Combine(root, "doc.assets", "page-0001.png"), [1]);
            Directory.CreateDirectory(Path.Combine(root, "doc.drmd", "assets"));
            File.WriteAllBytes(Path.Combine(root, "doc.drmd", "assets", "page-0002.png"), [1]);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(root)!, "outside.png"), [1]);
            string? Resolve(string? reference, string? sidecar) => ExportReviewImages.Resolve(reference, markdown, sidecar);

            Assert.Equal(Path.Combine(root, "doc.assets", "page-0001.png"), Resolve("doc.assets/page-0001.png", null));
            Assert.Equal(Path.Combine(root, "doc.drmd", "assets", "page-0002.png"), Resolve("page-0002", Path.Combine(root, "doc.drmd")));
            Assert.Equal("data:image/png;base64,AA==", Resolve("data:image/png;base64,AA==", null));
            Assert.Null(Resolve("../outside.png", null));
            Assert.Null(Resolve(Path.Combine(Path.GetDirectoryName(root)!, "outside.png"), null));
            Assert.Null(Resolve("https://example.invalid/page.png", null));
            Assert.Null(Resolve(null, null));
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(root)!, "outside.png"));
            Directory.Delete(root, true);
        }
    }

    private static GuiReviewItem? CurrentItem(ReviewWindow window) => (GuiReviewItem?)Property(window, "CurrentItem");

    private static object? Property(ReviewWindow window, string name) =>
        typeof(ReviewWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static T Get<T>(Window window, string name) where T : class =>
        (T)(window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException($"Missing generated field: {name}"));

    private static void SetField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
}

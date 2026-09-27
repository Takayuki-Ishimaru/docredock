using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Gui;
using DocRedock.Render;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

/// <summary>v0.2.10 evaluation, priority 2: a small unresolved path is hard to make out on the
/// whole page, so the review window zooms straight to each framed element; and the page Markdown
/// can be read rendered (tables as grids, diagrams as connections) as well as as source.</summary>
public sealed class ReviewZoomAndPreviewTests
{
    private const string PageMarkdown =
        "# 1ページ目（form.pdf）\n\n| Item | Owner |\n| --- | --- |\n| Design | Aoki<br>Sato |\n\n" +
        "FORM\\_TEXT\\_SENTINEL: **approved**\n\n> \\[PDF page 1: note\\]\n\n" +
        "```mermaid\nflowchart LR\n    pdf_p1_n1[START]\n    pdf_p1_n2[END]\n    pdf_p1_n1 -->|UNASSIGNED_LABEL| pdf_p1_n2\n```\n";

    private static GuiReviewItem Item(string root, string imagePath, params ReviewRegion[] regions)
    {
        var page = new ReviewPage(1, "page-0001", DocumentFormatKind.Pdf,
            regions.Select((region, index) => new ReviewElement(ReviewElementKind.Line, $"path{index}", region)).ToArray(),
            "form.assets/page-0001.png", false, HasTables: true);
        return new GuiReviewItem("form.pdf", Path.Combine(root, "form.md"), page, ExportReviewText.Location(page),
            ExportReviewText.DescribeJapanese(page), imagePath, PageMarkdown);
    }

    // A page-sized raster, so a zoomed element really scrolls instead of fitting anyway.
    private static string WritePng(string root, int width = 1224, int height = 1584)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, (byte)255);
        var path = Path.Combine(root, "page.png");
        File.WriteAllBytes(path, PngRasterImage.Encode(width, height, rgb));
        return path;
    }

    [AvaloniaFact]
    public void Zoom_to_element_centers_each_framed_element_in_turn_and_fit_returns_to_the_page()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-zoom-").FullName;
        try
        {
            var small = new ReviewRegion(.45, .48, .04, .02);
            var other = new ReviewRegion(.30, .70, .05, .03);
            var window = new ReviewWindow([Item(root, WritePng(root), small, other)]);
            try
            {
                window.Show();
                window.UpdateLayout();
                var scroll = Get<ScrollViewer>(window, "ImageScrollViewer");
                var surface = Get<Panel>(window, "ImageSurface");
                Assert.True((bool)Property(window, "IsFitMode")!);
                var fit = (double)Property(window, "ZoomScale")!;
                // Fitted: the whole page is inside the pane.
                Assert.True(1224 * fit <= scroll.Bounds.Width + 1 && 1584 * fit <= scroll.Bounds.Height + 1);
                Assert.True(Get<Button>(window, "ZoomToElementButton").IsVisible);
                Assert.False(Get<Button>(window, "FitButton").IsEnabled);

                Click(window, "OnZoomToElement");
                Assert.Equal(0, (int)Property(window, "FocusIndex")!);
                Assert.False((bool)Property(window, "IsFitMode")!);
                var zoomed = (double)Property(window, "ZoomScale")!;
                Assert.True(zoomed > fit * 4, $"zoom {zoomed} vs fit {fit}");
                AssertCentered(surface, scroll, small);
                Assert.Equal("1／2件目", Get<TextBlock>(window, "FocusText").Text);

                Click(window, "OnZoomToElement");
                Assert.Equal(1, (int)Property(window, "FocusIndex")!);
                AssertCentered(surface, scroll, other);
                Click(window, "OnZoomToElement");
                Assert.Equal(0, (int)Property(window, "FocusIndex")!);

                Click(window, "OnFit");
                Assert.True((bool)Property(window, "IsFitMode")!);
                Assert.Equal(fit, (double)Property(window, "ZoomScale")!, 6);
                Assert.Equal(string.Empty, Get<TextBlock>(window, "FocusText").Text);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Switching_pages_returns_to_the_whole_page_with_that_pages_elements()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-zoom-").FullName;
        try
        {
            var image = WritePng(root);
            var second = Item(root, image, new ReviewRegion(.45, .48, .04, .02));
            var thirdItem = Item(root, image, new ReviewRegion(.80, .74, .03, .02), new ReviewRegion(.1, .1, .02, .02));
            var third = thirdItem with { Page = thirdItem.Page with { Number = 3, PartitionId = "page-0003" } };
            var window = new ReviewWindow([second, third]);
            try
            {
                window.Show();
                window.UpdateLayout();
                Click(window, "OnZoomToElement");
                Assert.False((bool)Property(window, "IsFitMode")!);

                Get<ComboBox>(window, "PageSelector").SelectedIndex = 1;
                window.UpdateLayout();
                Assert.True((bool)Property(window, "IsFitMode")!);
                Assert.Equal(-1, (int)Property(window, "FocusIndex")!);
                Assert.Equal(2, ((IReadOnlyList<Rect>)Property(window, "FocusTargets")!).Count);
                Click(window, "OnZoomToElement");
                AssertCentered(Get<Panel>(window, "ImageSurface"), Get<ScrollViewer>(window, "ImageScrollViewer"), new ReviewRegion(.80, .74, .03, .02));
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Zoom_steps_keep_the_center_and_never_go_below_the_page_fit()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-zoom-").FullName;
        try
        {
            var window = new ReviewWindow([Item(root, WritePng(root), new ReviewRegion(.4, .4, .1, .1))]);
            try
            {
                window.Show();
                window.UpdateLayout();
                var fit = (double)Property(window, "ZoomScale")!;
                Click(window, "OnZoomIn");
                Assert.Equal(fit * 1.25, (double)Property(window, "ZoomScale")!, 6);
                Assert.Equal($"{Math.Round(fit * 125):0}%", Get<TextBlock>(window, "ZoomText").Text);
                Assert.False((bool)Property(window, "IsFitMode")!);
                Click(window, "OnZoomOut");
                Click(window, "OnZoomOut");
                Assert.Equal(fit, (double)Property(window, "ZoomScale")!, 6);
                Assert.False(Get<Button>(window, "ZoomOutButton").IsEnabled);
                // All the way out is the whole page again: it follows the pane and 「全体」 has
                // nothing left to do.
                Assert.True((bool)Property(window, "IsFitMode")!);
                Assert.False(Get<Button>(window, "FitButton").IsEnabled);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Page_without_located_elements_or_image_offers_no_zoom_to_element()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-zoom-").FullName;
        try
        {
            var page = new ReviewPage(1, "page-0001", DocumentFormatKind.Pdf, [new ReviewElement(ReviewElementKind.UnanalyzedContent, "form")],
                null, true, HasTables: false);
            var window = new ReviewWindow([new GuiReviewItem("form.pdf", Path.Combine(root, "form.md"), page, "1ページ目",
                ExportReviewText.DescribeJapanese(page), null, "text")]);
            try
            {
                Assert.False(Get<StackPanel>(window, "ZoomBar").IsVisible);
                Assert.False(Get<Button>(window, "ZoomToElementButton").IsVisible);
                Assert.True(Get<TextBlock>(window, "NoImageText").IsVisible);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Markdown_panel_renders_tables_and_diagrams_and_switches_to_the_source()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-preview-").FullName;
        try
        {
            var window = new ReviewWindow([Item(root, WritePng(root, 40, 40), new ReviewRegion(.1, .1, .2, .2))]);
            try
            {
                var preview = Get<ContentControl>(window, "MarkdownPreviewHost");
                var texts = TextsIn(preview);
                Assert.Contains("1ページ目（form.pdf）", texts);
                // Escapes resolve and inline markup is styled, not shown as Markdown syntax.
                Assert.Contains("FORM_TEXT_SENTINEL: approved", texts);
                Assert.Contains("[PDF page 1: note]", texts);
                Assert.Contains("START → END（UNASSIGNED_LABEL）", texts);
                Assert.DoesNotContain(texts, text => text.Contains("flowchart", StringComparison.Ordinal));
                // The table is a grid of cells, with the <br> kept as a line break inside its cell.
                var grid = Assert.Single(((Control)preview.Content!).GetLogicalDescendants().OfType<Grid>(),
                    candidate => candidate.RowDefinitions.Count == 2 && candidate.ColumnDefinitions.Count == 2);
                Assert.Contains("Aoki\nSato", TextsIn(grid));

                Get<RadioButton>(window, "MarkdownSourceRadio").IsChecked = true;
                Assert.False(Get<ScrollViewer>(window, "MarkdownPreviewScroll").IsVisible);
                Assert.True(Get<TextBox>(window, "PageMarkdownTextBox").IsVisible);
                Assert.Contains("```mermaid", Get<TextBox>(window, "PageMarkdownTextBox").Text);
                Get<RadioButton>(window, "MarkdownRenderedRadio").IsChecked = true;
                Assert.True(Get<ScrollViewer>(window, "MarkdownPreviewScroll").IsVisible);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Rendered_view_hides_svg_markup_reads_lazy_quotes_and_keeps_list_numbers()
    {
        const string markdown = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>SVGTEXT</text></svg>\n\n" +
            "> \n一部の接続は図形配置から推定されています。\n\n4. four\n5. five\n- bullet\n";
        var preview = ReviewMarkdownPreview.Build(markdown);

        var texts = TextsIn(preview);
        Assert.Contains("SVGプレビュー（内容は「ソース」で確認できます）", texts);
        Assert.DoesNotContain(texts, text => text.Contains("SVGTEXT", StringComparison.Ordinal) || text.Contains("<svg", StringComparison.Ordinal));
        Assert.Contains("一部の接続は図形配置から推定されています。", texts);
        Assert.DoesNotContain(">", texts);
        Assert.Equal(["4.", "5.", "•"], texts.Where(text => text is "4." or "5." or "•" or "1." or "2." or "3.").ToArray());
    }

    private static void AssertCentered(Panel surface, ScrollViewer scroll, ReviewRegion region)
    {
        var center = new Point((region.X + region.Width / 2) * 1224, (region.Y + region.Height / 2) * 1584);
        var onScreen = surface.TranslatePoint(center, scroll) ?? throw new InvalidOperationException("not visible");
        Assert.InRange(onScreen.X, scroll.Bounds.Width / 2 - 2, scroll.Bounds.Width / 2 + 2);
        Assert.InRange(onScreen.Y, scroll.Bounds.Height / 2 - 2, scroll.Bounds.Height / 2 + 2);
    }

    private static IReadOnlyList<string> TextsIn(ILogical root) => root.GetLogicalDescendants().OfType<TextBlock>()
        .Select(block => block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.Select(inline => inline switch
            {
                Avalonia.Controls.Documents.Run run => run.Text,
                Avalonia.Controls.Documents.LineBreak => "\n",
                _ => string.Empty,
            }))
            : block.Text ?? string.Empty).ToArray();

    private static void Click(ReviewWindow window, string handler)
    {
        typeof(ReviewWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [null, new RoutedEventArgs()]);
        window.UpdateLayout();
    }

    private static object? Property(ReviewWindow window, string name) =>
        typeof(ReviewWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static T Get<T>(Window window, string name) where T : class =>
        (T)(window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException($"Missing generated field: {name}"));
}

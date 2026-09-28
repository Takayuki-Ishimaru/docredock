using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Gui;
using DocRedock.Render;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

/// <summary>v0.2.11 evaluation, section 4: once zoomed in (256% and more), the red fill and stroke
/// grew with the image and covered the very detail they point at. The marks now keep their width on
/// screen at any zoom and can be switched off to look at the original; and the result panel says
/// what the content policy left out of the Markdown.</summary>
public sealed class ReviewHighlightTests
{
    private static GuiReviewItem Item(string root, string imagePath, int number, params ReviewElement[] elements)
    {
        var page = new ReviewPage(number, $"page-{number:D4}", DocumentFormatKind.Pdf, elements, "form.assets/page-0001.png", false, HasTables: false);
        return new GuiReviewItem("form.pdf", System.IO.Path.Combine(root, "form.md"), page, ExportReviewText.Location(page),
            ExportReviewText.DescribeJapanese(page), imagePath, "# page\n\ntext\n");
    }

    private static string WritePng(string root, int width = 1224, int height = 1584)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, (byte)255);
        var path = System.IO.Path.Combine(root, "page.png");
        File.WriteAllBytes(path, PngRasterImage.Encode(width, height, rgb));
        return path;
    }

    private static ReviewElement TracedLine(double x, double y) => new(ReviewElementKind.Line, $"line{x}", new ReviewRegion(x, y, .04, .02),
        Outline: [new ReviewPoint(x, y), new ReviewPoint(x + .04, y + .02)]);

    [AvaloniaFact]
    public void The_marks_can_be_hidden_while_zooming_and_stay_hidden_on_other_pages()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-marks-").FullName;
        try
        {
            var image = WritePng(root);
            var window = new ReviewWindow([Item(root, image, 1, TracedLine(.45, .48), TracedLine(.2, .3)), Item(root, image, 2, TracedLine(.6, .6))]);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True((bool)Property(window, "HighlightsVisible")!);

                Get<CheckBox>(window, "HighlightCheckBox").IsChecked = false;
                window.UpdateLayout();
                Assert.False((bool)Property(window, "HighlightsVisible")!);
                // Finding each element still works with the marks off.
                Click(window, "OnZoomToElement");
                Click(window, "OnZoomToElement");
                Assert.Equal(1, (int)Property(window, "FocusIndex")!);
                Assert.Equal("2／2件目", Get<TextBlock>(window, "FocusText").Text);

                Get<ComboBox>(window, "PageSelector").SelectedIndex = 1;
                window.UpdateLayout();
                Assert.False((bool)Property(window, "HighlightsVisible")!);
                Assert.Equal(1, (int)Property(window, "HighlightCount")!);
                Assert.Contains("「強調表示」", Get<TextBlock>(window, "LegendText").Text, StringComparison.Ordinal);

                Get<CheckBox>(window, "HighlightCheckBox").IsChecked = true;
                window.UpdateLayout();
                Assert.True((bool)Property(window, "HighlightsVisible")!);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void The_marks_keep_their_width_on_screen_at_any_zoom()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-marks-").FullName;
        try
        {
            var window = new ReviewWindow([Item(root, WritePng(root), 1, TracedLine(.45, .48),
                new ReviewElement(ReviewElementKind.Shape, "shape", new ReviewRegion(.1, .1, .1, .1)))]);
            try
            {
                window.Show();
                window.UpdateLayout();
                var fit = (IReadOnlyList<double>)Property(window, "HighlightScreenThicknesses")!;
                Assert.Equal(3, fit.Count);
                Assert.All(fit, width => Assert.InRange(width, .5, 4));

                Click(window, "OnZoomToElement");
                Assert.True((double)Property(window, "ZoomScale")! > 2.5);
                Assert.Equal(fit, (IReadOnlyList<double>)Property(window, "HighlightScreenThicknesses")!, new Tolerance());
                Click(window, "OnZoomIn");
                Click(window, "OnZoomIn");
                Assert.Equal(fit, (IReadOnlyList<double>)Property(window, "HighlightScreenThicknesses")!, new Tolerance());
                Click(window, "OnFit");
                Assert.Equal(fit, (IReadOnlyList<double>)Property(window, "HighlightScreenThicknesses")!, new Tolerance());
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Text_kept_with_a_doubt_is_framed_by_a_dashed_outline_and_named_in_the_legend()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-marks-").FullName;
        try
        {
            var window = new ReviewWindow([Item(root, WritePng(root), 1,
                new ReviewElement(ReviewElementKind.UncertainText, "n#0:F9", new ReviewRegion(.1, .2, .3, .02)))]);
            try
            {
                window.Show();
                window.UpdateLayout();
                var frame = Assert.Single(Get<Canvas>(window, "HighlightCanvas").Children.OfType<Rectangle>());
                Assert.NotNull(frame.StrokeDashArray);
                Assert.Null(frame.Fill);
                Assert.Contains("文字の対応や表示の有無を確定できなかった文字の位置", Get<TextBlock>(window, "LegendText").Text, StringComparison.Ordinal);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_result_panel_names_what_the_content_policy_left_out()
    {
        var text = (string)typeof(MainWindow).GetMethod("HiddenContentText", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new[]
        {
            new Diagnostic("DocxHiddenTextExcluded", "Hidden or deleted DOCX text was excluded (1).", DiagnosticSeverity.Information),
            new Diagnostic("PdfClippedTextExcluded", "PDF text entirely outside the visible area was excluded (2).", DiagnosticSeverity.Information),
            new Diagnostic("PdfTableInferred", "PDF page 1: reconstructed 2x2 table.", DiagnosticSeverity.Information),
            new Diagnostic("VisualConnectorUnresolved", "PDF page 1 edge endpoint is ambiguous.", DiagnosticSeverity.Warning),
        }])!;

        Assert.Equal("表示されない内容を除外しました（Wordの非表示・削除済みの文字 1件、PDFの表示範囲外の文字 2件）。" +
            "含めるには「内容の公開範囲」で「非表示内容も含める」を選んでください。", text);
        Assert.Equal(string.Empty, (string)typeof(MainWindow).GetMethod("HiddenContentText", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Array.Empty<Diagnostic>()])!);
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double left, double right) => Math.Abs(left - right) < 1e-6;
        public int GetHashCode(double value) => 0;
    }

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

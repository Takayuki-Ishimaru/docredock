using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DocRedock.Core.Documents;

namespace DocRedock.Gui;

/// <summary>Side-by-side source comparison for the pages an export could not fully convert: the
/// attached page image with the unresolved elements framed, next to the Markdown that page became.
/// Opened from the result panel's 「該当ページを確認」 button.</summary>
public partial class ReviewWindow : Window
{
    private static readonly IBrush HighlightStroke = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.Parse("#26E5484D"));
    private readonly IReadOnlyList<GuiReviewItem> _items;
    private Bitmap? _bitmap;

    public ReviewWindow() : this([]) { }

    public ReviewWindow(IReadOnlyList<GuiReviewItem> items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        InitializeComponent();
        var multipleSources = items.Select(item => item.MarkdownPath).Distinct(StringComparer.Ordinal).Count() > 1;
        PageSelector.ItemsSource = items.Select(item => multipleSources ? $"{item.SourceName} — {item.Location}" : item.Location).ToArray();
        if (items.Count > 0) PageSelector.SelectedIndex = 0;
        else ShowItem(null);
    }

    /// <summary>The page currently shown; exposed for tests.</summary>
    internal GuiReviewItem? CurrentItem { get; private set; }

    /// <summary>How many unresolved elements are currently marked on the page image.</summary>
    internal int HighlightCount { get; private set; }

    private void OnPageChanged(object? sender, SelectionChangedEventArgs e) =>
        ShowItem(PageSelector.SelectedIndex >= 0 && PageSelector.SelectedIndex < _items.Count ? _items[PageSelector.SelectedIndex] : null);

    private void ShowItem(GuiReviewItem? item)
    {
        CurrentItem = item;
        HighlightCanvas.Children.Clear();
        HighlightCount = 0;
        PageImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        if (item is null)
        {
            PageDescriptionText.Text = "確認が必要なページはありません。";
            PageMarkdownTextBox.Text = string.Empty;
            ShowNoImage("表示する画像はありません。");
            OpenImageButton.IsVisible = false;
            OpenMarkdownButton.IsVisible = false;
            return;
        }
        Title = $"原本照合 — {item.SourceName} {item.Location}";
        PageDescriptionText.Text = item.Description;
        PageMarkdownTextBox.Text = item.PageMarkdown;
        OpenMarkdownButton.IsVisible = File.Exists(item.MarkdownPath);
        OpenImageButton.IsVisible = item.ImagePath is { } path && !path.StartsWith("data:", StringComparison.Ordinal) && File.Exists(path);
        _bitmap = LoadBitmap(item.ImagePath);
        if (_bitmap is null)
        {
            ShowNoImage(NoImageReason(item));
            LegendText.Text = string.Empty;
            return;
        }
        var width = _bitmap.PixelSize.Width;
        var height = _bitmap.PixelSize.Height;
        PageImage.Source = _bitmap;
        foreach (var surface in new Control[] { ImageSurface, PageImage, HighlightCanvas })
        {
            surface.Width = width;
            surface.Height = height;
        }
        var thickness = Math.Max(2, width / 400.0);
        foreach (var element in item.Page.Elements.Where(element => element.Region is not null || element.Outline is { Count: >= 2 }))
        {
            // A light frame shows the area; the element's own stroke, when known, is traced on top
            // so a diagonal line is pointed at exactly rather than by its whole bounding box.
            if (element.Region is { } region)
            {
                var frame = new Rectangle
                {
                    Width = Math.Max(thickness * 3, region.Width * width),
                    Height = Math.Max(thickness * 3, region.Height * height),
                    Stroke = HighlightStroke,
                    StrokeThickness = element.Outline is null ? thickness : thickness / 2,
                    StrokeDashArray = element.Outline is null ? null : [4, 3],
                    Fill = HighlightFill,
                };
                Canvas.SetLeft(frame, region.X * width);
                Canvas.SetTop(frame, region.Y * height);
                HighlightCanvas.Children.Add(frame);
            }
            if (element.Outline is { Count: >= 2 } outline)
                HighlightCanvas.Children.Add(new Polyline
                {
                    Points = outline.Select(point => new Avalonia.Point(point.X * width, point.Y * height)).ToList(),
                    Stroke = HighlightStroke,
                    StrokeThickness = thickness * 2.5,
                    Opacity = .75,
                });
            HighlightCount++;
        }
        LegendText.Text = HighlightCount > 0
            ? "赤い線と枠は、変換できなかった線や図形の位置です。左の原本と右のMarkdownを見比べてください。"
            : "左の原本と右のMarkdownを見比べてください（この画像では位置を枠で示せません）。";
        ImageViewbox.IsVisible = true;
        NoImageText.IsVisible = false;
    }

    private void ShowNoImage(string reason)
    {
        ImageViewbox.IsVisible = false;
        NoImageText.Text = reason;
        NoImageText.IsVisible = true;
    }

    private static string NoImageReason(GuiReviewItem item)
    {
        if (item.Page.Format != DocumentFormatKind.Pdf)
            return $"この形式ではページ画像を作成しません。元のファイルの{item.Location}を開いて確認してください。";
        if (item.Page.ReviewImageUnavailable)
            return $"照合画像を作成できませんでした（PDF rasterizerが未構成、または実行に失敗）。原本PDFの{item.Page.Number}ページ目を開いて確認してください。";
        if (item.Page.ReviewImageReference is null)
            return $"照合画像の添付はオフです。原本PDFの{item.Page.Number}ページ目を開いて確認してください。";
        return "照合画像が見つかりません。移動または削除された可能性があります。原本と見比べてください。";
    }

    private static Bitmap? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (path.StartsWith("data:image/", StringComparison.Ordinal))
            {
                var comma = path.IndexOf(',', StringComparison.Ordinal);
                if (comma < 0 || !path[..comma].EndsWith(";base64", StringComparison.Ordinal)) return null;
                using var memory = new MemoryStream(Convert.FromBase64String(path[(comma + 1)..]), writable: false);
                return new Bitmap(memory);
            }
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A missing or unreadable image falls back to the source-document instructions.
            return null;
        }
    }

    private void OnOpenImage(object? sender, RoutedEventArgs e) => OpenWithShell(CurrentItem?.ImagePath);

    private void OnOpenMarkdown(object? sender, RoutedEventArgs e) => OpenWithShell(CurrentItem?.MarkdownPath);

    private static void OpenWithShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("data:", StringComparison.Ordinal) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        PageImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }
}

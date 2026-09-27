using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Gui;

/// <summary>Side-by-side source comparison for the pages an export could not fully convert: the
/// attached page image with the unresolved elements framed, next to the Markdown that page became.
/// Opened from the result panel's 「該当ページを確認」 button. The image zooms (straight to each
/// framed element, or step by step), and the Markdown shows rendered or as source.</summary>
public partial class ReviewWindow : Window
{
    private static readonly IBrush HighlightStroke = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.Parse("#26E5484D"));
    private const double MaxZoom = 8;
    private const double ZoomStep = 1.25;
    private readonly IReadOnlyList<GuiReviewItem> _items;
    private Bitmap? _bitmap;
    private IReadOnlyList<Rect> _focusTargets = [];
    private int _focusIndex = -1;
    private double _zoom = 1;
    private bool _fitMode = true;

    public ReviewWindow() : this([]) { }

    public ReviewWindow(IReadOnlyList<GuiReviewItem> items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        InitializeComponent();
        // Wheel zoom has to see the event before the scroll viewer turns it into scrolling.
        ImageScrollViewer.AddHandler(PointerWheelChangedEvent, OnImageWheel, RoutingStrategies.Tunnel);
        ImageScrollViewer.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || _bitmap is null) return;
            // A resized pane moves the whole-page scale: follow it when fitted, and otherwise keep
            // the zoom within the new bounds (and the buttons in step with them).
            ApplyZoom(_fitMode ? FitZoom() : _zoom);
        };
        var multipleSources = items.Select(item => item.MarkdownPath).Distinct(StringComparer.Ordinal).Count() > 1;
        PageSelector.ItemsSource = items.Select(item => multipleSources ? $"{item.SourceName} — {item.Location}" : item.Location).ToArray();
        if (items.Count > 0) PageSelector.SelectedIndex = 0;
        else ShowItem(null);
    }

    /// <summary>The page currently shown; exposed for tests.</summary>
    internal GuiReviewItem? CurrentItem { get; private set; }

    /// <summary>How many unresolved elements are currently marked on the page image.</summary>
    internal int HighlightCount { get; private set; }

    /// <summary>The image scale currently applied (1 = one image pixel per unit).</summary>
    internal double ZoomScale => _zoom;

    /// <summary>Whether the image is scaled to fit the pane rather than zoomed by hand.</summary>
    internal bool IsFitMode => _fitMode;

    /// <summary>Which framed element the last 「要確認箇所へ拡大」 zoomed to, or -1.</summary>
    internal int FocusIndex => _focusIndex;

    /// <summary>The framed elements in image pixels, in the order the zoom button visits them.</summary>
    internal IReadOnlyList<Rect> FocusTargets => _focusTargets;

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
        _focusTargets = [];
        _focusIndex = -1;
        _fitMode = true;
        if (item is null)
        {
            PageDescriptionText.Text = "確認が必要なページはありません。";
            PageMarkdownTextBox.Text = string.Empty;
            MarkdownPreviewHost.Content = null;
            ShowNoImage("表示する画像はありません。");
            OpenImageButton.IsVisible = false;
            OpenMarkdownButton.IsVisible = false;
            UpdateZoomControls();
            return;
        }
        Title = $"原本照合 — {item.SourceName} {item.Location}";
        PageDescriptionText.Text = item.Description;
        PageMarkdownTextBox.Text = item.PageMarkdown;
        MarkdownPreviewHost.Content = ReviewMarkdownPreview.Build(item.PageMarkdown);
        OpenMarkdownButton.IsVisible = File.Exists(item.MarkdownPath);
        OpenImageButton.IsVisible = item.ImagePath is { } path && !path.StartsWith("data:", StringComparison.Ordinal) && File.Exists(path);
        _bitmap = LoadBitmap(item.ImagePath);
        if (_bitmap is null)
        {
            ShowNoImage(NoImageReason(item));
            LegendText.Text = string.Empty;
            UpdateZoomControls();
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
        var targets = new List<Rect>();
        foreach (var element in item.Page.Elements.Where(element => element.Region is not null || element.Outline is { Count: >= 2 }))
        {
            // A light frame shows the area; the element's own stroke, when known, is traced on top
            // so a diagonal line is pointed at exactly rather than by its whole bounding box.
            if (element.Region is { } region)
            {
                // Unanalyzed content is framed by the area it may paint in (often the whole page),
                // not by what it painted, so it gets a dashed outline without a tint over the page.
                var bounded = element.Kind == ReviewElementKind.UnanalyzedContent;
                var frame = new Rectangle
                {
                    Width = Math.Max(thickness * 3, region.Width * width),
                    Height = Math.Max(thickness * 3, region.Height * height),
                    Stroke = HighlightStroke,
                    StrokeThickness = element.Outline is null ? thickness : thickness / 2,
                    StrokeDashArray = element.Outline is null && !bounded ? null : [4, 3],
                    Fill = bounded ? null : HighlightFill,
                };
                Canvas.SetLeft(frame, region.X * width);
                Canvas.SetTop(frame, region.Y * height);
                HighlightCanvas.Children.Add(frame);
            }
            if (element.Outline is { Count: >= 2 } outline)
                HighlightCanvas.Children.Add(new Polyline
                {
                    Points = outline.Select(point => new Point(point.X * width, point.Y * height)).ToList(),
                    Stroke = HighlightStroke,
                    StrokeThickness = thickness * 2.5,
                    Opacity = .75,
                });
            targets.Add(FocusRect(element, width, height));
            HighlightCount++;
        }
        _focusTargets = targets;
        LegendText.Text = HighlightCount > 0
            ? item.Page.Elements.Any(element => element.Kind == ReviewElementKind.UnanalyzedContent)
                ? "赤い線と枠は、変換できなかった線や図形、または解析できなかった描画部品が描かれる範囲です。「要確認箇所へ拡大」で順に拡大できます。左の原本と右のMarkdownを見比べてください。"
                : "赤い線と枠は、変換できなかった線や図形の位置です。「要確認箇所へ拡大」で順に拡大できます。左の原本と右のMarkdownを見比べてください。"
            : "左の原本と右のMarkdownを見比べてください（この画像では位置を枠で示せません）。";
        ImageScrollViewer.IsVisible = true;
        NoImageText.IsVisible = false;
        ApplyZoom(FitZoom());
    }

    /// <summary>The pixel rectangle one element occupies: its frame, or its traced stroke's extent.</summary>
    private static Rect FocusRect(ReviewElement element, double width, double height)
    {
        if (element.Region is { } region)
            return new Rect(region.X * width, region.Y * height, region.Width * width, region.Height * height);
        var points = element.Outline!;
        var left = points.Min(point => point.X) * width;
        var top = points.Min(point => point.Y) * height;
        return new Rect(left, top, points.Max(point => point.X) * width - left, points.Max(point => point.Y) * height - top);
    }

    private void OnZoomToElement(object? sender, RoutedEventArgs e)
    {
        if (_bitmap is null || _focusTargets.Count == 0) return;
        _focusIndex = (_focusIndex + 1) % _focusTargets.Count;
        var target = _focusTargets[_focusIndex];
        // Leave enough of the surroundings to recognize where on the page the element sits.
        var margin = Math.Max(24, Math.Max(target.Width, target.Height) * .5);
        var framed = target.Inflate(margin).Intersect(new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height));
        if (framed.Width <= 0 || framed.Height <= 0) framed = target.Inflate(margin);
        var viewport = ViewportSize();
        var scale = Math.Min(viewport.Width / framed.Width, viewport.Height / framed.Height);
        // An element that spans (nearly) the whole page is best seen with the whole page.
        _fitMode = scale <= FitZoom() * 1.05;
        if (_fitMode) ApplyZoom(FitZoom());
        else ApplyZoom(scale, framed.Center, new Point(viewport.Width / 2, viewport.Height / 2));
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => StepZoom(ZoomStep);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => StepZoom(1 / ZoomStep);

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        _fitMode = true;
        _focusIndex = -1;
        ApplyZoom(FitZoom());
    }

    // Steps keep the point at the center of the pane where it is.
    private void StepZoom(double factor)
    {
        if (_bitmap is null) return;
        var viewport = ViewportSize();
        var center = new Point(viewport.Width / 2, viewport.Height / 2);
        _fitMode = false;
        ApplyZoom(_zoom * factor, ContentPoint(center), center);
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap is null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0) return;
        var pointer = e.GetPosition(ImageScrollViewer);
        _fitMode = false;
        ApplyZoom(_zoom * (e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep), ContentPoint(pointer), pointer);
        e.Handled = true;
    }

    /// <summary>Where a point of the pane falls on the image, in image pixels.</summary>
    private Point ContentPoint(Point viewportPoint)
    {
        var origin = ImageZoom.TranslatePoint(new Point(0, 0), ImageScrollViewer) ?? default;
        return new Point((viewportPoint.X - origin.X) / _zoom, (viewportPoint.Y - origin.Y) / _zoom);
    }

    private Size ViewportSize()
    {
        var bounds = ImageScrollViewer.Bounds.Size;
        // Before the first layout pass the pane has no size yet; the window's own size is the
        // closest honest guess, and the fit is recomputed as soon as the pane is measured.
        return bounds.Width > 1 && bounds.Height > 1 ? bounds : new Size(Math.Max(1, Width * .55), Math.Max(1, Height * .7));
    }

    private double FitZoom()
    {
        if (_bitmap is null) return 1;
        var viewport = ViewportSize();
        return Math.Max(.01, Math.Min((viewport.Width - 2) / _bitmap.PixelSize.Width, (viewport.Height - 2) / _bitmap.PixelSize.Height));
    }

    /// <summary>Scales the image and, when an anchor is given, scrolls so that image point
    /// <paramref name="contentPoint"/> lands on pane point <paramref name="viewportPoint"/>.</summary>
    private void ApplyZoom(double scale, Point? contentPoint = null, Point? viewportPoint = null)
    {
        var fit = FitZoom();
        var minimum = Math.Min(fit, 1);
        _zoom = Math.Clamp(scale, minimum, Math.Max(MaxZoom, minimum));
        // Zooming all the way out lands on the whole page, which from then on follows the pane
        // exactly as after 「全体」.
        if (!_fitMode && Math.Abs(_zoom - fit) < 1e-9) _fitMode = true;
        if (ImageZoom.LayoutTransform is not ScaleTransform current || current.ScaleX != _zoom || current.ScaleY != _zoom)
            ImageZoom.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        if (contentPoint is { } content && viewportPoint is { } anchor)
        {
            // The extent only changes after a layout pass; scroll against the new one.
            ImageScrollViewer.UpdateLayout();
            var extent = ImageScrollViewer.Extent;
            var viewport = ImageScrollViewer.Viewport;
            ImageScrollViewer.Offset = new Vector(
                Math.Clamp(content.X * _zoom - anchor.X, 0, Math.Max(0, extent.Width - viewport.Width)),
                Math.Clamp(content.Y * _zoom - anchor.Y, 0, Math.Max(0, extent.Height - viewport.Height)));
        }
        else if (_fitMode) ImageScrollViewer.Offset = default;
        UpdateZoomControls();
    }

    private void UpdateZoomControls()
    {
        var hasImage = _bitmap is not null;
        ZoomBar.IsVisible = hasImage;
        ZoomText.Text = hasImage ? $"{Math.Round(_zoom * 100):0}%" : string.Empty;
        ZoomToElementButton.IsVisible = _focusTargets.Count > 0;
        ZoomOutButton.IsEnabled = hasImage && _zoom > Math.Min(FitZoom(), 1) + 1e-6;
        ZoomInButton.IsEnabled = hasImage && _zoom < MaxZoom - 1e-6;
        FitButton.IsEnabled = hasImage && !_fitMode;
        FocusText.Text = _focusIndex >= 0 && _focusTargets.Count > 0 ? $"{_focusIndex + 1}／{_focusTargets.Count}件目" : string.Empty;
    }

    private void OnMarkdownViewChanged(object? sender, RoutedEventArgs e)
    {
        // Both radio buttons raise this; the view follows whichever is now checked.
        var rendered = MarkdownRenderedRadio.IsChecked == true;
        MarkdownPreviewScroll.IsVisible = rendered;
        PageMarkdownTextBox.IsVisible = !rendered;
    }

    private void ShowNoImage(string reason)
    {
        ImageScrollViewer.IsVisible = false;
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

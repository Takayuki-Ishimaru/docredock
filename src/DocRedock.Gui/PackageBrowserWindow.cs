using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DocRedock.Api;

namespace DocRedock.Gui;

/// <summary>Reads only a verified snapshot; model-facing Markdown and the source index stay separate.</summary>
public sealed class PackageBrowserWindow : Window
{
    private static readonly JsonSerializerOptions SourceJson = new() { WriteIndented = true };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox query = new() { PlaceholderText = "日本語・全角文字でも検索できます" };
    private readonly TextBox markdown = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox sources = new() { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 150 };
    private readonly TextBox review = new() { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 160, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly CheckBox consent = new() { Content = new TextBlock { Text = "確認レポートを読み、共有範囲と原本照合の必要事項を確認しました", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsVisible = false };
    private readonly CheckBox hiddenReview = new() { Content = "非表示内容を含むパッケージも共有範囲の確認後に読む" };
    private readonly ListBox parts = new() { MinWidth = 180 };
    private readonly Button copy = new() { Content = "この分割本文だけをコピー", IsEnabled = false };
    private AiPackageVerifiedPackage? package;
    private CancellationTokenSource? loading;
    private readonly string? initialPath;
    private string packageName = "";

    public PackageBrowserWindow(string? path = null)
    {
        initialPath = path;
        Title = "AIパッケージを検索して読む"; Width = 1060; Height = 780; MinWidth = 700; MinHeight = 540;
        var openZip = new Button { Content = "ZIPを開く" };
        var openFolder = new Button { Content = "フォルダーを開く" };
        var search = new Button { Content = "検索" };
        var all = new Button { Content = "すべての分割" };
        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { openZip, openFolder } });
        top.Children.Add(status);
        top.Children.Add(new TextBlock { Text = "全文はパッケージ内のdocument.mdです。必要な分割本文だけをAIへ渡し、出典索引はここで確認してください。検証は意味・著者の真正性・共有権限を保証しません。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        top.Children.Add(new Expander { Header = "検証済みの確認レポート（review.md）", Content = review });
        top.Children.Add(consent);
        top.Children.Add(hiddenReview);
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        searchRow.Children.Add(query); Grid.SetColumn(search, 1); searchRow.Children.Add(search); Grid.SetColumn(all, 2); searchRow.Children.Add(all);
        top.Children.Add(searchRow);
        var bottom = new StackPanel { Spacing = 8, Children = { copy,
            new Expander { Header = "出典索引（本文のコピーには含めません）", Content = sources } } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        body.Children.Add(parts); Grid.SetColumn(markdown, 1); body.Children.Add(markdown);
        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom); layout.Children.Add(body);
        Content = layout;
        Avalonia.Automation.AutomationProperties.SetName(query, "本文を検索");
        Avalonia.Automation.AutomationProperties.SetName(parts, "一致する分割");
        Avalonia.Automation.AutomationProperties.SetName(markdown, "選択した分割本文");
        Avalonia.Automation.AutomationProperties.SetName(sources, "元セルとノードの出典索引");
        openZip.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "AIパッケージZIPを選択", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("AIパッケージ") { Patterns = ["*.zip"] }] });
            if (files.Count > 0 && files[0] is { } file) await LoadFromUiAsync(file.Path.LocalPath);
        };
        openFolder.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "manifest.jsonのあるフォルダーを選択", AllowMultiple = false });
            if (folders.Count > 0 && folders[0] is { } folder) await LoadFromUiAsync(folder.Path.LocalPath);
        };
        search.Click += (_, _) => Search(query.Text ?? "");
        all.Click += (_, _) => Search("");
        parts.SelectionChanged += (_, _) => ShowSelection();
        consent.IsCheckedChanged += (_, _) => RefreshReviewState();
        hiddenReview.IsCheckedChanged += (_, _) => { consent.IsChecked = false; RefreshReviewState(); };
        copy.Click += async (_, _) =>
        {
            if (!CanRead || markdown.Text is not { Length: > 0 } text) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetValueAsync(DataFormat.Text, text);
        };
        Opened += async (_, _) => { if (initialPath is not null) await LoadFromUiAsync(initialPath); };
        Closed += (_, _) => loading?.Cancel();
    }

    private bool ReviewRequired => package is not null && package.Review.StopReasons(hiddenReview.IsChecked == true, package.Manifest.ContentPolicy).Count > 0;
    internal bool CanRead => package is not null && (!ReviewRequired || consent.IsChecked == true);
    internal string CurrentMarkdown => markdown.Text ?? "";
    internal string CurrentSources => sources.Text ?? "";
    internal string CurrentStatus => status.Text ?? "";
    internal string CurrentQuery => query.Text ?? "";
    internal bool RequireHiddenReview { get => hiddenReview.IsChecked == true; set => hiddenReview.IsChecked = value; }
    internal IReadOnlyList<string> VisiblePartIds => parts.Items.Cast<string>().ToArray();
    internal void ConfirmReview() => consent.IsChecked = true;
    internal void ClearReviewConfirmation() => consent.IsChecked = false;

    private void RefreshReviewState()
    {
        if (package is null) return;
        consent.IsVisible = ReviewRequired;
        Search(query.Text ?? "");
    }

    private void UpdateStatus(string? searchResult = null)
    {
        if (package is null) return;
        status.Text = packageName + " — 検証完了" + (ReviewRequired
            ? CanRead ? "・確認済み。本文を表示できます。" : "・確認が必要です。確認前は本文を表示しません。"
            : "。本文を表示できます。") + (searchResult is null ? "" : " " + searchResult);
    }

    private async Task LoadFromUiAsync(string path)
    {
        try { await LoadPackageAsync(path); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or AiPackageVerificationException or UnauthorizedAccessException)
        { /* The owning load sets status; an older failed load cannot overwrite a newer result. */ }
    }

    internal async Task LoadPackageAsync(string path)
    {
        // Publish the new owner before awaiting cancellation callbacks, so a newer load can cancel it.
        var previous = loading;
        var cancellation = new CancellationTokenSource(); loading = cancellation;
        package = null; consent.IsChecked = false; consent.IsVisible = false; review.Text = ""; query.Text = ""; ShowParts([]);
        status.Text = "全ファイル・出典索引・確認レポートを検証しています…";
        try
        {
            if (previous is not null) await previous.CancelAsync().ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            var verified = await Task.Run(() => AiPackageManifestReader.ReadVerifiedPackageAsync(path, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            package = verified;
            packageName = Path.GetFileName(path);
            review.Text = verified.ReviewMarkdown;
            RefreshReviewState();
        }
        catch (Exception exception)
        {
            if (loading == cancellation && exception is not OperationCanceledException)
                status.Text = "読み込みを停止しました: " + exception.Message;
            throw;
        }
        finally { if (loading == cancellation) loading = null; cancellation.Dispose(); }
    }

    internal void Search(string text)
    {
        query.Text = text;
        if (!CanRead) { ShowParts([]); UpdateStatus(); return; }
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowParts(package!.Parts.Select(part => part.Part.Id));
            UpdateStatus();
            return;
        }
        var matches = AiPackageSearch.Find(package!, text);
        ShowParts(matches.Select(match => match.PartId));
        UpdateStatus($"検証済み本文の検索: {matches.Count}分割が一致しました。");
    }

    private void ShowParts(IEnumerable<string> ids)
    {
        markdown.Text = ""; sources.Text = ""; copy.IsEnabled = false;
        parts.ItemsSource = CanRead ? ids.ToArray() : Array.Empty<string>();
        parts.SelectedIndex = parts.ItemCount > 0 ? 0 : -1;
    }

    private void ShowSelection()
    {
        if (!CanRead || parts.SelectedItem is not string id) { markdown.Text = ""; sources.Text = ""; copy.IsEnabled = false; return; }
        var part = package!.Parts.Single(part => part.Part.Id == id);
        markdown.Text = part.Markdown;
        sources.Text = JsonSerializer.Serialize(new { locations = part.Part.Locations, sources = part.Sources }, SourceJson);
        copy.IsEnabled = true;
    }
}

using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Rtmd.Core.Reporting;
using Rtmd.Formats.Pdf;
using Rtmd.RoundTrip;

namespace Rtmd.Gui;

/// <summary>
/// Local desktop front-end. It passes paths chosen by the user directly to the
/// workflow service; documents are never copied to a web server or uploaded.
/// </summary>
public partial class MainWindow : Window
{
    private const long MaxSourceBytes = 209_715_200;
    private const long MaxMarkdownBytes = 16_777_216;
    private const long MaxPackageBytes = 536_870_912 - MaxMarkdownBytes;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx", ".pdf" };

    private readonly GuiWorkflowService _workflow;
    private readonly List<IStorageFile> _sourceFiles = [];
    private IStorageFile? _markdownFile;
    private IStorageFile? _packageFile;
    private string? _exportDirectory;
    private string? _restoreDirectory;
    private string? _latestOutputDirectory;
    private string? _latestMarkdownPath;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _restoreCancellation;
    private bool _exportBusy;
    private bool _restoreBusy;

    // This keeps the window usable by a plain App.axaml.cs while Program may
    // also construct it with its configured GuiWorkflowService instance.
    public MainWindow() : this(new GuiWorkflowService())
    {
    }

    public MainWindow(GuiWorkflowService workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        InitializeComponent();
        LoadSettings();
        UpdateExportSelection();
        UpdateRestoreSelection();
        UpdateButtons();
    }

    private async void OnPickExportFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "変換する文書を選択",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("対応文書") { Patterns = ["*.docx", "*.xlsx", "*.pptx", "*.pdf"] }],
        });
        if (files.Count > 0) SelectExportFiles(files);
    }

    private async void OnPickRestoreFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "編集済みMarkdownとRTMDパッケージを選択",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("RTMD復元ファイル") { Patterns = ["*.md", "*.rtmdpkg"] }],
        });
        SelectRestoreFiles(files);
        await TrySelectCompanionPackageAsync();
    }

    private async void OnPickExportSourceFolder(object? sender, RoutedEventArgs e)
    {
        ClearError(ExportErrorText);
        var directory = await PickDirectoryAsync("変換する文書があるフォルダーを選択");
        if (directory is null) return;
        try
        {
            var paths = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => SourceExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToArray();
            var files = new List<IStorageFile>(paths.Length);
            foreach (var path in paths)
                if (await StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file) files.Add(file);
            if (files.Count == 0)
            {
                ShowError(ExportErrorText, "フォルダー直下にDOCX、XLSX、PPTX、PDFがありません。");
                return;
            }
            SelectExportFiles(files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError(ExportErrorText, exception.Message);
        }
    }

    private async void OnPickExportFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await PickDirectoryAsync("書き出し先フォルダーを選択");
        if (picked is not null) _exportDirectory = picked;
        ExportFolderText.Text = OutputFolderLabel(_exportDirectory);
        SaveSettings();
        UpdateButtons();
    }

    private async void OnPickRestoreFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await PickDirectoryAsync("復元先フォルダーを選択");
        if (picked is not null) _restoreDirectory = picked;
        RestoreFolderText.Text = OutputFolderLabel(_restoreDirectory);
        SaveSettings();
        UpdateButtons();
    }

    private void OnClearExportFile(object? sender, RoutedEventArgs e)
    {
        _sourceFiles.Clear();
        ClearError(ExportErrorText);
        UpdateExportSelection();
        UpdateButtons();
    }

    private void OnOcrChanged(object? sender, RoutedEventArgs e)
    {
        OcrLanguagesTextBox.IsEnabled = OcrToggle.IsChecked == true;
        OcrLanguagesPanel.IsVisible = OcrToggle.IsChecked == true;
        SaveSettings();
    }

    private void OnReadableChanged(object? sender, RoutedEventArgs e)
    {
        ReadableOptionsPanel.IsVisible = ReadableExportToggle.IsChecked == true;
        SaveSettings();
    }

    private void OnSettingsChanged(object? sender, RoutedEventArgs e) => SaveSettings();

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_sourceFiles.Count == 0 || _exportBusy) return;
        ClearError(ExportErrorText);

        var sourcePaths = _sourceFiles.Select(LocalPath).ToArray();
        if (sourcePaths.Any(path => path is null))
        {
            ShowError(ExportErrorText, "この場所のファイルは利用できません。ローカルファイルを選択してください。");
            return;
        }

        var readable = ReadableExportToggle.IsChecked == true;
        SetExportBusy(true, readable ? "読みやすいMarkdownを作成しています…" : "MarkdownとRTMDパッケージを作成しています…");
        _exportCancellation = new CancellationTokenSource();
        try
        {
            var languages = OcrLanguagesTextBox.Text?.Trim();
            var results = new List<GuiExportResult>();
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                ShowTransientStatus($"{index + 1}/{sourcePaths.Length}: {Path.GetFileName(sourcePaths[index]!)} を変換しています…");
                results.Add(await _workflow.ExportAsync(
                    sourcePaths[index]!,
                    _exportDirectory!,
                    OcrToggle.IsChecked == true,
                    string.IsNullOrWhiteSpace(languages) ? ["jpn", "eng"] : languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    readable: readable,
                    cancellationToken: _exportCancellation.Token,
                    useUniqueName: true,
                    showFormulas: ShowFormulasCheckBox.IsChecked == true,
                    includeSvgPreviews: IncludeSvgCheckBox.IsChecked == true,
                    includeDiagrams: IncludeDiagramsCheckBox.IsChecked == true,
                    embedReadableImages: EmbedReadableImagesCheckBox.IsChecked == true));
            }

            _latestOutputDirectory = _exportDirectory;
            _latestMarkdownPath = results[^1].MarkdownPath;
            SaveSettings();
            ShowResult(
                success: true,
                title: results.Count == 1 ? "書き出しが完了しました" : $"{results.Count}件の書き出しが完了しました",
                message: string.Join(Environment.NewLine, results.Select(result => result.MarkdownPath)),
                fidelity: results.Select(result => result.Fidelity).Distinct(StringComparer.Ordinal).Count() == 1 ? results[0].Fidelity : "複数形式",
                diagnostics: results.SelectMany(result => result.Diagnostics).ToArray());
        }
        catch (OperationCanceledException)
        {
            ShowResult(false, "書き出しをキャンセルしました", "処理を中断しました。出力途中のファイルは削除されています。", null, []);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ShowError(ExportErrorText, exception.Message);
            ShowResult(false, "書き出しできませんでした", exception.Message, null, []);
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            SetExportBusy(false, null);
        }
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (_markdownFile is null || _packageFile is null || string.IsNullOrWhiteSpace(_restoreDirectory) || _restoreBusy) return;
        ClearError(RestoreErrorText);

        var markdownPath = LocalPath(_markdownFile);
        var packagePath = LocalPath(_packageFile);
        if (markdownPath is null || packagePath is null)
        {
            ShowError(RestoreErrorText, "選択したファイルにローカルパスがありません。ローカルファイルを選択してください。");
            return;
        }

        SetRestoreBusy(true, "RTMDパッケージを検証して文書を復元しています…");
        _restoreCancellation = new CancellationTokenSource();
        try
        {
            var result = await _workflow.RestoreAsync(markdownPath, packagePath, _restoreDirectory!, PdfFallbackToggle.IsChecked == true,
                _restoreCancellation.Token, useUniqueName: true);
            var success = result.Succeeded;
            if (success) _latestOutputDirectory = _restoreDirectory;
            if (success) SaveSettings();
            ShowResult(
                success,
                success ? "復元が完了しました" : "復元できませんでした",
                success
                    ? $"{result.Format.ToUpperInvariant()}ファイルを生成しました。内容を対応するアプリで確認してください。\n{result.OutputPath}"
                    : "RTMDの安全性チェックにより、この変更は適用されませんでした。",
                result.Fidelity,
                result.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            ShowResult(false, "復元をキャンセルしました", "処理を中断しました。", null, []);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ShowError(RestoreErrorText, exception.Message);
            ShowResult(false, "復元できませんでした", exception.Message, null, []);
        }
        finally
        {
            _restoreCancellation?.Dispose();
            _restoreCancellation = null;
            SetRestoreBusy(false, null);
        }
    }

    private void OnExportDragOver(object? sender, DragEventArgs e) => SetDragState(ExportDropZone, e);
    private void OnRestoreDragOver(object? sender, DragEventArgs e) => SetDragState(RestoreDropZone, e);
    private void OnExportDragLeave(object? sender, RoutedEventArgs e) => ExportDropZone.Classes.Set("drag-over", false);
    private void OnRestoreDragLeave(object? sender, RoutedEventArgs e) => RestoreDropZone.Classes.Set("drag-over", false);

    private void OnExportDrop(object? sender, DragEventArgs e)
    {
        ExportDropZone.Classes.Set("drag-over", false);
        SelectExportFiles(e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>() ?? []);
    }

    private async void OnRestoreDrop(object? sender, DragEventArgs e)
    {
        RestoreDropZone.Classes.Set("drag-over", false);
        SelectRestoreFiles(e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>() ?? []);
        await TrySelectCompanionPackageAsync();
    }

    private void SelectExportFiles(IEnumerable<IStorageFile> files)
    {
        ClearError(ExportErrorText);
        var selected = files.ToArray();
        if (selected.Length == 0) return;
        var unsupported = selected.Where(file => !SourceExtensions.Contains(Path.GetExtension(file.Name))).ToArray();
        if (unsupported.Length > 0)
        {
            ShowError(ExportErrorText, "DOCX、XLSX、PPTX、PDFのいずれかを選択してください。");
            return;
        }
        if (selected.Any(file => !WithinSizeLimit(file, MaxSourceBytes)))
        {
            ShowError(ExportErrorText, "ファイルサイズは200 MB以下にしてください。");
            return;
        }
        _sourceFiles.Clear();
        _sourceFiles.AddRange(selected);
        if (string.IsNullOrWhiteSpace(_exportDirectory) && _sourceFiles.FirstOrDefault() is { } first && LocalPath(first) is { } localPath)
            _exportDirectory = Path.GetDirectoryName(localPath);
        UpdateExportSelection();
        ExportFolderText.Text = OutputFolderLabel(_exportDirectory);
        UpdateButtons();
    }

    private void SelectRestoreFiles(IEnumerable<IStorageFile> files)
    {
        ClearError(RestoreErrorText);
        var found = false;
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                if (!WithinSizeLimit(file, MaxMarkdownBytes))
                {
                    ShowError(RestoreErrorText, "Markdownは16 MB以下にしてください。");
                    continue;
                }
                _markdownFile = file;
                if (string.IsNullOrWhiteSpace(_restoreDirectory) && LocalPath(file) is { } localPath)
                    _restoreDirectory = Path.GetDirectoryName(localPath);
                found = true;
            }
            else if (extension.Equals(".rtmdpkg", StringComparison.OrdinalIgnoreCase))
            {
                if (!WithinSizeLimit(file, MaxPackageBytes))
                {
                    ShowError(RestoreErrorText, "RTMDパッケージが大きすぎます。");
                    continue;
                }
                _packageFile = file;
                found = true;
            }
        }
        if (!found) ShowError(RestoreErrorText, ".mdと.rtmdpkgを選択してください。");
        UpdateRestoreSelection();
        RestoreFolderText.Text = OutputFolderLabel(_restoreDirectory);
        UpdateButtons();
    }

    private async Task TrySelectCompanionPackageAsync()
    {
        if (_markdownFile is null || _packageFile is not null || LocalPath(_markdownFile) is not { } markdownPath) return;
        var candidate = Path.ChangeExtension(markdownPath, ".rtmdpkg");
        if (!File.Exists(candidate) || new FileInfo(candidate).Length > MaxPackageBytes) return;
        var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(candidate));
        if (file is null) return;
        _packageFile = file;
        UpdateRestoreSelection();
        UpdateButtons();
    }

    private async Task<string?> PickDirectoryAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : LocalPath(folders[0]);
    }

    private void UpdateExportSelection()
    {
        ExportFileTypeText.Text = _sourceFiles.Count switch
        {
            0 => "—",
            1 => Path.GetExtension(_sourceFiles[0].Name).TrimStart('.').ToUpperInvariant(),
            _ => _sourceFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        ExportFileNameText.Text = _sourceFiles.Count switch
        {
            0 => "ファイル未選択",
            1 => _sourceFiles[0].Name,
            _ => $"{_sourceFiles.Count}件のファイル",
        };
        ExportFileDetailText.Text = _sourceFiles.Count switch
        {
            0 => "DOCX · XLSX · PPTX · PDF",
            1 => FileDetail(_sourceFiles[0]),
            _ => string.Join(" · ", _sourceFiles.Select(file => Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)),
        };
    }

    private void UpdateRestoreSelection()
    {
        MarkdownFileNameText.Text = _markdownFile?.Name ?? "編集済みMarkdown";
        MarkdownFileDetailText.Text = _markdownFile is null ? "未選択" : FileDetail(_markdownFile);
        MarkdownStatusText.Text = _markdownFile is null ? "○" : "✓";
        PackageFileNameText.Text = _packageFile?.Name ?? "RTMDパッケージ";
        PackageFileDetailText.Text = _packageFile is null ? "未選択" : FileDetail(_packageFile);
        PackageStatusText.Text = _packageFile is null ? "○" : "✓";
    }

    private void UpdateButtons()
    {
        ExportButton.IsEnabled = _sourceFiles.Count > 0 && !string.IsNullOrWhiteSpace(_exportDirectory) && !_exportBusy;
        RestoreButton.IsEnabled = _markdownFile is not null && _packageFile is not null && !string.IsNullOrWhiteSpace(_restoreDirectory) && !_restoreBusy;
    }

    private void SetExportBusy(bool busy, string? status)
    {
        _exportBusy = busy;
        ExportButton.Content = busy ? "処理中…" : "書き出す";
        ExportFolderButton.IsEnabled = !busy;
        ExportDropZone.IsEnabled = !busy;
        UpdateButtons();
        if (status is not null) ShowTransientStatus(status);
    }

    private void SetRestoreBusy(bool busy, string? status)
    {
        _restoreBusy = busy;
        RestoreButton.Content = busy ? "処理中…" : "復元する";
        RestoreFolderButton.IsEnabled = !busy;
        RestoreDropZone.IsEnabled = !busy;
        UpdateButtons();
        if (status is not null) ShowTransientStatus(status);
    }

    private void ShowTransientStatus(string message)
    {
        ResultPanel.IsVisible = true;
        ResultPanel.Classes.Set("error", false);
        ResultSymbol.Classes.Set("error", false);
        ResultSymbolText.Classes.Set("on-dark", true);
        ResultSymbolText.Classes.Set("on-accent", false);
        ResultSymbolText.Text = "…";
        ResultKickerText.Text = "PROCESSING";
        ResultTitleText.Text = "処理しています";
        ResultMessageText.Text = message;
        OperationProgressBar.IsVisible = true;
        ResultFidelityText.IsVisible = false;
        DiagnosticsTextBox.IsVisible = false;
        OpenOutputFolderButton.IsVisible = false;
        OpenMarkdownButton.IsVisible = false;
        CancelOperationButton.IsVisible = true;
    }

    private void ShowResult(bool success, string title, string message, string? fidelity, IReadOnlyList<Diagnostic> diagnostics)
    {
        ResultPanel.IsVisible = true;
        ResultPanel.Classes.Set("error", !success);
        ResultSymbol.Classes.Set("error", !success);
        ResultSymbolText.Classes.Set("on-dark", success);
        ResultSymbolText.Classes.Set("on-accent", !success);
        ResultSymbolText.Text = success ? "✓" : "!";
        ResultKickerText.Text = success ? "COMPLETE" : "CHECK REQUIRED";
        ResultTitleText.Text = title;
        ResultMessageText.Text = message;
        OperationProgressBar.IsVisible = false;
        ResultFidelityText.Text = fidelity ?? string.Empty;
        ResultFidelityText.IsVisible = !string.IsNullOrWhiteSpace(fidelity);
        var importantDiagnostics = diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Information).ToArray();
        DiagnosticsTextBox.Text = string.Join(Environment.NewLine, importantDiagnostics.Select(diagnostic =>
            $"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Code}: {diagnostic.Message}"));
        DiagnosticsTextBox.IsVisible = importantDiagnostics.Length > 0;
        OpenOutputFolderButton.IsVisible = success && !string.IsNullOrWhiteSpace(_latestOutputDirectory);
        OpenMarkdownButton.IsVisible = success && !string.IsNullOrWhiteSpace(_latestMarkdownPath);
        CancelOperationButton.IsVisible = false;
    }

    private void OnOpenOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestOutputDirectory)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _latestOutputDirectory, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowResult(false, "フォルダーを開けませんでした", exception.Message, null, []);
        }
    }

    private void OnOpenMarkdown(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestMarkdownPath) || !File.Exists(_latestMarkdownPath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _latestMarkdownPath, UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowResult(false, "Markdownを開けませんでした", exception.Message, null, []); }
    }

    private void OnCancelOperation(object? sender, RoutedEventArgs e)
    {
        _exportCancellation?.Cancel();
        _restoreCancellation?.Cancel();
    }

    private void OnCloseResult(object? sender, RoutedEventArgs e) => ResultPanel.IsVisible = false;

    private static void SetDragState(Control zone, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.TryGetFiles()?.Any() == true;
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        zone.Classes.Set("drag-over", hasFiles);
    }

    private static bool CanExportWithoutReplacing(string sourcePath, string outputDirectory, bool readable, out string message)
    {
        var baseName = Path.GetFileNameWithoutExtension(GuiWorkflowService.SafeFileName(Path.GetFileName(sourcePath), "document"));
        var candidates = readable
            ? [Path.Combine(outputDirectory, baseName + ".md")]
            : new[]
            {
                Path.Combine(outputDirectory, baseName + ".md"),
                Path.Combine(outputDirectory, baseName + ".rtmdpkg"),
                Path.Combine(outputDirectory, baseName + ".rtmd"),
            };
        var conflicts = candidates.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        message = conflicts.Length == 0
            ? string.Empty
            : $"既存の出力を保護するため書き出しを停止しました。別の出力フォルダーを選ぶか、次の項目を移動してください。{Environment.NewLine}{string.Join(Environment.NewLine, conflicts)}";
        return conflicts.Length == 0;
    }

    private static bool WithinSizeLimit(IStorageFile file, long limit)
    {
        var path = LocalPath(file);
        return path is null || !File.Exists(path) || new FileInfo(path).Length <= limit;
    }

    private static string FileDetail(IStorageFile file)
    {
        var path = LocalPath(file);
        return path is not null && File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "選択済み";
    }

    private static string? LocalPath(IStorageItem? item) => item?.Path is { IsFile: true } path ? path.LocalPath : null;
    private static string OutputFolderLabel(string? path) => path is null ? "出力先: 未選択" : "出力先: " + path;
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):N1} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):N2} GB",
    };

    private static bool IsExpected(Exception exception) => exception is
        InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or WorkspaceIntegrityException or PdfExtractionException;

    private void LoadSettings()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return;
            var settings = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path));
            if (settings is null) return;
            _exportDirectory = ExistingDirectory(settings.ExportDirectory);
            _restoreDirectory = ExistingDirectory(settings.RestoreDirectory);
            if (settings.Readable is not null) ReadableExportToggle.IsChecked = settings.Readable;
            if (settings.OcrEnabled is not null) OcrToggle.IsChecked = settings.OcrEnabled;
            if (!string.IsNullOrWhiteSpace(settings.OcrLanguages)) OcrLanguagesTextBox.Text = settings.OcrLanguages;
            if (settings.PdfFallback is not null) PdfFallbackToggle.IsChecked = settings.PdfFallback;
            if (settings.ShowFormulas is not null) ShowFormulasCheckBox.IsChecked = settings.ShowFormulas;
            if (settings.IncludeSvgPreviews is not null) IncludeSvgCheckBox.IsChecked = settings.IncludeSvgPreviews;
            if (settings.IncludeDiagrams is not null) IncludeDiagramsCheckBox.IsChecked = settings.IncludeDiagrams;
            if (settings.EmbedReadableImages is not null) EmbedReadableImagesCheckBox.IsChecked = settings.EmbedReadableImages;
            ExportFolderText.Text = OutputFolderLabel(_exportDirectory);
            RestoreFolderText.Text = OutputFolderLabel(_restoreDirectory);
            OcrLanguagesTextBox.IsEnabled = OcrToggle.IsChecked == true;
            OcrLanguagesPanel.IsVisible = OcrToggle.IsChecked == true;
            ReadableOptionsPanel.IsVisible = ReadableExportToggle.IsChecked == true;
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private void SaveSettings()
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new GuiSettings(_exportDirectory, _restoreDirectory,
                ReadableExportToggle.IsChecked, OcrToggle.IsChecked, OcrLanguagesTextBox.Text, PdfFallbackToggle.IsChecked,
                ShowFormulasCheckBox.IsChecked, IncludeSvgCheckBox.IsChecked, IncludeDiagramsCheckBox.IsChecked,
                EmbedReadableImagesCheckBox.IsChecked)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ExistingDirectory(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoundHound", "gui-settings.json");
    private sealed record GuiSettings(
        string? ExportDirectory,
        string? RestoreDirectory,
        bool? Readable,
        bool? OcrEnabled,
        string? OcrLanguages,
        bool? PdfFallback,
        bool? ShowFormulas = null,
        bool? IncludeSvgPreviews = null,
        bool? IncludeDiagrams = null,
        bool? EmbedReadableImages = null);

    private static void ShowError(TextBlock control, string message)
    {
        control.Text = message;
        control.IsVisible = true;
    }

    private static void ClearError(TextBlock control)
    {
        control.Text = string.Empty;
        control.IsVisible = false;
    }
}

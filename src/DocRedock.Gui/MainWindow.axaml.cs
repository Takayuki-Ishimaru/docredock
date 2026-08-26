using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using DocRedock.RoundTrip;

namespace DocRedock.Gui;

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
    private readonly UpdateCheckService _updateCheckService;
    private readonly List<IStorageFile> _sourceFiles = [];
    private IStorageFile? _markdownFile;
    private string? _sidecarPath;
    private string? _exportDirectory;
    private string? _restoreDirectory;
    private string? _latestOutputDirectory;
    private string? _latestMarkdownPath;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _restoreCancellation;
    private bool _exportBusy;
    private bool _restoreBusy;
    private bool _updateCheckStarted;
    private Uri? _updateReleaseUri;
    private bool _componentsInitialized;

    // This keeps the window usable by a plain App.axaml.cs while Program may
    // also construct it with its configured GuiWorkflowService instance.
    public MainWindow() : this(new GuiWorkflowService(), new UpdateCheckService())
    {
    }

    public MainWindow(GuiWorkflowService workflow)
        : this(workflow, new UpdateCheckService())
    {
    }

    internal MainWindow(
        GuiWorkflowService workflow,
        UpdateCheckService updateCheckService)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _updateCheckService =
            updateCheckService ?? throw new ArgumentNullException(nameof(updateCheckService));
        InitializeComponent();
        _componentsInitialized = true;
        LoadSettings();
        var experimentalEnabled = ExperimentalFeatures.IsEnabled;
        RoundTripExportRadio.IsEnabled = experimentalEnabled;
        RestoreModeRadio.IsEnabled = experimentalEnabled;
        PdfFallbackToggle.IsEnabled = experimentalEnabled;
        if (!experimentalEnabled)
        {
            ReadableExportToggle.IsChecked = true;
            RoundTripExportRadio.IsChecked = false;
        }
        UpdateContentPolicyWarning();
        UpdateExportSelection();
        UpdateRestoreSelection();
        UpdateButtons();
        // Keep the initial visual state in sync with the default workflow and
        // the persisted readable/round-trip setting before the window is shown.
        OnReadableChanged(null, new RoutedEventArgs());
        OnModeChanged(null, new RoutedEventArgs());
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
            Title = "編集済みMarkdownとDocRedock復元ファイルを選択",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("DocRedock 復元ファイル") { Patterns = ["*.md", "*.drmd", "*.drmdpkg"] }],
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

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        var restore = sender == RestoreModeRadio
            ? RestoreModeRadio.IsChecked == true
            : sender == ExportModeRadio
                ? ExportModeRadio.IsChecked != true
                : RestoreModeRadio.IsChecked == true;
        if (!restore && ExportModeRadio.IsChecked != true)
            ExportModeRadio.IsChecked = true;
        if (restore && RestoreModeRadio.IsChecked != true)
            RestoreModeRadio.IsChecked = true;

        ExportWorkspace.IsVisible = !restore;
        RestoreWorkspace.IsVisible = restore;
        ExportActionBar.IsVisible = !restore;
        RestoreActionBar.IsVisible = restore;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 880;
        ExportWorkspace.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "1.15*,*");
        RestoreWorkspace.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "1.15*,*");
        ExportWorkspace.ColumnSpacing = narrow ? 0 : 16;
        RestoreWorkspace.ColumnSpacing = narrow ? 0 : 16;
        Grid.SetColumn(ExportSettingsColumn, narrow ? 0 : 1);
        Grid.SetRow(ExportSettingsColumn, narrow ? 1 : 0);
        Grid.SetColumn(RestoreSettingsColumn, narrow ? 0 : 1);
        Grid.SetRow(RestoreSettingsColumn, narrow ? 1 : 0);
    }

    private void OnOcrChanged(object? sender, RoutedEventArgs e)
    {
        OcrLanguagesTextBox.IsEnabled = OcrToggle.IsChecked == true;
        OcrLanguagesPanel.IsVisible = OcrToggle.IsChecked == true;
        SaveSettings();
    }

    private void OnReadableChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radio && radio.IsChecked == true)
            ReadableExportToggle.IsChecked = radio != RoundTripExportRadio;

        var readable = ReadableExportToggle.IsChecked == true;
        RoundTripExportRadio.IsChecked = !readable;
        ReadableOptionsPanel.IsVisible = ReadableExportToggle.IsChecked == true;
        SidecarOptionsPanel.IsVisible = !readable;
        SaveSettings();
        UpdateButtons();
    }

    private void OnSettingsChanged(object? sender, RoutedEventArgs e) => SaveSettings();

    private void OnContentPolicyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_componentsInitialized) return;
        UpdateContentPolicyWarning();
        SaveSettings();
    }

    private string SelectedContentPolicy() =>
        (ContentPolicyComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } policy ? policy : "visible";

    private void UpdateContentPolicyWarning() =>
        CompletePolicyWarning.IsVisible = StringComparer.Ordinal.Equals(SelectedContentPolicy(), "complete");

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
        SetExportBusy(true, readable ? "読みやすいMarkdownを作成しています…" : "MarkdownとDocRedockサイドカーを作成しています…");
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
                    embedReadableImages: EmbedReadableImagesCheckBox.IsChecked == true,
                    zipSidecar: ZipSidecarCheckBox.IsChecked == true,
                    contentPolicy: SelectedContentPolicy()));
            }

            _latestOutputDirectory = _exportDirectory;
            _latestMarkdownPath = results[^1].MarkdownPath;
            SaveSettings();
            ShowResult(
                success: true,
                title: results.Count == 1 ? "書き出しが完了しました" : $"{results.Count}件の書き出しが完了しました",
                message: string.Join(Environment.NewLine, results.Select(result => result.IsReadable
                    ? $"Markdown: {result.MarkdownPath}"
                    : $"Markdown: {result.MarkdownPath}{Environment.NewLine}サイドカー: {result.SidecarPath}（{(result.SidecarForm == DocRedock.RoundTrip.SidecarForm.Zip ? "zip" : "ディレクトリ")}）")),
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
        if (_markdownFile is null || string.IsNullOrWhiteSpace(_sidecarPath) || string.IsNullOrWhiteSpace(_restoreDirectory) || _restoreBusy) return;
        ClearError(RestoreErrorText);

        var markdownPath = LocalPath(_markdownFile);
        if (markdownPath is null)
        {
            ShowError(RestoreErrorText, "選択したファイルにローカルパスがありません。ローカルファイルを選択してください。");
            return;
        }

        SetRestoreBusy(true, "DocRedock復元情報を検証して文書を復元しています…");
        _restoreCancellation = new CancellationTokenSource();
        try
        {
            var result = await _workflow.RestoreAsync(markdownPath, _sidecarPath, _restoreDirectory!, PdfFallbackToggle.IsChecked == true,
                _restoreCancellation.Token, useUniqueName: true);
            var success = result.Succeeded;
            if (success) _latestOutputDirectory = _restoreDirectory;
            if (success) SaveSettings();
            ShowResult(
                success,
                success ? "復元が完了しました" : "復元できませんでした",
                success
                    ? $"{result.Format.ToUpperInvariant()}ファイルを生成しました。内容を対応するアプリで確認してください。\n{result.OutputPath}"
                    : "DocRedockの安全性チェックにより、この変更は適用されませんでした。",
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
        var items = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
        SelectRestoreFiles(items.OfType<IStorageFile>());
        var sidecarFolder = items.OfType<IStorageFolder>()
            .Select(LocalPath)
            .FirstOrDefault(path => path is not null &&
                (Path.GetExtension(path).Equals(".drmd", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(path).Equals(".drmd", StringComparison.OrdinalIgnoreCase)));
        if (sidecarFolder is not null && WithinSizeLimit(sidecarFolder, MaxPackageBytes))
            _sidecarPath = sidecarFolder;
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
        if (!ExperimentalFeatures.IsEnabled && selected.Any(file => Path.GetExtension(file.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(ExportErrorText, $"PDF出力は実験機能です。利用するには {ExperimentalFeatures.EnvironmentVariable}=1 を設定してください。");
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
            else if (extension.Equals(".drmd", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".drmdpkg", StringComparison.OrdinalIgnoreCase))
            {
                if (!WithinSizeLimit(file, MaxPackageBytes))
                {
                    ShowError(RestoreErrorText, "DocRedock復元ファイルが大きすぎます。");
                    continue;
                }
                _sidecarPath = LocalPath(file);
                found = true;
            }
        }
        if (!found) ShowError(RestoreErrorText, ".mdと.drmd（または.drmdpkg）を選択してください。");
        UpdateRestoreSelection();
        RestoreFolderText.Text = OutputFolderLabel(_restoreDirectory);
        UpdateButtons();
    }

    private async Task TrySelectCompanionPackageAsync()
    {
        if (_markdownFile is null || _sidecarPath is not null || LocalPath(_markdownFile) is not { } markdownPath) return;
        var candidates = new[]
        {
            Path.ChangeExtension(markdownPath, ".drmd"),
            Path.ChangeExtension(markdownPath, ".drmdpkg"),
        };
        _sidecarPath = candidates.FirstOrDefault(candidate =>
            (File.Exists(candidate) || Directory.Exists(candidate)) && WithinSizeLimit(candidate, MaxPackageBytes));
        if (_sidecarPath is null) return;
        UpdateRestoreSelection();
        UpdateButtons();
    }

    private async void OnPickSidecarFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "DocRedockサイドカーフォルダー（.drmd）を選択",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || LocalPath(folders[0]) is not { } path) return;
        if (!Path.GetExtension(path).Equals(".drmd", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(path).Equals(".drmd", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(RestoreErrorText, ".drmdサイドカーフォルダーを選択してください。");
            return;
        }
        if (!WithinSizeLimit(path, MaxPackageBytes))
        {
            ShowError(RestoreErrorText, "DocRedockサイドカーが大きすぎます。");
            return;
        }
        _sidecarPath = path;
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
        ExportFilePanel.IsVisible = _sourceFiles.Count > 0;
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
        var totalBytes = _sourceFiles.Sum(file => LocalPath(file) is { } path && File.Exists(path) ? new FileInfo(path).Length : 0L);
        ExportActionSummaryText.Text = _sourceFiles.Count == 0
            ? "文書を選択してください"
            : $"{_sourceFiles.Count}件 · {FormatBytes(totalBytes)}";
    }

    private void UpdateRestoreSelection()
    {
        RestoreMarkdownPanel.IsVisible = _markdownFile is not null;
        RestoreSidecarPanel.IsVisible = _sidecarPath is not null;
        MarkdownFileNameText.Text = _markdownFile?.Name ?? "編集済みMarkdown";
        MarkdownFileDetailText.Text = _markdownFile is null ? "未選択" : FileDetail(_markdownFile);
        MarkdownStatusText.Text = _markdownFile is null ? "○" : "✓";
        PackageFileNameText.Text = _sidecarPath is null ? "DocRedock復元ファイル" : Path.GetFileName(_sidecarPath);
        PackageFileDetailText.Text = _sidecarPath is null ? "未選択" : PathDetail(_sidecarPath);
        PackageStatusText.Text = _sidecarPath is null ? "○" : "✓";
        RestoreActionSummaryText.Text = (_markdownFile is not null, _sidecarPath is not null) switch
        {
            (true, true) => "Markdownと復元ファイルを選択済み",
            (true, false) => "復元ファイルを追加してください",
            (false, true) => "編集済みMarkdownを追加してください",
            _ => "Markdownと復元ファイルを選択してください",
        };
    }

    private void UpdateButtons()
    {
        ExportButton.IsEnabled = _sourceFiles.Count > 0 && !string.IsNullOrWhiteSpace(_exportDirectory) && !_exportBusy;
        RestoreButton.IsEnabled = _markdownFile is not null && _sidecarPath is not null && !string.IsNullOrWhiteSpace(_restoreDirectory) && !_restoreBusy;
        ExportActionSummaryText.Text = _sourceFiles.Count == 0
            ? "文書を選択してください"
            : $"{_sourceFiles.Count}件 · {FormatBytes(_sourceFiles.Sum(file => LocalPath(file) is { } path && File.Exists(path) ? new FileInfo(path).Length : 0L))}";
        RestoreActionSummaryText.Text = (_markdownFile is not null, _sidecarPath is not null) switch
        {
            (true, true) => "Markdownと復元ファイルを選択済み",
            (true, false) => "復元ファイルを追加してください",
            (false, true) => "編集済みMarkdownを追加してください",
            _ => "Markdownと復元ファイルを選択してください",
        };
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

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;
        try
        {
            var update = await _updateCheckService.CheckAsync();
            if (update is null)
            {
                return;
            }

            _updateReleaseUri = update.ReleaseUri;
            UpdateVersionText.Text =
                $"v{UpdateCheckService.FormatVersion(update.LatestVersion)} を利用できます。" +
                $"（現在 v{UpdateCheckService.FormatVersion(update.CurrentVersion)}）";
            UpdatePanel.IsVisible = true;
        }
        catch (Exception exception)
        {
            // Update availability is informational and must never prevent startup.
            Debug.WriteLine($"Update check failed: {exception.GetType().Name}");
        }
    }

    private void OnOpenUpdatePage(object? sender, RoutedEventArgs e)
    {
        if (_updateReleaseUri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _updateReleaseUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowResult(
                false,
                "リリースページを開けませんでした",
                exception.Message,
                null,
                []);
        }
    }

    private void OnCloseUpdate(object? sender, RoutedEventArgs e) =>
        UpdatePanel.IsVisible = false;

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
                Path.Combine(outputDirectory, baseName + ".drmdpkg"),
                Path.Combine(outputDirectory, baseName + ".drmd"),
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

    private static bool WithinSizeLimit(string path, long limit)
    {
        if (File.Exists(path)) return new FileInfo(path).Length <= limit;
        if (!Directory.Exists(path)) return false;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
            if (total > limit) return false;
        }
        return true;
    }

    private static string FileDetail(IStorageFile file)
    {
        var path = LocalPath(file);
        return path is not null && File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "選択済み";
    }

    private static string PathDetail(string path)
    {
        if (File.Exists(path)) return FormatBytes(new FileInfo(path).Length);
        if (Directory.Exists(path)) return "サイドカーフォルダー";
        return "未選択";
    }

    private static string? LocalPath(IStorageItem? item) => item?.Path?.LocalPath;
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
            if (settings.ZipSidecar is not null) ZipSidecarCheckBox.IsChecked = settings.ZipSidecar;
            ContentPolicyComboBox.SelectedIndex = settings.ContentPolicy switch { "complete" => 1, "sanitized" => 2, _ => 0 };
            ExportFolderText.Text = OutputFolderLabel(_exportDirectory);
            RestoreFolderText.Text = OutputFolderLabel(_restoreDirectory);
            OcrLanguagesTextBox.IsEnabled = OcrToggle.IsChecked == true;
            OcrLanguagesPanel.IsVisible = OcrToggle.IsChecked == true;
            ReadableOptionsPanel.IsVisible = ReadableExportToggle.IsChecked == true;
            SidecarOptionsPanel.IsVisible = ReadableExportToggle.IsChecked != true;
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
                EmbedReadableImagesCheckBox.IsChecked, ZipSidecarCheckBox.IsChecked, SelectedContentPolicy())));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ExistingDirectory(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocRedock", "gui-settings.json");
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
        bool? EmbedReadableImages = null,
        bool? ZipSidecar = null,
        string? ContentPolicy = null);

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
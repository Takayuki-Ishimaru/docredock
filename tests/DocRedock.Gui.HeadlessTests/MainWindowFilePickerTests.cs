using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocRedock.Gui;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

/// <summary>
/// Exercises everything that happens after the native OS file dialog returns: <c>OnPickExportFile</c>
/// / <c>OnPickRestoreFiles</c> validating the selection, the export directory defaulting from the
/// chosen file, and the full "選択 → 書き出す" conversion producing a Markdown file. The dialog itself
/// (<see cref="MainWindow.StorageProvider"/>'s native picker) cannot run under a headless test host,
/// so these tests substitute <see cref="MainWindow.FilePickerOverride"/> and
/// <see cref="MainWindow.FolderPickerOverride"/> — seams that production code never sets — and drive
/// the resulting file selection exactly as the real dialog's callback would. The real, native-OS
/// dialog gate remains a manual checklist: see docs/reference/gui-file-picker-e2e.md.
/// </summary>
public sealed class MainWindowFilePickerTests
{
    [AvaloniaFact]
    public async Task Export_file_picker_selection_shows_file_and_defaults_export_directory()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-picker-");
        var window = new MainWindow();
        try
        {
            // A real prior run of the GUI on this machine may have persisted an export directory to
            // gui-settings.json; clear it so the "defaults from the picked file" behavior under test
            // does not depend on that leftover local state.
            SetPrivateField(window, "_exportDirectory", null);

            var sourcePath = Path.Combine(root.FullName, "invoice.docx");
            File.Copy(FindFixture("complex-design-doc.docx"), sourcePath);
            var sourceFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourcePath));
            Assert.NotNull(sourceFile);

            FilePickerOpenOptions? requestedOptions = null;
            SetFilePickerOverride(window, options =>
            {
                requestedOptions = options;
                return Task.FromResult<IReadOnlyList<IStorageFile>>([sourceFile!]);
            });

            Invoke(window, "OnPickExportFile", null, new RoutedEventArgs());

            Assert.Equal("invoice.docx", Get<TextBlock>(window, "ExportFileNameText").Text);
            Assert.Equal(root.FullName, GetPrivateField(window, "_exportDirectory") as string);
            Assert.True(Get<Button>(window, "ExportButton").IsEnabled);
            Assert.False(Get<TextBlock>(window, "ExportErrorText").IsVisible);

            Assert.NotNull(requestedOptions);
            Assert.True(requestedOptions!.AllowMultiple);
            var patterns = requestedOptions.FileTypeFilter!
                .SelectMany(type => type.Patterns ?? [])
                .ToArray();
            Assert.Contains("*.docx", patterns);
            Assert.Contains("*.xlsx", patterns);
            Assert.Contains("*.pptx", patterns);
            Assert.Contains("*.pdf", patterns);
        }
        finally
        {
            window.Close();
            root.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task Export_end_to_end_converts_selected_file_to_markdown()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("docredock-gui-picker-src-");
        var outputRoot = Directory.CreateTempSubdirectory("docredock-gui-picker-out-");
        var window = new MainWindow();
        try
        {
            // See the note in the previous test: start from a clean slate regardless of any
            // directory or mode persisted by a real prior run of the GUI on this machine.
            SetPrivateField(window, "_exportDirectory", null);
            Get<RadioButton>(window, "ReadableExportToggle").IsChecked = true;

            var sourcePath = Path.Combine(sourceRoot.FullName, "report.docx");
            File.Copy(FindFixture("complex-design-doc.docx"), sourcePath);
            var sourceFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourcePath));
            var outputFolder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(outputRoot.FullName));
            Assert.NotNull(sourceFile);
            Assert.NotNull(outputFolder);

            // No OCR engine is installed in this headless environment, and the fixture is a native,
            // text-based DOCX, so keep OCR off to avoid an unrelated dependency on this path.
            Get<ToggleSwitch>(window, "OcrToggle").IsChecked = false;

            SetFilePickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFile>>([sourceFile!]));
            SetFolderPickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFolder>>([outputFolder!]));

            Invoke(window, "OnPickExportFile", null, new RoutedEventArgs());
            Invoke(window, "OnPickExportFolder", null, new RoutedEventArgs());

            // IStorageFolder.Path is a folder URI and normalizes to a trailing separator; the picked
            // directory itself is what matters here, not that exact formatting.
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(outputRoot.FullName),
                Path.TrimEndingDirectorySeparator((string)GetPrivateField(window, "_exportDirectory")!));
            // "Markdownのみ" (readable) mode was forced above, so a bare .md is expected below.
            Assert.True(Get<RadioButton>(window, "ReadableExportToggle").IsChecked == true);
            Assert.True(Get<Button>(window, "ExportButton").IsEnabled);

            Invoke(window, "OnExport", null, new RoutedEventArgs());
            PumpUntil(() => GetPrivateField(window, "_exportBusy") is false, TimeSpan.FromSeconds(30));

            var markdownFiles = Directory.GetFiles(outputRoot.FullName, "*.md");
            Assert.Single(markdownFiles);
            Assert.True(Get<Control>(window, "ResultPanel").IsVisible);
            Assert.Equal("COMPLETE", Get<TextBlock>(window, "ResultKickerText").Text);
            Assert.Equal("書き出しが完了しました", Get<TextBlock>(window, "ResultTitleText").Text);
        }
        finally
        {
            window.Close();
            sourceRoot.Delete(true);
            outputRoot.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task Export_file_picker_rejects_unsupported_extension_without_selecting()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-picker-badext-");
        var window = new MainWindow();
        try
        {
            var textPath = Path.Combine(root.FullName, "notes.txt");
            File.WriteAllText(textPath, "plain text, not a supported document");
            var textFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(textPath));
            Assert.NotNull(textFile);

            SetFilePickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFile>>([textFile!]));
            Invoke(window, "OnPickExportFile", null, new RoutedEventArgs());

            var errorText = Get<TextBlock>(window, "ExportErrorText");
            Assert.True(errorText.IsVisible);
            Assert.Contains("のいずれかを選択してください", errorText.Text, StringComparison.Ordinal);
            Assert.Empty((List<IStorageFile>)GetPrivateField(window, "_sourceFiles")!);
            Assert.False(Get<Button>(window, "ExportButton").IsEnabled);
        }
        finally
        {
            window.Close();
            root.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task Export_file_picker_empty_result_leaves_prior_selection_unchanged()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-picker-cancel-");
        var window = new MainWindow();
        try
        {
            var sourcePath = Path.Combine(root.FullName, "invoice.docx");
            File.Copy(FindFixture("complex-design-doc.docx"), sourcePath);
            var sourceFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourcePath));
            Assert.NotNull(sourceFile);

            SetFilePickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFile>>([sourceFile!]));
            Invoke(window, "OnPickExportFile", null, new RoutedEventArgs());
            Assert.Equal("invoice.docx", Get<TextBlock>(window, "ExportFileNameText").Text);

            // Simulate the user cancelling the native dialog: it returns an empty selection.
            SetFilePickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFile>>([]));
            Invoke(window, "OnPickExportFile", null, new RoutedEventArgs());

            Assert.Equal("invoice.docx", Get<TextBlock>(window, "ExportFileNameText").Text);
            Assert.False(Get<TextBlock>(window, "ExportErrorText").IsVisible);
            Assert.Single((List<IStorageFile>)GetPrivateField(window, "_sourceFiles")!);
        }
        finally
        {
            window.Close();
            root.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task Restore_file_picker_selects_markdown_and_autodetects_sidecar()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("docredock-gui-picker-restore-src-");
        var exportRoot = Directory.CreateTempSubdirectory("docredock-gui-picker-restore-out-");
        var window = new MainWindow();
        try
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, "roundtrip.docx");
            File.Copy(FindFixture("complex-design-doc.docx"), sourcePath);

            // Produce a genuine .md + .drmd sidecar pair via a real round-trip export — the same
            // workflow OnExport itself calls — so the restore picker has real artifacts to select.
            // Every await inside GuiWorkflowService.ExportAsync's round-trip path uses
            // ConfigureAwait(false), so blocking on it here from this dispatcher-affine test thread
            // cannot deadlock.
            var exported = new GuiWorkflowService()
                .ExportAsync(sourcePath, exportRoot.FullName, enableOcr: false, readable: false, useUniqueName: true)
                .GetAwaiter().GetResult();
            Assert.True(File.Exists(exported.MarkdownPath));
            Assert.True(Directory.Exists(exported.SidecarPath));

            var markdownFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(exported.MarkdownPath));
            Assert.NotNull(markdownFile);

            SetFilePickerOverride(window, _ => Task.FromResult<IReadOnlyList<IStorageFile>>([markdownFile!]));
            Invoke(window, "OnPickRestoreFiles", null, new RoutedEventArgs());

            Assert.True(Get<Control>(window, "RestoreMarkdownPanel").IsVisible);
            Assert.True(Get<Control>(window, "RestoreSidecarPanel").IsVisible);
            Assert.Equal(exported.SidecarPath, GetPrivateField(window, "_sidecarPath") as string);
            Assert.True(Get<Button>(window, "RestoreButton").IsEnabled);
            Assert.False(Get<TextBlock>(window, "RestoreErrorText").IsVisible);
        }
        finally
        {
            window.Close();
            sourceRoot.Delete(true);
            exportRoot.Delete(true);
        }
    }

    /// <summary>
    /// Drains the headless dispatcher's job queue until <paramref name="condition"/> holds. Needed
    /// only for handlers whose continuation is posted back to the UI thread after genuine background
    /// work (e.g. <c>OnExport</c>'s document conversion); picker handlers backed by an
    /// already-completed <see cref="Task"/> run their continuations inline and need no pumping.
    /// </summary>
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for the headless dispatcher to settle.");
            Thread.Sleep(20);
        }
    }

    private static string FindFixture(string fileName)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Docx", fileName);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate the checked-in fixture: tests/DocRedock.Tests/Fixtures/Docx/{fileName}");
    }

    private static object? GetPrivateField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);

    private static void SetPrivateField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    // FilePickerOverride/FolderPickerOverride are `internal` (test-only seams; see MainWindow.axaml.cs),
    // and this test project has no InternalsVisibleTo, so they are set via reflection like every other
    // non-public member this file touches, rather than direct property syntax.
    private static void SetFilePickerOverride(
        MainWindow window, Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>> handler) =>
        typeof(MainWindow).GetProperty("FilePickerOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, handler);

    private static void SetFolderPickerOverride(
        MainWindow window, Func<FolderPickerOpenOptions, Task<IReadOnlyList<IStorageFolder>>> handler) =>
        typeof(MainWindow).GetProperty("FolderPickerOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, handler);

    private static T Get<T>(MainWindow window, string name)
        where T : class =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException($"Missing generated field: {name}"));

    private static void Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);
}

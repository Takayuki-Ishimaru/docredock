using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DocRedock.Gui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DocRedock.Gui.HeadlessTests.GuiTestAppBuilder))]

namespace DocRedock.Gui.HeadlessTests;

public static class GuiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public void Main_window_constructs_without_xaml_initialization_crash()
    {
        var window = new MainWindow();
        try
        {
            Assert.NotNull(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Office_experimental_modes_are_explicitly_selectable_without_environment_flag()
    {
        var previous = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", null);
        var window = new MainWindow();
        try
        {
            var restoreMode = Get<RadioButton>(window, "RestoreModeRadio");
            var exportMode = Get<RadioButton>(window, "ExportModeRadio");
            var roundTrip = Get<RadioButton>(window, "RoundTripExportRadio");
            var readable = Get<RadioButton>(window, "ReadableExportToggle");
            var exportWorkspace = Get<Control>(window, "ExportWorkspace");
            var restoreWorkspace = Get<Control>(window, "RestoreWorkspace");

            Assert.True(restoreMode.IsEnabled);
            Assert.True(roundTrip.IsEnabled);
            Assert.Equal("復元する（実験）", restoreMode.Content?.ToString());
            Assert.Equal(".md + .drmd（実験機能）", RadioLabel(roundTrip));
            Assert.True(Get<Control>(window, "PdfSourceChip").IsVisible);
            Assert.True(Get<Control>(window, "PdfFallbackPanel").IsVisible);
            Assert.True(Get<ToggleSwitch>(window, "PdfFallbackToggle").IsEnabled);

            restoreMode.IsChecked = true;
            Assert.True(restoreWorkspace.IsVisible);
            Assert.False(exportWorkspace.IsVisible);
            exportMode.IsChecked = true;
            Assert.True(exportWorkspace.IsVisible);
            Assert.False(restoreWorkspace.IsVisible);

            roundTrip.IsChecked = true;
            Assert.False(Get<Control>(window, "ReadableOptionsPanel").IsVisible);
            Assert.True(Get<Control>(window, "SidecarOptionsPanel").IsVisible);
            Assert.True(Get<Control>(window, "RoundTripWarningText").IsVisible);
            readable.IsChecked = true;
            Assert.True(Get<Control>(window, "ReadableOptionsPanel").IsVisible);
            Assert.False(Get<Control>(window, "SidecarOptionsPanel").IsVisible);
            Assert.False(Get<Control>(window, "RoundTripWarningText").IsVisible);

            Invoke(window, "SetExportBusy", true, null);
            Assert.False(exportMode.IsEnabled);
            Assert.False(restoreMode.IsEnabled);
            Assert.False(exportWorkspace.IsEnabled);
            Assert.False(restoreWorkspace.IsEnabled);
            Assert.False(Get<ToggleSwitch>(window, "PdfFallbackToggle").IsEnabled);
            Assert.Equal("処理中…", Get<TextBlock>(window, "ExportButtonLabel").Text);
            Assert.Equal("…", Get<TextBlock>(window, "ExportButtonShortcut").Text);
            Assert.Equal("処理の進捗", AutomationProperties.GetName(Get<ProgressBar>(window, "OperationProgressBar")));
            Assert.Equal("診断情報", AutomationProperties.GetName(Get<TextBox>(window, "DiagnosticsTextBox")));

            Invoke(window, "SetExportBusy", false, null);
            Assert.True(exportMode.IsEnabled);
            Assert.True(restoreMode.IsEnabled);
            Assert.True(exportWorkspace.IsEnabled);
            Assert.True(restoreWorkspace.IsEnabled);
            Assert.True(Get<ToggleSwitch>(window, "PdfFallbackToggle").IsEnabled);
            Assert.Equal("書き出す", Get<TextBlock>(window, "ExportButtonLabel").Text);
            Assert.Equal("⌘↩", Get<TextBlock>(window, "ExportButtonShortcut").Text);
            Assert.IsType<StackPanel>(Get<Button>(window, "ExportButton").Content);

            Invoke(window, "SetRestoreBusy", true, null);
            Assert.False(exportWorkspace.IsEnabled);
            Assert.False(restoreWorkspace.IsEnabled);
            Assert.False(exportMode.IsEnabled);
            Assert.False(restoreMode.IsEnabled);
            Invoke(window, "SetRestoreBusy", false, null);
            Assert.True(exportWorkspace.IsEnabled);
            Assert.True(restoreWorkspace.IsEnabled);
        }
        finally
        {
            window.Close();
            Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previous);
        }
    }

    [AvaloniaFact]
    public void Busy_result_remains_open_for_cancellation_and_announces_status()
    {
        var window = new MainWindow();
        try
        {
            var resultPanel = Get<Control>(window, "ResultPanel");
            var closeResult = Get<Button>(window, "CloseResultButton");
            var ocrToggle = Get<ToggleSwitch>(window, "OcrToggle");
            var pdfToggle = Get<ToggleSwitch>(window, "PdfFallbackToggle");

            Assert.Equal("OCRを有効化", AutomationProperties.GetName(ocrToggle));
            Assert.Equal("PDF再描画フォールバックを許可", AutomationProperties.GetName(pdfToggle));
            Assert.Equal("処理結果と進捗", AutomationProperties.GetName(resultPanel));

            Invoke(window, "ShowTransientStatus", "変換しています…");
            Invoke(window, "SetExportBusy", true, null);
            Assert.False(closeResult.IsEnabled);
            Invoke(window, "OnCloseResult", null, new RoutedEventArgs());
            Assert.True(resultPanel.IsVisible);
            Assert.Equal("変換しています…", AutomationProperties.GetHelpText(resultPanel));

            Invoke(window, "SetExportBusy", false, null);
            Assert.True(closeResult.IsEnabled);
            Invoke(window, "OnCloseResult", null, new RoutedEventArgs());
            Assert.False(resultPanel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Restore_rejects_duplicate_same_kind_files_without_replacing_selection()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-restore-");
        var window = new MainWindow();
        try
        {
            var firstMarkdownPath = Path.Combine(root.FullName, "first.md");
            var secondMarkdownPath = Path.Combine(root.FullName, "second.md");
            var firstSidecarPath = Path.Combine(root.FullName, "first.drmd");
            var secondSidecarPath = Path.Combine(root.FullName, "second.drmd");
            File.WriteAllText(firstMarkdownPath, "# first");
            File.WriteAllText(secondMarkdownPath, "# second");
            File.WriteAllText(firstSidecarPath, "sidecar");
            File.WriteAllText(secondSidecarPath, "sidecar");
            var firstMarkdown = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(firstMarkdownPath));
            var secondMarkdown = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(secondMarkdownPath));
            var firstSidecar = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(firstSidecarPath));
            var secondSidecar = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(secondSidecarPath));
            Assert.NotNull(firstMarkdown);
            Assert.NotNull(secondMarkdown);
            Assert.NotNull(firstSidecar);
            Assert.NotNull(secondSidecar);

            Invoke(window, "SelectRestoreFiles", (object)new[] { firstMarkdown! });
            var markdownBefore = typeof(MainWindow).GetField("_markdownFile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Invoke(window, "SelectRestoreFiles", (object)new[] { firstMarkdown!, secondMarkdown! });
            Assert.Same(markdownBefore, typeof(MainWindow).GetField("_markdownFile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Contains("1つだけ", Get<TextBlock>(window, "RestoreErrorText").Text, StringComparison.Ordinal);

            Invoke(window, "SelectRestoreFiles", (object)new[] { firstSidecar! });
            var sidecarBefore = typeof(MainWindow).GetField("_sidecarPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Invoke(window, "SelectRestoreFiles", (object)new[] { firstSidecar!, secondSidecar! });
            Assert.Equal(sidecarBefore, typeof(MainWindow).GetField("_sidecarPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Contains("1つだけ", Get<TextBlock>(window, "RestoreErrorText").Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            root.Delete(true);
        }
    }

    private static T Get<T>(MainWindow window, string name)
        where T : class =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException($"Missing generated field: {name}"));

    private static string RadioLabel(RadioButton radio) =>
        ((StackPanel)radio.Content!).Children.OfType<TextBlock>().First().Text!;

    private static void Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);
}

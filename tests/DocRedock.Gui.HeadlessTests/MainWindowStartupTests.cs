using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DocRedock.Core.Reporting;
using DocRedock.Gui;
using DocRedock.Api;
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
            var reviewMode = Get<ComboBox>(window, "OcrReviewModeComboBox");
            Assert.Equal("OCR照合情報の詳細", AutomationProperties.GetName(reviewMode));
            Assert.Equal(new[] { "low-confidence", "all", "summary" },
                reviewMode.Items.Cast<ComboBoxItem>().Select(item => item.Tag?.ToString()));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Current_version_and_manual_update_action_are_visible_on_startup()
    {
        var window = new MainWindow();
        try
        {
            var version = Get<TextBlock>(window, "CurrentVersionText");
            var checkUpdates = Get<Button>(window, "CheckUpdatesButton");

            Assert.StartsWith("v", version.Text);
            Assert.Equal("更新を確認", checkUpdates.Content?.ToString());
            Assert.Equal("更新を確認", AutomationProperties.GetName(checkUpdates));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Diagnostics_are_grouped_and_present_actionable_Japanese_guidance()
    {
        var diagnostics = new[]
        {
            new Diagnostic("VisualConnectorUnresolved", "first", DiagnosticSeverity.Warning),
            new Diagnostic("VisualConnectorUnresolved", "second", DiagnosticSeverity.Warning),
            new Diagnostic("ExternalRelationshipSkipped", "informational", DiagnosticSeverity.Information),
        };
        var method = typeof(MainWindow).GetMethod(
            "FormatDiagnosticsForDisplay", BindingFlags.Static | BindingFlags.NonPublic)!;

        var formatted = Assert.IsType<string>(method.Invoke(null, [diagnostics]));

        Assert.Contains("警告 VisualConnectorUnresolved（2件）", formatted, StringComparison.Ordinal);
        Assert.Contains("接続先を一意に判断できませんでした", formatted, StringComparison.Ordinal);
        Assert.Contains("対処:", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalRelationshipSkipped", formatted, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Pdf_ocr_capability_is_disabled_when_no_ocr_provider_is_available_at_all()
    {
        // Both the Tesseract engine and the native OS provider are unavailable here, independent of
        // the rasterizer, which is also forced unavailable via the env var below. This is the only
        // combination that should disable the toggle.
        var previous = Environment.GetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER");
        Environment.SetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER", "1");
        var reporter = new CapabilityReporter(
            _ => null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(false, string.Empty)),
            () => new CapabilityStatus("ocr-native", "unavailable", "system",
                Action: "No native OCR provider is bundled for this platform; install Tesseract."));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new GuiWorkflowService(), reporter,
                () => new CapabilityStatus("pdf-rasterizer", "unavailable", Action: "Install pdftoppm or mutool, or configure an executable path."));
            var ocrToggle = Get<ToggleSwitch>(window, "OcrToggle");
            var unavailable = Get<TextBlock>(window, "OcrUnavailableText");

            Assert.False(ocrToggle.IsEnabled);
            Assert.NotEqual(true, ocrToggle.IsChecked);
            Assert.True(unavailable.IsVisible);
            Assert.Contains("rasterizer: unavailable", unavailable.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("PDF OCR: Ready", unavailable.Text, StringComparison.Ordinal);
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("DOCREDOCK_DISABLE_PDF_RASTERIZER", previous);
        }
    }

    [AvaloniaFact]
    public void Pdf_ocr_capability_stays_enabled_when_the_rasterizer_is_unavailable_but_tesseract_is_ready()
    {
        // Regression test for the reported "OCR could not be enabled on Windows" bug: pdftoppm and
        // mutool are rarely installed on Windows, but that must not disable OCR of images embedded
        // in DOCX/XLSX/PPTX (or of Tesseract-OCR'd PDF pages) — only image-only PDF pages need the
        // rasterizer. The toggle must stay enabled and explain the rasterizer gap as a side note.
        var reporter = new CapabilityReporter(
            name => name == "tesseract" ? "/tools/tesseract" : null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(true, "List of available languages in /data (2):\neng\njpn\n")),
            () => new CapabilityStatus("ocr-native", "unavailable", "windows-media",
                Action: "No Windows OCR language pack is installed."));
        var window = new MainWindow(new GuiWorkflowService(), reporter,
            () => new CapabilityStatus("pdf-rasterizer", "unavailable", Action: "Install pdftoppm or mutool, or configure an executable path."));
        try
        {
            var ocrToggle = Get<ToggleSwitch>(window, "OcrToggle");
            var status = Get<TextBlock>(window, "OcrUnavailableText");

            Assert.True(ocrToggle.IsEnabled);
            Assert.Contains("PDF OCR: Ready", status.Text, StringComparison.Ordinal);
            Assert.Contains("pdftoppm", status.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Ocr_capability_decision_is_a_pure_function_independent_of_any_window()
    {
        // GuiWorkflowService.DescribeOcrCapability backs ApplyPdfOcrCapability above; exercising it
        // directly (no AvaloniaFact/window needed) pins the same contract at the unit level.
        var rasterizer = new CapabilityStatus("pdf-rasterizer", "unavailable", Action: "Install pdftoppm or mutool.");
        var engineReady = new CapabilityStatus("ocr-engine", "ready", "tesseract");
        var nativeUnavailable = new CapabilityStatus("ocr-native", "unavailable", "windows-media");
        var jpn = new CapabilityStatus("ocr-jpn", "ready", "tesseract");
        var eng = new CapabilityStatus("ocr-eng", "ready", "tesseract");

        var decision = GuiWorkflowService.DescribeOcrCapability(rasterizer, engineReady, nativeUnavailable, jpn, eng);

        Assert.True(decision.Enabled);
        Assert.Contains("PDF OCR: Ready", decision.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Pdf_ocr_stays_available_when_a_native_provider_is_configured_but_unverified()
    {
        var reporter = new CapabilityReporter(
            _ => null,
            (_, _, _) => Task.FromResult(new CapabilityProbeResult(false, string.Empty)),
            () => new CapabilityStatus("ocr-native", "partial", "apple-vision", "/tools/swift"));
        var window = new MainWindow(new GuiWorkflowService(), reporter,
            () => new CapabilityStatus("pdf-rasterizer", "ready", "pdftoppm", "/tools/pdftoppm"));
        try
        {
            var ocrToggle = Get<ToggleSwitch>(window, "OcrToggle");
            var status = Get<TextBlock>(window, "OcrUnavailableText");

            Assert.True(ocrToggle.IsEnabled);
            Assert.Contains("Verification pending", status.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
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

            var inferenceMode = Get<ComboBox>(window, "VisualInferenceModeComboBox");
            var inferenceLabels = inferenceMode.Items.Cast<ComboBoxItem>().Select(item => item.Content?.ToString() ?? string.Empty).ToArray();
            Assert.Equal(["接続推定なし（native only）", "安全優先（safe・推奨）", "復元優先（balanced）"], inferenceLabels);
            var balancedWarning = Get<TextBlock>(window, "BalancedInferenceWarning");
            inferenceMode.SelectedIndex = 2;
            Assert.True(balancedWarning.IsVisible);
            inferenceMode.SelectedIndex = 1;
            Assert.False(balancedWarning.IsVisible);

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

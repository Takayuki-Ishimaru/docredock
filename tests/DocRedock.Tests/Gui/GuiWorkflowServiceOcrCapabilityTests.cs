using DocRedock.Api;
using DocRedock.Gui;

namespace DocRedock.Tests.Gui;

/// <summary>
/// Covers GuiWorkflowService.DescribeOcrCapability, the pure decision behind the GUI's OCR toggle.
/// Before this, MainWindow.ApplyPdfOcrCapability additionally required the PDF rasterizer
/// (pdftoppm/mutool) to be "ready" before enabling OCR at all — but the rasterizer is only needed
/// to OCR image-only PDF pages, never for OCR of images embedded in DOCX/XLSX/PPTX, and it is
/// rarely installed on Windows. That gate is what caused "OCR could not be enabled" reports even
/// when Windows Media OCR or Tesseract were perfectly usable.
/// </summary>
public sealed class GuiWorkflowServiceOcrCapabilityTests
{
    private static CapabilityStatus Rasterizer(string status, string? action = null) =>
        new("pdf-rasterizer", status, "pdftoppm", action is null ? "/tools/pdftoppm" : null, action);

    private static CapabilityStatus Status(string id, string status, string? provider = null, string? action = null) =>
        new(id, status, provider, Action: action);

    [Fact]
    public void No_provider_ready_or_partial_disables_ocr()
    {
        var decision = GuiWorkflowService.DescribeOcrCapability(
            Rasterizer("unavailable", "Install pdftoppm or mutool, or configure an executable path."),
            Status("ocr-engine", "unavailable", "tesseract", "Install Tesseract OCR and its language data."),
            Status("ocr-native", "unavailable", "system", "No native OCR provider is bundled for this platform; install Tesseract."),
            Status("ocr-jpn", "unavailable"),
            Status("ocr-eng", "unavailable"));

        Assert.False(decision.Enabled);
        Assert.DoesNotContain("PDF OCR: Ready", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("Install Tesseract", decision.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_tesseract_engine_enables_ocr_even_when_the_rasterizer_is_unavailable()
    {
        // This is the Windows regression: pdftoppm/mutool are rarely installed, but Tesseract (or a
        // native OS OCR provider) works independently of any PDF rasterizer.
        var decision = GuiWorkflowService.DescribeOcrCapability(
            Rasterizer("unavailable", "Install pdftoppm or mutool, or configure an executable path."),
            Status("ocr-engine", "ready", "tesseract"),
            Status("ocr-native", "unavailable", "windows-media", "No Windows OCR language pack is installed."),
            Status("ocr-jpn", "ready", "tesseract"),
            Status("ocr-eng", "ready", "tesseract"));

        Assert.True(decision.Enabled);
        Assert.Contains("PDF OCR: Ready", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("pdftoppm", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("mutool", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("DOCX/XLSX/PPTX", decision.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_engine_with_a_ready_rasterizer_shows_no_rasterizer_note()
    {
        var decision = GuiWorkflowService.DescribeOcrCapability(
            Rasterizer("ready"),
            Status("ocr-engine", "ready", "tesseract"),
            Status("ocr-native", "unavailable", "system"),
            Status("ocr-jpn", "ready", "tesseract"),
            Status("ocr-eng", "ready", "tesseract"));

        Assert.True(decision.Enabled);
        Assert.Contains("PDF OCR: Ready", decision.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("pdftoppm", decision.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_native_provider_enables_ocr_with_verification_pending_message_and_rasterizer_note()
    {
        var decision = GuiWorkflowService.DescribeOcrCapability(
            Rasterizer("unavailable", "Install pdftoppm or mutool, or configure an executable path."),
            Status("ocr-engine", "unavailable", "tesseract"),
            Status("ocr-native", "partial", "apple-vision"),
            Status("ocr-jpn", "unavailable"),
            Status("ocr-eng", "unavailable"));

        Assert.True(decision.Enabled);
        Assert.Contains("Verification pending", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("apple-vision", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("pdftoppm", decision.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_native_provider_enables_ocr_and_names_it_as_the_provider()
    {
        var decision = GuiWorkflowService.DescribeOcrCapability(
            Rasterizer("ready"),
            Status("ocr-engine", "unavailable", "tesseract"),
            Status("ocr-native", "ready", "windows-media"),
            Status("ocr-jpn", "unavailable"),
            Status("ocr-eng", "unavailable"));

        Assert.True(decision.Enabled);
        Assert.Contains("PDF OCR: Ready (windows-media)", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("OS 標準 OCR", decision.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("日本語 unavailable", decision.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("English unavailable", decision.StatusText, StringComparison.Ordinal);
    }
}

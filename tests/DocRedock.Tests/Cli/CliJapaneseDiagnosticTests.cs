using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Tests.Pdf;

namespace DocRedock.Tests.Cli;

/// <summary>The Japanese diagnostic guidance: the table shared by the GUI and the CLI, and the CLI line under --lang ja.
/// Codes, severities and --verbose detail lines stay as they are; only the summary and action change.</summary>
[Collection("Environment variables")]
public sealed class CliJapaneseDiagnosticTests : IDisposable
{
    private const string HiddenContentJapanese =
        "WARNING HiddenContentIncluded: 非表示の内容を出力に含めました。 共有してよい内容か確認してください。共有しないときは --content-policy visible で書き出してください。";

    private readonly string? previousExperimental = Environment.GetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL");
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-ja-").FullName;

    public CliJapaneseDiagnosticTests() => Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", "1");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOCREDOCK_ENABLE_EXPERIMENTAL", previousExperimental);
        Directory.Delete(root, true);
    }

    private async Task<(int Exit, string Output)> ExportHiddenLayerAsync(params string[] options)
    {
        var stdout = new StringWriter();
        var app = new CliApplication(stdout, new StringWriter(), new DocRedock.Api.DocumentService(null, null, discoverPdfRasterizer: false));
        var markdown = Path.Combine(root, "layer-off" + options.Length + ".md");
        var exit = await app.RunAsync(["export", PdfFormXObjectFixtureTests.FixturePath("layer-off.pdf"), "--output", markdown, "--ocr", "off", .. options]);
        return (exit, stdout.ToString());
    }

    [Theory]
    [InlineData("HiddenContentIncluded", "非表示の内容を出力に含めました。", "共有してよい内容か確認してください。共有しないときは「表示中の内容」で書き出してください。")]
    [InlineData("ImageNotWritten", "抽出した画像が本文から参照されていません。", "原本の図と出力を見比べてください。")]
    [InlineData("XlsxDuplicateCellReference", "同じセル番地が重複していたため、最後のセルを使いました。", "元ブックの該当セルを確認してください。")]
    [InlineData("XlsxFormulaCachedValueMissing", "保存済みの計算結果がない数式セルがあります。", "Excel等で再計算して保存してから、もう一度変換してください。")]
    public void Known_codes_have_their_japanese_summary_and_action(string code, string summary, string action)
    {
        Assert.True(DiagnosticGuidanceJapanese.IsKnown(code));
        Assert.Equal((summary, action), DiagnosticGuidanceJapanese.Describe(code, "original message"));
    }

    [Fact]
    public void Unknown_code_gets_the_generic_entry_that_names_the_code_and_quotes_the_original_message()
    {
        Assert.False(DiagnosticGuidanceJapanese.IsKnown("NoSuchDiagnosticCode", originalMessage: "raw text"));

        var (summary, action) = DiagnosticGuidanceJapanese.Describe("NoSuchDiagnosticCode", "raw text");

        Assert.Equal("変換時の注意事項があります（NoSuchDiagnosticCode）。", summary);
        Assert.StartsWith("元文書と生成結果の該当箇所を見比べてください。", action, StringComparison.Ordinal);
        Assert.EndsWith("詳細（英語）: raw text", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_diagnostics_use_pdf_wording_where_the_table_has_it_and_office_wording_elsewhere()
    {
        var pdf = DiagnosticGuidanceJapanese.Describe("VisualConnectorUnresolved", "message", "pdf:page:1");
        var office = DiagnosticGuidanceJapanese.Describe("VisualConnectorUnresolved", "message", "/word/document.xml");

        Assert.Equal("PDFの線や矢印の接続先・意味を自動で確定できませんでした。", pdf.Summary);
        Assert.Equal("図のコネクタの接続先を一意に判断できませんでした。", office.Summary);
    }

    [Fact]
    public async Task Under_lang_ja_a_known_warning_prints_its_japanese_summary_and_action_after_the_code()
    {
        var (exit, output) = await ExportHiddenLayerAsync("--content-policy", "complete", "--lang", "ja");

        Assert.Equal(1, exit);
        Assert.Contains(HiddenContentJapanese, output, StringComparison.Ordinal);
        // The one occurrence keeps its own message on the next line, which says where it is.
        Assert.Contains(HiddenContentJapanese + Environment.NewLine + "  - Complete content policy includes", output, StringComparison.Ordinal);
        Assert.DoesNotContain("「", output.Split(Environment.NewLine).Single(line => line.StartsWith("WARNING HiddenContentIncluded", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_lang_the_same_warning_stays_in_english()
    {
        var (exit, output) = await ExportHiddenLayerAsync("--content-policy", "complete");

        Assert.Equal(1, exit);
        Assert.Contains("WARNING HiddenContentIncluded: Complete content policy includes", output, StringComparison.Ordinal);
        Assert.DoesNotContain("非表示の内容を出力に含めました", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_lang_ja_verbose_detail_lines_stay_in_english()
    {
        var (_, output) = await ExportHiddenLayerAsync("--content-policy", "complete", "--lang", "ja", "--verbose");

        Assert.Contains(HiddenContentJapanese, output, StringComparison.Ordinal);
        Assert.Contains("  - Complete content policy includes", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DeletedRevisionContentIncluded", null, "--revisions final")]
    [InlineData("TrackedRevisionsPresent", null, "--revisions final")]
    [InlineData("HiddenContentIncluded", null, "--content-policy visible")]
    [InlineData("PdfColumnLayoutDetected", null, "照合画像")]
    [InlineData("PdfTableReconstructionFailed", null, "照合画像")]
    [InlineData("PdfFormXObjectUnparsed", null, "照合画像")]
    [InlineData("PdfFontResourceAmbiguous", null, "原本PDF")]
    [InlineData("PdfTextUndecodable", null, "原本PDF")]
    [InlineData("VisualConnectorUnresolved", "pdf:page:1", "照合画像")]
    public void The_command_line_explanation_names_no_gui_control(string code, string? partUri, string expected)
    {
        var (_, gui) = DiagnosticGuidanceJapanese.Describe(code, "message", partUri);
        var (_, cli) = DiagnosticGuidanceJapanese.DescribeForCli(code, "message", partUri);

        Assert.Contains("「", gui, StringComparison.Ordinal);
        Assert.DoesNotContain("「", cli, StringComparison.Ordinal);
        Assert.Contains(expected, cli, StringComparison.Ordinal);
    }
}

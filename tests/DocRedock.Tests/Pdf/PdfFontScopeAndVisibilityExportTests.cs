using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfFontResourceScopeTests;

namespace DocRedock.Tests.Pdf;

/// <summary>The v0.2.11 evaluation's font-scope and visibility cases, as an export reports them (see
/// generate_font_scope_pdf.py for the fixtures): a reused font name exports exactly like the
/// separately named control, text no viewer shows follows the content policy like hidden Word
/// text, and text that was kept but could not be verified asks for source review.</summary>
public sealed class PdfFontScopeAndVisibilityExportTests
{
    [Theory]
    [InlineData("font-scope-forms.pdf")]
    [InlineData("font-scope-inline.pdf")]
    public async Task A_reused_or_inline_font_exports_exactly_like_separately_named_fonts(string variant)
    {
        var root = Directory.CreateTempSubdirectory("docredock-font-").FullName;
        try
        {
            var control = await ExportAsync(root, "font-scope-flat.pdf");
            var export = await ExportAsync(root, variant);

            Assert.Equal(control.Markdown, export.Markdown);
            Assert.Matches("XXX[\\s\\S]*YYY[\\s\\S]*ZZZ", export.Markdown);
            Assert.DoesNotContain("AAA", export.Markdown, StringComparison.Ordinal);
            var summary = ExportSummaryBuilder.Build(export.Result.Graph, export.Result.Diagnostics);
            Assert.Equal((0, 0, 0), (summary.ReviewPages, summary.UnanalyzedContent, summary.Warnings));
            Assert.Contains("Human review: not required", summary.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("sanitized", false)]
    [InlineData("complete", true)]
    public async Task Text_outside_a_forms_bbox_follows_the_content_policy(string policy, bool included)
    {
        var root = Directory.CreateTempSubdirectory("docredock-clip-").FullName;
        try
        {
            var export = await ExportAsync(root, "form-bbox-clipped.pdf", policy);

            Assert.Contains("PAGE\\_TEXT", export.Markdown, StringComparison.Ordinal);
            Assert.Contains("VISIBLE\\_TEXT", export.Markdown, StringComparison.Ordinal);
            Assert.Equal(included, export.Markdown.Contains("CLIPPED\\_SENTINEL", StringComparison.Ordinal));
            var hidden = Assert.Single(export.Result.Graph.Nodes, node => node.Layer == ContentLayer.Hidden);
            Assert.Equal("CLIPPED_SENTINEL", Assert.IsType<TextNodeContent>(hidden.Content).Text);
            if (included)
                Assert.Contains(export.Result.Diagnostics, d => d.Code == "HiddenContentIncluded" && d.Severity == DiagnosticSeverity.Warning);
            else
            {
                var excluded = Assert.Single(export.Result.Diagnostics, d => d.Code == "PdfClippedTextExcluded");
                Assert.Equal(DiagnosticSeverity.Information, excluded.Severity);
                Assert.DoesNotContain(export.Result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
            }
            // Deciding that text is not visible is not a conversion problem: nothing to review.
            Assert.Equal(0, ExportSummaryBuilder.Build(export.Result.Graph, export.Result.Diagnostics).ReviewPages);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Text_on_a_layer_that_is_off_is_left_out_of_visible_output()
    {
        var root = Directory.CreateTempSubdirectory("docredock-layer-").FullName;
        try
        {
            var visible = await ExportAsync(root, "layer-off.pdf");
            var complete = await ExportAsync(Directory.CreateDirectory(Path.Combine(root, "complete")).FullName, "layer-off.pdf", "complete");

            Assert.Contains("SHOWN\\_LAYER", visible.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("HIDDEN\\_LAYER", visible.Markdown, StringComparison.Ordinal);
            Assert.Equal(DiagnosticSeverity.Information,
                Assert.Single(visible.Result.Diagnostics, d => d.Code == "PdfHiddenLayerTextExcluded").Severity);
            Assert.Contains("HIDDEN\\_LAYER", complete.Markdown, StringComparison.Ordinal);
            Assert.Contains(complete.Result.Diagnostics, d => d.Code == "HiddenContentIncluded");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Text_in_a_font_that_cannot_be_resolved_is_kept_and_sent_to_source_review()
    {
        var root = Directory.CreateTempSubdirectory("docredock-ambiguous-").FullName;
        try
        {
            var source = Path.Combine(root, "ambiguous.pdf");
            await File.WriteAllBytesAsync(source, Document(
                Object(1, "<< /Type /Page /MediaBox [0 0 612 792] /Contents 2 0 R /Resources << /Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R /Fm2 21 0 R >> >> >>"),
                Stream(2, "", "BT /F1 12 Tf 72 700 Td (AAA) Tj ET\nBT /F9 12 Tf 72 650 Td (AAA) Tj ET"),
                Stream(20, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F9 12 0 R >> >>", ""),
                Stream(21, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F9 14 0 R >> >>", ""),
                Font(10, 11), CMap(11, 'X'), Font(12, 13), CMap(13, 'Y'), Font(14, 15), CMap(15, 'Z')));
            var markdown = Path.Combine(root, "out.md");
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown));
            var text = await File.ReadAllTextAsync(markdown);
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);
            var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);

            Assert.Contains("XXX", text, StringComparison.Ordinal);
            Assert.Contains("AAA", text, StringComparison.Ordinal);
            Assert.DoesNotContain("YYY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ZZZ", text, StringComparison.Ordinal);
            Assert.Contains("could not be determined - compare with the source page", text, StringComparison.Ordinal);
            Assert.Contains(result.Diagnostics, d => d.Code == "PdfFontResourceAmbiguous" && d.Severity == DiagnosticSeverity.Warning);
            Assert.Equal((1, 1, 1), (summary.ReviewPages, summary.UnanalyzedContent, summary.UnanalyzedContentPages));
            Assert.Contains("Human review: required (unanalyzed content 1 page(s))", summary.ToString(), StringComparison.Ordinal);
            var element = Assert.Single(page.Elements);
            Assert.Equal(ReviewElementKind.UncertainText, element.Kind);
            Assert.EndsWith(":F9", element.SourceId, StringComparison.Ordinal);
            Assert.Equal("1ページ目：文字1件は、文字の対応または表示の有無を確定できませんでした（Markdownには含めています）。" +
                "照合画像を作成できなかったため、原本PDFの1ページ目で内容を確認してください。", ExportReviewText.DescribeJapanese(page));
            Assert.Equal("page 1: 1 text item(s) whose characters or visibility could not be determined; kept in the Markdown",
                ExportReviewText.DescribeEnglish(page));
            Assert.Contains(result.Diagnostics, d => d.Code == "PdfReviewImageUnavailable" &&
                d.Message.StartsWith("PDF page 1: text whose characters or visibility could not be determined needs source comparison", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(ReadableDocumentExportResult Result, string Markdown)> ExportAsync(string root, string fixture, string policy = "visible")
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, Path.GetFileNameWithoutExtension(fixture))).FullName;
        var markdown = Path.Combine(directory, "out.md");
        var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(PdfFormXObjectFixtureTests.FixturePath(fixture), markdown, ContentPolicy: policy));
        return (result, await File.ReadAllTextAsync(markdown));
    }
}

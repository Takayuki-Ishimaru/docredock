using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Tests.Pdf;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Api;

/// <summary>Regressions for the 2026-10-29 evaluation of v0.3.10: lone rules, first-line indents, running headers and
/// paragraphs cut by a column or a page break in PDFs, unreadable sources, and the CLI's output for automation.</summary>
public sealed class V0311ReviewRegressionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-v0311-review-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static string Text(double x, double y, string text, double size = 10) => $"BT /F1 {N(size)} Tf 1 0 0 1 {N(x)} {N(y)} Tm ({text}) Tj ET";

    // A line of 'width' points made of words, as wide as it can be without passing it.
    private static string Fill(string seed, double width, double size = 10)
    {
        var words = new List<string>();
        for (var index = 0; ; index++)
        {
            var next = string.Join(' ', words.Append($"{seed}{index}"));
            if (Width(next, size) > width) return string.Join(' ', words);
            words.Add($"{seed}{index}");
        }
    }

    [Fact]
    public void A_lone_rule_under_a_running_header_is_no_connector_and_sets_the_header_apart()
    {
        // A one-page bulletin: a header line, the rule under it across the text width, and a paragraph.
        var pdf = Page(string.Join("\n",
            Text(56, 800, "Bulletin 42", 9),
            "0.8 w 56 792 m 539 792 l S",
            Text(56, 740, "The warehouse was reorganised this year, and shipping errors fell by half."),
            Text(56, 726, "The next step is a new order system.")));
        var result = PdfTextExtractor.Extract(pdf);

        Assert.DoesNotContain(result.Diagnostics ?? [], message => message.StartsWith("VisualConnectorUnresolved", StringComparison.Ordinal));
        Assert.Null(result.VisualGraphs?.GetValueOrDefault(1));
        var rule = Assert.Single(result.Pages[0].SeparatorRules!);
        Assert.True(rule.Horizontal);
        Assert.Equal(792, rule.Position, 1);
        var header = Assert.Single(result.Pages[0].Regions, region => region.Text == "Bulletin 42");
        Assert.True(header.IsPageFurniture);
        Assert.All(result.Pages[0].Regions.Where(region => region.Text != "Bulletin 42"), region => Assert.False(region.IsPageFurniture));
    }

    [Fact]
    public void A_short_bar_over_a_word_and_a_line_that_meets_a_box_stay_drawings()
    {
        // An overline 0.55 em over "reset" belongs to that word; a line that runs into a box is a connector.
        var pdf = Page(string.Join("\n",
            Text(72, 720, "reset", 12), "0.6 w 72 726.6 m 100 726.6 l S",
            "300 600 80 40 re S", "0.8 w 200 620 m 300 620 l S"));
        var result = PdfTextExtractor.Extract(pdf);
        Assert.Null(result.Pages[0].SeparatorRules);
        Assert.NotNull(result.VisualGraphs?.GetValueOrDefault(1));
    }

    [Fact]
    public void A_paragraph_whose_first_line_is_indented_by_an_em_is_one_paragraph()
    {
        const double size = 10, left = 72, width = 300;
        var first = Fill("a", width - size);
        var second = Fill("b", width);
        const string last = "and the end.";
        var pdf = Page(string.Join("\n",
            Text(left + size, 700, first), Text(left, 686, second), Text(left, 672, last)));
        var regions = PdfTextExtractor.Extract(pdf).Pages[0].Regions;
        var paragraph = Assert.Single(regions);
        Assert.Equal($"{first} {second} {last}", paragraph.Text);
    }

    [Fact]
    public async Task A_paragraph_cut_by_a_page_break_reads_whole_and_the_running_header_is_listed_once()
    {
        // Two pages with the same header; the paragraph runs full to the foot of page 1 without closing a sentence.
        var lines = Enumerable.Range(0, 40).Select(index => Fill($"w{index}x", 400)).ToArray();
        string PageContent(IEnumerable<string> body, double start) => string.Join("\n",
            new[] { Text(72, 800, "Internal - Sales planning", 9) }.Concat(body.Select((line, index) => Text(72, start - index * 14, line))));
        var page1 = PageContent(lines.Take(30), 740);
        var page2 = PageContent(lines.Skip(30).Append("And this ends it."), 740);
        var pdf = TwoPages(page1, page2);
        var source = Path.Combine(root, "cut.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        var markdownPath = Path.Combine(root, "cut.md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, markdownPath, IncludePdfFallbackImages: false));
        var markdown = await File.ReadAllTextAsync(markdownPath);

        Assert.StartsWith("# cut\n", markdown.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains(lines[29] + " " + lines[30], markdown, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, "Internal - Sales planning"));
        Assert.Contains("Document headers and footers (reference)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_encrypted_pdf_and_a_password_protected_office_file_say_what_they_are()
    {
        var pdf = Path.Combine(root, "locked.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n1 0 obj << /Type /Page >> endobj\ntrailer << /Root 1 0 R /Encrypt 5 0 R >>\n%%EOF");
        var office = Path.Combine(root, "locked.docx");
        // An encrypted OOXML file is an OLE compound file holding EncryptionInfo and EncryptedPackage streams.
        var compound = new byte[4096];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(compound, 0);
        Encoding.Unicode.GetBytes("EncryptedPackage").CopyTo(compound, 1024);
        await File.WriteAllBytesAsync(office, compound);
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);

        var encrypted = await Assert.ThrowsAsync<DocumentSourceException>(() =>
            service.ExportReadableAsync(new ReadableDocumentExportOptions(pdf, Path.Combine(root, "locked-pdf.md"))));
        Assert.Equal(DocumentSourceProblem.EncryptedPdf, encrypted.Problem);
        Assert.Contains("locked.pdf", encrypted.JapaneseMessage, StringComparison.Ordinal);
        var protectedFile = await Assert.ThrowsAsync<DocumentSourceException>(() =>
            service.ExportReadableAsync(new ReadableDocumentExportOptions(office, Path.Combine(root, "locked-docx.md"))));
        Assert.Equal(DocumentSourceProblem.PasswordProtected, protectedFile.Problem);
        Assert.Contains("password", protectedFile.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_json_is_one_envelope_for_a_failure_and_names_the_pdf_option()
    {
        var pdf = Path.Combine(root, "plain.pdf");
        await File.WriteAllBytesAsync(pdf, Page(Text(72, 700, "Hello")));
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new DocRedock.Cli.CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false))
            .RunAsync(["export", pdf, "--json", "--output", Path.Combine(root, "plain.md")]);

        Assert.Equal(4, code);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("docredock.export.v2", json.RootElement.GetProperty("schema").GetString());
        var result = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(4, result.GetProperty("exit_code").GetInt32());
        Assert.Equal("pdf_not_enabled", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("--allow-pdf", result.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("plain.pdf", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_json_success_has_structured_fields_and_words_for_severity()
    {
        var pdf = Path.Combine(root, "ok.pdf");
        await File.WriteAllBytesAsync(pdf, Page(Text(72, 700, "Hello")));
        var output = new StringWriter();
        var code = await new DocRedock.Cli.CliApplication(output, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false))
            .RunAsync(["export", pdf, "--allow-pdf", "--json", "--ocr", "off", "--output", Path.Combine(root, "ok.md")]);

        using var json = JsonDocument.Parse(output.ToString());
        var result = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(code, result.GetProperty("exit_code").GetInt32());
        Assert.Equal("pdf", result.GetProperty("format").GetString());
        Assert.Equal("readable", result.GetProperty("mode").GetString());
        Assert.Equal("safe", result.GetProperty("visual_inference").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
        Assert.All(result.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => Assert.Equal(JsonValueKind.String, diagnostic.GetProperty("severity").ValueKind));
    }

    [Fact]
    public async Task Batch_collisions_name_the_inputs_and_keep_source_extension_separates_them()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "in")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "report.pdf"), Page(Text(72, 700, "PDF text")));
        await File.WriteAllBytesAsync(Path.Combine(folder, "report.xlsx"), []);
        var error = new StringWriter();
        var cli = new DocRedock.Cli.CliApplication(new StringWriter(), error, new DocumentService(null, null, discoverPdfRasterizer: false));
        Assert.Equal(2, await cli.RunAsync(["export", folder, "--allow-pdf", "--output-dir", Path.Combine(root, "out")]));
        Assert.Contains("report.pdf", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("report.xlsx", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--keep-source-extension", error.ToString(), StringComparison.Ordinal);

        await new DocRedock.Cli.CliApplication(new StringWriter(), new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false))
            .RunAsync(["export", folder, "--allow-pdf", "--ocr", "off", "--output-dir", Path.Combine(root, "out"), "--keep-source-extension"]);
        Assert.True(File.Exists(Path.Combine(root, "out", "report.pdf.md")));
    }

    [Fact]
    public async Task Readable_markdown_given_to_verify_is_explained()
    {
        var markdown = Path.Combine(root, "readable.md");
        await File.WriteAllTextAsync(markdown, "# Title\n\nBody\n");
        var error = new StringWriter();
        var code = await new DocRedock.Cli.CliApplication(new StringWriter(), error).RunAsync(["verify", markdown]);
        Assert.Equal(2, code);
        Assert.Contains("readable Markdown", error.ToString(), StringComparison.Ordinal);
    }

    // A two-byte font of fixed widths: East Asian characters an em wide, digits half an em.
    private static Dictionary<int, string> AsianFont(string name, params char[] characters) => new()
    {
        [4] = $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [5 0 R] /ToUnicode 6 0 R >>",
        [5] = $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /{name} /DW 1000 /W [{string.Join(" ", characters.Where(char.IsAsciiDigit).Select(character => $"{(int)character} [500]"))}] >>",
        [6] = TwoByteCMap(characters.Select(character => ((int)character, character)).ToArray()),
    };

    private static string Hex(string text) => "<" + string.Concat(text.Select(character => ((int)character).ToString("X4"))) + ">";

    [Fact]
    public void A_number_in_japanese_text_gets_no_space_from_the_gap_around_it()
    {
        // 「開催11月1日」 drawn in pieces 0.6 em apart, as a justified line or the spacing between Asian and Latin text sets them.
        const double size = 10;
        var pieces = new[] { "開催", "11", "月", "1", "日" };
        var content = new StringBuilder();
        var x = 72.0;
        foreach (var piece in pieces)
        {
            content.Append($"BT /F1 {N(size)} Tf 1 0 0 1 {N(x)} 700 Tm {Hex(piece)} Tj ET\n");
            x += piece.Sum(character => char.IsAsciiDigit(character) ? 0.5 : 1.0) * size + 0.6 * size;
        }
        var pdf = Page(content.ToString(), objects: AsianFont("Gothic", '開', '催', '月', '日', '1'));
        Assert.Equal("開催11月1日", Assert.Single(PdfTextExtractor.Extract(pdf).Pages[0].Regions).Text);
    }

    [Fact]
    public void A_short_line_in_bold_or_in_another_face_at_body_size_is_a_heading()
    {
        var body = Enumerable.Range(0, 4).Select(index => Fill($"text{index}x", 300)).ToArray();
        var fonts = new Dictionary<int, string>
        {
            [4] = SimpleFont,
            [7] = SimpleFont.Replace("/BaseFont /Measured", "/BaseFont /Measured-Bold", StringComparison.Ordinal),
            [8] = SimpleFont.Replace("/BaseFont /Measured", "/BaseFont /Gothic", StringComparison.Ordinal),
        };
        string Line(string font, double y, string text) => $"BT /{font} 10 Tf 1 0 0 1 72 {N(y)} Tm ({text}) Tj ET";
        var pdf = Page(string.Join("\n",
                Line("F2", 760, "Results"), Line("F1", 740, body[0]), Line("F1", 726, body[1] + "."),
                Line("F3", 700, "Next steps"), Line("F1", 680, body[2]), Line("F1", 666, body[3] + ".")),
            "<< /Font << /F1 4 0 R /F2 7 0 R /F3 8 0 R >> >>", fonts);
        var page = PdfTextExtractor.Extract(pdf).Pages[0];
        Assert.Equal(3, PdfPageProjection.HeadingLevel(page, page.Regions.Single(region => region.Text == "Results")));
        Assert.Equal(3, PdfPageProjection.HeadingLevel(page, page.Regions.Single(region => region.Text == "Next steps")));
        Assert.All(page.Regions.Where(region => region.Text is not ("Results" or "Next steps")),
            region => Assert.Equal(0, PdfPageProjection.HeadingLevel(page, region)));
    }

    [Fact]
    public async Task A_tagged_figure_description_reaches_the_markdown()
    {
        const string resources = "<< /Font << /F1 4 0 R >> /XObject << /Im1 7 0 R >> >>";
        var objects = new Dictionary<int, string>
        {
            [4] = SimpleFont,
            [7] = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >> stream\n\u0000\nendstream",
            [8] = "<< /Type /StructElem /S /Figure /Alt (Sales chart: April 720, September 800) /Pg 1 0 R /K 0 >>",
        };
        var pdf = Page(string.Join("\n", Text(72, 720, "Quarterly sales"),
            "/Figure << /MCID 0 >> BDC q 200 0 0 100 72 560 cm /Im1 Do Q EMC"), resources, objects);
        var source = Path.Combine(root, "figure.pdf");
        await File.WriteAllBytesAsync(source, pdf);
        var markdownPath = Path.Combine(root, "figure.md");
        await new DocumentService(null, null, discoverPdfRasterizer: false).ExportReadableAsync(
            new ReadableDocumentExportOptions(source, markdownPath, IncludePdfFallbackImages: false));
        Assert.Contains("Image description: Sales chart: April 720, September 800", await File.ReadAllTextAsync(markdownPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Readable_markdown_repeats_vertical_merges_by_default_and_never_horizontal_ones()
    {
        Assert.True(new ReadableDocumentExportOptions("a.docx", "a.md").ExpandMergedCells);
        // A total spanning two columns, over a value spanning two rows.
        IReadOnlyList<IReadOnlyList<TableCell>> rows =
        [
            [new TableCell("Item"), new TableCell("Q1"), new TableCell("Q2")],
            [new TableCell("Rent", RowSpan: 2), new TableCell("10"), new TableCell("12")],
            [new TableCell("", RowSpan: 0), new TableCell("11"), new TableCell("13")],
            [new TableCell("Total", ColSpan: 2), new TableCell("25")],
        ];
        var graph = new DocumentGraph("1.1", "doc_merge", DocumentFormatKind.Docx,
            [new DocumentPartition("body", 0, [new DocumentNode("t", NodeKind.Table, null, 0, ContentLayer.Body, new TableNodeContent(rows))])]);
        var markdown = new DocRedock.Markdown.ReadableMarkdownSerializer(new(ExpandMergedCells: true)).Serialize(graph);
        Assert.Contains("| Rent | 11 | 13 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Total |  | 25 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Total | Total", markdown, StringComparison.Ordinal);
    }

    // Two pages, each drawing its content with MeasuredPdf's font.
    private static byte[] TwoPages(string first, string second)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        pdf.Append("1 0 obj << /Type /Page /MediaBox [0 0 595 842] /Resources ").Append(Resources).Append(" /Contents 2 0 R >> endobj\n");
        pdf.Append("2 0 obj ").Append(Stream(first)).Append(" endobj\n");
        pdf.Append("3 0 obj << /Type /Page /MediaBox [0 0 595 842] /Resources ").Append(Resources).Append(" /Contents 5 0 R >> endobj\n");
        pdf.Append("4 0 obj ").Append(SimpleFont).Append(" endobj\n");
        pdf.Append("5 0 obj ").Append(Stream(second)).Append(" endobj\n");
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }
}

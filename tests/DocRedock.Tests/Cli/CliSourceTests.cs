using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Cli;

/// <summary><c>docredock source</c>: one piece of the original document - a part, node, sheet range, slide or page - read as
/// it is stored, so that an export that needs attention can be checked without reading the whole file.</summary>
public sealed class CliSourceTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-source-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private static string Fixture(params string[] path) => HtmlFixtures.Path_(path);

    private static async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false)).RunAsync(args);
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<JsonElement> JsonAsync(params string[] args)
    {
        var (exit, output, error) = await RunAsync([.. args, "--json"]);
        Assert.True(exit == 0, error);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    private string Docx(params string[] paragraphs) => SourceTestPackages.Docx(root, "plan.docx", paragraphs);

    [Fact]
    public async Task The_overview_of_a_docx_lists_its_parts_with_sizes_and_content_types()
    {
        var json = await JsonAsync("source", Docx("Alpha"));

        Assert.Equal("overview", json.GetProperty("locator").GetProperty("kind").GetString());
        var parts = json.GetProperty("parts").EnumerateArray().ToDictionary(part => part.GetProperty("name").GetString()!);
        Assert.Equal(json.GetProperty("part_count").GetInt32(), parts.Count);
        Assert.True(parts["word/document.xml"].GetProperty("size").GetInt64() > 0);
        Assert.Contains("wordprocessingml.document.main+xml", parts["word/document.xml"].GetProperty("content_type").GetString());
        Assert.Equal("image/png", parts["word/media/image1.png"].GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task The_overview_of_an_xlsx_names_its_sheets_in_order()
    {
        var json = await JsonAsync("source", SourceTestPackages.Xlsx(root, "book.xlsx"));

        Assert.Equal(2, json.GetProperty("sheet_count").GetInt32());
        var sheets = json.GetProperty("sheets").EnumerateArray().ToArray();
        Assert.Equal(["Data", "メモ"], sheets.Select(sheet => sheet.GetProperty("name").GetString()));
        Assert.Equal("xl/worksheets/sheet1.xml", sheets[0].GetProperty("part").GetString());
        Assert.Equal("visible", sheets[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_overview_of_a_pptx_counts_its_slides_and_names_their_parts()
    {
        var json = await JsonAsync("source", SourceTestPackages.Pptx(root, "deck.pptx"));

        Assert.Equal(2, json.GetProperty("slide_count").GetInt32());
        Assert.Equal(["ppt/slides/slide1.xml", "ppt/slides/slide2.xml"],
            json.GetProperty("slides").EnumerateArray().Select(slide => slide.GetProperty("part").GetString()));
    }

    [Fact]
    public async Task The_overview_of_a_pdf_gives_its_page_count_and_needs_allow_pdf()
    {
        var pdf = Fixture("Pdf", "two-column-paper-en.pdf");

        var json = await JsonAsync("source", pdf, "--allow-pdf");
        var (exit, _, error) = await RunAsync("source", pdf);

        Assert.Equal(3, json.GetProperty("page_count").GetInt32());
        Assert.Equal(4, exit);
        Assert.Contains("--allow-pdf", error);
    }

    [Fact]
    public async Task A_part_is_read_in_windows_that_continue_exactly_where_the_last_one_stopped()
    {
        var document = Docx("Alpha", "Beta", "Gamma");
        var whole = await JsonAsync("source", document, "--part", "word/document.xml", "--max-chars", "100000");
        var xml = whole.GetProperty("content").GetString()!;
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("next_offset").ValueKind);
        Assert.Equal(xml.Length, whole.GetProperty("total_chars").GetInt64());

        var read = new System.Text.StringBuilder();
        var offset = 0;
        var windows = 0;
        while (true)
        {
            var window = await JsonAsync("source", document, "--part", "word/document.xml", "--offset", offset.ToString(), "--max-chars", "40");
            var content = window.GetProperty("content").GetString()!;
            Assert.Equal(offset, window.GetProperty("offset").GetInt32());
            Assert.True(content.Length <= 40);
            read.Append(content);
            windows++;
            if (window.GetProperty("next_offset").ValueKind == JsonValueKind.Null) break;
            Assert.Equal(offset + content.Length, window.GetProperty("next_offset").GetInt32());
            offset = window.GetProperty("next_offset").GetInt32();
        }

        Assert.True(windows > 1);
        Assert.Equal(xml, read.ToString());
        Assert.Contains("Beta", xml);
    }

    [Fact]
    public async Task A_part_name_is_accepted_with_or_without_the_leading_slash()
    {
        var document = Docx("Alpha");

        var plain = await JsonAsync("source", document, "--part", "word/document.xml");
        var slashed = await JsonAsync("source", document, "--part", "/word/document.xml");

        Assert.Equal(plain.GetProperty("content").GetString(), slashed.GetProperty("content").GetString());
        Assert.Equal("word/document.xml", slashed.GetProperty("part").GetString());
    }

    [Fact]
    public async Task A_binary_part_that_is_not_a_picture_is_refused_with_its_size_and_type()
    {
        var (exit, _, error) = await RunAsync("source", Docx("Alpha"), "--part", "word/embeddings/oleObject1.bin");

        Assert.Equal(2, exit);
        Assert.Contains("word/embeddings/oleObject1.bin", error);
        Assert.Contains("binary", error);
        Assert.Matches(@"\d+ bytes", error);
    }

    [Fact]
    public async Task An_unknown_part_is_an_error_that_names_parts_that_exist()
    {
        var (exit, _, error) = await RunAsync("source", Docx("Alpha"), "--part", "word/nothing.xml");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown part 'word/nothing.xml'", error);
        Assert.Contains("word/document.xml", error);
    }

    [Fact]
    public async Task An_offset_past_the_end_of_a_part_is_an_error()
    {
        var (exit, _, error) = await RunAsync("source", Docx("Alpha"), "--part", "word/document.xml", "--offset", "99999");

        Assert.Equal(2, exit);
        Assert.Contains("past the end", error);
    }

    [Fact]
    public async Task A_window_never_ends_between_the_two_halves_of_a_surrogate_pair()
    {
        var document = Docx(string.Concat(Enumerable.Repeat("\U0001F600", 20)));
        var read = new System.Text.StringBuilder();
        var offset = 0;
        while (true)
        {
            var window = await JsonAsync("source", document, "--part", "word/document.xml", "--offset", offset.ToString(), "--max-chars", "7");
            var content = window.GetProperty("content").GetString()!;
            Assert.False(content.Length > 0 && char.IsHighSurrogate(content[^1]), "ends inside a surrogate pair");
            read.Append(content);
            if (window.GetProperty("next_offset").ValueKind == JsonValueKind.Null) break;
            offset = window.GetProperty("next_offset").GetInt32();
        }

        Assert.Contains(string.Concat(Enumerable.Repeat("\U0001F600", 20)), read.ToString());
    }

    [Fact]
    public async Task A_node_from_an_ai_package_resolves_to_its_raw_xml()
    {
        var document = Docx("Alpha paragraph", "Beta paragraph", "Gamma paragraph");
        var ids = await PackageNodeIdsAsync(document);

        var found = new List<string>();
        foreach (var id in ids)
        {
            var json = await JsonAsync("source", document, "--node", id);
            var node = json.GetProperty("node");
            Assert.Equal(id, node.GetProperty("id").GetString());
            Assert.Equal("node", json.GetProperty("locator").GetProperty("kind").GetString());
            Assert.Equal("docx", node.GetProperty("anchor").GetProperty("format").GetString());
            var slice = node.GetProperty("raw_slice");
            Assert.Equal("/word/document.xml", slice.GetProperty("part_uri").GetString());
            Assert.True(slice.GetProperty("sha256_matches").GetBoolean());
            Assert.Contains(node.GetProperty("text").GetString()!, json.GetProperty("content").GetString());
            Assert.StartsWith("<w:p", json.GetProperty("content").GetString());
            found.Add(node.GetProperty("text").GetString()!);
        }

        Assert.Contains("Alpha paragraph", found);
        Assert.Contains("Gamma paragraph", found);
    }

    [Fact]
    public async Task Every_node_id_of_a_real_fixtures_ai_package_resolves_to_the_same_node()
    {
        var document = Fixture("Docx", "complex-design-doc.docx");
        var ids = await PackageNodeIdsAsync(document);
        Assert.True(ids.Count > 5);

        // The ids the package carries are those of the same conversion run here with the default options.
        foreach (var id in ids.Take(6))
        {
            var json = await JsonAsync("source", document, "--node", id);
            Assert.Equal(id, json.GetProperty("node").GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task A_node_without_a_raw_slice_gives_its_anchor_and_points_to_the_part()
    {
        var workbook = SourceTestPackages.Xlsx(root, "book.xlsx");
        var ids = await PackageNodeIdsAsync(workbook);

        var json = await JsonAsync("source", workbook, "--node", ids[0]);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("node").GetProperty("raw_slice").ValueKind);
        Assert.Equal("xlsx", json.GetProperty("node").GetProperty("anchor").GetProperty("format").GetString());
        Assert.Equal("", json.GetProperty("content").GetString());
        Assert.Contains("--sheet", json.GetProperty("note").GetString());
    }

    [Fact]
    public async Task An_unknown_node_id_is_an_error()
    {
        var (exit, _, error) = await RunAsync("source", Docx("Alpha"), "--node", "n_DOESNOTEXIST0000");

        Assert.Equal(2, exit);
        Assert.Contains("n_DOESNOTEXIST0000", error);
        Assert.Contains("not found", error);
    }

    [Fact]
    public async Task A_sheet_range_gives_values_formulas_and_merged_ranges()
    {
        var json = await JsonAsync("source", SourceTestPackages.Xlsx(root, "book.xlsx"), "--sheet", "Data", "--range", "A1:D4");

        Assert.Equal("Data", json.GetProperty("sheet").GetString());
        Assert.Equal("A1:D4", json.GetProperty("range").GetString());
        var cells = json.GetProperty("cells").EnumerateArray().ToDictionary(cell => cell.GetProperty("address").GetString()!);
        Assert.Equal("Item", cells["A1"].GetProperty("value").GetString());
        Assert.Equal("10", cells["B2"].GetProperty("value").GetString());
        Assert.Equal("20", cells["C2"].GetProperty("value").GetString());
        Assert.Equal("B2*2", cells["C2"].GetProperty("formula").GetString());
        Assert.False(cells["B2"].TryGetProperty("formula", out _));
        Assert.Equal("A4:B4", cells["A4"].GetProperty("merged_range").GetString());
        Assert.Equal(["A4:B4"], json.GetProperty("merged_ranges").EnumerateArray().Select(range => range.GetString()));
        Assert.Equal([3], json.GetProperty("hidden_rows").EnumerateArray().Select(row => row.GetInt32()));
        Assert.Equal(["D"], json.GetProperty("hidden_columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Fact]
    public async Task A_sheet_without_a_range_reads_its_used_range_and_a_japanese_name_stays_readable()
    {
        var workbook = SourceTestPackages.Xlsx(root, "book.xlsx");

        var (exit, output, error) = await RunAsync("source", workbook, "--sheet", "メモ", "--json");

        Assert.True(exit == 0, error);
        Assert.Contains("こんにちは", output);
        Assert.DoesNotContain("\\u30", output);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.Equal("A1", json.GetProperty("used_range").GetString());
        Assert.Equal("こんにちは", json.GetProperty("cells")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_unknown_sheet_lists_the_sheet_names()
    {
        var (exit, _, error) = await RunAsync("source", SourceTestPackages.Xlsx(root, "book.xlsx"), "--sheet", "Nope");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown sheet 'Nope'", error);
        Assert.Contains("\"Data\"", error);
        Assert.Contains("\"メモ\"", error);
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("A1:")]
    [InlineData("1:5")]
    [InlineData("XFE1")]
    [InlineData("A1:B2:C3")]
    [InlineData("hello")]
    public async Task An_invalid_range_is_an_error(string range)
    {
        var (exit, _, error) = await RunAsync("source", SourceTestPackages.Xlsx(root, "book.xlsx"), "--sheet", "Data", "--range", range);

        Assert.Equal(2, exit);
        Assert.Contains("is not a cell range", error);
    }

    [Fact]
    public async Task A_range_needs_a_sheet()
    {
        var (exit, _, error) = await RunAsync("source", SourceTestPackages.Xlsx(root, "book.xlsx"), "--range", "A1:B2");

        Assert.Equal(2, exit);
        Assert.Contains("--range needs --sheet", error);
    }

    [Fact]
    public async Task A_slide_gives_its_part_name_xml_and_notes_part()
    {
        var deck = SourceTestPackages.Pptx(root, "deck.pptx");
        var json = await JsonAsync("source", deck, "--slide", "1");

        Assert.Equal("ppt/slides/slide1.xml", json.GetProperty("part").GetString());
        Assert.Equal(1, json.GetProperty("slide").GetInt32());
        Assert.Equal("ppt/notesSlides/notesSlide1.xml", json.GetProperty("notes_part").GetString());
        Assert.Contains("Quarterly plan", json.GetProperty("content").GetString());
        var second = await JsonAsync("source", deck, "--slide", "2");
        Assert.Contains("Second slide", second.GetProperty("content").GetString());
        Assert.False(second.TryGetProperty("notes_part", out _));
    }

    [Theory]
    [InlineData("0", "must be a whole number of 1 or more")]
    [InlineData("3", "has 2 slide(s)")]
    [InlineData("99", "has 2 slide(s)")]
    public async Task A_slide_out_of_range_is_an_error(string slide, string expected)
    {
        var (exit, _, error) = await RunAsync("source", SourceTestPackages.Pptx(root, "deck.pptx"), "--slide", slide);

        Assert.Equal(2, exit);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task A_page_gives_the_text_lines_with_positions_and_font_sizes()
    {
        var pdf = Fixture("Pdf", "two-column-paper-en.pdf");

        var json = await JsonAsync("source", pdf, "--allow-pdf", "--page", "1", "--max-chars", "100000");

        var content = json.GetProperty("content").GetString()!;
        Assert.Contains("Anomaly Detection for Monitoring Data", content);
        Assert.Matches(@"\[x=[\d.]+ y=[\d.]+ size=[\d.]+\] Anomaly Detection", content);
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        // The lines as extracted: no heading marks the export would add.
        Assert.DoesNotContain("# Anomaly", content);
    }

    [Fact]
    public async Task A_long_page_is_paged_and_a_page_out_of_range_is_an_error()
    {
        var pdf = Fixture("Pdf", "two-column-paper-en.pdf");

        var first = await JsonAsync("source", pdf, "--allow-pdf", "--page", "1", "--max-chars", "300");
        var (exit, _, error) = await RunAsync("source", pdf, "--allow-pdf", "--page", "9");

        Assert.Equal(300, first.GetProperty("content").GetString()!.Length);
        Assert.Equal(300, first.GetProperty("next_offset").GetInt32());
        Assert.Equal(2, exit);
        Assert.Contains("has 3 page(s)", error);
    }

    [Fact]
    public async Task A_page_is_refused_without_allow_pdf_as_export_refuses_it()
    {
        var (exit, _, error) = await RunAsync("source", Fixture("Pdf", "two-column-paper-en.pdf"), "--page", "1");

        Assert.Equal(4, exit);
        Assert.Contains("PDF export is off by default", error);
    }

    [Theory]
    [InlineData("--part", "word/document.xml", "--node", "n_x")]
    [InlineData("--sheet", "Data", "--slide", "1")]
    [InlineData("--page", "1", "--part", "word/document.xml")]
    public async Task Conflicting_locators_are_an_error(params string[] locators)
    {
        var (exit, _, error) = await RunAsync(["source", Docx("Alpha"), .. locators]);

        Assert.Equal(2, exit);
        Assert.Contains("Use only one locator", error);
    }

    [Fact]
    public async Task A_locator_that_does_not_fit_the_format_is_an_error()
    {
        var docx = Docx("Alpha");
        var (sheetExit, _, sheetError) = await RunAsync("source", docx, "--sheet", "Data");
        var (slideExit, _, slideError) = await RunAsync("source", docx, "--slide", "1");
        var (pageExit, _, pageError) = await RunAsync("source", docx, "--page", "1", "--allow-pdf");
        var (partExit, _, partError) = await RunAsync("source", Fixture("Pdf", "two-column-paper-en.pdf"), "--allow-pdf", "--part", "word/document.xml");

        Assert.Equal([2, 2, 2, 2], [sheetExit, slideExit, pageExit, partExit]);
        Assert.Contains("--sheet does not fit this docx file", sheetError);
        Assert.Contains("--slide does not fit this docx file", slideError);
        Assert.Contains("--page does not fit this docx file", pageError);
        Assert.Contains("--part does not fit this pdf file", partError);
        Assert.Contains("--page", partError);
    }

    [Theory]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "x")]
    [InlineData("--max-chars", "0")]
    [InlineData("--slide", "0")]
    [InlineData("--page", "0")]
    public async Task A_bad_number_is_a_usage_error(string option, string value)
    {
        var (exit, _, _) = await RunAsync("source", Docx("Alpha"), option, value);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Paging_options_do_not_apply_to_an_overview_or_a_sheet()
    {
        var workbook = SourceTestPackages.Xlsx(root, "book.xlsx");
        var (overviewExit, _, _) = await RunAsync("source", workbook, "--offset", "5");
        var (sheetExit, _, _) = await RunAsync("source", workbook, "--sheet", "Data", "--max-chars", "5");

        Assert.Equal([2, 2], [overviewExit, sheetExit]);
    }

    [Fact]
    public async Task Missing_unsupported_and_legacy_files_report_like_export()
    {
        var text = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(text, "not a document");
        var legacy = Path.Combine(root, "old.doc");
        await File.WriteAllBytesAsync(legacy, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]);

        var (missing, _, _) = await RunAsync("source", Path.Combine(root, "missing.docx"));
        var (unsupported, _, unsupportedError) = await RunAsync("source", text);
        var (old, _, oldError) = await RunAsync("source", legacy);
        var (none, _, noneError) = await RunAsync("source");

        Assert.Equal(2, missing);
        Assert.Equal(4, unsupported);
        Assert.Contains("not a supported or readable", unsupportedError);
        Assert.Equal(4, old);
        Assert.Contains("Legacy Office files are not supported", oldError);
        Assert.Equal(2, none);
        Assert.Contains("Use source <file>", noneError);
    }

    [Fact]
    public async Task Json_output_carries_the_schema_the_locator_and_the_raw_notice()
    {
        var document = Docx("Alpha");
        foreach (var args in new[]
                 {
                     new[] { "source", document },
                     ["source", document, "--part", "word/document.xml"],
                     ["source", SourceTestPackages.Xlsx(root, "book.xlsx"), "--sheet", "Data"],
                     ["source", SourceTestPackages.Pptx(root, "deck.pptx"), "--slide", "2"],
                 })
        {
            var json = await JsonAsync(args);

            Assert.Equal("docredock.source.v1", json.GetProperty("schema").GetString());
            Assert.Equal(args[1], json.GetProperty("input").GetString());
            Assert.Contains("hidden text, comments and deleted tracked changes", json.GetProperty("raw_notice").GetString());
            Assert.True(json.GetProperty("locator").TryGetProperty("kind", out _));
            Assert.True(json.TryGetProperty("format", out _));
        }
    }

    [Fact]
    public async Task Json_text_windows_carry_offset_next_offset_total_and_note()
    {
        var json = await JsonAsync("source", Docx("Alpha"), "--part", "word/document.xml", "--max-chars", "10");

        Assert.Equal(0, json.GetProperty("offset").GetInt32());
        Assert.Equal(10, json.GetProperty("next_offset").GetInt32());
        Assert.True(json.GetProperty("total_chars").GetInt64() > 10);
        Assert.Contains("--offset 10", json.GetProperty("note").GetString());
        Assert.Equal("word/document.xml", json.GetProperty("locator").GetProperty("part").GetString());
    }

    [Fact]
    public async Task Plain_output_names_the_part_and_repeats_the_raw_notice()
    {
        var (exit, output, error) = await RunAsync("source", Docx("Alpha"), "--part", "word/document.xml");

        Assert.True(exit == 0, error);
        Assert.Contains("part: word/document.xml", output);
        Assert.Contains("Alpha", output);
        Assert.Contains("notice: This is the document as stored.", output);
    }

    [Fact]
    public async Task Help_documents_the_source_command()
    {
        var (exit, output, _) = await RunAsync("help", "source");
        var (_, general, _) = await RunAsync("--help");

        Assert.Equal(0, exit);
        Assert.Contains("--part", output);
        Assert.Contains("--max-chars", output);
        Assert.Contains("docredock source <file>", general);
        Assert.Contains("read_source", general);
    }

    /// <summary>The node ids an AI package of the document lists in its <c>source-index.json</c>, in order.</summary>
    private async Task<List<string>> PackageNodeIdsAsync(string document)
    {
        var package = Path.Combine(root, "pkg-" + Guid.NewGuid().ToString("N"));
        var (exit, _, error) = await RunAsync("export", document, "--ai-package", "dir", "--output", package, "--ocr", "off");
        Assert.True(exit is 0 or 1, error);
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package, "source-index.json")));
        return index.RootElement.GetProperty("parts").EnumerateArray()
            .SelectMany(part => part.GetProperty("sources").EnumerateArray())
            .SelectMany(source => source.GetProperty("node_ids").EnumerateArray())
            .Select(id => id.GetString()!).ToList();
    }
}

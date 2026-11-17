using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Cli;

/// <summary>An OCR engine that answers with two known lines, so a test sees what the one-picture OCR of <c>source --image --ocr</c> returns.</summary>
internal sealed class TwoLineOcrEngine : IOcrEngine
{
    public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
        new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

    public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed, new OcrResult("ID-4711\nsecond line",
        [
            new OcrTextRegion("ID-4711", new Geometry("image-pixels", 10, 20, 30, 12), 0.93, 1),
            new OcrTextRegion("second line", new Geometry("image-pixels", 10, 40, 60, 12), null, 2),
        ]), []));
}

/// <summary><c>docredock source --image</c>: a picture of the original, found by the id the Markdown export prints (or by its zip
/// part), with its type, size, pixel size and place, saved to a file and never printed; and its text on request.</summary>
public sealed class CliSourceImageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-source-image-").FullName;
    private readonly List<string> savedFolders = [];

    public void Dispose()
    {
        foreach (var folder in savedFolders) if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Delete(root, true);
    }

    private static string Fixture(params string[] path) => HtmlFixtures.Path_(path);

    private static readonly string Docx = Fixture("Docx", "complex-design-doc.docx");
    private static readonly string Pdf = Fixture("Pdf", "complex-layout.pdf");

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IOcrEngine? engine, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CliApplication(output, error, new DocumentService(engine, null, discoverPdfRasterizer: false)).RunAsync(args);
        return (exit, output.ToString(), error.ToString());
    }

    private static Task<(int Exit, string Output, string Error)> RunAsync(params string[] args) => RunAsync(null, args);

    private static async Task<JsonElement> JsonAsync(IOcrEngine? engine, params string[] args)
    {
        var (exit, output, error) = await RunAsync(engine, [.. args, "--json"]);
        Assert.True(exit == 0, error);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    private static byte[] ZipPart(string package, string part)
    {
        using var archive = ZipFile.OpenRead(package);
        using var stream = archive.GetEntry(part)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task An_id_returns_the_picture_with_its_type_size_dimensions_and_place_and_saves_the_exact_bytes()
    {
        var file = Path.Combine(root, "picture.png");

        var json = await JsonAsync(null, "source", Docx, "--image", "img-0002", "--output", file);

        var image = json.GetProperty("image");
        var original = ZipPart(Docx, "word/media/image2.png");
        Assert.Equal("img-0002", image.GetProperty("id").GetString());
        Assert.Equal("word/media/image2.png", image.GetProperty("part").GetString());
        Assert.Equal("image/png", image.GetProperty("media_type").GetString());
        Assert.Equal(original.Length, image.GetProperty("size").GetInt64());
        Assert.Equal(1200, image.GetProperty("width").GetInt32());
        Assert.Equal(700, image.GetProperty("height").GetInt32());
        Assert.Equal("Picture 2", image.GetProperty("alt_text").GetString());
        Assert.Equal("image", json.GetProperty("locator").GetProperty("kind").GetString());
        Assert.Equal("docredock.source.v1", json.GetProperty("schema").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("raw_notice").GetString()));
        Assert.Equal(file, json.GetProperty("saved_path").GetString());
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        Assert.False(json.TryGetProperty("ocr", out _));
    }

    [Fact]
    public async Task The_other_picture_has_its_own_id_and_bytes()
    {
        var json = await JsonAsync(null, "source", Docx, "--image", "img-0001", "--output", Path.Combine(root, "first.png"));

        Assert.Equal("word/media/image1.png", json.GetProperty("image").GetProperty("part").GetString());
        Assert.Equal(ZipPart(Docx, "word/media/image1.png"), await File.ReadAllBytesAsync(Path.Combine(root, "first.png")));
    }

    [Fact]
    public async Task Without_output_the_picture_goes_to_a_new_folder_under_the_temporary_directory_and_its_bytes_are_never_printed()
    {
        var (exit, output, error) = await RunAsync("source", Docx, "--image", "img-0002", "--json");

        Assert.True(exit == 0, error);
        var saved = JsonDocument.Parse(output).RootElement.GetProperty("saved_path").GetString()!;
        savedFolders.Add(Path.GetDirectoryName(saved)!);
        Assert.Equal("img-0002.png", Path.GetFileName(saved));
        Assert.Equal("docredock-source", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(saved))));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), saved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ZipPart(Docx, "word/media/image2.png"), await File.ReadAllBytesAsync(saved));
        Assert.DoesNotContain("PNG", output, StringComparison.Ordinal);
        Assert.True(output.Length < 2000, "the reply is a description of the picture, not its bytes");
    }

    [Fact]
    public async Task The_text_form_names_the_picture_and_where_it_was_saved()
    {
        var file = Path.Combine(root, "t.png");

        var (exit, output, error) = await RunAsync("source", Docx, "--image", "img-0002", "--output", file);

        Assert.True(exit == 0, error);
        Assert.Contains("image: img-0002, image/png", output, StringComparison.Ordinal);
        Assert.Contains("1200x700 px", output, StringComparison.Ordinal);
        Assert.Contains("saved: " + file, output, StringComparison.Ordinal);
        Assert.DoesNotContain("PNG", output.Replace("image/png", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_name_returns_the_picture_through_image_and_through_part()
    {
        var viaPart = await JsonAsync(null, "source", Docx, "--part", "word/media/image2.png", "--output", Path.Combine(root, "a.png"));
        var viaImage = await JsonAsync(null, "source", Docx, "--image", "/word/media/image2.png", "--output", Path.Combine(root, "b.png"));

        foreach (var json in new[] { viaPart, viaImage })
        {
            Assert.Equal("img-0002", json.GetProperty("image").GetProperty("id").GetString());
            Assert.Equal(1200, json.GetProperty("image").GetProperty("width").GetInt32());
        }
        Assert.Equal("part", viaPart.GetProperty("locator").GetProperty("kind").GetString());
        Assert.Equal(ZipPart(Docx, "word/media/image2.png"), await File.ReadAllBytesAsync(Path.Combine(root, "a.png")));
        Assert.Equal(ZipPart(Docx, "word/media/image2.png"), await File.ReadAllBytesAsync(Path.Combine(root, "b.png")));
    }

    [Fact]
    public async Task An_existing_output_file_is_kept_unless_force_is_given()
    {
        var file = Path.Combine(root, "kept.png");
        await File.WriteAllTextAsync(file, "keep me");

        var (exit, _, error) = await RunAsync("source", Docx, "--image", "img-0001", "--output", file);
        Assert.Equal(2, exit);
        Assert.Contains("--force", error, StringComparison.Ordinal);
        Assert.Equal("keep me", await File.ReadAllTextAsync(file));

        var (forced, _, forcedError) = await RunAsync("source", Docx, "--image", "img-0001", "--output", file, "--force");
        Assert.True(forced == 0, forcedError);
        Assert.Equal(ZipPart(Docx, "word/media/image1.png"), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task An_unknown_id_is_an_input_error_that_lists_the_ids_that_exist()
    {
        var (exit, _, error) = await RunAsync("source", Docx, "--image", "img-0099");

        Assert.Equal(2, exit);
        Assert.Contains("img-0099", error, StringComparison.Ordinal);
        Assert.Contains("img-0001", error, StringComparison.Ordinal);
        Assert.Contains("img-0002", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--offset", "5")]
    [InlineData("--max-chars", "100")]
    public async Task Offset_and_max_chars_do_not_apply_to_a_picture(string option, string value)
    {
        var (exit, _, error) = await RunAsync("source", Docx, "--image", "img-0002", option, value);
        var (partExit, _, partError) = await RunAsync("source", Docx, "--part", "word/media/image2.png", option, value);

        Assert.Equal(2, exit);
        Assert.Contains(option, error, StringComparison.Ordinal);
        Assert.Equal(2, partExit);
        Assert.Contains(option, partError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_cannot_be_combined_with_another_locator_and_ocr_needs_a_picture()
    {
        var (conflict, _, conflictError) = await RunAsync("source", Docx, "--image", "img-0002", "--node", "n_X");
        var (noPicture, _, noPictureError) = await RunAsync("source", Docx, "--part", "word/document.xml", "--ocr");
        var (overview, _, overviewError) = await RunAsync("source", Docx, "--ocr");

        Assert.Equal(2, conflict);
        Assert.Contains("--image", conflictError, StringComparison.Ordinal);
        Assert.Equal(2, noPicture);
        Assert.Contains("--ocr", noPictureError, StringComparison.Ordinal);
        Assert.Equal(2, overview);
        Assert.Contains("--ocr", overviewError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_is_only_for_a_picture()
    {
        var (exit, _, error) = await RunAsync("source", Docx, "--part", "word/document.xml", "--output", Path.Combine(root, "x.xml"));

        Assert.Equal(2, exit);
        Assert.Contains("--output", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "x.xml")));
    }

    [Fact]
    public async Task A_pdf_picture_is_refused_without_allow_pdf_like_every_pdf_read()
    {
        var (exit, _, error) = await RunAsync("source", Pdf, "--image", "pdf-img-0004");

        Assert.Equal(4, exit);
        Assert.Contains("--allow-pdf", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_picture_is_found_by_its_id_with_allow_pdf_and_says_its_page()
    {
        var file = Path.Combine(root, "pdf.png");

        var json = await JsonAsync(null, "source", Pdf, "--allow-pdf", "--image", "pdf-img-0004", "--output", file);

        var image = json.GetProperty("image");
        Assert.Equal("pdf-img-0004", image.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("part").ValueKind);
        Assert.Equal("image/png", image.GetProperty("media_type").GetString());
        Assert.Equal(2, image.GetProperty("page").GetInt32());
        Assert.True(image.GetProperty("width").GetInt32() > 0);
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Equal(image.GetProperty("size").GetInt64(), bytes.Length);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [Fact]
    public async Task A_pdf_has_no_zip_parts_and_an_unknown_pdf_picture_lists_the_known_ids()
    {
        var (exit, _, error) = await RunAsync("source", Pdf, "--allow-pdf", "--image", "word/media/image1.png");

        Assert.Equal(2, exit);
        Assert.Contains("pdf-img-0004", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_overview_of_a_docx_lists_its_pictures_with_their_ids()
    {
        var json = await JsonAsync(null, "source", Docx);

        Assert.Equal(2, json.GetProperty("image_count").GetInt32());
        Assert.False(json.GetProperty("images_truncated").GetBoolean());
        var images = json.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(["img-0001", "img-0002"], images.Select(image => image.GetProperty("id").GetString()));
        Assert.Equal(["word/media/image1.png", "word/media/image2.png"], images.Select(image => image.GetProperty("part").GetString()));
        Assert.Equal(["Picture 1", "Picture 2"], images.Select(image => image.GetProperty("name").GetString()));
        Assert.All(images, image => Assert.Equal("image/png", image.GetProperty("media_type").GetString()));
        Assert.All(images, image => Assert.True(image.GetProperty("size").GetInt64() > 0));
    }

    [Fact]
    public async Task The_overview_of_a_pdf_lists_its_pictures_with_their_pages()
    {
        var json = await JsonAsync(null, "source", Pdf, "--allow-pdf");

        var image = Assert.Single(json.GetProperty("images").EnumerateArray());
        Assert.Equal("pdf-img-0004", image.GetProperty("id").GetString());
        Assert.Equal(2, image.GetProperty("page").GetInt32());
        Assert.Equal("image/png", image.GetProperty("media_type").GetString());
    }

    [Fact]
    public async Task The_overview_of_a_pptx_says_the_slide_of_each_picture()
    {
        var json = await JsonAsync(null, "source", Fixture("Pptx", "complex-design-doc.pptx"));

        var images = json.GetProperty("images").EnumerateArray().ToArray();
        Assert.NotEmpty(images);
        Assert.All(images, image => Assert.True(image.GetProperty("slide").GetInt32() is >= 1 and <= 15));
        var first = images[0].GetProperty("id").GetString()!;

        var one = await JsonAsync(null, "source", Fixture("Pptx", "complex-design-doc.pptx"), "--image", first, "--output", Path.Combine(root, "slide.png"));
        Assert.Equal(images[0].GetProperty("slide").GetInt32(), one.GetProperty("image").GetProperty("slide").GetInt32());
    }

    [Fact]
    public async Task The_overview_of_a_document_without_pictures_lists_none()
    {
        var json = await JsonAsync(null, "source", SourceTestPackages.Pptx(root, "deck.pptx"));

        Assert.Equal(0, json.GetProperty("image_count").GetInt32());
        Assert.Empty(json.GetProperty("images").EnumerateArray());
    }

    [Fact]
    public async Task An_xlsx_picture_says_its_sheet()
    {
        var book = XlsxWithPicture();

        var overview = await JsonAsync(null, "source", book);
        var image = Assert.Single(overview.GetProperty("images").EnumerateArray());
        var one = await JsonAsync(null, "source", book, "--image", image.GetProperty("id").GetString()!, "--output", Path.Combine(root, "sheet.png"));

        Assert.Equal("Data", image.GetProperty("sheet").GetString());
        Assert.Equal("Data", one.GetProperty("image").GetProperty("sheet").GetString());
        Assert.Equal("xl/media/image1.png", one.GetProperty("image").GetProperty("part").GetString());
        Assert.Equal(3, one.GetProperty("image").GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task A_large_overview_lists_at_most_200_pictures_and_says_so()
    {
        var path = SourceTestPackages.Docx(root, "many.docx", "Alpha");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            for (var index = 2; index <= 205; index++)
                using (var media = archive.CreateEntry($"word/media/image{index}.png").Open())
                    media.Write(PngRasterImage.Encode(2, 2, [(byte)index, (byte)(index >> 8), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));

        var json = await JsonAsync(null, "source", path);

        Assert.Equal(205, json.GetProperty("image_count").GetInt32());
        Assert.True(json.GetProperty("images_truncated").GetBoolean());
        Assert.Equal(200, json.GetProperty("images").GetArrayLength());
        Assert.Contains("first 200 of 205", json.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ocr_returns_the_lines_the_engine_read_with_their_positions_for_a_docx_picture()
    {
        var json = await JsonAsync(new TwoLineOcrEngine(), "source", Docx, "--image", "img-0002", "--ocr", "--output", Path.Combine(root, "o.png"));

        var ocr = json.GetProperty("ocr");
        Assert.True(ocr.GetProperty("available").GetBoolean());
        Assert.Equal("test.ocr", ocr.GetProperty("engine").GetString());
        var lines = ocr.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(["ID-4711", "second line"], lines.Select(line => line.GetProperty("text").GetString()));
        Assert.Equal((10, 20, 30, 12), (lines[0].GetProperty("x").GetInt32(), lines[0].GetProperty("y").GetInt32(), lines[0].GetProperty("w").GetInt32(), lines[0].GetProperty("h").GetInt32()));
        Assert.Equal(0.93, lines[0].GetProperty("confidence").GetDouble());
        Assert.False(lines[1].TryGetProperty("confidence", out _));
    }

    [Fact]
    public async Task Ocr_works_for_a_pdf_picture_which_the_export_leaves_unread()
    {
        var json = await JsonAsync(new TwoLineOcrEngine(), "source", Pdf, "--allow-pdf", "--image", "pdf-img-0004", "--ocr", "--output", Path.Combine(root, "p.png"));

        var lines = json.GetProperty("ocr").GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(["ID-4711", "second line"], lines.Select(line => line.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Ocr_works_through_a_picture_part_too()
    {
        var json = await JsonAsync(new TwoLineOcrEngine(), "source", Docx, "--part", "word/media/image2.png", "--ocr", "--output", Path.Combine(root, "q.png"));

        Assert.Equal(2, json.GetProperty("ocr").GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Without_an_engine_the_picture_is_still_returned_and_ocr_says_it_is_not_available()
    {
        var json = await JsonAsync(null, "source", Docx, "--image", "img-0002", "--ocr", "--output", Path.Combine(root, "n.png"));

        Assert.Equal("img-0002", json.GetProperty("image").GetProperty("id").GetString());
        var ocr = json.GetProperty("ocr");
        Assert.False(ocr.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(ocr.GetProperty("reason").GetString()));
        Assert.False(ocr.TryGetProperty("lines", out _));
    }

    [Fact]
    public async Task The_ocr_flag_can_be_given_before_other_options()
    {
        var (exit, output, error) = await RunAsync(new TwoLineOcrEngine(), "source", Docx, "--image", "img-0002", "--ocr", "--json", "--output", Path.Combine(root, "z.png"));

        Assert.True(exit == 0, error);
        Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("ocr").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Export_still_takes_a_value_for_ocr()
    {
        var (bare, _, bareError) = await RunAsync("export", Docx, "--ocr", "-o", Path.Combine(root, "bare.md"));
        var (valued, _, valuedError) = await RunAsync("export", Docx, "--ocr", "off", "-o", Path.Combine(root, "off.md"));

        Assert.Equal(2, bare);
        Assert.Contains("--ocr must be auto, on, or off", bareError, StringComparison.Ordinal);
        Assert.True(valued is 0 or 1, valuedError);
    }

    private string XlsxWithPicture()
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var path = SourceTestPackages.Xlsx(root, "pic.xlsx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry("xl/worksheets/sheet1.xml")!.Delete();
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Item</t></is></c></row></sheetData><drawing r:id=\"rDrawing\"/></worksheet>");
        Add("xl/worksheets/_rels/sheet1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
        Add("xl/drawings/drawing1.xml", "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:row>1</xdr:row></xdr:from><xdr:ext cx=\"1000\" cy=\"1000\"/>" +
            "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"1\" name=\"Logo\"/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"rImage\"/></xdr:blipFill><xdr:spPr/></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>");
        Add("xl/drawings/_rels/drawing1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image1.png\"/></Relationships>");
        using (var media = archive.CreateEntry("xl/media/image1.png").Open()) media.Write(PngRasterImage.Encode(3, 2, new byte[3 * 2 * 3]));
        return path;
    }
}

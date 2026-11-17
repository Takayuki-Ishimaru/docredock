using System.IO.Compression;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Tests.Api;

namespace DocRedock.Tests.Cli;

/// <summary>The <c>image</c> and <c>ocr</c> arguments of the MCP tool read_source: the picture comes back as an MCP image content
/// block beside the JSON that describes it, or, when a client cannot be handed it, only as a saved file the JSON names.</summary>
public sealed class McpSourceImageTests : IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly string root = Directory.CreateTempSubdirectory("docredock-mcp-image-").FullName;

    public void Dispose()
    {
        Directory.Delete(root, true);
    }

    private static readonly string Docx = HtmlFixtures.Path_("Docx", "complex-design-doc.docx");

    private static async Task<JsonElement> CallAsync(object arguments, IOcrEngine? engine = null, long? maxInline = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object> { ["name"] = "read_source", ["arguments"] = arguments },
        });
        var output = new StringWriter();
        var service = new DocumentService(engine, null, discoverPdfRasterizer: false);
        var server = maxInline is { } cap
            ? new McpServer(new StringReader(line + "\n"), output, new StringWriter(), service, cap)
            : new McpServer(new StringReader(line + "\n"), output, new StringWriter(), service);
        await server.RunAsync();
        var response = JsonDocument.Parse(output.ToString().Trim()).RootElement.Clone();
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        return response.GetProperty("result");
    }

    private static JsonElement[] Blocks(JsonElement result) => result.GetProperty("content").EnumerateArray().Select(item => item.Clone()).ToArray();

    private static JsonElement Described(JsonElement result)
    {
        var first = Blocks(result)[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        return JsonDocument.Parse(first.GetProperty("text").GetString()!).RootElement.Clone();
    }

    [Fact]
    public async Task Tools_list_shows_the_image_and_ocr_arguments()
    {
        var output = new StringWriter();
        var line = JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" });
        await new McpServer(new StringReader(line + "\n"), output, new StringWriter()).RunAsync();

        var tool = JsonDocument.Parse(output.ToString().Trim()).RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "read_source");

        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("image").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("ocr").GetProperty("type").GetString());
        Assert.Contains("img-0002", properties.GetProperty("image").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("image content block", tool.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_instructions_tell_the_reader_to_ask_for_a_picture_by_its_id()
    {
        var output = new StringWriter();
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new { protocolVersion = "2025-06-18", capabilities = new { } },
        });
        await new McpServer(new StringReader(line + "\n"), output, new StringWriter()).RunAsync();

        var instructions = JsonDocument.Parse(output.ToString().Trim()).RootElement.GetProperty("result").GetProperty("instructions").GetString()!;

        Assert.Contains("read_source with image", instructions, StringComparison.Ordinal);
        Assert.Contains("ocr: true", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_comes_back_as_json_text_and_a_png_image_block()
    {
        var result = await CallAsync(new { path = Docx, image = "img-0002" });

        var blocks = Blocks(result);
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(2, blocks.Length);
        var json = Described(result);
        Assert.Equal("img-0002", json.GetProperty("image").GetProperty("id").GetString());
        Assert.True(json.GetProperty("image_returned").GetBoolean());
        Assert.True(File.Exists(json.GetProperty("saved_path").GetString()));
        Assert.Equal("image", blocks[1].GetProperty("type").GetString());
        Assert.Equal("image/png", blocks[1].GetProperty("mimeType").GetString());
        var data = Convert.FromBase64String(blocks[1].GetProperty("data").GetString()!);
        Assert.Equal(PngSignature, data[..8]);
        Assert.Equal(json.GetProperty("image").GetProperty("size").GetInt64(), data.Length);
        Directory.Delete(Path.GetDirectoryName(json.GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public async Task A_picture_part_is_returned_the_same_way()
    {
        var result = await CallAsync(new { path = Docx, part = "word/media/image1.png" });

        Assert.Equal(["text", "image"], Blocks(result).Select(block => block.GetProperty("type").GetString()));
        Directory.Delete(Path.GetDirectoryName(Described(result).GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public async Task A_picture_type_a_client_cannot_show_is_only_saved_and_the_reply_says_why()
    {
        var package = SourceTestPackages.Docx(root, "emf.docx", "Alpha");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        using (var media = archive.CreateEntry("word/media/image2.emf").Open())
            media.Write([0x01, 0x00, 0x00, 0x00, 0x6C, 0x00, 0x00, 0x00]);

        var result = await CallAsync(new { path = package, image = "word/media/image2.emf" });

        var blocks = Blocks(result);
        var json = Described(result);
        Assert.Single(blocks);
        Assert.False(json.GetProperty("image_returned").GetBoolean());
        Assert.Equal("unsupported_media_type", json.GetProperty("reason").GetString());
        Assert.Contains("image/emf", json.GetProperty("image_note").GetString(), StringComparison.Ordinal);
        var saved = json.GetProperty("saved_path").GetString()!;
        Assert.EndsWith(".emf", saved, StringComparison.Ordinal);
        Assert.Equal(8, new FileInfo(saved).Length);
        Directory.Delete(Path.GetDirectoryName(saved)!, true);
    }

    [Fact]
    public async Task A_picture_over_the_cap_is_only_saved()
    {
        var result = await CallAsync(new { path = Docx, image = "img-0002" }, maxInline: 1000);

        var json = Described(result);
        Assert.Single(Blocks(result));
        Assert.False(json.GetProperty("image_returned").GetBoolean());
        Assert.Equal("too_large", json.GetProperty("reason").GetString());
        Assert.True(File.Exists(json.GetProperty("saved_path").GetString()));
        Directory.Delete(Path.GetDirectoryName(json.GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public void The_default_cap_is_five_megabytes()
    {
        Assert.Equal(5 * 1024 * 1024, McpServer.MaxInlineImageBytes);
    }

    [Fact]
    public async Task Ocr_true_adds_the_lines_the_engine_read_for_a_docx_picture()
    {
        var result = await CallAsync(new { path = Docx, image = "img-0002", ocr = true }, new TwoLineOcrEngine());

        var json = Described(result);
        var ocr = json.GetProperty("ocr");
        Assert.True(ocr.GetProperty("available").GetBoolean());
        Assert.Equal(["ID-4711", "second line"], ocr.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("text").GetString()));
        Assert.Equal(["text", "image"], Blocks(result).Select(block => block.GetProperty("type").GetString()));
        Directory.Delete(Path.GetDirectoryName(json.GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public async Task Ocr_true_works_for_a_pdf_picture()
    {
        var result = await CallAsync(new { path = HtmlFixtures.Path_("Pdf", "complex-layout.pdf"), allow_pdf = true, image = "pdf-img-0004", ocr = true }, new TwoLineOcrEngine());

        var json = Described(result);
        Assert.Equal(2, json.GetProperty("ocr").GetProperty("lines").GetArrayLength());
        Assert.Equal(2, json.GetProperty("image").GetProperty("page").GetInt32());
        Directory.Delete(Path.GetDirectoryName(json.GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public async Task Without_an_engine_ocr_reports_that_it_is_not_available_and_the_picture_is_still_returned()
    {
        var result = await CallAsync(new { path = Docx, image = "img-0002", ocr = true });

        var json = Described(result);
        Assert.False(json.GetProperty("ocr").GetProperty("available").GetBoolean());
        Assert.Equal(["text", "image"], Blocks(result).Select(block => block.GetProperty("type").GetString()));
        Directory.Delete(Path.GetDirectoryName(json.GetProperty("saved_path").GetString()!)!, true);
    }

    [Fact]
    public async Task An_unknown_picture_is_a_tool_error_that_lists_the_ids()
    {
        var result = await CallAsync(new { path = Docx, image = "img-0042" });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.DoesNotContain(Blocks(result), block => block.GetProperty("type").GetString() == "image");
        var text = string.Join("\n", Blocks(result).Select(block => block.GetProperty("text").GetString()));
        Assert.Contains("img-0001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_overview_lists_the_pictures_without_an_image_block()
    {
        var result = await CallAsync(new { path = Docx });

        Assert.Single(Blocks(result));
        Assert.Equal(2, Described(result).GetProperty("image_count").GetInt32());
    }
}

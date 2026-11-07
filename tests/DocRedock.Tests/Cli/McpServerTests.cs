using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

/// <summary>The MCP server (<c>docredock mcp</c>): JSON-RPC 2.0 over standard input and output, one message a line, whose
/// tools run the same conversions and package commands as the CLI. The 2026-11-07 evaluation found no server to connect
/// an MCP client to and noted that a layer calling the CLI was needed.</summary>
public sealed class McpServerTests : IDisposable
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly string root = Directory.CreateTempSubdirectory("docredock-mcp-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private static readonly string[] ToolNames = ["export_markdown", "export_ai_package", "package_review", "package_search", "package_read"];

    /// <summary>Runs the server over the given lines and returns every line it wrote, each parsed.</summary>
    private static async Task<IReadOnlyList<JsonElement>> RunAsync(params string[] lines)
    {
        var output = new StringWriter();
        var server = new McpServer(new StringReader(string.Join("\n", lines) + "\n"), output, new StringWriter(),
            new DocumentService(null, null, discoverPdfRasterizer: false));
        await server.RunAsync();
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

    private static string Request(object id, string method, object? parameters = null) =>
        JsonSerializer.Serialize(parameters is null
            ? new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method }
            : new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

    private static string Call(object id, string tool, object arguments) =>
        Request(id, "tools/call", new Dictionary<string, object> { ["name"] = tool, ["arguments"] = arguments });

    private static async Task<JsonElement> CallAsync(string tool, object arguments)
    {
        var responses = await RunAsync(Call(1, tool, arguments));
        var response = Assert.Single(responses);
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        return response.GetProperty("result");
    }

    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var value) && value.GetBoolean();

    private static string[] Texts(JsonElement result) =>
        result.GetProperty("content").EnumerateArray().Select(item =>
        {
            Assert.Equal("text", item.GetProperty("type").GetString());
            return item.GetProperty("text").GetString()!;
        }).ToArray();

    [Fact]
    public async Task Initialize_answers_a_supported_protocol_version_and_names_the_server()
    {
        var response = Assert.Single(await RunAsync(Request(1, "initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1" },
        })));

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        var result = response.GetProperty("result");
        Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("docredock", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("serverInfo").GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
    }

    [Fact]
    public async Task Initialize_with_an_unknown_version_answers_the_latest_one_the_server_speaks()
    {
        var response = Assert.Single(await RunAsync(Request(1, "initialize", new { protocolVersion = "1999-01-01", capabilities = new { } })));

        Assert.Equal(McpServer.LatestProtocolVersion, response.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("2025-06-18", McpServer.LatestProtocolVersion);
    }

    [Fact]
    public async Task Notifications_get_no_response_even_when_unknown()
    {
        var responses = await RunAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""",
            """{"jsonrpc":"2.0","method":"no/such/notification"}""",
            "",
            Request("after", "ping"));

        var response = Assert.Single(responses);
        Assert.Equal("after", response.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Tools_list_names_the_five_tools_with_closed_object_schemas()
    {
        var response = Assert.Single(await RunAsync(Request(2, "tools/list")));
        var tools = response.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();

        Assert.Equal(ToolNames.Order(), tools.Select(tool => tool.GetProperty("name").GetString()!).Order());
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
            foreach (var name in required) Assert.True(schema.GetProperty("properties").TryGetProperty(name!, out _), name);
        }
        string[] Required(string name) => tools.Single(tool => tool.GetProperty("name").GetString() == name)
            .GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
        Assert.Equal(["path"], Required("export_markdown"));
        Assert.Equal(["path"], Required("export_ai_package"));
        Assert.Equal(["package"], Required("package_review"));
        Assert.Equal(["package", "query"], Required("package_search"));
        Assert.Equal(["package", "part"], Required("package_read"));
    }

    [Fact]
    public async Task Protocol_errors_carry_their_json_rpc_codes_and_the_server_goes_on()
    {
        var responses = await RunAsync(
            "{not json",
            """{"jsonrpc":"1.0","id":3,"method":"ping"}""",
            Request("m", "no/such/method"),
            Call(4, "no_such_tool", new { }),
            """[{"jsonrpc":"2.0","id":5,"method":"ping"}]""",
            Request(6, "ping"));

        Assert.Equal(6, responses.Count);
        Assert.Equal(-32700, responses[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, responses[0].GetProperty("id").ValueKind);
        Assert.Equal(-32600, responses[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(3, responses[1].GetProperty("id").GetInt32());
        Assert.Equal(-32601, responses[2].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("m", responses[2].GetProperty("id").GetString());
        Assert.Equal(-32602, responses[3].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(4, responses[3].GetProperty("id").GetInt32());
        Assert.Equal(-32600, responses[4].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(6, responses[5].GetProperty("id").GetInt32());
        Assert.True(responses[5].TryGetProperty("result", out _));
        Assert.All(responses, response => Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString()));
    }

    [Theory]
    [InlineData("""{}""", "path")]
    [InlineData("""{"path":5}""", "path")]
    [InlineData("""{"path":"a.docx","colour":"red"}""", "colour")]
    [InlineData("""{"path":"a.docx","content_policy":"everything"}""", "content_policy")]
    [InlineData("""{"path":"a.docx","max_chars":0}""", "max_chars")]
    [InlineData("""{"path":"a.docx","allow_pdf":"yes"}""", "allow_pdf")]
    public async Task Invalid_arguments_are_a_tool_error_that_names_the_argument(string arguments, string name)
    {
        var line = $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"export_markdown","arguments":{{{arguments}}}}}""";
        var response = Assert.Single(await RunAsync(line));
        var result = response.GetProperty("result");

        Assert.True(IsError(result));
        Assert.Contains(name, string.Join("\n", Texts(result)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_markdown_returns_the_json_result_and_then_the_markdown()
    {
        var document = Document("plan.docx", Paragraph("設計方針の概要") + Paragraph("Second paragraph"));
        var outputDir = Path.Combine(root, "md-out");

        var result = await CallAsync("export_markdown", new { path = document, output_dir = outputDir });

        Assert.False(IsError(result));
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        using var envelope = JsonDocument.Parse(texts[0]);
        Assert.Equal("docredock.export.v2", envelope.RootElement.GetProperty("schema").GetString());
        var item = Assert.Single(envelope.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(0, item.GetProperty("exit_code").GetInt32());
        Assert.False(item.GetProperty("needs_attention").GetBoolean());
        Assert.Contains("設計方針の概要", texts[1], StringComparison.Ordinal);
        Assert.Contains("Second paragraph", texts[1], StringComparison.Ordinal);
        var written = Path.Combine(outputDir, "plan.md");
        Assert.True(File.Exists(written));
        Assert.Equal(await File.ReadAllTextAsync(written), texts[1]);
    }

    [Fact]
    public async Task Export_markdown_passes_the_content_options_to_the_conversion()
    {
        var document = Document("revised.docx", "<w:p><w:r><w:t>Kept </w:t></w:r><w:del><w:r><w:delText>Removed</w:delText></w:r></w:del></w:p>");

        var markup = Texts(await CallAsync("export_markdown", new { path = document, output_dir = Path.Combine(root, "markup") }));
        var final = Texts(await CallAsync("export_markdown", new { path = document, output_dir = Path.Combine(root, "final"), revisions = "final" }));

        Assert.Contains("Removed", markup[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Removed", final[1], StringComparison.Ordinal);
        Assert.Contains("Kept", final[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_markdown_is_cut_at_max_chars_with_a_note_that_names_the_whole_file()
    {
        var body = string.Concat(Enumerable.Range(1, 300).Select(index => Paragraph($"Paragraph number {index} of a long document.")));
        var document = Document("long.docx", body);
        var outputDir = Path.Combine(root, "long-out");

        var texts = Texts(await CallAsync("export_markdown", new { path = document, output_dir = outputDir, max_chars = 1000 }));

        var full = await File.ReadAllTextAsync(Path.Combine(outputDir, "long.md"));
        Assert.True(full.Length > 1000);
        Assert.StartsWith(full[..1000], texts[1], StringComparison.Ordinal);
        var note = texts[1][1000..];
        Assert.Contains("truncated", note, StringComparison.Ordinal);
        Assert.Contains(full.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), note, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(outputDir, "long.md"), note, StringComparison.Ordinal);
        Assert.True(note.Length < 600);
    }

    [Fact]
    public async Task A_missing_document_is_a_tool_error_with_the_failed_json_result()
    {
        var result = await CallAsync("export_markdown", new { path = Path.Combine(root, "missing.docx"), output_dir = Path.Combine(root, "x") });

        Assert.True(IsError(result));
        using var envelope = JsonDocument.Parse(Texts(result)[0]);
        var item = Assert.Single(envelope.RootElement.GetProperty("results").EnumerateArray());
        Assert.NotEqual(0, item.GetProperty("exit_code").GetInt32());
        Assert.Equal(["failed"], item.GetProperty("attention").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task An_ai_package_can_be_searched_and_read_through_the_package_tools()
    {
        var document = Document("guide.docx",
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Installation</w:t></w:r></w:p>" +
            Paragraph("Run the installer and choose the zircon option."));
        var outputDir = Path.Combine(root, "pkg-out");

        var export = await CallAsync("export_ai_package", new { path = document, output_dir = outputDir });

        Assert.False(IsError(export));
        var texts = Texts(export);
        using (var envelope = JsonDocument.Parse(texts[0]))
            Assert.Equal("ai-package", envelope.RootElement.GetProperty("results")[0].GetProperty("mode").GetString());
        var packageLine = Assert.Single(texts, text => text.StartsWith("package_path: ", StringComparison.Ordinal));
        var package = packageLine["package_path: ".Length..].Trim();
        Assert.Equal(Path.Combine(outputDir, "guide-package"), package);
        Assert.True(File.Exists(Path.Combine(package, "manifest.json")));

        var search = await CallAsync("package_search", new { package, query = "zircon" });
        Assert.False(IsError(search));
        Assert.Contains("part-0001", Texts(search)[0], StringComparison.Ordinal);

        var read = await CallAsync("package_read", new { package, part = "part-0001" });
        Assert.False(IsError(read));
        Assert.Contains("zircon", string.Join("\n", Texts(read)), StringComparison.Ordinal);

        var review = await CallAsync("package_review", new { package });
        Assert.False(IsError(review));
        using var reviewJson = JsonDocument.Parse(Texts(review)[0]);
        Assert.False(reviewJson.RootElement.GetProperty("review_required").GetBoolean());
    }

    [Fact]
    public async Task A_package_that_needs_review_withholds_its_body_until_allow_review()
    {
        var document = Document("tracked.docx",
            "<w:p><w:r><w:t>Body text with marmalade </w:t></w:r><w:ins><w:r><w:t>inserted</w:t></w:r></w:ins></w:p>");
        var export = await CallAsync("export_ai_package", new { path = document, output_dir = Path.Combine(root, "tracked-out") });
        var package = Texts(export).Single(text => text.StartsWith("package_path: ", StringComparison.Ordinal))["package_path: ".Length..].Trim();

        var review = await CallAsync("package_review", new { package });
        using (var reviewJson = JsonDocument.Parse(Texts(review)[0]))
            Assert.True(reviewJson.RootElement.GetProperty("review_required").GetBoolean());

        var unsearched = string.Join("\n", Texts(await CallAsync("package_search", new { package, query = "marmalade" })));
        Assert.Contains("Review required", unsearched, StringComparison.Ordinal);
        Assert.Contains("part-0001", string.Join("\n", Texts(await CallAsync("package_search", new { package, query = "marmalade", allow_review = true }))), StringComparison.Ordinal);

        var withheld = await CallAsync("package_read", new { package, part = "part-0001" });
        Assert.False(IsError(withheld));
        var withheldText = string.Join("\n", Texts(withheld));
        Assert.Contains("Review required", withheldText, StringComparison.Ordinal);
        Assert.DoesNotContain("marmalade", withheldText, StringComparison.Ordinal);

        var allowed = await CallAsync("package_read", new { package, part = "part-0001", allow_review = true });
        Assert.False(IsError(allowed));
        Assert.Contains("marmalade", string.Join("\n", Texts(allowed)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_tool_on_a_missing_package_is_a_tool_error()
    {
        var result = await CallAsync("package_search", new { package = Path.Combine(root, "nothing-here"), query = "x" });

        Assert.True(IsError(result));
        Assert.NotEmpty(string.Join("", Texts(result)));
    }

    [Fact]
    public async Task Requests_are_answered_in_order_one_json_object_a_line()
    {
        var document = Document("order.docx", Paragraph("Order"));
        var output = new StringWriter();
        var server = new McpServer(new StringReader(string.Join("\n",
                Request(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { } }),
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                Request(2, "tools/list"),
                Call(3, "export_markdown", new { path = document, output_dir = Path.Combine(root, "order-out") }),
                Request(4, "ping")) + "\n"),
            output, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));

        await server.RunAsync();

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([1, 2, 3, 4], lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt32()));
        Assert.All(lines, line => Assert.DoesNotContain('\r', line));
    }

    [Fact]
    public async Task The_cli_documents_the_mcp_command()
    {
        var output = new StringWriter();
        var exit = await new CliApplication(output, new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false))
            .RunAsync(["help", "mcp"]);

        Assert.Equal(0, exit);
        Assert.Contains("mcp", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("export_markdown", output.ToString(), StringComparison.Ordinal);
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private string Document(string name, string body)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>");
        return path;
    }
}

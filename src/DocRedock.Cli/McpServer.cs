using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DocRedock.Api;

namespace DocRedock.Cli;

/// <summary>The MCP server of <c>docredock mcp</c>: JSON-RPC 2.0 over a pair of text streams (the MCP stdio transport,
/// one JSON message a line) whose five tools run the CLI's own export and package commands in this process. Only
/// protocol messages are written to the output; everything else goes to the log.</summary>
public sealed class McpServer(TextReader input, TextWriter output, TextWriter log, DocumentService? documentService = null)
{
    public const string LatestProtocolVersion = "2025-06-18";
    private static readonly string[] ProtocolVersions = [LatestProtocolVersion, "2025-03-26", "2024-11-05"];
    // The same encoder as the CLI's JSON: Japanese stays readable instead of becoming \uXXXX escapes.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private const int DefaultMaxCharacters = 40_000;

    private const string Instructions =
        "Use export_markdown to read a document (Word, Excel, PowerPoint, or PDF with allow_pdf) that fits in the conversation. " +
        "For a large document use export_ai_package, then package_review, package_search and package_read to read it part by part. " +
        "package_search and package_read withhold the text of a package that needs review until a person has checked package_review and allow_review is passed.";

    // JSON-RPC 2.0 error codes.
    private const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602, InternalError = -32603;

    /// <summary>One argument of a tool: its JSON schema type, the values it allows and its smallest value.</summary>
    private sealed record Parameter(string Name, string Type, string Description, string[]? Values = null, int? Minimum = null);

    private sealed record Tool(string Name, string Description, string[] Required, Parameter[] Parameters);

    private static readonly Parameter PathParameter = new("path", "string",
        "Path of the document (.docx, .xlsx, .pptx, or .pdf together with allow_pdf): absolute, or relative to the server's working directory.");
    private static readonly Parameter OutputDirParameter = new("output_dir", "string",
        "Folder for the output; created if missing. Default: a new folder under the system temporary directory (docredock-mcp/<id>).");
    private static readonly Parameter OverwriteParameter = new("overwrite", "boolean", "Replace output that already exists (never the input document). Default: false.");
    private static readonly Parameter PackageParameter = new("package", "string",
        "Path of an AI package made by export_ai_package: its folder or a .zip file. Absolute, or relative to the server's working directory.");
    private static readonly Parameter AllowReviewParameter = new("allow_review", "boolean",
        "Read the body of a package that needs review. Pass true only after a person has checked package_review and confirmed that the conversion and the sharing are acceptable; never to get past the withheld body on your own.");

    // The options that choose what an export contains; both export tools take them.
    private static readonly Parameter[] ExportOptions =
    [
        PathParameter, OutputDirParameter,
        new("content_policy", "string", "What the export contains: visible (default) is what the document shows; complete adds hidden text, notes and comments; sanitized is the body only, without headers, footers, comments or metadata.",
            ["visible", "complete", "sanitized"]),
        new("revisions", "string", "Word tracked changes: final is the text after the changes; markup shows the changes in the text (default for visible and complete); reject is the text as it was before the changes.",
            ["final", "markup", "reject"]),
        new("sheets", "string", "Excel only: comma-separated names of the worksheets to convert, such as \"Summary,Q3\". Default: all worksheets."),
        new("allow_pdf", "boolean", "Allow a PDF file to be converted (off by default). Default: false."),
        new("ocr", "string", "Reading text in images: auto (default) reads it when an OCR engine is available, on requires it, off skips it.", ["auto", "on", "off"]),
        new("lang", "string", "Language of the generated text and of the messages. Default: from the document.", ["ja", "en"]),
    ];

    private static readonly Tool[] Tools =
    [
        new("export_markdown",
            "Converts one document (Word, Excel, PowerPoint, or PDF with allow_pdf) to Markdown and returns it. The reply holds the conversion's JSON result (exit_code, warnings, review items, and needs_attention with the reasons a person should compare the output with the source), then the Markdown text, cut at max_chars with a note naming the file that holds all of it. The Markdown is also written to output_dir. Use it for a document that fits in the conversation; for a large one use export_ai_package.",
            ["path"], [.. ExportOptions,
                new("max_chars", "integer", "Most characters of Markdown to return in the reply. Default: 40000. The file in output_dir always holds all of it.", Minimum: 1),
                OverwriteParameter]),
        new("export_ai_package",
            "Converts one document to an AI package: a folder of parts of about chunk_chars characters each, with the source location of every part, plus review.md and manifest.json. The reply holds the conversion's JSON result and then \"package_path: <folder>\". Use it for a large document, then read the package with package_review, package_search and package_read instead of loading everything.",
            ["path"], [.. ExportOptions,
                new("chunk_chars", "integer", "Target size of a part in characters. Default: 12000; the conversion accepts 128 to 1000000.", Minimum: 1),
                new("table_row_blocks", "boolean", "Split large tables into blocks of rows that repeat the header row. Default: false."),
                OverwriteParameter]),
        new("package_review",
            "Verifies an AI package and returns JSON: review_required, why (stop_reasons: conversion review, revision sharing, hidden content), affected_parts and review_markdown. Call it before reading a package. When review_required is true, package_search and package_read withhold the text until a person has checked this report and allow_review is passed.",
            ["package"], [PackageParameter]),
        new("package_search",
            "Searches the text of an AI package and returns JSON with the matching parts (part ids such as part-0001), where they come from in the source and the matching lines. Use the part ids with package_read. A package that needs review answers with a review-required message until allow_review is passed; run package_review first.",
            ["package", "query"], [PackageParameter, new("query", "string", "The text to search for (plain text, not a pattern)."), AllowReviewParameter]),
        new("package_read",
            "Reads one part of an AI package and returns JSON with part_id, markdown, review_required and estimated_tokens (and sources and locations with with_sources). Part ids come from package_search or from the package's manifest.json. A package that needs review answers with a review-required message instead of the body until allow_review is passed.",
            ["package", "part"], [PackageParameter, new("part", "string", "Id of the part, such as part-0001."),
                new("with_sources", "boolean", "Also return the source location of each piece of the part. Default: false."), AllowReviewParameter]),
    ];

    /// <summary>Reads one message a line until the input ends and answers each request in turn, so replies come in the
    /// order of the requests. A notification (a message without an id) is never answered. Cancelling the token ends the
    /// server.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await log.WriteLineAsync($"docredock mcp {CliApplication.Version}: reading JSON-RPC messages from standard input.");
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (await HandleAsync(line, cancellationToken) is not { } reply) continue;
            // One write for the whole line, so a reader never sees half a message; always "\n", also on Windows.
            await output.WriteAsync(reply + "\n");
            await output.FlushAsync(cancellationToken);
        }
    }

    /// <summary>The serialized reply to one line, or null when it needs none.</summary>
    private async Task<string?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException ex)
        {
            await log.WriteLineAsync("docredock mcp: unparsable message: " + ex.Message);
            return Error(null, ParseError, "Parse error: " + ex.Message).ToJsonString(Json);
        }
        using (document)
        {
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object)
                return Error(null, InvalidRequest, "Invalid Request: send one JSON object a line (batches are not supported).").ToJsonString(Json);
            var hasId = message.TryGetProperty("id", out var idValue);
            JsonElement? id = hasId && idValue.ValueKind is JsonValueKind.String or JsonValueKind.Number ? idValue : null;
            if (!(message.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0"))
                return Error(id, InvalidRequest, "Invalid Request: \"jsonrpc\" must be \"2.0\".").ToJsonString(Json);
            if (hasId && id is null)
                return Error(null, InvalidRequest, "Invalid Request: \"id\" must be a string or a number.").ToJsonString(Json);
            if (!message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            {
                // A response of the client to a request: this server sends none, so there is nothing to answer.
                if (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)) return null;
                return Error(id, InvalidRequest, "Invalid Request: \"method\" must be a string.").ToJsonString(Json);
            }
            // A notification, known or not (notifications/initialized, notifications/cancelled, ...).
            if (id is null) return null;
            try
            {
                message.TryGetProperty("params", out var parameters);
                return (await DispatchAsync(method.GetString()!, id, parameters, cancellationToken)).ToJsonString(Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await log.WriteLineAsync("docredock mcp: internal error: " + ex);
                return Error(id, InternalError, "Internal error: " + ex.Message).ToJsonString(Json);
            }
        }
    }

    private async Task<JsonObject> DispatchAsync(string method, JsonElement? id, JsonElement parameters, CancellationToken cancellationToken) => method switch
    {
        "initialize" => Result(id, Initialize(parameters)),
        "ping" => Result(id, new JsonObject()),
        "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(ToolDefinition)]) }),
        "tools/call" => await CallToolAsync(id, parameters, cancellationToken),
        _ => Error(id, MethodNotFound, "Method not found: " + method),
    };

    private static JsonObject Initialize(JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        return new JsonObject
        {
            // The client's version when this server speaks it, otherwise the latest one, which the client may refuse.
            ["protocolVersion"] = requested is not null && ProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "docredock", ["version"] = CliApplication.Version },
            ["instructions"] = Instructions,
        };
    }

    private static JsonNode ToolDefinition(Tool tool)
    {
        var properties = new JsonObject();
        foreach (var parameter in tool.Parameters)
        {
            var property = new JsonObject { ["type"] = parameter.Type };
            if (parameter.Values is { } values) property["enum"] = JsonSerializer.SerializeToNode(values);
            if (parameter.Minimum is { } minimum) property["minimum"] = minimum;
            property["description"] = parameter.Description;
            properties[parameter.Name] = property;
        }
        return new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = JsonSerializer.SerializeToNode(tool.Required),
                ["additionalProperties"] = false,
            },
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement? id, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            return Error(id, InvalidParams, "Invalid params: tools/call needs the name of a tool.");
        var tool = Tools.FirstOrDefault(candidate => candidate.Name == name.GetString());
        if (tool is null) return Error(id, InvalidParams, "Unknown tool: " + name.GetString());
        // Arguments the tool cannot use are an error of the tool call, not of the protocol, so the client can correct them.
        var values = parameters.TryGetProperty("arguments", out var given) && given.ValueKind != JsonValueKind.Null ? given : default;
        var problems = Validate(tool, values);
        if (problems.Count > 0) return Result(id, ToolResult(true, [$"Invalid arguments for {tool.Name}: " + string.Join("; ", problems)]));
        var arguments = new ToolArguments(values);
        try
        {
            return Result(id, tool.Name switch
            {
                "export_markdown" => await ExportMarkdownAsync(arguments, cancellationToken),
                "export_ai_package" => await ExportAiPackageAsync(arguments, cancellationToken),
                "package_review" => ToolResult(await RunCliAsync(["package", "review", FullPath(arguments, "package"), "--json"], cancellationToken), []),
                "package_search" => ToolResult(await RunCliAsync(PackageSearch(arguments), cancellationToken), []),
                _ => ToolResult(await RunCliAsync(PackageRead(arguments), cancellationToken), []),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log.WriteLineAsync($"docredock mcp: {tool.Name} failed: {ex}");
            return Result(id, ToolResult(true, [ex.Message]));
        }
    }

    /// <summary>What is wrong with the arguments, each problem naming its argument; empty when the call can run.</summary>
    private static List<string> Validate(Tool tool, JsonElement arguments)
    {
        var problems = new List<string>();
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) return ["'arguments' must be an object."];
        var present = arguments.ValueKind == JsonValueKind.Object ? arguments.EnumerateObject().ToArray() : [];
        problems.AddRange(tool.Required.Where(name => present.All(property => property.Name != name)).Select(name => $"'{name}' is required."));
        foreach (var property in present)
        {
            var parameter = tool.Parameters.FirstOrDefault(candidate => candidate.Name == property.Name);
            if (parameter is null) problems.Add($"'{property.Name}' is not an argument of {tool.Name}.");
            else if (Check(parameter, property.Value) is { } problem) problems.Add($"'{parameter.Name}' {problem}");
        }
        return problems;
    }

    private static string? Check(Parameter parameter, JsonElement value)
    {
        switch (parameter.Type)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) return "must be a string.";
                var text = value.GetString()!;
                if (string.IsNullOrWhiteSpace(text)) return "must not be empty.";
                return parameter.Values is { } values && !values.Contains(text) ? $"must be one of: {string.Join(", ", values)}." : null;
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false.";
            default:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return "must be an integer.";
                return parameter.Minimum is { } minimum && number < minimum ? $"must be at least {minimum.ToString(CultureInfo.InvariantCulture)}." : null;
        }
    }

    /// <summary>The checked arguments of a tool call.</summary>
    private readonly struct ToolArguments(JsonElement values)
    {
        public string? Text(string name) => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(name, out var value) ? value.GetString() : null;
        public bool Flag(string name) => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        public int? Integer(string name) => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(name, out var value) ? value.GetInt32() : null;
    }

    /// <summary>What a CLI run wrote and returned. Exit codes 0 (done), 1 (done with warnings) and 11 (done, compare
    /// with the source) are results; any other is a failure of the tool.</summary>
    private sealed record CliRun(int ExitCode, string Output, string Error)
    {
        public bool Failed => ExitCode is not (0 or 1 or 11);
    }

    private async Task<CliRun> RunCliAsync(string[] args, CancellationToken cancellationToken)
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var exitCode = await new CliApplication(standardOutput, standardError, documentService).RunAsync(args, cancellationToken);
        return new CliRun(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    /// <summary>The reply of a tool: the CLI's output, then the tool's own items, then what the CLI wrote to its error stream.</summary>
    private static JsonObject ToolResult(CliRun run, IEnumerable<string> items)
    {
        var texts = new List<string> { string.IsNullOrWhiteSpace(run.Output) ? "(no output)" : Compact(run.Output) };
        texts.AddRange(items);
        if (!string.IsNullOrWhiteSpace(run.Error)) texts.Add(run.Error);
        return ToolResult(run.Failed, texts);
    }

    // The CLI indents its JSON for people; a client reads it as well without the indentation, in fewer tokens.
    private static string Compact(string text)
    {
        try { return JsonNode.Parse(text)?.ToJsonString(Json) ?? text; }
        catch (JsonException) { return text; }
    }

    private static JsonObject ToolResult(bool isError, IEnumerable<string> texts) => new()
    {
        ["content"] = new JsonArray([.. texts.Select(text => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text })]),
        ["isError"] = isError,
    };

    private async Task<JsonObject> ExportMarkdownAsync(ToolArguments arguments, CancellationToken cancellationToken)
    {
        var source = FullPath(arguments, "path");
        var markdown = Path.Combine(OutputDirectory(arguments), Path.GetFileNameWithoutExtension(source) + ".md");
        var cli = new List<string> { "export", source, "--output", markdown, "--images", "omit", "--json" };
        AddExportOptions(cli, arguments);
        if (arguments.Flag("overwrite")) cli.Add("--force");
        var run = await RunCliAsync([.. cli], cancellationToken);
        var items = new List<string>();
        if (!run.Failed && File.Exists(markdown))
            items.Add(Truncated(await File.ReadAllTextAsync(markdown, cancellationToken), arguments.Integer("max_chars") ?? DefaultMaxCharacters, markdown));
        return ToolResult(run, items);
    }

    private async Task<JsonObject> ExportAiPackageAsync(ToolArguments arguments, CancellationToken cancellationToken)
    {
        var source = FullPath(arguments, "path");
        var package = Path.Combine(OutputDirectory(arguments), Path.GetFileNameWithoutExtension(source) + "-package");
        var cli = new List<string> { "export", source, "--ai-package", "dir", "--output", package, "--json" };
        AddExportOptions(cli, arguments);
        if (arguments.Integer("chunk_chars") is { } chunk) cli.AddRange(["--chunk-chars", chunk.ToString(CultureInfo.InvariantCulture)]);
        if (arguments.Flag("table_row_blocks")) cli.Add("--table-row-blocks");
        if (arguments.Flag("overwrite")) cli.Add("--force");
        var run = await RunCliAsync([.. cli], cancellationToken);
        return ToolResult(run, run.Failed ? Array.Empty<string>() : ["package_path: " + package]);
    }

    private static string[] PackageSearch(ToolArguments arguments)
    {
        var query = arguments.Text("query")!;
        // The command line reads a word that starts with "-" as an option, so such a query goes in the --query=text form.
        var cli = new List<string> { "package", "search", FullPath(arguments, "package"), query.StartsWith('-') ? "--query=" + query : query };
        if (arguments.Flag("allow_review")) cli.Add("--allow-review");
        cli.Add("--json");
        return [.. cli];
    }

    private static string[] PackageRead(ToolArguments arguments)
    {
        var cli = new List<string> { "package", "read", FullPath(arguments, "package"), "--part", arguments.Text("part")! };
        if (arguments.Flag("with_sources")) cli.Add("--with-sources");
        if (arguments.Flag("allow_review")) cli.Add("--allow-review");
        cli.Add("--json");
        return [.. cli];
    }

    private static void AddExportOptions(List<string> cli, ToolArguments arguments)
    {
        AddOption(cli, "--content-policy", arguments.Text("content_policy"));
        AddOption(cli, "--revisions", arguments.Text("revisions"));
        AddOption(cli, "--sheets", arguments.Text("sheets"));
        if (arguments.Flag("allow_pdf")) cli.Add("--allow-pdf");
        AddOption(cli, "--ocr", arguments.Text("ocr"));
        AddOption(cli, "--lang", arguments.Text("lang"));
    }

    private static void AddOption(List<string> cli, string option, string? value)
    {
        if (value is not null) cli.AddRange([option, value]);
    }

    private static string FullPath(ToolArguments arguments, string name) => Path.GetFullPath(arguments.Text(name)!);

    private static string OutputDirectory(ToolArguments arguments)
    {
        var directory = Path.GetFullPath(arguments.Text("output_dir")
            ?? Path.Combine(Path.GetTempPath(), "docredock-mcp", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The Markdown, or its first characters and a note with the size of the whole and where to find it.</summary>
    private static string Truncated(string markdown, int maxCharacters, string file)
    {
        if (markdown.Length <= maxCharacters) return markdown;
        // Never cut between the two halves of a surrogate pair (an emoji, a rare kanji).
        var length = char.IsHighSurrogate(markdown[maxCharacters - 1]) ? maxCharacters - 1 : maxCharacters;
        return markdown[..length] + FormattableString.Invariant(
            $"\n\n[DocRedock: truncated at {length} of {markdown.Length} characters; the full Markdown is at {file}]");
    }

    // The id is echoed exactly as the client wrote it (a number or a string); null when the request had none that is valid.
    private static JsonObject Envelope(JsonElement? id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id is { } value ? JsonNode.Parse(value.GetRawText()) : null,
    };

    private static JsonObject Result(JsonElement? id, JsonNode result)
    {
        var response = Envelope(id);
        response["result"] = result;
        return response;
    }

    private static JsonObject Error(JsonElement? id, int code, string message)
    {
        var response = Envelope(id);
        response["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        return response;
    }
}

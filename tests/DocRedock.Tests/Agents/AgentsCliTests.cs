using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Agents;

public sealed class AgentsCliTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-agents-cli-").FullName;
    private string Home => Path.Combine(root, "home");
    private string Cli => Path.Combine(root, "docredock-test.exe");

    public AgentsCliTests() => File.WriteAllText(Cli, "x");

    public void Dispose() => Directory.Delete(root, true);

    private async Task<(int Code, string Out, string Err)> RunAsync(string? stdin, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(output, error, new DocumentService(null, null, discoverPdfRasterizer: false), new StringReader(stdin ?? string.Empty))
        {
            AgentRunner = new FakeRunner(),
        };
        var code = await app.RunAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    private string[] Common => ["--home", Home, "--command", Cli];

    [Fact]
    public async Task Install_json_end_to_end_writes_files_and_reports_every_item()
    {
        var (code, output, _) = await RunAsync(null, ["agents", "install", .. Common, "--json", "--client", "copilot,codex", "--enforce"]);
        Assert.Equal(0, code);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.Equal("docredock.agents.v1", json.GetProperty("schema").GetString());
        Assert.Equal("install", json.GetProperty("operation").GetString());
        Assert.False(json.GetProperty("dry_run").GetBoolean());
        Assert.Equal(Cli, json.GetProperty("cli").GetString());
        Assert.True(json.GetProperty("cli_exists").GetBoolean());
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(6, items.Length);
        Assert.All(items, item => Assert.Equal("installed", item.GetProperty("state").GetString()));
        Assert.DoesNotContain(items, item => item.GetProperty("client").GetString() == "codex" && item.GetProperty("item").GetString() == "hook"
            && item.GetProperty("action").GetString() == "create");
        Assert.Contains($"command = '{Cli}'", File.ReadAllText(Path.Combine(Home, ".codex", "config.toml")));
        Assert.False(File.Exists(Path.Combine(Home, ".copilot", "hooks", "docredock.json")));
    }

    [Fact]
    public async Task Auto_approve_flags_add_and_remove_the_approval_and_json_lists_the_approval_items()
    {
        var (code, output, _) = await RunAsync(null, ["agents", "install", .. Common, "--json", "--auto-approve"]);
        Assert.Equal(0, code);
        var items = JsonDocument.Parse(output).RootElement.GetProperty("items").EnumerateArray().ToArray();
        var approvals = items.Where(item => item.GetProperty("item").GetString() == "approval").ToArray();
        Assert.Equal(["claude", "copilot", "codex"], approvals.Select(item => item.GetProperty("client").GetString()!).ToArray());
        Assert.All(approvals, item => { Assert.Equal("installed", item.GetProperty("state").GetString()); Assert.Equal(item.GetProperty("client").GetString() == "codex" ? "update" : "create", item.GetProperty("action").GetString()); });
        Assert.Contains("mcp__docredock__*", File.ReadAllText(Path.Combine(Home, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(Home, ".copilot", "hooks", "docredock-approve.json")));
        Assert.Contains("default_tools_approval_mode = \"approve\"", File.ReadAllText(Path.Combine(Home, ".codex", "config.toml")));

        var (_, status, _) = await RunAsync(null, ["agents", "status", .. Common, "--json"]);
        var states = JsonDocument.Parse(status).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("client").GetString() + "/" + item.GetProperty("item").GetString(), item => item.GetProperty("state").GetString());
        Assert.Equal("installed", states["claude/approval"]);
        Assert.Equal("installed", states["copilot/approval"]);
        Assert.Equal("installed", states["codex/approval"]);
        Assert.Equal("missing", states["claude/hook"]);

        // Without a flag the approval is kept; --no-auto-approve removes it; the hook flags work the same way.
        await RunAsync(null, ["agents", "install", .. Common, "--enforce"]);
        Assert.False(File.Exists(Path.Combine(Home, ".codex", "hooks.json")));
        await RunAsync(null, ["agents", "install", .. Common]);
        Assert.True(File.Exists(Path.Combine(Home, ".copilot", "hooks", "docredock-approve.json")));
        var (removeCode, removed, _) = await RunAsync(null, ["agents", "install", .. Common, "--no-auto-approve", "--no-enforce"]);
        Assert.Equal(0, removeCode);
        Assert.Contains("removed", removed);
        Assert.False(File.Exists(Path.Combine(Home, ".copilot", "hooks", "docredock-approve.json")));
        Assert.False(File.Exists(Path.Combine(Home, ".codex", "hooks.json")));
        Assert.DoesNotContain("default_tools_approval_mode", File.ReadAllText(Path.Combine(Home, ".codex", "config.toml")));
        Assert.DoesNotContain("permissions", File.Exists(Path.Combine(Home, ".claude", "settings.json")) ? File.ReadAllText(Path.Combine(Home, ".claude", "settings.json")) : string.Empty);
    }

    [Fact]
    public async Task Repeating_the_same_flag_is_fine_and_dry_run_with_no_flags_lists_removals()
    {
        var (code, _, _) = await RunAsync(null, ["agents", "install", .. Common, "--auto-approve", "--auto-approve", "--enforce", "--enforce", "--dry-run"]);
        Assert.Equal(0, code);
        await RunAsync(null, ["agents", "install", .. Common, "--auto-approve"]);
        var (planCode, plan, _) = await RunAsync(null, ["agents", "install", .. Common, "--no-auto-approve", "--dry-run"]);
        Assert.Equal(0, planCode);
        Assert.Contains("would remove", plan);
        Assert.True(File.Exists(Path.Combine(Home, ".copilot", "hooks", "docredock-approve.json")));
    }

    [Fact]
    public async Task Status_json_has_a_stable_shape()
    {
        await RunAsync(null, ["agents", "install", .. Common, "--client", "codex"]);
        var (code, output, _) = await RunAsync(null, ["agents", "status", .. Common, "--json", "--client", "codex", "--client", "copilot"]);
        Assert.Equal(0, code);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.Equal(["schema", "operation", "dry_run", "cli", "cli_exists", "exit_code", "items", "notes"], json.EnumerateObject().Select(property => property.Name).ToArray());
        var first = json.GetProperty("items")[0];
        Assert.Equal(["client", "item", "state", "action", "path", "detail"], first.EnumerateObject().Select(property => property.Name).ToArray());
        var states = json.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("client").GetString() + "/" + item.GetProperty("item").GetString(), item => item.GetProperty("state").GetString());
        Assert.Equal("installed", states["codex/mcp"]);
        Assert.Equal("missing", states["codex/hook"]);
        Assert.Equal("missing", states["copilot/skill"]);
    }

    [Fact]
    public async Task Human_output_is_a_table_followed_by_what_to_do_next()
    {
        var (code, output, _) = await RunAsync(null, ["agents", "install", .. Common, "--client", "codex", "--enforce", "--dry-run"]);
        Assert.Equal(0, code);
        Assert.Contains("dry run", output);
        Assert.Contains("would add", output);
        Assert.Contains("retired", output);
        Assert.False(Directory.Exists(Home) && Directory.EnumerateFileSystemEntries(Home).Any());

        var (_, installed, _) = await RunAsync(null, ["agents", "install", .. Common, "--client", "codex"]);
        Assert.Contains("Restart the assistants", installed);
        var (_, status, _) = await RunAsync(null, ["agents", "status", .. Common, "--client", "codex"]);
        Assert.Contains("installed", status);
    }

    [Fact]
    public async Task A_conflict_gives_the_partial_exit_code_and_uninstall_restores_a_clean_home()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".copilot"));
        var original = "{\n  \"mcpServers\": {\n    \"docredock\": {\n      \"command\": \"mine\"\n    }\n  }\n}\n";
        File.WriteAllText(Path.Combine(Home, ".copilot", "mcp-config.json"), original);
        var (code, output, _) = await RunAsync(null, ["agents", "install", .. Common, "--client", "copilot", "--json"]);
        Assert.Equal(1, code);
        Assert.Equal(1, JsonDocument.Parse(output).RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal(original, File.ReadAllText(Path.Combine(Home, ".copilot", "mcp-config.json")));

        var (uninstall, _, _) = await RunAsync(null, ["agents", "uninstall", .. Common, "--client", "copilot"]);
        Assert.Equal(1, uninstall);
        Assert.Equal([Path.Combine(Home, ".copilot", "mcp-config.json")], Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories).ToArray());
    }

    [Theory]
    [InlineData("agents install --client vim")]
    [InlineData("agents install --client")]
    [InlineData("agents frobnicate")]
    [InlineData("agents")]
    [InlineData("agents install extra")]
    [InlineData("agents install --bogus")]
    [InlineData("agents status --enforce")]
    [InlineData("agents status --no-enforce")]
    [InlineData("agents status --auto-approve")]
    [InlineData("agents uninstall --auto-approve")]
    [InlineData("agents uninstall --no-auto-approve")]
    [InlineData("agents uninstall --enforce")]
    [InlineData("agents install --enforce --no-enforce")]
    [InlineData("agents install --no-enforce --enforce")]
    [InlineData("agents install --auto-approve --no-auto-approve")]
    [InlineData("agents install --no-auto-approve --auto-approve")]
    [InlineData("agents status --dry-run")]
    [InlineData("agents install --home")]
    public async Task Usage_errors_exit_with_code_2_and_write_nothing(string commandLine)
    {
        var (code, output, error) = await RunAsync(null, [.. commandLine.Split(' '), "--command", Cli]);
        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.NotEqual(string.Empty, error);
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public async Task Help_documents_the_advanced_options_and_exit_codes()
    {
        var (code, output, _) = await RunAsync(null, "help", "agents");
        Assert.Equal(0, code);
        Assert.Contains("--home DIR", output);
        Assert.Contains("--command PATH", output);
        Assert.Contains("--enforce", output);
        Assert.Contains("--no-enforce", output);
        Assert.Contains("--auto-approve", output);
        Assert.Contains("--no-auto-approve", output);
        Assert.Contains("docredock-approve.json", output);
        Assert.Contains("mcp__docredock__*", output);
        Assert.Contains("copilot-approve", output);
        Assert.Contains("Exit codes", output);
        var (_, main, _) = await RunAsync(null, "--help");
        Assert.Contains("docredock agents install", main);
        Assert.Contains("--auto-approve|--no-auto-approve", main);
        Assert.DoesNotContain("docredock hook", main);
        var (unknown, _, _) = await RunAsync(null, "agent");
        Assert.Equal(2, unknown);
    }

    [Theory]
    [InlineData("claude", "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:/a/a.docx\"}}", "permissionDecision")]
    [InlineData("codex", "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"unzip -p a.xlsx\"}}", "permissionDecision")]
    [InlineData("copilot", "{\"toolName\":\"view\",\"toolArgs\":\"{\\\"path\\\":\\\"a.pptx\\\"}\"}", "permissionDecision")]
    public async Task The_hook_command_prints_the_deny_decision_and_exits_0(string client, string stdin, string expected)
    {
        var (code, output, _) = await RunAsync(stdin, "hook", client);
        Assert.Equal(0, code);
        Assert.Equal(client=="copilot"?"{}":"", output);
        Assert.DoesNotContain(expected, output);
    }

    [Theory]
    [InlineData("claude", "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"a.pdf\"}}", "")]
    [InlineData("claude", "garbage", "")]
    [InlineData("codex", "", "")]
    [InlineData("copilot", "{\"toolName\":\"view\",\"toolArgs\":{\"path\":\"a.md\"}}", "{}")]
    [InlineData("copilot", "garbage", "{}")]
    public async Task The_hook_command_allows_quietly_and_exits_0(string client, string stdin, string expected)
    {
        var (code, output, error) = await RunAsync(stdin, "hook", client);
        Assert.Equal(0, code);
        Assert.Equal(expected, output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task The_hook_command_exits_0_even_for_a_wrong_client_or_a_broken_input()
    {
        Assert.Equal(0, (await RunAsync("{}", "hook", "vim")).Code);
        Assert.Equal(0, (await RunAsync("{}", "hook")).Code);
        var app = new CliApplication(new StringWriter(), new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false), new ThrowingReader());
        Assert.Equal(0, await app.RunAsync(["hook", "claude"]));
    }

    [Fact]
    public async Task The_hook_writes_utf8_and_keeps_a_japanese_file_name_intact()
    {
        var stdin = "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:/資料/設計書.docx\"}}";
        var stream = new MemoryStream();
        var app = new CliApplication(new StringWriter(), new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false), new StringReader(stdin)) { HookOutput = stream };
        Assert.Equal(0, await app.RunAsync(["hook", "claude"]));
        var bytes = stream.ToArray();
        Assert.Empty(bytes); // Native reads, including Japanese paths, produce no restrictive decision.
    }

    private sealed class ThrowingReader : TextReader
    {
        public override Task<string> ReadToEndAsync() => throw new IOException("closed");
        public override string ReadToEnd() => throw new IOException("closed");
    }
}

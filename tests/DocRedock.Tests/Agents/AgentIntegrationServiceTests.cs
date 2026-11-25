using DocRedock.Api.Agents;

namespace DocRedock.Tests.Agents;

public sealed class AgentIntegrationServiceTests
{
    private const string Begin = "<!-- docredock:begin (managed by `docredock agents install`; remove with `docredock agents uninstall`) -->";
    private const string End = "<!-- docredock:end -->";
    private static readonly AgentClient[] All = [AgentClient.Claude, AgentClient.Copilot, AgentClient.Codex];

    private static string[] Bak(AgentSandbox box) =>
        Directory.EnumerateFiles(box.Home, "*.docredock.bak", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(box.Home, path).Replace('\\', '/')).Order().ToArray();

    [Fact]
    public void Install_into_an_empty_home_writes_exactly_the_expected_files_without_hooks()
    {
        using var box = new AgentSandbox();
        var report = box.Service.Install(AgentSandbox.Options(), box.Environment());

        Assert.False(report.HasProblems);
        Assert.Equal("docredock.agents.v1", report.Schema);
        Assert.Equal(9, report.Items.Count);
        Assert.Equal(
            [".agents/skills/docredock-office/SKILL.md", ".claude.json", ".claude/CLAUDE.md", ".claude/skills/docredock-office/SKILL.md",
                ".codex/AGENTS.md", ".codex/config.toml", ".copilot/instructions/docredock.instructions.md", ".copilot/mcp-config.json",
                ".copilot/skills/docredock-office/SKILL.md"],
            box.Snapshot().Keys.ToArray());
        Assert.All(report.Items, item => Assert.Equal(AgentItemState.Installed, item.State));

        var claude = box.Json(".claude.json")["mcpServers"]!["docredock"]!;
        Assert.Equal("stdio", (string?)claude["type"]);
        Assert.Equal(box.Cli, (string?)claude["command"]);
        Assert.Equal("mcp", (string?)claude["args"]![0]);
        Assert.Empty(claude["env"]!.AsObject());

        var copilot = box.Json(".copilot", "mcp-config.json")["mcpServers"]!["docredock"]!;
        Assert.Equal("stdio", (string?)copilot["type"]);
        Assert.Equal(box.Cli, (string?)copilot["command"]);
        Assert.Equal("*", (string?)copilot["tools"]![0]);

        Assert.Equal(
            $"# >>> docredock (managed by `docredock agents`; remove with `docredock agents uninstall`) >>>\n[mcp_servers.docredock]\ncommand = '{box.Cli}'\nargs = [\"mcp\"]\n# <<< docredock <<<\n",
            File.ReadAllText(box.P(".codex", "config.toml")));

        var block = File.ReadAllText(box.P(".claude", "CLAUDE.md"));
        Assert.StartsWith(Begin + "\n## Office documents\n", block);
        Assert.EndsWith("\n" + End + "\n", block);
        Assert.Equal(block, File.ReadAllText(box.P(".codex", "AGENTS.md")));

        var instructions = File.ReadAllText(box.P(".copilot", "instructions", "docredock.instructions.md"));
        Assert.StartsWith("---\napplyTo: '**'\n---\n\n## Office documents\n", instructions);
        Assert.DoesNotContain("docredock:begin", instructions);

        foreach (var skill in new[] { box.P(".claude", "skills", "docredock-office", "SKILL.md"), box.P(".copilot", "skills", "docredock-office", "SKILL.md"), box.P(".agents", "skills", "docredock-office", "SKILL.md") })
        {
            var text = File.ReadAllText(skill);
            Assert.StartsWith("---\nname: docredock-office\ndescription: Extract text and structure", text);
            Assert.Contains($"\"{box.Cli}\" export \"<file>\" -o \"<file>.md\"", text);
            Assert.DoesNotContain("{{", text);
            Assert.DoesNotContain("\r", text);
        }
        Assert.Empty(Bak(box));
    }

    [Fact]
    public void Install_with_claude_on_path_uses_the_claude_command_line_and_does_not_write_claude_json()
    {
        using var box = new AgentSandbox().WithClaudeOnPath();
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());

        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Mcp).State);
        Assert.False(File.Exists(box.P(".claude.json")));
        var call = Assert.Single(box.Runner.Calls);
        Assert.Equal(Path.Combine(box.BinDirectory, box.IsWindows ? "claude.EXE" : "claude"), call.File);
        Assert.Equal(["mcp", "add", "--scope", "user", "docredock", "--", box.Cli, "mcp"], call.Arguments);
        Assert.Equal(box.Home, call.Environment["HOME"]);
        Assert.Equal(box.Home, call.Environment["USERPROFILE"]);
    }

    [Fact]
    public void Updating_a_claude_server_removes_then_adds_it_and_uninstall_removes_it()
    {
        using var box = new AgentSandbox().WithClaudeOnPath();
        box.Write(box.P(".claude.json"), $$"""
            {
              "mcpServers": {
                "docredock": {
                  "type": "stdio",
                  "command": "{{box.OtherCli.Replace("\\", "\\\\")}}",
                  "args": [
                    "mcp"
                  ],
                  "env": {}
                }
              }
            }

            """);
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());
        Assert.Equal(AgentAction.Update, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Mcp).Action);
        Assert.Equal(2, box.Runner.Calls.Count);
        Assert.Equal(["mcp", "remove", "--scope", "user", "docredock"], box.Runner.Calls[0].Arguments);
        Assert.Equal(["mcp", "add", "--scope", "user", "docredock", "--", box.Cli, "mcp"], box.Runner.Calls[1].Arguments);

        box.Runner.Calls.Clear();
        var removed = box.Service.Uninstall(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment(box.OtherCli));
        Assert.Equal(AgentAction.Remove, AgentSandbox.Item(removed, AgentClient.Claude, AgentItem.Mcp).Action);
        Assert.Equal(["mcp", "remove", "--scope", "user", "docredock"], Assert.Single(box.Runner.Calls).Arguments);
    }

    [Fact]
    public void A_failing_claude_command_is_an_error_item_and_the_others_still_install()
    {
        using var box = new AgentSandbox().WithClaudeOnPath();
        box.Runner.ExitCode = 3;
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());
        var mcp = AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Mcp);
        Assert.Equal(AgentItemState.Error, mcp.State);
        Assert.Contains("boom", mcp.Detail);
        Assert.True(report.HasProblems);
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Skill).State);
    }

    [Fact]
    public void A_second_install_changes_nothing()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        var before = box.Snapshot();
        var report = box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        Assert.Equal(before, box.Snapshot());
        Assert.Empty(Bak(box));
        Assert.All(report.Items, item => { Assert.Equal(AgentAction.None, item.Action); Assert.Equal(AgentItemState.Installed, item.State); });
    }

    private static void SeedShared(AgentSandbox box)
    {
        box.Write(box.P(".claude.json"), "{\n  \"numStartups\": 3,\n  \"mcpServers\": {\n    \"other\": {\n      \"command\": \"x\"\n    }\n  }\n}\n");
        box.Write(box.P(".claude", "settings.json"), "{\n  \"theme\": \"dark\",\n  \"hooks\": {\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"echo hi\"\n          }\n        ]\n      }\n    ],\n    \"Stop\": []\n  }\n}\n");
        box.Write(box.P(".claude", "CLAUDE.md"), "# My notes\n\nUse tabs.\n");
        box.Write(box.P(".copilot", "mcp-config.json"), "{\n  \"mcpServers\": {\n    \"other\": {\n      \"type\": \"http\",\n      \"url\": \"https://example.com\"\n    }\n  },\n  \"trusted\": true\n}\n");
        box.Write(box.P(".codex", "config.toml"), "model = \"gpt\"\n\n[projects.'C:\\work']\ntrust_level = \"trusted\"\n\n[mcp_servers.other]\ncommand = \"y\"\n");
        box.Write(box.P(".codex", "AGENTS.md"), "Rule one.\n\nRule two.\n");
        box.Write(box.P(".codex", "hooks.json"), "{\n  \"hooks\": {\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"^Bash$\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"audit\"\n          }\n        ]\n      }\n    ]\n  }\n}\n");
    }

    [Fact]
    public void Existing_content_survives_install_with_backups_and_uninstall_restores_the_files_exactly()
    {
        using var box = new AgentSandbox();
        SeedShared(box);
        var before = box.Snapshot();

        var report = box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        Assert.False(report.HasProblems);
        var after = box.Snapshot();
        Assert.NotEqual(before[".claude.json"], after[".claude.json"]);
        Assert.Contains("\"numStartups\": 3", after[".claude.json"]);
        Assert.Contains("\"other\"", after[".claude.json"]);
        Assert.StartsWith("# My notes\n\nUse tabs.\n\n" + Begin, after[".claude/CLAUDE.md"]);
        Assert.StartsWith("model = \"gpt\"\n\n[projects.'C:\\work']\ntrust_level = \"trusted\"\n\n[mcp_servers.other]\ncommand = \"y\"\n\n# >>> docredock", after[".codex/config.toml"]);
        Assert.Contains("\"echo hi\"", after[".claude/settings.json"]);
        Assert.Contains("\"command\": \"audit\"", after[".codex/hooks.json"]);

        // Backups hold the original bytes, and only the files that existed got one.
        Assert.Equal(
            [".claude.json.docredock.bak", ".claude/CLAUDE.md.docredock.bak", ".codex/AGENTS.md.docredock.bak",
                ".codex/config.toml.docredock.bak", ".copilot/mcp-config.json.docredock.bak"],
            Bak(box));
        foreach (var bak in Bak(box)) Assert.Equal(before[bak[..^".docredock.bak".Length]], File.ReadAllText(box.P(bak)));

        box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        var restored = box.Snapshot();
        foreach (var (path, text) in before) Assert.Equal(text, restored[path]);
        Assert.Equal(before.Count + 5, restored.Count); // the originals plus their backups; every file we created is gone
        Assert.Equal(before.Keys.Concat(Bak(box)).Order().ToArray(), restored.Keys.ToArray());
    }

    [Fact]
    public void Files_that_existed_before_install_survive_uninstall_even_when_left_empty()
    {
        using var box = new AgentSandbox();
        box.Write(box.P(".copilot", "mcp-config.json"), "{}");
        box.Write(box.P(".codex", "AGENTS.md"), "");
        box.Write(box.P(".claude", "settings.json"), "{}\n");
        box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        Assert.Contains("docredock", File.ReadAllText(box.P(".codex", "AGENTS.md")));

        box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        Assert.Equal("{}", File.ReadAllText(box.P(".copilot", "mcp-config.json")));
        Assert.Equal("", File.ReadAllText(box.P(".codex", "AGENTS.md")));
        Assert.Equal("{}\n", File.ReadAllText(box.P(".claude", "settings.json")));
        // Files install created are still deleted.
        Assert.False(File.Exists(box.P(".codex", "config.toml")));
        Assert.False(File.Exists(box.P(".claude", "CLAUDE.md")));
        Assert.False(File.Exists(box.P(".claude.json")));
        Assert.False(Directory.Exists(box.P(".agents")));
        Assert.False(Directory.Exists(box.P(".claude", "skills")));
        Assert.False(File.Exists(box.P(".copilot", "hooks", "docredock.json")));
    }

    [Fact]
    public void Crlf_text_files_keep_their_line_ending_and_are_restored()
    {
        using var box = new AgentSandbox();
        File.WriteAllBytes(box.P(".claude", "CLAUDE.md") is var path ? Prepare(path) : path, "# Notes\r\n\r\nUse tabs.\r\n"u8.ToArray());
        box.Service.Install(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());
        var text = File.ReadAllText(box.P(".claude", "CLAUDE.md"));
        Assert.Contains("\r\n" + Begin + "\r\n## Office documents\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        box.Service.Uninstall(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());
        Assert.Equal("# Notes\r\n\r\nUse tabs.\r\n", File.ReadAllText(box.P(".claude", "CLAUDE.md")));

        static string Prepare(string file) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); return file; }
    }

    [Fact]
    public void Install_with_another_command_updates_our_entries_in_place()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        var moved = box.Environment(box.OtherCli);

        var status = box.Service.Status(AgentSandbox.Options(), moved);
        Assert.Equal(AgentItemState.Outdated, AgentSandbox.Item(status, AgentClient.Claude, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(status, AgentClient.Claude, AgentItem.Instructions).State);
        Assert.Equal(AgentItemState.Missing, AgentSandbox.Item(status, AgentClient.Codex, AgentItem.Hook).State);

        var report = box.Service.Install(AgentSandbox.Options(enforce: true), moved);
        Assert.False(report.HasProblems);
        Assert.Equal(AgentAction.Update, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp).Action);
        Assert.Equal(AgentAction.None, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Instructions).Action);
        Assert.Equal(box.OtherCli, (string?)box.Json(".claude.json")["mcpServers"]!["docredock"]!["command"]);
        Assert.Equal(box.OtherCli, (string?)box.Json(".copilot", "mcp-config.json")["mcpServers"]!["docredock"]!["command"]);
        var toml = File.ReadAllText(box.P(".codex", "config.toml"));
        Assert.Contains($"command = '{box.OtherCli}'", toml);
        Assert.DoesNotContain(box.Cli, toml);
        Assert.Contains(box.OtherCli, File.ReadAllText(box.P(".claude", "skills", "docredock-office", "SKILL.md")));
        Assert.False(File.Exists(box.P(".claude","settings.json")));
        Assert.Equal(AgentItemState.Missing,AgentSandbox.Item(box.Service.Status(AgentOptions.AllClients,moved),AgentClient.Codex,AgentItem.Hook).State);
    }

    [Fact]
    public void Uninstall_after_a_fresh_install_leaves_an_empty_home()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        var report = box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        Assert.False(report.HasProblems);
        Assert.All(report.Items, item => Assert.Equal(AgentItemState.Missing, item.State));
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));
        var again = box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        Assert.All(again.Items, item => Assert.Equal(AgentAction.None, item.Action));
    }

    [Fact]
    public void An_unmanaged_server_is_a_conflict_that_is_left_alone_while_other_items_install()
    {
        using var box = new AgentSandbox();
        var mcpConfig = "{\n  \"mcpServers\": {\n    \"docredock\": {\n      \"command\": \"node\",\n      \"args\": [\n        \"mine.js\"\n      ]\n    }\n  }\n}\n";
        var toml = "[mcp_servers.\"docredock\"]\ncommand = \"mine\"\n";
        box.Write(box.P(".copilot", "mcp-config.json"), mcpConfig);
        box.Write(box.P(".codex", "config.toml"), toml);

        var report = box.Service.Install(AgentSandbox.Options(), box.Environment());
        Assert.True(report.HasProblems);
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Skill).State);
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Instructions).State);
        Assert.Equal(mcpConfig, File.ReadAllText(box.P(".copilot", "mcp-config.json")));
        Assert.Equal(toml, File.ReadAllText(box.P(".codex", "config.toml")));
        Assert.True(File.Exists(box.P(".codex", "AGENTS.md")));

        var removed = box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(removed, AgentClient.Codex, AgentItem.Mcp).State);
        Assert.Equal(mcpConfig, File.ReadAllText(box.P(".copilot", "mcp-config.json")));
        Assert.Equal(toml, File.ReadAllText(box.P(".codex", "config.toml")));
        Assert.False(File.Exists(box.P(".codex", "AGENTS.md")));
    }

    [Theory]
    [InlineData("[mcp_servers.docredock]\ncommand = \"x\"\n", true)]
    [InlineData("[ mcp_servers . \"docredock\" ]\ncommand = \"x\"\n", true)]
    [InlineData("[mcp_servers.'docredock']  # mine\ncommand = \"x\"\n", true)]
    [InlineData("[mcp_servers.docredock.env]\nA = \"1\"\n", true)]
    [InlineData("[mcp_servers]\ndocredock = { command = \"x\" }\n", true)]
    [InlineData("mcp_servers.docredock.command = \"x\"\n", true)]
    [InlineData("[mcp_servers.docredock2]\ncommand = \"x\"\n", false)]
    [InlineData("# [mcp_servers.docredock]\n[mcp_servers.other]\ndocredock = \"note\"\n", false)]
    public void Toml_conflicts_are_detected_in_every_spelling(string existing, bool conflict)
    {
        using var box = new AgentSandbox();
        box.Write(box.P(".codex", "config.toml"), existing);
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), box.Environment());
        var mcp = AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp);
        if (conflict)
        {
            Assert.Equal(AgentItemState.Conflict, mcp.State);
            Assert.Equal(existing, File.ReadAllText(box.P(".codex", "config.toml")));
        }
        else
        {
            Assert.Equal(AgentItemState.Installed, mcp.State);
            Assert.StartsWith(existing + "\n# >>> docredock", File.ReadAllText(box.P(".codex", "config.toml")));
        }
    }

    [Fact]
    public void A_toml_path_with_a_single_quote_uses_a_basic_string()
    {
        using var box = new AgentSandbox();
        var cli = Path.Combine(box.Root, "it's", "docredock.exe");
        box.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), box.Environment(cli));
        Assert.Contains("command = \"" + cli.Replace("\\", "\\\\") + "\"\n", File.ReadAllText(box.P(".codex", "config.toml")));
    }

    [Fact]
    public void Invalid_json_in_a_shared_file_leaves_it_untouched_and_is_reported_as_an_error()
    {
        using var box = new AgentSandbox();
        var broken = "{ \"mcpServers\": { not json";
        var comment = "{\n  // keep me\n  \"a\": 1\n}\n";
        box.Write(box.P(".claude.json"), broken);
        box.Write(box.P(".copilot", "mcp-config.json"), "[1, 2]");
        box.Write(box.P(".claude", "settings.json"), comment);
        var report = box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        Assert.Equal(AgentItemState.Error, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Error, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Error, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Hook).State);
        Assert.True(report.HasProblems);
        Assert.Equal(broken, File.ReadAllText(box.P(".claude.json")));
        Assert.Equal("[1, 2]", File.ReadAllText(box.P(".copilot", "mcp-config.json")));
        Assert.Equal(comment, File.ReadAllText(box.P(".claude", "settings.json")));
        Assert.Empty(Bak(box));
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Skill).State);
    }

    [Fact]
    public void Dry_run_lists_the_changes_and_writes_nothing()
    {
        using var box = new AgentSandbox().WithClaudeOnPath();
        var plan = box.Service.Plan(AgentSandbox.Options(enforce: true), box.Environment());
        Assert.True(plan.DryRun);
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));
        Assert.Empty(box.Runner.Calls);
        Assert.Equal(9, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.Equal(AgentAction.Create, item.Action));

        var dry = box.Service.Install(AgentSandbox.Options(dryRun: true), box.Environment());
        Assert.True(dry.DryRun);
        Assert.Equal(9, dry.Items.Count);
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));

        box.Service.Install(AgentSandbox.Options(enforce: true), box.Environment());
        var before = box.Snapshot();
        var uninstall = box.Service.Uninstall(AgentSandbox.Options(dryRun: true), box.Environment());
        Assert.All(uninstall.Items.Where(item => item.Item is not (AgentItem.Approval or AgentItem.Hook) && (item.Item != AgentItem.Mcp || item.Client != AgentClient.Claude)), item => Assert.Equal(AgentAction.Remove, item.Action));
        Assert.Equal(before, box.Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retired_enforce_never_installs_a_restrictive_hook(bool windows)
    {
        using var box=new AgentSandbox {IsWindows=windows};
        var report=box.Service.Install(AgentSandbox.Options(enforce:true),box.Environment());
        Assert.False(report.HasProblems);
        Assert.DoesNotContain(report.Items,i=>i.Item==AgentItem.Hook);
        Assert.DoesNotContain(report.Notes,n=>n.Contains("/hooks",StringComparison.Ordinal));
        Assert.Contains(report.Notes,n=>n.Contains("retired",StringComparison.Ordinal));
        Assert.All(box.Service.Status(AgentOptions.AllClients,box.Environment()).Items.Where(i=>i.Item==AgentItem.Hook),i=>Assert.Equal(AgentItemState.Missing,i.State));
        Assert.False(File.Exists(box.P(".claude","settings.json")));
        Assert.False(File.Exists(box.P(".codex","hooks.json")));
        Assert.False(File.Exists(box.P(".copilot","hooks","docredock.json")));
        box.Service.Uninstall(AgentOptions.AllClients,box.Environment());
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));
    }

    [Fact]
    public void Without_enforce_an_existing_legacy_hook_is_removed_and_a_missing_one_is_not_added()
    {
        using var box=new AgentSandbox();
        AgentDirectInspectionTests.SeedLegacyHook(box,AgentClient.Claude);
        var report=box.Service.Install(AgentSandbox.Options(clients:[AgentClient.Claude,AgentClient.Codex]),box.Environment(box.OtherCli));
        Assert.Equal(AgentAction.Remove,AgentSandbox.Item(report,AgentClient.Claude,AgentItem.Hook).Action);
        Assert.DoesNotContain(report.Items,i=>i.Client==AgentClient.Codex&&i.Item==AgentItem.Hook);
        Assert.False(File.Exists(box.P(".claude","settings.json")));
    }

    [Fact]
    public void Environment_overrides_decide_where_each_client_is_configured()
    {
        using var box = new AgentSandbox();
        var claude = Path.Combine(box.Root, "cc");
        var copilot = Path.Combine(box.Root, "cp");
        var codex = Path.Combine(box.Root, "cx");
        {
            var env = box.Environment() with { ClaudeConfigDir = claude, CopilotHome = copilot, CodexHome = codex };
            var report = box.Service.Install(AgentSandbox.Options(enforce: true), env);
            Assert.False(report.HasProblems);
            Assert.Equal(".agents", Assert.Single(Directory.GetFileSystemEntries(box.Home).Select(Path.GetFileName)));
            Assert.True(File.Exists(Path.Combine(claude, ".claude.json")));
            Assert.True(File.Exists(Path.Combine(claude, "CLAUDE.md")));
            Assert.True(File.Exists(Path.Combine(claude, "skills", "docredock-office", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(claude, "settings.json")));
            Assert.True(File.Exists(Path.Combine(copilot, "mcp-config.json")));
            Assert.True(File.Exists(Path.Combine(copilot, "instructions", "docredock.instructions.md")));
            Assert.False(File.Exists(Path.Combine(copilot, "hooks", "docredock.json")));
            Assert.True(File.Exists(Path.Combine(codex, "config.toml")));
            Assert.True(File.Exists(Path.Combine(codex, "AGENTS.md")));
            Assert.False(File.Exists(Path.Combine(codex, "hooks.json")));
            // The Codex skill stays under the home directory, not CODEX_HOME.
            Assert.True(File.Exists(box.P(".agents", "skills", "docredock-office", "SKILL.md")));
            Assert.False(Directory.Exists(Path.Combine(codex, "skills")));
            Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(box.Service.Status(AgentSandbox.Options(), env), AgentClient.Codex, AgentItem.Skill).State);

            box.Service.Uninstall(AgentSandbox.Options(), env);
            Assert.False(Directory.Exists(claude));
            Assert.False(Directory.Exists(copilot));
            Assert.False(Directory.Exists(codex));
            Assert.Empty(Directory.GetFileSystemEntries(box.Home));
        }
    }

    [Fact]
    public void Status_reports_installed_missing_outdated_and_conflict_and_the_cli_file()
    {
        using var box = new AgentSandbox();
        var empty = box.Service.Status(AgentSandbox.Options(), box.Environment());
        Assert.Equal(15, empty.Items.Count);
        Assert.All(empty.Items, item => Assert.Equal(AgentItemState.Missing, item.State));
        Assert.True(empty.CliExists);
        Assert.False(empty.HasProblems);

        box.Service.Install(AgentSandbox.Options(), box.Environment());
        box.Write(box.P(".codex", "config.toml"), "[mcp_servers.docredock]\ncommand = \"z\"\n");
        var report = box.Service.Status(AgentSandbox.Options(), box.Environment(box.OtherCli));
        Assert.False(report.CliExists);
        Assert.Contains(report.Notes, note => note.Contains(box.OtherCli, StringComparison.Ordinal));
        Assert.Equal(AgentItemState.Outdated, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Skill).State);
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Instructions).State);
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Missing, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Hook).State);
        Assert.True(report.HasProblems);

        File.WriteAllText(box.P(".claude", "CLAUDE.md"), File.ReadAllText(box.P(".claude", "CLAUDE.md")).Replace("is recommended", "is always recommended"));
        Assert.Equal(AgentItemState.Outdated, AgentSandbox.Item(box.Service.Status(AgentSandbox.Options(), box.Environment()), AgentClient.Claude, AgentItem.Instructions).State);
    }

    [Fact]
    public void Only_the_requested_clients_are_configured()
    {
        using var box = new AgentSandbox();
        var report = box.Service.Install(AgentSandbox.Options(clients: [AgentClient.Copilot, AgentClient.Copilot]), box.Environment());
        Assert.All(report.Items, item => Assert.Equal(AgentClient.Copilot, item.Client));
        Assert.Equal(3, report.Items.Count);
        Assert.Equal(".copilot", Assert.Single(Directory.GetFileSystemEntries(box.Home).Select(Path.GetFileName)));
    }

    [Fact]
    public void An_unterminated_block_is_an_error_and_the_file_is_not_touched()
    {
        using var box = new AgentSandbox();
        var text = "Mine.\n" + Begin + "\nhalf a block\n";
        box.Write(box.P(".codex", "AGENTS.md"), text);
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), box.Environment());
        Assert.Equal(AgentItemState.Error, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Instructions).State);
        Assert.Equal(text, File.ReadAllText(box.P(".codex", "AGENTS.md")));
    }
}

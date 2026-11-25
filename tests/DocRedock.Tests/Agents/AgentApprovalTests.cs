using DocRedock.Api.Agents;

namespace DocRedock.Tests.Agents;

/// <summary>The Approval item (DocRedock's tools run without asking) and the on/off switches of the hook and approval items.</summary>
public sealed class AgentApprovalTests
{
    private const string Rule = "mcp__docredock__*";
    private const string ApprovalLine = "default_tools_approval_mode = \"approve\"";
    private static readonly AgentClient[] All = [AgentClient.Claude, AgentClient.Copilot, AgentClient.Codex];

    private static string[] Bak(AgentSandbox box) =>
        Directory.EnumerateFiles(box.Home, "*.docredock.bak", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(box.Home, path).Replace('\\', '/')).Order().ToArray();

    private static AgentItemState State(AgentSandbox box, AgentClient client, AgentItem item, AgentEnvironment? environment = null) =>
        AgentSandbox.Item(box.Service.Status(AgentSandbox.Options(), environment ?? box.Environment()), client, item).State;

    private static string CodexBlock(AgentSandbox box, bool approve, string? cli = null) =>
        "# >>> docredock (managed by `docredock agents`; remove with `docredock agents uninstall`) >>>\n[mcp_servers.docredock]\n" +
        $"command = '{cli ?? box.Cli}'\nargs = [\"mcp\"]\n" + (approve ? ApprovalLine + "\n" : string.Empty) + "# <<< docredock <<<\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Auto_approve_writes_exactly_the_expected_entries_for_each_client(bool windows)
    {
        using var box = new AgentSandbox { IsWindows = windows };
        var report = box.Service.Install(AgentSandbox.Options(approve: true), box.Environment());

        Assert.False(report.HasProblems);
        Assert.Equal(12, report.Items.Count);
        Assert.All(All, client => Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(report, client, AgentItem.Approval).State));
        Assert.All(All, client => Assert.Equal(client == AgentClient.Codex ? AgentAction.Update : AgentAction.Create, AgentSandbox.Item(report, client, AgentItem.Approval).Action)); // Codex: the Mcp item has just created the block the approval line goes into
        Assert.Contains(report.Notes, note => note.Contains("Copilot CLI", StringComparison.Ordinal) && note.Contains("VS Code", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Items, item => item.Item == AgentItem.Hook);

        Assert.Equal("{\n  \"permissions\": {\n    \"allow\": [\n      \"" + Rule + "\"\n    ]\n  }\n}\n", File.ReadAllText(box.P(".claude", "settings.json")));
        Assert.Equal(CodexBlock(box, approve: true), File.ReadAllText(box.P(".codex", "config.toml")));

        var copilot = box.Json(".copilot", "hooks", "docredock-approve.json");
        Assert.Equal(["version", "hooks"], copilot.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(1, (int?)copilot["version"]);
        var hooks = copilot["hooks"]!.AsObject();
        Assert.Equal(["permissionRequest"], hooks.Select(pair => pair.Key).ToArray());
        var entry = Assert.Single(hooks["permissionRequest"]!.AsArray())!;
        Assert.Equal("command", (string?)entry["type"]);
        Assert.Equal($"& \"{box.Cli}\" hook copilot-approve", (string?)entry["powershell"]);
        Assert.Equal(windows ? $"\"{box.Cli.Replace('\\', '/')}\" hook copilot-approve" : $"\"{box.Cli.Replace("\\", "\\\\")}\" hook copilot-approve", (string?)entry["bash"]);
        Assert.Equal(10, (int?)entry["timeoutSec"]);
        Assert.False(File.Exists(box.P(".copilot", "hooks", "docredock.json")));
        Assert.False(File.Exists(box.P(".codex", "hooks.json")));
    }

    [Fact]
    public void A_second_install_with_auto_approve_changes_nothing_and_status_says_installed()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true, approve: true), box.Environment());
        var before = box.Snapshot();
        var report = box.Service.Install(AgentSandbox.Options(enforce: true, approve: true), box.Environment());
        Assert.Equal(before, box.Snapshot());
        Assert.Empty(Bak(box));
        Assert.All(report.Items, item => { Assert.Equal(AgentAction.None, item.Action); Assert.Equal(AgentItemState.Installed, item.State); });
        Assert.Equal(12, report.Items.Count);
        Assert.All(box.Service.Status(AgentSandbox.Options(), box.Environment()).Items, item => Assert.Equal(item.Item==AgentItem.Hook?AgentItemState.Missing:AgentItemState.Installed, item.State));
        // Approval does not require a restrictive hook.
        var settings = box.Json(".claude", "settings.json");
        Assert.Null(settings["hooks"]);
        Assert.Equal([Rule], settings["permissions"]!["allow"]!.AsArray().Select(node => (string)node!).ToArray());
    }

    private static void SeedSettings(AgentSandbox box, string allow = "\"Bash(git status)\", \"Read\"") =>
        box.Write(box.P(".claude", "settings.json"),
            "{\n  \"theme\": \"dark\",\n  \"permissions\": {\n    \"allow\": [\n      " + allow.Replace(", ", ",\n      ") + "\n    ],\n    \"deny\": [\n      \"Bash(rm *)\"\n    ]\n  }\n}\n");

    [Fact]
    public void Existing_allow_entries_and_settings_survive_with_a_backup_and_uninstall_restores_the_file_exactly()
    {
        using var box = new AgentSandbox();
        SeedSettings(box);
        box.Write(box.P(".codex", "config.toml"), "model = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"y\"\n");
        var before = box.Snapshot();

        var report = box.Service.Install(AgentSandbox.Options(approve: true, clients: [AgentClient.Claude, AgentClient.Codex]), box.Environment());
        Assert.False(report.HasProblems);
        var settings = box.Json(".claude", "settings.json");
        Assert.Equal("dark", (string?)settings["theme"]);
        Assert.Equal(["Bash(git status)", "Read", Rule], settings["permissions"]!["allow"]!.AsArray().Select(node => (string)node!).ToArray());
        Assert.Equal(["Bash(rm *)"], settings["permissions"]!["deny"]!.AsArray().Select(node => (string)node!).ToArray());
        var toml = File.ReadAllText(box.P(".codex", "config.toml"));
        Assert.StartsWith("model = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"y\"\n\n# >>> docredock", toml);
        Assert.Contains("args = [\"mcp\"]\n" + ApprovalLine + "\n# <<< docredock <<<\n", toml);

        // The backups hold the originals even though two items wrote the same file.
        Assert.Equal(before[".claude/settings.json"], File.ReadAllText(box.P(".claude", "settings.json.docredock.bak")));
        Assert.Equal(before[".codex/config.toml"], File.ReadAllText(box.P(".codex", "config.toml.docredock.bak")));

        box.Service.Uninstall(AgentSandbox.Options(clients: [AgentClient.Claude, AgentClient.Codex]), box.Environment());
        Assert.Equal(before[".claude/settings.json"], File.ReadAllText(box.P(".claude", "settings.json")));
        Assert.Equal(before[".codex/config.toml"], File.ReadAllText(box.P(".codex", "config.toml")));
    }

    [Fact]
    public void Fresh_install_with_hook_and_approval_leaves_an_empty_home_after_uninstall_and_pre_existing_empty_files_come_back()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true, approve: true), box.Environment());
        var removed = box.Service.Uninstall(AgentSandbox.Options(), box.Environment());
        Assert.False(removed.HasProblems);
        Assert.Equal(AgentAction.Remove, AgentSandbox.Item(removed, AgentClient.Claude, AgentItem.Approval).Action);
        Assert.Equal(AgentAction.Remove, AgentSandbox.Item(removed, AgentClient.Codex, AgentItem.Approval).Action);
        Assert.Equal(AgentAction.Remove, AgentSandbox.Item(removed, AgentClient.Copilot, AgentItem.Approval).Action);
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));

        box.Write(box.P(".claude", "settings.json"), "{}\n");
        box.Service.Install(AgentSandbox.Options(enforce: true, approve: true, clients: AgentClient.Claude), box.Environment());
        box.Service.Uninstall(AgentSandbox.Options(clients: AgentClient.Claude), box.Environment());
        Assert.Equal("{}\n", File.ReadAllText(box.P(".claude", "settings.json")));
    }

    [Fact]
    public void Our_rule_is_added_once_to_an_allow_list_that_already_has_it_and_removed_without_dropping_a_non_empty_list()
    {
        using var box = new AgentSandbox();
        SeedSettings(box, "\"" + Rule + "\", \"Read\"");
        var report = box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Claude), box.Environment());
        Assert.Equal(AgentAction.None, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Approval).Action);
        Assert.Empty(Bak(box));

        box.Service.Install(AgentSandbox.Options(approve: false, clients: AgentClient.Claude), box.Environment());
        var settings = box.Json(".claude", "settings.json");
        Assert.Equal(["Read"], settings["permissions"]!["allow"]!.AsArray().Select(node => (string)node!).ToArray());
        Assert.NotNull(settings["permissions"]!["deny"]);
    }

    [Fact]
    public void Removing_the_only_rule_removes_the_empty_allow_list_and_permissions_object_but_keeps_other_settings()
    {
        using var box = new AgentSandbox();
        box.Write(box.P(".claude", "settings.json"), "{\n  \"theme\": \"dark\"\n}\n");
        box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Claude), box.Environment());
        Assert.NotNull(box.Json(".claude", "settings.json")["permissions"]);
        box.Service.Install(AgentSandbox.Options(approve: false, clients: AgentClient.Claude), box.Environment());
        Assert.Equal("{\n  \"theme\": \"dark\"\n}\n", File.ReadAllText(box.P(".claude", "settings.json")));
    }

    [Fact]
    public void Auto_approve_false_removes_only_ours_and_null_keeps_what_is_there()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(enforce: true, approve: true), box.Environment());

        // null keeps everything, including the approval, and adds nothing.
        var kept = box.Service.Install(AgentSandbox.Options(), box.Environment());
        Assert.All(kept.Items, item => Assert.Equal(AgentAction.None, item.Action));
        Assert.DoesNotContain(kept.Items, item => item.Item is AgentItem.Hook or AgentItem.Approval);
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Approval));

        var plan = box.Service.Plan(AgentSandbox.Options(approve: false), box.Environment());
        Assert.Equal(3, plan.Items.Count(item => item.Item == AgentItem.Approval && item.Action == AgentAction.Remove));
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Approval));

        var report = box.Service.Install(AgentSandbox.Options(approve: false), box.Environment());
        Assert.False(report.HasProblems);
        Assert.Equal(3, report.Items.Count(item => item.Item == AgentItem.Approval && item.Action == AgentAction.Remove && item.State == AgentItemState.Missing));
        Assert.All(All, client => Assert.Equal(AgentItemState.Missing, State(box, client, AgentItem.Approval)));
        // MCP configuration stays and restrictive hooks remain absent.
        Assert.All(All, client => Assert.Equal(AgentItemState.Missing, State(box, client, AgentItem.Hook)));
        Assert.All(All, client => Assert.Equal(AgentItemState.Installed, State(box, client, AgentItem.Mcp)));
        Assert.Equal(CodexBlock(box, approve: false), File.ReadAllText(box.P(".codex", "config.toml")));
        Assert.False(File.Exists(box.P(".claude","settings.json")));
        Assert.False(File.Exists(box.P(".copilot", "hooks", "docredock-approve.json")));
        Assert.False(File.Exists(box.P(".copilot", "hooks", "docredock.json")));

        // Removing again does nothing.
        var again = box.Service.Install(AgentSandbox.Options(approve: false), box.Environment());
        Assert.DoesNotContain(again.Items, item => item.Item == AgentItem.Approval);
    }

    [Fact]
    public void Retiring_a_legacy_hook_keeps_the_separate_approval_settings()
    {
        using var box=new AgentSandbox();
        foreach(var client in All)AgentDirectInspectionTests.SeedLegacyHook(box,client);
        var report=box.Service.Install(AgentSandbox.Options(approve:true),box.Environment());
        Assert.Equal(3,report.Items.Count(i=>i.Item==AgentItem.Hook&&i.Action==AgentAction.Remove));
        Assert.All(All,client=>Assert.Equal(AgentItemState.Missing,State(box,client,AgentItem.Hook)));
        Assert.All(All,client=>Assert.Equal(AgentItemState.Installed,State(box,client,AgentItem.Approval)));
        Assert.Null(box.Json(".claude","settings.json")["hooks"]);
        Assert.Equal([Rule],box.Json(".claude","settings.json")["permissions"]!["allow"]!.AsArray().Select(n=>(string)n!).ToArray());
        Assert.False(File.Exists(box.P(".codex","hooks.json")));
        Assert.False(File.Exists(box.P(".copilot","hooks","docredock.json")));
    }

    [Fact]
    public void Installing_the_mcp_item_keeps_an_approval_line_unless_approval_is_removed()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Codex), box.Environment());
        var moved = box.Environment(box.OtherCli);

        Assert.Equal(AgentItemState.Outdated, State(box, AgentClient.Codex, AgentItem.Mcp, moved));
        Assert.Equal(AgentItemState.Outdated, State(box, AgentClient.Codex, AgentItem.Approval, moved));
        // A plain install updates the command and keeps the approval line.
        var report = box.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), moved);
        Assert.Equal(AgentAction.Update, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp).Action);
        Assert.Equal(CodexBlock(box, approve: true, box.OtherCli), File.ReadAllText(box.P(".codex", "config.toml")));
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Approval, moved));

        // Without an approval line a plain install does not add one.
        using var plain = new AgentSandbox();
        plain.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), plain.Environment());
        plain.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), plain.Environment(plain.OtherCli));
        Assert.Equal(CodexBlock(plain, approve: false, plain.OtherCli), File.ReadAllText(plain.P(".codex", "config.toml")));
        Assert.Equal(AgentItemState.Missing, State(plain, AgentClient.Codex, AgentItem.Approval));

        // Approval is added to an existing block in place, and a hand-edited block is outdated.
        plain.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Codex), plain.Environment(plain.OtherCli));
        Assert.Equal(CodexBlock(plain, approve: true, plain.OtherCli), File.ReadAllText(plain.P(".codex", "config.toml")));
        plain.Write(plain.P(".codex", "config.toml"), CodexBlock(plain, approve: true, plain.OtherCli).Replace("args = [\"mcp\"]", "args = [\"mcp\", \"--x\"]"));
        Assert.Equal(AgentItemState.Outdated, State(plain, AgentClient.Codex, AgentItem.Approval, plain.Environment(plain.OtherCli)));
    }

    [Fact]
    public void An_outdated_approval_is_updated_in_place_when_the_option_is_null_and_never_added_when_missing()
    {
        using var box = new AgentSandbox();
        box.Service.Install(AgentSandbox.Options(approve: true, clients: [AgentClient.Copilot, AgentClient.Codex]), box.Environment());
        var moved = box.Environment(box.OtherCli);
        Assert.Equal(AgentItemState.Outdated, State(box, AgentClient.Copilot, AgentItem.Approval, moved));

        var report = box.Service.Install(AgentSandbox.Options(clients: [AgentClient.Claude, AgentClient.Copilot, AgentClient.Codex]), moved);
        Assert.Equal(AgentAction.Update, AgentSandbox.Item(report, AgentClient.Copilot, AgentItem.Approval).Action);
        Assert.DoesNotContain(report.Items, item => item.Client == AgentClient.Claude && item.Item == AgentItem.Approval);
        Assert.Contains("DocRedock.Cli.exe", File.ReadAllText(box.P(".copilot", "hooks", "docredock-approve.json")));
        Assert.DoesNotContain("docredock-test", File.ReadAllText(box.P(".copilot", "hooks", "docredock-approve.json")));
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Copilot, AgentItem.Approval, moved));
        Assert.False(File.Exists(box.P(".claude", "settings.json")));
    }

    [Fact]
    public void Dry_run_lists_the_approval_changes_and_writes_nothing()
    {
        using var box = new AgentSandbox();
        var plan = box.Service.Plan(AgentSandbox.Options(approve: true), box.Environment());
        Assert.Equal(12, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.Equal(AgentAction.Create, item.Action));
        Assert.Empty(Directory.GetFileSystemEntries(box.Home));

        box.Service.Install(AgentSandbox.Options(approve: true), box.Environment());
        var before = box.Snapshot();
        var uninstall = box.Service.Uninstall(AgentSandbox.Options(dryRun: true), box.Environment());
        Assert.Equal(3, uninstall.Items.Count(item => item.Item == AgentItem.Approval && item.Action == AgentAction.Remove));
        Assert.Equal(before, box.Snapshot());
    }

    [Fact]
    public void A_deny_or_ask_rule_for_the_docredock_tools_is_a_conflict_that_is_left_alone()
    {
        using var box = new AgentSandbox();
        var text = "{\n  \"permissions\": {\n    \"deny\": [\n      \"mcp__docredock__export_html\"\n    ]\n  }\n}\n";
        box.Write(box.P(".claude", "settings.json"), text);
        var report = box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Claude), box.Environment());
        var item = AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Approval);
        Assert.Equal(AgentItemState.Conflict, item.State);
        Assert.True(report.HasProblems);
        Assert.Equal(text, File.ReadAllText(box.P(".claude", "settings.json")));
        Assert.Empty(Bak(box));

        // Unrelated deny rules and similarly named servers are not conflicts.
        box.Write(box.P(".claude", "settings.json"), "{\n  \"permissions\": {\n    \"ask\": [\n      \"mcp__docredock2__x\"\n    ],\n    \"deny\": [\n      \"Bash(rm *)\"\n    ]\n  }\n}\n");
        Assert.Equal(AgentItemState.Installed, AgentSandbox.Item(box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Claude), box.Environment()), AgentClient.Claude, AgentItem.Approval).State);
    }

    [Fact]
    public void Malformed_permissions_and_unparsable_files_are_errors_and_stay_untouched()
    {
        using var box = new AgentSandbox();
        foreach (var text in new[] { "{\"permissions\": []}", "{\"permissions\": {\"allow\": \"Read\"}}", "{\n  // c\n  \"a\": 1\n}\n", "[1]"})
        {
            box.Write(box.P(".claude", "settings.json"), text);
            var report = box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Claude), box.Environment());
            Assert.Equal(AgentItemState.Error, AgentSandbox.Item(report, AgentClient.Claude, AgentItem.Approval).State);
            Assert.Equal(text, File.ReadAllText(box.P(".claude", "settings.json")));
            Assert.Equal(AgentItemState.Error, State(box, AgentClient.Claude, AgentItem.Approval));
        }
        Assert.Empty(Bak(box));
    }

    [Fact]
    public void An_unmanaged_codex_server_is_a_conflict_for_the_approval_too()
    {
        using var box = new AgentSandbox();
        var toml = "[mcp_servers.docredock]\ncommand = \"mine\"\n";
        box.Write(box.P(".codex", "config.toml"), toml);
        var report = box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Codex), box.Environment());
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Mcp).State);
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(report, AgentClient.Codex, AgentItem.Approval).State);
        Assert.Equal(toml, File.ReadAllText(box.P(".codex", "config.toml")));
        var removed = box.Service.Uninstall(AgentSandbox.Options(clients: AgentClient.Codex), box.Environment());
        Assert.Equal(toml, File.ReadAllText(box.P(".codex", "config.toml")));
        Assert.Equal(AgentItemState.Conflict, AgentSandbox.Item(removed, AgentClient.Codex, AgentItem.Approval).State);
    }

    [Fact]
    public void A_foreign_file_at_the_copilot_approval_path_is_outdated_backed_up_and_restored_by_uninstall()
    {
        using var box = new AgentSandbox();
        var mine = "{\"version\":1,\"hooks\":{}}";
        box.Write(box.P(".copilot", "hooks", "docredock-approve.json"), mine);
        Assert.Equal(AgentItemState.Outdated, State(box, AgentClient.Copilot, AgentItem.Approval));
        box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Copilot), box.Environment());
        Assert.Equal(mine, File.ReadAllText(box.P(".copilot", "hooks", "docredock-approve.json.docredock.bak")));
        box.Service.Uninstall(AgentSandbox.Options(clients: AgentClient.Copilot), box.Environment());
        Assert.Equal(mine, File.ReadAllText(box.P(".copilot", "hooks", "docredock-approve.json")));
    }

    [Fact]
    public void Status_reports_each_approval_state_independently_of_the_mcp_item()
    {
        using var box = new AgentSandbox();
        Assert.All(All, client => Assert.Equal(AgentItemState.Missing, State(box, client, AgentItem.Approval)));
        box.Service.Install(AgentSandbox.Options(clients: AgentClient.Codex), box.Environment());
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Mcp));
        Assert.Equal(AgentItemState.Missing, State(box, AgentClient.Codex, AgentItem.Approval));
        // The approval line without the rest of the block's text being ours is outdated, and removing the line leaves the Mcp item.
        box.Service.Install(AgentSandbox.Options(approve: true, clients: AgentClient.Codex), box.Environment());
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Approval));
        box.Service.Install(AgentSandbox.Options(approve: false, clients: AgentClient.Codex), box.Environment());
        Assert.Equal(AgentItemState.Installed, State(box, AgentClient.Codex, AgentItem.Mcp));
        Assert.Equal(AgentItemState.Missing, State(box, AgentClient.Codex, AgentItem.Approval));
    }

    [Fact]
    public void The_environment_overrides_decide_where_the_approval_is_written()
    {
        using var box = new AgentSandbox();
        var claude = Path.Combine(box.Root, "cc");
        var copilot = Path.Combine(box.Root, "cp");
        var codex = Path.Combine(box.Root, "cx");
        var env = box.Environment() with { ClaudeConfigDir = claude, CopilotHome = copilot, CodexHome = codex };
        box.Service.Install(AgentSandbox.Options(approve: true), env);
        Assert.Contains(Rule, File.ReadAllText(Path.Combine(claude, "settings.json")));
        Assert.True(File.Exists(Path.Combine(copilot, "hooks", "docredock-approve.json")));
        Assert.Contains(ApprovalLine, File.ReadAllText(Path.Combine(codex, "config.toml")));
        box.Service.Uninstall(AgentSandbox.Options(), env);
        Assert.False(Directory.Exists(copilot));
        Assert.False(Directory.Exists(codex));
        Assert.False(Directory.Exists(claude));
    }
}

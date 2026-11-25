using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DocRedock.Api.Agents;
using DocRedock.Gui;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

[Collection("Package browser UI")]
public sealed class AgentIntegrationTests
{
    private sealed class NoCommandRunner : IExternalCommandRunner
    {
        public ExternalCommandResult Run(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment) =>
            throw new InvalidOperationException("No external command is expected: " + fileName);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("docredock-agents-ui-").FullName;
        public string Home => Path.Combine(Root, "home");
        public string Cli => Path.Combine(Root, "DocRedock.Cli");
        public Fixture() { Directory.CreateDirectory(Home); File.WriteAllText(Cli, ""); }
        public AgentIntegrationSetup Setup(bool cliFound = true) => new(
            new AgentEnvironment(Home, null, null, null, OperatingSystem.IsWindows(), Cli, [], [], new NoCommandRunner()), cliFound);
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static async Task<AgentIntegrationWindow> OpenAsync(AgentIntegrationSetup setup)
    {
        var window = new AgentIntegrationWindow(setup) { ConfirmOverride = _ => Task.FromResult(true) };
        window.Show();
        await window.WhenIdleAsync();
        return window;
    }

    private static async Task ClickAsync(AgentIntegrationWindow window, Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.WhenIdleAsync();
    }

    [AvaloniaFact]
    public void Main_window_has_the_agent_button_and_opens_the_dialog()
    {
        using var fixture = new Fixture();
        var window = new MainWindow { AgentIntegrationSetupOverride = fixture.Setup() };
        try
        {
            window.Show();
            var button = window.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "AgentIntegrationButton");
            Assert.Equal("AI連携", button.Content?.ToString());
            Assert.Equal("AIツール連携を設定", Avalonia.Automation.AutomationProperties.GetName(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(window.AgentWindow);
            Assert.Equal("AIツール連携", window.AgentWindow!.Title);
            window.AgentWindow.Close();
            Assert.Null(window.AgentWindow);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Statuses_start_unconfigured_and_the_changes_list_the_planned_items()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            foreach (var client in Enum.GetValues<AgentClient>()) Assert.Equal("未設定", window.StatusText(client));
            Assert.Contains("Claude Code MCPサーバー: 作成 ", window.ChangesText);
            Assert.Contains("GitHub Copilot スキル: 作成 ", window.ChangesText);
            Assert.Contains("Codex 指示: 作成 ", window.ChangesText);
            Assert.DoesNotContain("フック", window.ChangesText);
            Assert.True(window.InstallEnabled);
            Assert.False(window.Enforce);
            Assert.False(window.AutoApprove);
            Assert.DoesNotContain("自動承認", window.ChangesText);
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".copilot")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Install_writes_the_files_and_shows_the_result()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            await ClickAsync(window, window.InstallButton);
            Assert.True(File.Exists(Path.Combine(fixture.Home, ".copilot", "mcp-config.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Home, ".codex", "config.toml")));
            Assert.True(File.Exists(Path.Combine(fixture.Home, ".claude.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Home, ".claude", "CLAUDE.md")));
            foreach (var client in Enum.GetValues<AgentClient>()) Assert.Equal("設定済み", window.StatusText(client));
            Assert.StartsWith("設定しました。開いているAIツールを再起動すると反映されます。", window.ResultText);
            Assert.Equal("変更はありません", window.ChangesText);
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".copilot", "hooks", "docredock.json")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Unchecked_client_is_left_alone()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            window.SelectClient(AgentClient.Codex, false);
            await window.WhenIdleAsync();
            Assert.DoesNotContain("Codex", window.ChangesText);
            await ClickAsync(window, window.InstallButton);
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".codex")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
            Assert.Equal("設定済み", window.StatusText(AgentClient.Copilot));
            Assert.Equal("未設定", window.StatusText(AgentClient.Codex));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Nothing_selected_disables_the_buttons()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            foreach (var client in Enum.GetValues<AgentClient>()) window.SelectClient(client, false);
            await window.WhenIdleAsync();
            Assert.False(window.InstallEnabled);
            Assert.False(window.UninstallEnabled);
            Assert.Equal("変更はありません", window.ChangesText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Direct_inspection_is_available_and_no_restrictive_hook_option_is_offered()
    {
        using var fixture=new Fixture();
        var window=await OpenAsync(fixture.Setup());
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(),c=>c.Name=="AgentHookCheckBox");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("先に変換する必要はありません",StringComparison.Ordinal)==true);
            window.Enforce=true;
            Assert.False(window.Enforce);
            await ClickAsync(window,window.InstallButton);
            Assert.False(File.Exists(Path.Combine(fixture.Home,".copilot","hooks","docredock.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home,".codex","hooks.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home,".claude","settings.json")));
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public async Task Approval_checkbox_exists_with_its_note_and_is_unchecked_at_first()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            var check = window.GetVisualDescendants().OfType<CheckBox>().Single(item => item.Name == "AgentApproveCheckBox");
            Assert.Equal("DocRedockのツールを毎回の承認なしで使う", check.Content?.ToString());
            Assert.Equal("DocRedockのツールを毎回の承認なしで使う", Avalonia.Automation.AutomationProperties.GetName(check));
            Assert.NotEqual(true, check.IsChecked);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text).ToArray();
            Assert.Contains("DocRedockのツールは文書を読み、変換結果を一時フォルダーに書くだけです。Claude CodeとCodexは承認を省きます。GitHub Copilotでは、Copilot CLIだけが対象です（VS Codeでは「常に許可」を選んでください）。", texts);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(),c=>c.Name=="AgentHookCheckBox");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Approval_option_writes_the_entries_is_prechecked_afterwards_and_can_be_removed()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            window.AutoApprove = true;
            await window.WhenIdleAsync();
            Assert.Contains("Claude Code 自動承認: 作成 ", window.ChangesText);
            Assert.Contains("GitHub Copilot 自動承認: 作成 ", window.ChangesText);
            Assert.Contains("Codex 自動承認: 作成 ", window.ChangesText);
            await ClickAsync(window, window.InstallButton);
            Assert.Contains("mcp__docredock__*", File.ReadAllText(Path.Combine(fixture.Home, ".claude", "settings.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Home, ".copilot", "hooks", "docredock-approve.json")));
            Assert.Contains("default_tools_approval_mode = \"approve\"", File.ReadAllText(Path.Combine(fixture.Home, ".codex", "config.toml")));
            Assert.Contains("CLI", window.ResultText);
            Assert.Equal("変更はありません", window.ChangesText);
        }
        finally { window.Close(); }

        var reopened = await OpenAsync(fixture.Setup());
        try
        {
            Assert.True(reopened.AutoApprove);
            Assert.False(reopened.Enforce);
            Assert.Equal("変更はありません", reopened.ChangesText);

            reopened.AutoApprove = false;
            await reopened.WhenIdleAsync();
            Assert.Contains("Claude Code 自動承認: 削除 ", reopened.ChangesText);
            Assert.Contains("GitHub Copilot 自動承認: 削除 ", reopened.ChangesText);
            Assert.Contains("Codex 自動承認: 削除 ", reopened.ChangesText);
            Assert.DoesNotContain("MCPサーバー", reopened.ChangesText);
            await ClickAsync(reopened, reopened.InstallButton);
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".claude", "settings.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".copilot", "hooks", "docredock-approve.json")));
            Assert.DoesNotContain("default_tools_approval_mode", File.ReadAllText(Path.Combine(fixture.Home, ".codex", "config.toml")));
            Assert.False(reopened.AutoApprove);
            Assert.Equal("変更はありません", reopened.ChangesText);
            foreach (var client in Enum.GetValues<AgentClient>()) Assert.Equal("設定済み", reopened.StatusText(client));
        }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public async Task Updating_integration_removes_legacy_hooks_while_preserving_approval()
    {
        using var fixture=new Fixture();
        var path=Path.Combine(fixture.Home,".codex","hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var command=System.Text.Json.JsonSerializer.Serialize("docredock hook codex");
        File.WriteAllText(path,"{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"*\",\"hooks\":[{\"type\":\"command\",\"command\":"+command+"}]}]}}");
        var window=await OpenAsync(fixture.Setup());
        try
        {
            window.AutoApprove=true;
            await window.WhenIdleAsync();
            Assert.Contains("Codex 旧制限フック: 削除 ",window.ChangesText);
            await ClickAsync(window,window.InstallButton);
            Assert.False(File.Exists(path));
            Assert.False(window.Enforce);
            Assert.True(window.AutoApprove);
            Assert.Contains("mcp__docredock__*",File.ReadAllText(Path.Combine(fixture.Home,".claude","settings.json")));
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public async Task Uninstall_asks_first_and_removes_everything()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup());
        try
        {
            window.Enforce = true;
            window.AutoApprove = true;
            await window.WhenIdleAsync();
            await ClickAsync(window, window.InstallButton);
            string? asked = null;
            window.ConfirmOverride = message => { asked = message; return Task.FromResult(false); };
            await ClickAsync(window, window.UninstallButton);
            Assert.NotNull(asked);
            Assert.DoesNotContain("GitHub Copilot 旧制限フック: 削除 ", asked);
            Assert.Contains("Codex 自動承認: 削除 ", asked);
            Assert.Equal("設定済み", window.StatusText(AgentClient.Copilot));
            window.ConfirmOverride = _ => Task.FromResult(true);
            await ClickAsync(window, window.UninstallButton);
            foreach (var client in Enum.GetValues<AgentClient>()) Assert.Equal("未設定", window.StatusText(client));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".copilot", "mcp-config.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".copilot", "hooks", "docredock.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".codex", "config.toml")));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".copilot", "hooks", "docredock-approve.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Home, ".claude", "settings.json")));
            Assert.StartsWith("解除しました。", window.ResultText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Conflict_is_shown_and_reported_as_a_partial_result()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Home, ".copilot"));
        File.WriteAllText(Path.Combine(fixture.Home, ".copilot", "mcp-config.json"),
            "{\"mcpServers\":{\"docredock\":{\"type\":\"stdio\",\"command\":\"someone-else\",\"args\":[]}}}");
        var window = await OpenAsync(fixture.Setup());
        try
        {
            Assert.Equal("競合あり", window.StatusText(AgentClient.Copilot));
            Assert.NotEmpty(window.DetailText(AgentClient.Copilot));
            await ClickAsync(window, window.InstallButton);
            Assert.StartsWith("一部を設定できませんでした:", window.ResultText);
            Assert.Equal("設定済み", window.StatusText(AgentClient.Codex));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Missing_cli_blocks_install_but_not_uninstall()
    {
        using var fixture = new Fixture();
        var window = await OpenAsync(fixture.Setup(cliFound: false));
        try
        {
            Assert.Equal("DocRedockのコマンドライン版（DocRedock.Cli）が見つからないため設定できません。配布パッケージのDocRedockから開いてください。", window.CliMissingText);
            Assert.False(window.InstallEnabled);
            Assert.True(window.UninstallEnabled);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Resolver_finds_the_cli_for_each_package_layout()
    {
        string P(params string[] parts) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "docredock-layout", Path.Combine(parts)));

        var windowsGui = P("app", "DocRedock.exe");
        var windowsCli = P("app", "DocRedock.Cli.exe");
        Assert.Equal(windowsCli, AgentCliResolver.Resolve(windowsGui, path => path == windowsCli, isWindows: true));

        var macGui = P("pkg", "DocRedock.app", "Contents", "MacOS", "DocRedock");
        var macCli = P("pkg", "DocRedock.Cli");
        Assert.Equal(macCli, AgentCliResolver.Resolve(macGui, path => path == macCli, isWindows: false));

        var linuxGui = P("usr", "lib", "docredock", "DocRedock");
        var linuxCli = P("usr", "lib", "docredock", "DocRedock.Cli");
        Assert.Equal(linuxCli, AgentCliResolver.Resolve(linuxGui, path => path == linuxCli, isWindows: false));

        Assert.Null(AgentCliResolver.Resolve(linuxGui, _ => false, isWindows: false));
        Assert.Null(AgentCliResolver.Resolve(null, _ => true, isWindows: false));
        // The .exe name is only a Windows candidate.
        Assert.Null(AgentCliResolver.Resolve(windowsGui, path => path == windowsCli, isWindows: false));
    }
}

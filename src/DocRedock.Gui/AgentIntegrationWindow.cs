using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DocRedock.Api.Agents;

namespace DocRedock.Gui;

/// <summary>The environment a dialog works against and whether the CLI to register was found.</summary>
internal sealed record AgentIntegrationSetup(AgentEnvironment Environment, bool CliFound)
{
    /// <summary>The real user environment, with the packaged CLI when it exists next to the running GUI.</summary>
    internal static AgentIntegrationSetup FromProcess()
    {
        var cli = AgentCliResolver.ResolveCurrent();
        // Uninstall does not need the CLI path, so a placeholder (the GUI itself) keeps it usable without the CLI.
        return new(AgentEnvironment.FromProcess(cliPath: cli ?? System.Environment.ProcessPath), cli is not null);
    }
}

/// <summary>Sets up or removes the DocRedock integration for Claude Code, GitHub Copilot and Codex.</summary>
public sealed class AgentIntegrationWindow : Window
{
    private static readonly AgentClient[] Clients = [AgentClient.Claude, AgentClient.Copilot, AgentClient.Codex];
    private readonly AgentIntegrationSetup setup;
    private readonly AgentIntegrationService service = new();
    private readonly Dictionary<AgentClient, CheckBox> clientChecks = [];
    private readonly Dictionary<AgentClient, TextBlock> clientStatus = [];
    private readonly Dictionary<AgentClient, TextBlock> clientDetail = [];
    private readonly CheckBox approveCheck = new() { Name = "AgentApproveCheckBox", Content = "DocRedockのツールを毎回の承認なしで使う" };
    private readonly TextBlock changes = new() { Name = "AgentChangesText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { Name = "AgentResultText", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock cliMissing = new()
    {
        Name = "AgentCliMissingText", TextWrapping = TextWrapping.Wrap, Classes = { "status-error" }, IsVisible = false,
        Text = "DocRedockのコマンドライン版（DocRedock.Cli）が見つからないため設定できません。配布パッケージのDocRedockから開いてください。",
    };
    private readonly Button install = new() { Name = "AgentInstallButton", Content = "設定する", Classes = { "primary" } };
    private readonly Button uninstall = new() { Name = "AgentUninstallButton", Content = "解除する", Classes = { "secondary" } };
    private readonly Button close = new() { Name = "AgentCloseButton", Content = "閉じる", Classes = { "secondary" } };
    private Task running = Task.CompletedTask;
    private bool busy, suppress, selectionChangedWhileBusy;

    public AgentIntegrationWindow() : this(AgentIntegrationSetup.FromProcess()) { }

    internal AgentIntegrationWindow(AgentIntegrationSetup setup)
    {
        this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
        Title = "AIツール連携"; Width = 680; Height = 720; MinWidth = 520; MinHeight = 560;
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Claude Code・GitHub Copilot・Codexに、DocRedockで文書の本文や構造を取り出す連携を設定します。体裁や図の確認など必要に応じて、AIはOffice・PDFアプリや描画ツールで原本を直接開けます。先に変換する必要はありません。設定はこのユーザーのAIツール設定フォルダーに書き込み、いつでも解除できます。",
        });
        panel.Children.Add(cliMissing);
        panel.Children.Add(new TextBlock { Text = "設定するAIツール", Classes = { "section-label" } });
        foreach (var client in Clients)
        {
            var check = new CheckBox { Name = "Agent" + client + "CheckBox", Content = ClientName(client), IsChecked = true };
            var status = new TextBlock { Name = "Agent" + client + "StatusText", Text = "確認中…", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { Name = "Agent" + client + "DetailText", TextWrapping = TextWrapping.Wrap, Classes = { "caption", "muted" }, IsVisible = false, Margin = new Thickness(28, 0, 0, 0) };
            clientChecks[client] = check; clientStatus[client] = status; clientDetail[client] = detail;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(check); Grid.SetColumn(status, 1); row.Children.Add(status);
            panel.Children.Add(new StackPanel { Spacing = 2, Children = { row, detail } });
            check.IsCheckedChanged += (_, _) => OnSelectionChanged();
            Avalonia.Automation.AutomationProperties.SetName(check, ClientName(client) + "を設定する");
        }
        panel.Children.Add(approveCheck);
        panel.Children.Add(new TextBlock
        {
            Text = "DocRedockのツールは文書を読み、変換結果を一時フォルダーに書くだけです。Claude CodeとCodexは承認を省きます。GitHub Copilotでは、Copilot CLIだけが対象です（VS Codeでは「常に許可」を選んでください）。",
            TextWrapping = TextWrapping.Wrap, Classes = { "caption", "muted" }, Margin = new Thickness(28, 0, 0, 0),
        });
        approveCheck.IsCheckedChanged += (_, _) => OnSelectionChanged();
        panel.Children.Add(new TextBlock { Text = "設定すると行われる変更", Classes = { "section-label" } });
        panel.Children.Add(new Border { Classes = { "panel" }, Padding = new Thickness(10), Child = changes });
        panel.Children.Add(result);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { install, uninstall, close } };
        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = panel });
        Content = layout;
        Avalonia.Automation.AutomationProperties.SetName(approveCheck, "DocRedockのツールを毎回の承認なしで使う");
        install.Click += (_, _) => running = InstallAsync();
        uninstall.Click += (_, _) => running = UninstallAsync();
        close.Click += (_, _) => Close();
        cliMissing.IsVisible = !setup.CliFound;
        UpdateEnabled();
        Opened += (_, _) => running = RefreshAsync(updateStatuses: true);
    }

    /// <summary>Confirms an uninstall; tests replace it.</summary>
    internal Func<string, Task<bool>>? ConfirmOverride { get; set; }
    /// <summary>Completes when the running operation has finished.</summary>
    internal Task WhenIdleAsync() => running;
    internal string StatusText(AgentClient client) => clientStatus[client].Text ?? "";
    internal string DetailText(AgentClient client) => clientDetail[client].Text ?? "";
    internal string ChangesText => changes.Text ?? "";
    internal string ResultText => result.IsVisible ? result.Text ?? "" : "";
    internal string CliMissingText => cliMissing.IsVisible ? cliMissing.Text ?? "" : "";
    internal bool InstallEnabled => install.IsEnabled;
    internal bool UninstallEnabled => uninstall.IsEnabled;
    internal bool IsClientSelected(AgentClient client) => clientChecks[client].IsChecked == true;
    internal void SelectClient(AgentClient client, bool selected) => clientChecks[client].IsChecked = selected;
    /// <summary>Deprecated no-op: the restrictive hook is retired and direct reading of original files is allowed.</summary>
    internal bool Enforce { get => false; set { } }
    internal bool AutoApprove { get => approveCheck.IsChecked == true; set => approveCheck.IsChecked = value; }
    internal Button InstallButton => install;
    internal Button UninstallButton => uninstall;

    private static string ClientName(AgentClient client) => client switch
    {
        AgentClient.Claude => "Claude Code", AgentClient.Copilot => "GitHub Copilot", _ => "Codex",
    };

    private static string ItemName(AgentItem item) => item switch
    {
        AgentItem.Mcp => "MCPサーバー", AgentItem.Instructions => "指示", AgentItem.Skill => "スキル", AgentItem.Hook => "旧制限フック", _ => "自動承認",
    };

    private static string ActionName(AgentAction action) => action switch
    {
        AgentAction.Create => "作成", AgentAction.Update => "更新", AgentAction.Remove => "削除", _ => "",
    };

    private string Line(AgentItemReport item) => $"{ClientName(item.Client)} {ItemName(item.Item)}: {ActionName(item.Action)} {DisplayPath(item.Path)}".TrimEnd();

    // A path under the user's home is shown as ~/..., so the list stays readable.
    private string DisplayPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var home = Path.TrimEndingDirectorySeparator(setup.Environment.HomeDirectory);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return home.Length > 0 && path.StartsWith(home, comparison) && path.Length > home.Length && path[home.Length] is '/' or '\\'
            ? "~" + path[home.Length..]
            : path;
    }

    private AgentClient[] Selected() => Clients.Where(IsClientSelected).ToArray();

    private void OnSelectionChanged()
    {
        if (suppress) return;
        if (busy) { selectionChangedWhileBusy = true; return; }
        running = RefreshAsync(updateStatuses: false);
    }

    private void UpdateEnabled()
    {
        var any = Selected().Length > 0;
        foreach (var check in clientChecks.Values) check.IsEnabled = !busy;
        approveCheck.IsEnabled = !busy;
        install.IsEnabled = !busy && any && setup.CliFound;
        uninstall.IsEnabled = !busy && any;
        close.IsEnabled = !busy;
    }

    private void SetBusy(bool value) { busy = value; UpdateEnabled(); }

    private AgentOptions Options(bool dryRun = false) => new(Selected(), null, dryRun, approveCheck.IsChecked == true);

    internal async Task RefreshAsync(bool updateStatuses)
    {
        SetBusy(true);
        try
        {
            if (updateStatuses) ShowStatus(await Task.Run(() => service.Status(AgentOptions.AllClients, setup.Environment)));
            var options = Options();
            ShowChanges(options.Clients.Count > 0 ? await Task.Run(() => service.Plan(options, setup.Environment)) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            changes.Text = "状態を確認できませんでした: " + ex.Message;
        }
        finally { SetBusy(false); }
        if (selectionChangedWhileBusy) { selectionChangedWhileBusy = false; await RefreshAsync(updateStatuses: false); }
    }

    private void ShowChanges(AgentReport? plan)
    {
        var lines = plan?.Items.Where(item => item.Action != AgentAction.None).Select(Line).ToArray() ?? [];
        changes.Text = lines.Length == 0 ? "変更はありません" : string.Join(System.Environment.NewLine, lines);
    }

    private void ShowStatus(AgentReport status)
    {
        suppress = true;
        try
        {
            foreach (var client in Clients)
            {
                var items = status.Items.Where(item => item.Client == client).ToArray();
                // A leftover retired hook counts as needing an update; a missing one is not shown.
                var main = items.Where(item => item.Item != AgentItem.Approval && (item.Item != AgentItem.Hook || item.State != AgentItemState.Missing)).ToArray();
                var problem = items.FirstOrDefault(item => item.State == AgentItemState.Error)
                    ?? items.FirstOrDefault(item => item.State == AgentItemState.Conflict);
                clientStatus[client].Text = main.Length == 0 ? "未設定"
                    : main.Any(item => item.State == AgentItemState.Error) ? "エラー"
                    : main.Any(item => item.State == AgentItemState.Conflict) ? "競合あり"
                    : main.Any(item => item.State == AgentItemState.Outdated) ? "更新が必要"
                    : main.All(item => item.State == AgentItemState.Installed) ? "設定済み"
                    : main.All(item => item.State == AgentItemState.Missing) ? "未設定" : "一部設定済み";
                var detail = problem is null ? null : $"{ItemName(problem.Item)}: {problem.Detail}";
                clientDetail[client].Text = detail; clientDetail[client].IsVisible = detail is not null;
                ToolTip.SetTip(clientStatus[client], detail);
            }
            if (status.Items.Any(item => item.Item == AgentItem.Approval && item.State is AgentItemState.Installed or AgentItemState.Outdated))
                approveCheck.IsChecked = true;
        }
        finally { suppress = false; }
    }

    private async Task InstallAsync()
    {
        if (!setup.CliFound || Selected().Length == 0) return;
        SetBusy(true);
        try
        {
            var options = Options();
            var report = await Task.Run(() => service.Install(options, setup.Environment));
            await ShowOutcomeAsync(report, "設定しました。開いているAIツールを再起動すると反映されます。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowResult("設定できませんでした: " + ex.Message, false);
            SetBusy(false);
        }
    }

    private async Task UninstallAsync()
    {
        if (Selected().Length == 0) return;
        SetBusy(true);
        try
        {
            var options = Options();
            var plan = await Task.Run(() => service.Uninstall(options with { DryRun = true }, setup.Environment));
            var removals = plan.Items.Where(item => item.Action == AgentAction.Remove).Select(Line).ToArray();
            if (removals.Length == 0) { ShowResult("解除する設定はありません。", true); SetBusy(false); return; }
            var message = "次の設定を解除します。\n" + string.Join("\n", removals);
            if (!await (ConfirmOverride?.Invoke(message) ?? ConfirmAsync(message))) { SetBusy(false); return; }
            var report = await Task.Run(() => service.Uninstall(options, setup.Environment));
            await ShowOutcomeAsync(report, "解除しました。開いているAIツールを再起動すると反映されます。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowResult("解除できませんでした: " + ex.Message, false);
            SetBusy(false);
        }
    }

    private async Task ShowOutcomeAsync(AgentReport report, string success)
    {
        var selected = Options();
        var (status, plan) = await Task.Run(() => (
            service.Status(AgentOptions.AllClients, setup.Environment),
            selected.Clients.Count > 0 ? service.Plan(selected, setup.Environment) : null));
        ShowStatus(status); ShowChanges(plan);
        if (report.HasProblems)
        {
            var lines = report.Items.Where(item => item.State is AgentItemState.Conflict or AgentItemState.Error)
                .Select(item => $"{ClientName(item.Client)} {ItemName(item.Item)}: {item.Detail}");
            ShowResult("一部を設定できませんでした:\n" + string.Join("\n", lines), false);
        }
        else
        {
            var notes = report.Notes.Where(note => !note.StartsWith("The DocRedock executable", StringComparison.Ordinal)).Select(TranslateNote);
            ShowResult(string.Join("\n", new[] { success }.Concat(notes)), true);
        }
        SetBusy(false);
    }

    private static string TranslateNote(string note) =>
        note.StartsWith("The enforce option", StringComparison.Ordinal) ? "--enforce（フック）は廃止されました。DocRedockがかつて登録した制限フックがあれば削除し、原本の直接読み取りは許可されたままです。"
        : note.StartsWith("GitHub Copilot: the approval hook", StringComparison.Ordinal) ? "GitHub Copilotの自動承認はCopilot CLIだけが対象です。VS Codeでは「常に許可」を選んでください。"
        : note;

    private void ShowResult(string text, bool success)
    {
        result.Text = text; result.IsVisible = true;
        result.Classes.Remove("status-success"); result.Classes.Remove("status-error");
        result.Classes.Add(success ? "status-success" : "status-error");
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var yes = new Button { Content = "解除する", Classes = { "primary" } };
        var no = new Button { Content = "キャンセル", Classes = { "secondary" } };
        var dialog = new Window { Title = "解除の確認", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }
}

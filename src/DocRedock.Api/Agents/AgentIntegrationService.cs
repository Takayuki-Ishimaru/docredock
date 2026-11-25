using System.Text.Json.Nodes;

namespace DocRedock.Api.Agents;

/// <summary>Configures AI coding assistants (Claude Code, GitHub Copilot, Codex) to read Office documents through DocRedock:
/// registers the MCP server and advisory instructions/skill. Owned legacy restrictive hooks are removed.
/// Everything is written to the user's configuration with a backup of changed shared files, and uninstall removes only
/// what install added.</summary>
public sealed class AgentIntegrationService
{
    /// <summary>The report an install would produce, without writing anything.</summary>
    public AgentReport Plan(AgentOptions options, AgentEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Install(options with { DryRun = true }, environment);
    }

    /// <summary>Adds or updates the integration for the clients. With <see cref="AgentOptions.DryRun"/> nothing is written.</summary>
    public AgentReport Install(AgentOptions options, AgentEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var context = new ItemContext(environment, environment.CliPath, options.DryRun);
        var reports = new List<AgentItemReport>();
        foreach (var item in Items(options.EffectiveClients, environment))
        {
            var wanted = item.Item switch { AgentItem.Hook => false, AgentItem.Approval => options.AutoApprove, _ => true };
            if (wanted == true) { reports.Add(item.Install(context)); continue; }
            // The optional items (hook, approval): false removes ours when it is there, null only keeps an existing one current.
            var current = item.Status(context);
            if (wanted == false)
            {
                if (current.State is AgentItemState.Installed or AgentItemState.Outdated) reports.Add(item.Uninstall(context));
                else if(current.State==AgentItemState.Error)reports.Add(current);
            }
            else if (current.State == AgentItemState.Outdated) reports.Add(item.Install(context));
        }
        var notes = new List<string>();
        if (options.Enforce is not null)
            notes.Add("The enforce option is retired. Owned legacy restrictive hooks are removed; direct Office/PDF inspection uses the assistant's normal permissions.");
        if (options.AutoApprove == true && options.EffectiveClients.Contains(AgentClient.Copilot))
            notes.Add("GitHub Copilot: the approval hook is documented for Copilot CLI only; Copilot in VS Code may still ask (choose Always allow there).");
        return Build("install", options.DryRun, environment, reports, notes);
    }

    /// <summary>Removes what install added for the clients (and what it created, when nothing else is left in it).</summary>
    public AgentReport Uninstall(AgentOptions options, AgentEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var context = new ItemContext(environment, environment.CliPath, options.DryRun);
        // The approval items go first: Codex's approval line lives inside the block the Mcp item removes.
        var items = Items(options.EffectiveClients, environment).ToList();
        var reports = items.Select((item, index) => (Index: index, Item: item)).OrderBy(entry => entry.Item.Item == AgentItem.Approval ? 0 : 1)
            .Select(entry => (entry.Index, Report: entry.Item.Uninstall(context))).OrderBy(entry => entry.Index).Select(entry => entry.Report).ToList();
        return Build("uninstall", options.DryRun, environment, reports, []);
    }

    /// <summary>Reports each item of the clients as installed, missing, outdated, in conflict or unreadable.</summary>
    public AgentReport Status(AgentOptions options, AgentEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var context = new ItemContext(environment, environment.CliPath, DryRun: true);
        var reports = Items(options.EffectiveClients, environment).Select(item => item.Status(context)).ToList();
        return Build("status", false, environment, reports, []);
    }

    private static AgentReport Build(string operation, bool dryRun, AgentEnvironment environment, List<AgentItemReport> reports, List<string> notes)
    {
        if (!File.Exists(environment.CliPath)) notes.Add($"The DocRedock executable does not exist: {environment.CliPath}");
        return new(AgentReport.SchemaName, operation, dryRun, environment.CliPath, File.Exists(environment.CliPath), reports, notes);
    }

    private static string StopAbove(string directory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directory);
        return Path.GetDirectoryName(trimmed) ?? trimmed;
    }

    private static IEnumerable<ManagedItem> Items(IEnumerable<AgentClient> clients, AgentEnvironment env)
    {
        foreach (var client in clients)
        {
            switch (client)
            {
                case AgentClient.Claude:
                    foreach (var item in ClaudeItems(env)) yield return item;
                    break;
                case AgentClient.Copilot:
                    foreach (var item in CopilotItems(env)) yield return item;
                    break;
                default:
                    foreach (var item in CodexItems(env)) yield return item;
                    break;
            }
        }
    }

    private static bool SamePath(string a, string b, AgentEnvironment env) =>
        string.Equals(a, b, env.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static IEnumerable<ManagedItem> ClaudeItems(AgentEnvironment env)
    {
        var config = env.ClaudeConfigDir ?? Path.Combine(env.HomeDirectory, ".claude");
        var stop = StopAbove(config);
        var json = Path.Combine(env.ClaudeConfigDir ?? env.HomeDirectory, ".claude.json");
        yield return new ClaudeMcpItem(json, Path.GetDirectoryName(json)!,
            context => new JsonObject
            {
                ["type"] = "stdio", ["command"] = context.Cli, ["args"] = new JsonArray("mcp"), ["env"] = new JsonObject(),
            },
            (entry, expected) => SamePath(entry["command"]!.GetValue<string>(), expected["command"]!.GetValue<string>(), env)
                && JsonNode.DeepEquals(entry["args"], expected["args"]));
        yield return new BlockItem(AgentClient.Claude, AgentItem.Instructions, Path.Combine(config, "CLAUDE.md"), stop,
            _ => AgentTemplates.InstructionBlock, AgentTemplates.IsBegin, AgentTemplates.IsEnd);
        yield return new OwnFileItem(AgentClient.Claude, AgentItem.Skill, Path.Combine(config, "skills", "docredock-office", "SKILL.md"), stop,
            context => AgentTemplates.Skill(context.Cli));
        yield return new HookEntryItem(AgentClient.Claude, Path.Combine(config, "settings.json"), stop, "Read|Bash|PowerShell",
            context =>
            {
                // Claude Code runs hook commands with bash, or with PowerShell on Windows; PowerShell is chosen explicitly on Windows
                // because a quoted path is not a command there without the & operator.
                var handler = new JsonObject { ["type"] = "command" };
                if (context.Env.IsWindows)
                {
                    handler["shell"] = "powershell";
                    handler["command"] = AgentTemplates.PowerShellHookCommand(context.Cli, "claude");
                }
                else handler["command"] = AgentTemplates.BashHookCommand(context.Cli, "claude", context.Env.IsWindows);
                handler["timeout"] = 10;
                return handler;
            }, "claude");
        yield return new ClaudeAllowItem(Path.Combine(config, "settings.json"), stop);
    }

    private static IEnumerable<ManagedItem> CopilotItems(AgentEnvironment env)
    {
        var home = env.CopilotHome ?? Path.Combine(env.HomeDirectory, ".copilot");
        var stop = StopAbove(home);
        yield return new JsonServerItem(AgentClient.Copilot, Path.Combine(home, "mcp-config.json"), home,
            context => new JsonObject
            {
                ["type"] = "stdio", ["command"] = context.Cli, ["args"] = new JsonArray("mcp"), ["tools"] = new JsonArray("*"),
            },
            (entry, expected) => JsonNode.DeepEquals(entry, expected));
        yield return new OwnFileItem(AgentClient.Copilot, AgentItem.Instructions, Path.Combine(home, "instructions", "docredock.instructions.md"), stop,
            _ => AgentTemplates.CopilotInstructions);
        yield return new OwnFileItem(AgentClient.Copilot, AgentItem.Skill, Path.Combine(home, "skills", "docredock-office", "SKILL.md"), stop,
            context => AgentTemplates.Skill(context.Cli));
        yield return new LegacyCopilotHookItem(Path.Combine(home,"hooks","docredock.json"),stop);
        yield return new OwnFileItem(AgentClient.Copilot, AgentItem.Approval, Path.Combine(home, "hooks", "docredock-approve.json"), stop,
            context => AgentTemplates.CopilotApproveHooks(context.Cli, context.Env.IsWindows));
    }

    private static IEnumerable<ManagedItem> CodexItems(AgentEnvironment env)
    {
        var home = env.CodexHome ?? Path.Combine(env.HomeDirectory, ".codex");
        var stop = StopAbove(home);
        yield return new CodexTomlItem(AgentItem.Mcp, Path.Combine(home, "config.toml"), stop);
        yield return new BlockItem(AgentClient.Codex, AgentItem.Instructions, Path.Combine(home, "AGENTS.md"), stop,
            _ => AgentTemplates.InstructionBlock, AgentTemplates.IsBegin, AgentTemplates.IsEnd);
        yield return new OwnFileItem(AgentClient.Codex, AgentItem.Skill, Path.Combine(env.HomeDirectory, ".agents", "skills", "docredock-office", "SKILL.md"), env.HomeDirectory,
            context => AgentTemplates.Skill(context.Cli));
        yield return new HookEntryItem(AgentClient.Codex, Path.Combine(home, "hooks.json"), stop, "^Bash$",
            context =>
            {
                var handler = new JsonObject { ["type"] = "command", ["command"] = AgentTemplates.BashHookCommand(context.Cli, "codex", context.Env.IsWindows) };
                // Codex does not say which shell runs hooks on Windows, so the Windows command starts PowerShell itself.
                if (context.Env.IsWindows) handler["commandWindows"] = AgentTemplates.WindowsAnyShellHookCommand(context.Cli, "codex");
                handler["timeout"] = 10;
                return handler;
            }, "codex");
        yield return new CodexTomlItem(AgentItem.Approval, Path.Combine(home, "config.toml"), stop);
    }
}

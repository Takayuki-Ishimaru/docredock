using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocRedock.Api.Agents;

namespace DocRedock.Cli;

/// <summary><c>docredock agents install|uninstall|status</c> and the internal <c>docredock hook</c>: argument parsing and printing
/// around <see cref="AgentIntegrationService"/> and <see cref="AgentHook"/>.</summary>
internal static class AgentsCommand
{
    internal const string Help = """

        Configures AI coding assistants to read Office documents through DocRedock: the MCP server, instructions and a skill
        that recommend structured conversion while allowing direct Office/PDF source inspection and layout rendering, and, on request,
        the approval of
        DocRedock's tools so that the assistant does not ask before every call (--auto-approve).
          agents install    Add or update the integration (running it again updates it in place, otherwise changes nothing).
          agents uninstall  Remove what install added, and files or folders it created when nothing else is left in them.
          agents status     Show each item as installed, missing, outdated, conflict or error.
        Options
          --client claude|copilot|codex|all   Which assistants (default: all; repeat or separate with commas).
          --enforce | --no-enforce            Deprecated compatibility options: owned restrictive hooks are always removed.
          --auto-approve | --no-auto-approve  install: pre-approve DocRedock's MCP tools / remove that approval. Without either,
                                              an existing approval is kept (and updated when outdated).
          --dry-run                           install/uninstall: list the planned changes and write nothing.
          --json                              Print the result as JSON (schema docredock.agents.v1).
          --home DIR                          Advanced: configure the user home directory DIR instead of the real one
                                              (CLAUDE_CONFIG_DIR, COPILOT_HOME and CODEX_HOME are then ignored).
          --command PATH                      Advanced: the DocRedock CLI executable to register (default: this executable).
        Written per assistant (user scope; <config> is CLAUDE_CONFIG_DIR or ~/.claude, <copilot> COPILOT_HOME or ~/.copilot,
        <codex> CODEX_HOME or ~/.codex):
          Claude Code  MCP via `claude mcp add --scope user` when claude is on PATH, else mcpServers in <config>/.claude.json
                       (~/.claude.json); <config>/CLAUDE.md block; <config>/skills/docredock-office/SKILL.md; "mcp__docredock__*" in permissions.allow in <config>/settings.json;
                       a retired DocRedock hook is removed from <config>/settings.json
          Copilot      <copilot>/mcp-config.json; <copilot>/instructions/docredock.instructions.md;
                       <copilot>/skills/docredock-office/SKILL.md; a retired DocRedock hook is removed from <copilot>/hooks/docredock.json;
                       approval hook <copilot>/hooks/docredock-approve.json (Copilot CLI only; VS Code still asks: choose Always allow)
          Codex        <codex>/config.toml block; <codex>/AGENTS.md block; ~/.agents/skills/docredock-office/SKILL.md;
                       owned legacy restrictive hook removed from <codex>/hooks.json;
                       approval line default_tools_approval_mode = "approve" inside the config.toml block
        Shared files that exist are copied to <file>.docredock.bak before the first change. Restart the assistants afterwards.
        Exit codes: 0 done; 1 partial (a conflict with an entry DocRedock did not write, or an unreadable or unwritable file;
        the other items are still processed); 2 invalid arguments; 10 unexpected error.
        `docredock hook <claude|copilot|codex>` is the program the retired restrictive hooks ran: it never denies, so a hook that is
        still installed does not block anything (agents install removes it). It reads the hook event on standard input and
        always exits 0. `docredock hook copilot-approve` is the Copilot approval hook: it prints {"behavior":"allow"} for
        DocRedock's tools and {} for everything else.
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IExternalCommandRunner? runner = null)
    {
        string? subcommand = null, home = null, command = null;
        var clients = new List<AgentClient>();
        bool? enforce = null, autoApprove = null;
        bool dryRun = false, json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var equals = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            var key = equals >= 0 ? arg[..equals] : arg;
            string? Value()
            {
                if (equals >= 0) return arg[(equals + 1)..];
                return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            }
            switch (key)
            {
                case "--client":
                    var list = Value();
                    if (list is null) return await UsageAsync(error, "Option '--client' requires a value.");
                    foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        switch (name.ToLowerInvariant())
                        {
                            case "all": clients.AddRange(AgentOptions.AllClients.Clients); break;
                            case "claude": clients.Add(AgentClient.Claude); break;
                            case "copilot": clients.Add(AgentClient.Copilot); break;
                            case "codex": clients.Add(AgentClient.Codex); break;
                            default: return await UsageAsync(error, $"Unknown client '{name}'. Use claude, copilot, codex or all.");
                        }
                    }
                    break;
                case "--home":
                    home = Value();
                    if (home is null) return await UsageAsync(error, "Option '--home' requires a value.");
                    break;
                case "--command":
                    command = Value();
                    if (command is null) return await UsageAsync(error, "Option '--command' requires a value.");
                    break;
                case "--lang":
                    _ = Value();
                    break;
                case "--enforce" or "--no-enforce":
                    if (enforce is { } earlier && earlier != (key == "--enforce")) return await UsageAsync(error, "--enforce and --no-enforce cannot be used together.");
                    enforce = key == "--enforce";
                    break;
                case "--auto-approve" or "--no-auto-approve":
                    if (autoApprove is { } before && before != (key == "--auto-approve")) return await UsageAsync(error, "--auto-approve and --no-auto-approve cannot be used together.");
                    autoApprove = key == "--auto-approve";
                    break;
                case "--dry-run": dryRun = true; break;
                case "--json": json = true; break;
                default:
                    if (arg.StartsWith('-')) return await UsageAsync(error, $"Unknown option '{arg}'.");
                    if (subcommand is not null) return await UsageAsync(error, $"Unexpected argument '{arg}'.");
                    subcommand = arg;
                    break;
            }
        }
        if (subcommand is not ("install" or "uninstall" or "status"))
            return await UsageAsync(error, subcommand is null ? "Use agents install, agents uninstall or agents status." : $"Unknown agents command '{subcommand}'. Use install, uninstall or status.");
        if ((enforce is not null || autoApprove is not null) && subcommand != "install")
            return await UsageAsync(error, "--enforce, --no-enforce, --auto-approve and --no-auto-approve apply to agents install only.");
        if (dryRun && subcommand == "status") return await UsageAsync(error, "--dry-run applies to agents install and uninstall only.");

        var processPath = Environment.ProcessPath;
        if (command is null && (processPath is null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)))
            return await UsageAsync(error, "DocRedock is running through dotnet; pass --command with the path of the DocRedock CLI executable to register.");
        var environment = AgentEnvironment.FromProcess(home, command);
        if (runner is not null) environment = environment with { Runner = runner };
        var options = new AgentOptions(clients, enforce, dryRun, autoApprove);
        var service = new AgentIntegrationService();
        AgentReport report;
        try
        {
            report = subcommand switch
            {
                "install" => service.Install(options, environment),
                "uninstall" => service.Uninstall(options, environment),
                _ => service.Status(options, environment),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(ex.Message);
            return (int)ExitCode.InvalidInput;
        }
        var code = report.HasProblems ? (int)ExitCode.SuccessWithWarnings : 0;
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schema = report.Schema, operation = report.Operation, dry_run = report.DryRun, cli = report.Cli, cli_exists = report.CliExists,
                exit_code = code, items = report.Items, notes = report.Notes,
            }, Json));
            return code;
        }
        await WriteTableAsync(report, output);
        return code;
    }

    private static async Task<int> UsageAsync(TextWriter error, string message)
    {
        await error.WriteLineAsync(message + " Run docredock help agents for usage.");
        return (int)ExitCode.InvalidInput;
    }

    private static async Task WriteTableAsync(AgentReport report, TextWriter output)
    {
        var title = "DocRedock agents " + report.Operation + (report.DryRun ? " (dry run: nothing was written)" : string.Empty);
        await output.WriteLineAsync(title);
        await output.WriteLineAsync($"CLI: {report.Cli}" + (report.CliExists ? string.Empty : "  (this file does not exist)"));
        var rows = report.Items.Where(item => item.Item != AgentItem.Hook || item.State != AgentItemState.Missing).Select(item => (Client: item.Client.ToString().ToLowerInvariant(), Item: item.Item.ToString().ToLowerInvariant(),
            State: item.State.ToString().ToLowerInvariant(), Action: ActionText(report, item), Path: item.Path, Detail: item.Detail)).ToList();
        await output.WriteLineAsync($"{"client",-8} {"item",-13} {"state",-10} {"change",-9} path");
        foreach (var row in rows)
        {
            var line = $"{row.Client,-8} {row.Item,-13} {row.State,-10} {row.Action,-9} {row.Path}";
            await output.WriteLineAsync(line.TrimEnd());
            if (!string.IsNullOrEmpty(row.Detail)) await output.WriteLineAsync($"{string.Empty,-8} {row.Detail}");
        }
        foreach (var note in report.Notes) await output.WriteLineAsync("Note: " + note);
        var next = new StringBuilder();
        if (report.Items.Any(item => item.State is AgentItemState.Conflict or AgentItemState.Error))
            next.Append("Items in conflict or in error were left alone (exit code 1); fix them, then run the command again. ");
        if (report.Operation == "install" && !report.DryRun && report.Items.Any(item => item.Action != AgentAction.None))
            next.Append("Restart the assistants to load the changes.");
        if (report.Operation == "status" && report.Items.Any(item => item.State is AgentItemState.Missing or AgentItemState.Outdated && item.Item is not AgentItem.Approval && (item.Item != AgentItem.Hook || item.State == AgentItemState.Outdated)))
            next.Append("Run docredock agents install to add or update the missing items and remove retired hooks (the approval only with --auto-approve).");
        if (next.Length > 0) await output.WriteLineAsync("Next: " + next.ToString().TrimEnd());
    }

    private static string ActionText(AgentReport report, AgentItemReport item) => item.Action switch
    {
        AgentAction.Create => report.DryRun ? "would add" : "added",
        AgentAction.Update => report.DryRun ? "would update" : "updated",
        AgentAction.Remove => report.DryRun ? "would remove" : "removed",
        _ => report.Operation == "status" ? string.Empty : "unchanged",
    };

    /// <summary><c>docredock hook copilot-approve</c>: reads the event and always prints a decision ({} when not allowing).</summary>
    private static async Task RunApproveAsync(TextReader? input, TextWriter output, Stream? outputStream)
    {
        var text = string.Empty;
        try
        {
            if (input is not null) text = await input.ReadToEndAsync();
            else
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                text = await reader.ReadToEndAsync();
            }
        }
        catch (Exception)
        {
            // An unreadable event is not approved.
        }
        var result = AgentHook.ApproveCopilot(text);
        if (outputStream is null && input is null) outputStream = Console.OpenStandardOutput();
        if (outputStream is null) await output.WriteAsync(result);
        else
        {
            await outputStream.WriteAsync(new UTF8Encoding(false).GetBytes(result));
            await outputStream.FlushAsync();
        }
    }

    /// <summary><c>docredock hook &lt;client&gt;</c>: reads the event, prints the decision, never fails.</summary>
    public static async Task<int> RunHookAsync(string[] args, TextReader? input, TextWriter output, TextWriter error, Stream? outputStream = null)
    {
        try
        {
            var name = args.FirstOrDefault(arg => !arg.StartsWith('-'))?.ToLowerInvariant();
            if (name == "copilot-approve")
            {
                await RunApproveAsync(input, output, outputStream);
                return 0;
            }
            AgentClient? client = name switch { "claude" => AgentClient.Claude, "copilot" => AgentClient.Copilot, "codex" => AgentClient.Codex, _ => null };
            if (client is null)
            {
                await error.WriteLineAsync("Use docredock hook claude|copilot|codex.");
                return 0;
            }
            string text;
            if (input is not null) text = await input.ReadToEndAsync();
            else
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                text = await reader.ReadToEndAsync();
            }
            var cli = Environment.ProcessPath is { Length: > 0 } path && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? path : "docredock";
            var result = AgentHook.Run(client.Value, text, cli);
            if (result.Length > 0)
            {
                // Hook clients expect UTF-8 whatever the console code page is.
                if (outputStream is null && input is null) outputStream = Console.OpenStandardOutput();
                if (outputStream is null) await output.WriteAsync(result);
                else
                {
                    var bytes = new UTF8Encoding(false).GetBytes(result);
                    await outputStream.WriteAsync(bytes);
                    await outputStream.FlushAsync();
                }
            }
        }
        catch (Exception)
        {
            // A hook must never fail: Copilot denies the tool when it does.
        }
        return 0;
    }
}

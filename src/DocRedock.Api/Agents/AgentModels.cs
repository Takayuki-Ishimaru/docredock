using System.Diagnostics;

namespace DocRedock.Api.Agents;

/// <summary>An AI coding assistant DocRedock can configure.</summary>
public enum AgentClient { Claude, Copilot, Codex }

/// <summary>One thing the integration writes for a client.</summary>
public enum AgentItem { Mcp, Instructions, Skill, Hook, Approval }

/// <summary>What an item looks like on disk: ours and current, absent, ours but for another CLI or template text, a
/// same-named entry we did not write, or unreadable.</summary>
public enum AgentItemState { Installed, Missing, Outdated, Conflict, Error }

/// <summary>What an install or uninstall did (or, in a dry run, would do) to an item.</summary>
public enum AgentAction { None, Create, Update, Remove }

/// <summary>What to do and for which clients.</summary>
/// <param name="Clients">The clients to configure; empty means all of them.</param>
/// <param name="Enforce">Deprecated compatibility option. Owned legacy restrictive hooks are always removed, including when null or true.</param>
/// <param name="DryRun">Report the planned changes without writing anything.</param>
/// <param name="AutoApprove">Pre-approval of DocRedock's MCP tools (the Approval item): true adds or keeps it, false removes
/// ours when present, null keeps whatever is there (an existing one is updated in place when outdated, none is added).</param>
public sealed record AgentOptions(IReadOnlyList<AgentClient> Clients, bool? Enforce = null, bool DryRun = false, bool? AutoApprove = null)
{
    /// <summary>Options for every client.</summary>
    public static AgentOptions AllClients { get; } = new([AgentClient.Claude, AgentClient.Copilot, AgentClient.Codex]);

    internal IReadOnlyList<AgentClient> EffectiveClients => Clients is { Count: > 0 } ? Clients.Distinct().ToArray() : AllClients.Clients;
}

/// <summary>The result for one item of one client. <see cref="State"/> is the state after the operation (what a dry run
/// would leave), or the current state for a status call.</summary>
public sealed record AgentItemReport(AgentClient Client, AgentItem Item, AgentItemState State, AgentAction Action, string? Path, string? Detail);

/// <summary>The result of a plan, install, uninstall or status call.</summary>
public sealed record AgentReport(string Schema, string Operation, bool DryRun, string Cli, bool CliExists,
    IReadOnlyList<AgentItemReport> Items, IReadOnlyList<string> Notes)
{
    /// <summary>The schema name of the JSON form.</summary>
    public const string SchemaName = "docredock.agents.v1";

    /// <summary>True when an item is in conflict or could not be read or written (a partial result).</summary>
    public bool HasProblems => Items.Any(item => item.State is AgentItemState.Conflict or AgentItemState.Error);
}

/// <summary>The result of an external command.</summary>
public sealed record ExternalCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs external programs (the claude command line); replaced by a fake in tests.</summary>
public interface IExternalCommandRunner
{
    /// <summary>Runs <paramref name="fileName"/> with the arguments and the environment overrides (a null value removes the variable).</summary>
    ExternalCommandResult Run(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment);
}

/// <summary>Runs a real process with a timeout.</summary>
public sealed class ProcessCommandRunner : IExternalCommandRunner
{
    /// <inheritdoc />
    public ExternalCommandResult Run(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            foreach (var (key, value) in environment)
            {
                if (value is null) info.Environment.Remove(key); else info.Environment[key] = value;
            }
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return new(-1, string.Empty, "The command timed out after 60 seconds.");
            }
            process.WaitForExit();
            return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(-1, string.Empty, ex.Message);
        }
    }
}

/// <summary>Where the assistants keep their configuration, and what the integration may call.</summary>
/// <param name="HomeDirectory">The user home directory.</param>
/// <param name="ClaudeConfigDir">CLAUDE_CONFIG_DIR, when set.</param>
/// <param name="CopilotHome">COPILOT_HOME, when set.</param>
/// <param name="CodexHome">CODEX_HOME, when set.</param>
/// <param name="IsWindows">Whether to write Windows forms of hook commands.</param>
/// <param name="CliPath">The absolute path of the DocRedock CLI executable to register.</param>
/// <param name="PathDirectories">The directories of PATH, in order, where the claude command is looked up.</param>
/// <param name="PathExtensions">The executable extensions (PATHEXT) tried on Windows.</param>
/// <param name="Runner">Runs the claude command line.</param>
public sealed record AgentEnvironment(string HomeDirectory, string? ClaudeConfigDir, string? CopilotHome, string? CodexHome,
    bool IsWindows, string CliPath, IReadOnlyList<string> PathDirectories, IReadOnlyList<string> PathExtensions, IExternalCommandRunner Runner)
{
    /// <summary>The environment of the running process; <paramref name="home"/> and <paramref name="cliPath"/> replace the real
    /// home directory and the running executable.</summary>
    public static AgentEnvironment FromProcess(string? home = null, string? cliPath = null)
    {
        static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? Path.GetFullPath(value) : null;
        var windows = OperatingSystem.IsWindows();
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => item.Trim('"')).ToArray();
        var extensions = windows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var resolvedHome = home is { Length: > 0 } ? Path.GetFullPath(home) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(resolvedHome, home is null ? Variable("CLAUDE_CONFIG_DIR") : null, home is null ? Variable("COPILOT_HOME") : null,
            home is null ? Variable("CODEX_HOME") : null, windows, Path.GetFullPath(cliPath ?? Environment.ProcessPath ?? "docredock"),
            pathDirectories, extensions, new ProcessCommandRunner());
    }
}

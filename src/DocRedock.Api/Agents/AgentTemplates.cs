namespace DocRedock.Api.Agents;

/// <summary>The embedded texts the integration writes, and the quoting of the CLI path in hook commands.</summary>
internal static class AgentTemplates
{
    public const string BeginMarker = "<!-- docredock:begin";
    public const string EndMarker = "<!-- docredock:end -->";

    private static string Resource(string name)
    {
        using var stream = typeof(AgentTemplates).Assembly.GetManifestResourceStream("DocRedock.Api.Agents." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The instruction block with its markers, without a final line break.</summary>
    public static string InstructionBlock { get; } = Resource("instructions.md").TrimEnd('\n');

    /// <summary>The instruction block without its marker lines.</summary>
    public static string InstructionBody { get; } = string.Join('\n', InstructionBlock.Split('\n')[1..^1]);

    /// <summary>The Copilot instructions file: frontmatter, then the instruction text.</summary>
    public static string CopilotInstructions { get; } = "---\napplyTo: '**'\n---\n\n" + InstructionBody + "\n";

    public static string Skill(string cli) => Resource("skill.md").Replace("{{DOCREDOCK_CLI}}", cli, StringComparison.Ordinal);

    public static bool IsBegin(string line) => line.StartsWith(BeginMarker, StringComparison.Ordinal);
    public static bool IsEnd(string line) => line.TrimEnd() == EndMarker;

    public const string TomlBegin = "# >>> docredock (managed by `docredock agents`; remove with `docredock agents uninstall`) >>>";
    public const string TomlEnd = "# <<< docredock <<<";
    public static bool IsTomlBegin(string line) => line.StartsWith("# >>> docredock", StringComparison.Ordinal);
    public static bool IsTomlEnd(string line) => line.TrimEnd() == TomlEnd;

    /// <summary>The managed block of config.toml; a TOML literal string for the path unless it contains a single quote.</summary>
    public static string TomlBlock(string cli, bool approve = false)
    {
        var value = cli.Contains('\'', StringComparison.Ordinal)
            ? "\"" + cli.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + cli + "'";
        return $"{TomlBegin}\n[mcp_servers.docredock]\ncommand = {value}\nargs = [\"mcp\"]\n" + (approve ? TomlApprovalLine + "\n" : string.Empty) + TomlEnd;
    }

    /// <summary>The line inside the managed block that makes Codex run DocRedock's tools without asking (approval modes: auto, prompt, writes, approve).</summary>
    public const string TomlApprovalLine = "default_tools_approval_mode = \"approve\"";

    /// <summary>The Copilot hooks file that approves DocRedock's tools (a permissionRequest hook).</summary>
    public static string CopilotApproveHooks(string cli, bool windows) => AgentFiles.Pretty(new System.Text.Json.Nodes.JsonObject
    {
        ["version"] = 1,
        ["hooks"] = new System.Text.Json.Nodes.JsonObject
        {
            ["permissionRequest"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "command",
                ["bash"] = BashHookCommand(cli, "copilot-approve", windows),
                ["powershell"] = PowerShellHookCommand(cli, "copilot-approve"),
                ["timeoutSec"] = 10,
            }),
        },
    }) + "\n";

    /// <summary>The path as one word of a bash command: in double quotes, with the characters bash expands escaped.</summary>
    public static string BashQuote(string path, bool windows = false) =>
        "\"" + (windows ? path.Replace("\\", "/", StringComparison.Ordinal) : path.Replace("\\", "\\\\", StringComparison.Ordinal)).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal) + "\"";

    /// <summary>The path as one PowerShell word: in double quotes, with the characters PowerShell expands escaped.</summary>
    public static string PowerShellQuote(string path) =>
        "\"" + path.Replace("`", "``", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal).Replace("$", "`$", StringComparison.Ordinal) + "\"";

    /// <summary>A command line that runs the hook for a client with the shell the assistant uses on this system.</summary>
    public static string BashHookCommand(string cli, string client, bool windows = false) => $"{BashQuote(cli, windows)} hook {client}";
    public static string PowerShellHookCommand(string cli, string client) => $"& {PowerShellQuote(cli)} hook {client}";

    /// <summary>A Windows command line that works whatever shell runs it (cmd or PowerShell): it starts PowerShell explicitly.</summary>
    public static string WindowsAnyShellHookCommand(string cli, string client) =>
        $"powershell.exe -NoProfile -NonInteractive -Command \"& '{cli.Replace("'", "''", StringComparison.Ordinal)}' hook {client}\"";
}

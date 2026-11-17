using System.Text.Json.Nodes;
using DocRedock.Api.Agents;

namespace DocRedock.Tests.Agents;

/// <summary>Records the calls instead of running them.</summary>
internal sealed class FakeRunner : IExternalCommandRunner
{
    public List<(string File, string[] Arguments, Dictionary<string, string?> Environment)> Calls { get; } = [];
    public int ExitCode { get; set; }

    public ExternalCommandResult Run(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        Calls.Add((fileName, arguments.ToArray(), new Dictionary<string, string?>(environment)));
        return new(ExitCode, string.Empty, ExitCode == 0 ? string.Empty : "boom");
    }
}

/// <summary>A temporary home directory with a fake DocRedock executable, and an environment that points at it.</summary>
internal sealed class AgentSandbox : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docredock-agents-").FullName;
    public string Home => Path.Combine(Root, "home");
    public string Cli { get; }
    public string OtherCli { get; }
    public string BinDirectory => Path.Combine(Root, "bin");
    public FakeRunner Runner { get; } = new();
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    public string? ClaudeConfigDir { get; init; }
    public string? CopilotHome { get; init; }
    public string? CodexHome { get; init; }
    public AgentIntegrationService Service { get; } = new();

    public AgentSandbox()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(BinDirectory);
        Cli = Path.Combine(Root, "bin", "docredock-test.exe");
        OtherCli = Path.Combine(Root, "moved", "DocRedock.Cli.exe");
        File.WriteAllText(Cli, "x");
    }

    public void Dispose() => Directory.Delete(Root, true);

    /// <summary>Puts a fake claude executable on PATH.</summary>
    public AgentSandbox WithClaudeOnPath()
    {
        var name = IsWindows ? "claude.EXE" : "claude";
        File.WriteAllText(Path.Combine(BinDirectory, name), "x");
        pathDirectories = [BinDirectory];
        return this;
    }

    private string[] pathDirectories = [];

    public AgentEnvironment Environment(string? cli = null) => new(Home, ClaudeConfigDir, CopilotHome, CodexHome, IsWindows, cli ?? Cli,
        pathDirectories, IsWindows ? [".EXE"] : [], Runner);

    public static AgentOptions Options(bool? enforce = null, bool dryRun = false, bool? approve = null, params AgentClient[] clients) => new(clients, enforce, dryRun, approve);

    public string P(params string[] parts) => Path.Combine([Home, .. parts]);

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
    }

    /// <summary>Every file under the home with its bytes, to compare before and after.</summary>
    public SortedDictionary<string, string> Snapshot() => new(Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Home, path).Replace('\\', '/'), File.ReadAllText), StringComparer.Ordinal);

    public JsonNode Json(params string[] parts) => JsonNode.Parse(File.ReadAllText(P(parts)))!;

    public static AgentItemReport Item(AgentReport report, AgentClient client, AgentItem item) =>
        report.Items.Single(candidate => candidate.Client == client && candidate.Item == item);
}

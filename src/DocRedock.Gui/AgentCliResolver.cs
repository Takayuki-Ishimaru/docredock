namespace DocRedock.Gui;

/// <summary>Finds the command-line executable that AI tools are told to start, starting from the running GUI executable.</summary>
internal static class AgentCliResolver
{
    /// <summary>The candidate paths in the order they are tried. Release packages keep the CLI next to the GUI (Windows, Linux)
    /// or next to the .app bundle (macOS: DocRedock.app/Contents/MacOS/DocRedock).</summary>
    internal static IReadOnlyList<string> Candidates(string guiExecutablePath, bool isWindows)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(guiExecutablePath));
        if (string.IsNullOrEmpty(folder)) return [];
        var list = new List<string>();
        if (isWindows) list.Add(Path.Combine(folder, "DocRedock.Cli.exe"));
        list.Add(Path.Combine(folder, "DocRedock.Cli"));
        // macOS: the GUI is <dir>/DocRedock.app/Contents/MacOS/DocRedock and the CLI sits next to DocRedock.app.
        list.Add(Path.GetFullPath(Path.Combine(folder, "..", "..", "..", "DocRedock.Cli")));
        return list;
    }

    /// <summary>The first existing candidate, or null (for example in a development build).</summary>
    internal static string? Resolve(string? guiExecutablePath, Func<string, bool> exists, bool isWindows)
    {
        if (string.IsNullOrEmpty(guiExecutablePath)) return null;
        ArgumentNullException.ThrowIfNull(exists);
        foreach (var candidate in Candidates(guiExecutablePath, isWindows))
            if (exists(candidate)) return candidate;
        return null;
    }

    /// <summary>Resolves the CLI for the running process.</summary>
    internal static string? ResolveCurrent() =>
        Resolve(Environment.ProcessPath, File.Exists, OperatingSystem.IsWindows());
}

using System.Diagnostics;

namespace DocRedock.Render.Fonts;

public sealed record SystemFontFile(string Path, int? PreferredFaceIndex = null);

public static class SystemFontCatalog
{
    private const int MaxCatalogFiles = 8192;
    private const int MaxVisitedDirectories = 10_000;
    private static readonly Lazy<IReadOnlyList<SystemFontFile>> CachedFiles =
        new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<SystemFontFile> GetInstalledFontFiles() => CachedFiles.Value;

    private static IReadOnlyList<SystemFontFile> Discover()
    {
        var files = new List<SystemFontFile>();
        if (OperatingSystem.IsLinux())
        {
            var fontConfig = TryFontConfigMatch();
            if (fontConfig is not null) files.Add(fontConfig);
        }

        foreach (var directory in FontDirectories())
        {
            if (files.Count >= MaxCatalogFiles) break;
            AddDirectory(files, directory);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        return files
            .Where(file => seen.Add(file.Path))
            .Take(MaxCatalogFiles)
            .ToArray();
    }

    private static SystemFontFile? TryFontConfigMatch()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "fc-match",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("%{file}\n%{index}\n");
            startInfo.ArgumentList.Add("sans-serif:lang=ja");
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            var lines = output.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || !File.Exists(lines[0])) return null;
            var index = lines.Length > 1 && int.TryParse(lines[1], out var parsed) && parsed >= 0 ? parsed : (int?)null;
            return new SystemFontFile(Path.GetFullPath(lines[0]), index);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FontDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windows)) yield return Path.Combine(windows, "Fonts");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            if (!string.IsNullOrWhiteSpace(userProfile)) yield return Path.Combine(userProfile, "Library", "Fonts");
            yield break;
        }

        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "share", "fonts");
            yield return Path.Combine(userProfile, ".fonts");
        }
    }

    private static void AddDirectory(ICollection<SystemFontFile> output, string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && output.Count < MaxCatalogFiles && visited++ < MaxVisitedDirectories)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (output.Count >= MaxCatalogFiles) break;
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".otc", StringComparison.OrdinalIgnoreCase))
                        output.Add(new SystemFontFile(Path.GetFullPath(file)));
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        var info = new DirectoryInfo(child);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}

namespace DocRedock.Cli;

/// <summary>
/// Stages one or more output files/directories beside their final destination and
/// replaces existing outputs only after the producing operation has succeeded.
/// </summary>
internal sealed class StagedOutputTransaction : IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, string> stagedPaths;
    private bool committed;
    private bool disposed;

    public StagedOutputTransaction(IEnumerable<string> destinations, bool force)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var targets = destinations
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        if (targets.Length == 0) throw new ArgumentException("At least one output path is required.", nameof(destinations));

        var parentDirectories = targets
            .Select(path => Path.GetDirectoryName(path)
                ?? throw new ArgumentException($"Output path has no parent directory: {path}", nameof(destinations)))
            .Distinct(PathComparer)
            .ToArray();
        if (parentDirectories.Length != 1)
            throw new ArgumentException("Staged outputs must share one parent directory.", nameof(destinations));

        foreach (var target in targets)
        {
            if (!force && Exists(target))
                throw new IOException("Output already exists; refusing to overwrite it. Use --force to replace the requested output.");
        }

        Directory.CreateDirectory(parentDirectories[0]);
        StagingRoot = Path.Combine(parentDirectories[0], $".docredock-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(StagingRoot);
        stagedPaths = targets.ToDictionary(
            target => target,
            target => Path.Combine(StagingRoot, Path.GetFileName(target)),
            PathComparer);
    }

    public string StagingRoot { get; }

    public string PathFor(string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        return stagedPaths.TryGetValue(fullPath, out var staged)
            ? staged
            : throw new ArgumentException("The output path is not part of this transaction.", nameof(destination));
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("The staged outputs were already committed.");

        var backups = new Dictionary<string, string>(PathComparer);
        var installed = new List<string>();
        try
        {
            foreach (var target in stagedPaths.Keys)
            {
                if (!Exists(target)) continue;
                var backup = Path.Combine(
                    Path.GetDirectoryName(target)!,
                    $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.backup");
                Move(target, backup);
                backups[target] = backup;
            }

            foreach (var (target, staged) in stagedPaths)
            {
                if (!Exists(staged)) continue;
                Move(staged, target);
                installed.Add(target);
            }

            foreach (var backup in backups.Values) Delete(backup);
            committed = true;
        }
        catch
        {
            foreach (var target in installed.AsEnumerable().Reverse())
                if (Exists(target)) Delete(target);
            foreach (var (target, backup) in backups)
                if (Exists(backup)) Move(backup, target);
            throw;
        }
        finally
        {
            if (committed) CleanupStagingRoot();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CleanupStagingRoot();
    }

    private void CleanupStagingRoot()
    {
        if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, recursive: true);
        else if (File.Exists(StagingRoot)) File.Delete(StagingRoot);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}

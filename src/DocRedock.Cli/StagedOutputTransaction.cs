namespace DocRedock.Cli;

internal interface IStagedOutputFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void MoveFile(string source, string destination);
    void MoveDirectory(string source, string destination);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
}

internal sealed class PhysicalStagedOutputFileSystem : IStagedOutputFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveFile(string source, string destination) => File.Move(source, destination);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}

/// <summary>
/// Stages one or more output files/directories beside their final destination and
/// replaces existing outputs only after the producing operation has succeeded.
/// </summary>
internal sealed class StagedOutputTransaction : IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, string> stagedPaths;
    private readonly HashSet<string> requiredTargets;
    private readonly IStagedOutputFileSystem fileSystem;
    private bool committed;
    private bool disposed;

    public StagedOutputTransaction(
        IEnumerable<string> destinations,
        bool force,
        IEnumerable<string>? optionalDestinations = null,
        IStagedOutputFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        this.fileSystem = fileSystem ?? new PhysicalStagedOutputFileSystem();
        requiredTargets = destinations.Select(Path.GetFullPath).ToHashSet(PathComparer);
        if (requiredTargets.Count == 0) throw new ArgumentException("At least one output path is required.", nameof(destinations));
        var targets = requiredTargets
            .Concat((optionalDestinations ?? []).Select(Path.GetFullPath))
            .Distinct(PathComparer)
            .ToArray();

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

        this.fileSystem.CreateDirectory(parentDirectories[0]);
        StagingRoot = Path.Combine(parentDirectories[0], $".docredock-stage-{Guid.NewGuid():N}");
        this.fileSystem.CreateDirectory(StagingRoot);
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

        var missingStagedOutputs = stagedPaths
            .Where(pair => requiredTargets.Contains(pair.Key) && !Exists(pair.Value))
            .Select(pair => Path.GetFileName(pair.Key))
            .OrderBy(name => name, PathComparer)
            .ToArray();
        if (missingStagedOutputs.Length > 0)
            throw new InvalidOperationException(
                $"Cannot commit because staged output is missing: {string.Join(", ", missingStagedOutputs)}.");

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

            committed = true;
        }
        catch (Exception commitFailure)
        {
            var rollbackFailure = Rollback(installed, backups);
            if (rollbackFailure is not null)
                throw new AggregateException("Committing staged outputs failed and rollback was only partially successful.", commitFailure, rollbackFailure);
            throw;
        }
        finally
        {
            // Cleanup is deliberately outside the data-preservation boundary.  A
            // failed backup/staging deletion must never undo an already committed
            // set of outputs.  Leftovers are harmless and can be removed later.
            if (committed)
            {
                TryCleanupBackups(backups.Values);
                TryCleanupStagingRoot();
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        TryCleanupStagingRoot();
    }

    private Exception? Rollback(IEnumerable<string> installed, IReadOnlyDictionary<string, string> backups)
    {
        var failures = new List<Exception>();
        foreach (var target in installed.Reverse())
        {
            try
            {
                if (Exists(target)) Delete(target);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Failed to remove newly installed output '{target}' during rollback.", exception));
            }
        }
        foreach (var (target, backup) in backups)
        {
            try
            {
                if (Exists(backup)) Move(backup, target);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Failed to restore backup for '{target}' during rollback.", exception));
            }
        }
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Multiple rollback operations failed.", failures),
        };
    }

    private void TryCleanupBackups(IEnumerable<string> backups)
    {
        foreach (var backup in backups)
        {
            try
            {
                if (Exists(backup)) Delete(backup);
            }
            catch
            {
                // The committed target remains authoritative; preserve it even
                // when a best-effort cleanup is blocked by the file system.
            }
        }
    }

    private void TryCleanupStagingRoot()
    {
        try
        {
            if (fileSystem.DirectoryExists(StagingRoot)) fileSystem.DeleteDirectory(StagingRoot, recursive: true);
            else if (fileSystem.FileExists(StagingRoot)) fileSystem.DeleteFile(StagingRoot);
        }
        catch
        {
            // See TryCleanupBackups: cleanup failures are non-transactional.
        }
    }

    private bool Exists(string path) => fileSystem.FileExists(path) || fileSystem.DirectoryExists(path);

    private void Move(string source, string destination)
    {
        if (fileSystem.DirectoryExists(source)) fileSystem.MoveDirectory(source, destination);
        else fileSystem.MoveFile(source, destination);
    }

    private void Delete(string path)
    {
        if (fileSystem.DirectoryExists(path)) fileSystem.DeleteDirectory(path, recursive: true);
        else if (fileSystem.FileExists(path)) fileSystem.DeleteFile(path);
    }
}

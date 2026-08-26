using System.IO.Compression;

namespace DocRedock.RoundTrip;

public sealed record PackResult(string PackagePath, int EntryCount);
public sealed record UnpackResult(string OutputDirectory, string MarkdownPath, string WorkspacePath, int EntryCount);

public static class RoundTripPackage
{
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<PackResult> PackAsync(
        string markdownPath,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        markdownPath = Path.GetFullPath(markdownPath);
        var workspacePath = Path.Combine(Path.GetDirectoryName(markdownPath)!, Path.GetFileNameWithoutExtension(markdownPath) + ".drmd");
        return await PackAsync(markdownPath, workspacePath, packagePath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PackResult> PackAsync(
        string markdownPath,
        string workspacePath,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        markdownPath = Path.GetFullPath(markdownPath);
        workspacePath = Path.GetFullPath(workspacePath);
        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(markdownPath)) throw new FileNotFoundException("Markdown projection was not found.", markdownPath);
        if (File.Exists(packagePath) || Directory.Exists(packagePath)) throw new IOException("Package output already exists.");
        await using var lease = await SidecarContainer.OpenAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var workspace = await RoundTripWorkspace.OpenAsync(lease.RootPath, cancellationToken).ConfigureAwait(false);
        var verification = await workspace.VerifyAsync(markdownPath, requireUnchangedProjection: false, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid) throw new WorkspaceIntegrityException("Workspace must verify before packing.");

        var directory = Path.GetDirectoryName(packagePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(packagePath)}.{Guid.NewGuid():N}.tmp");
        var count = 0;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await AddFileAsync(archive, markdownPath, Path.GetFileName(markdownPath), cancellationToken).ConfigureAwait(false);
                    count++;
                    var workspaceName = Path.GetFileName(workspacePath);
                    foreach (var file in Directory.EnumerateFiles(lease.RootPath, "*", SearchOption.AllDirectories)
                                 .OrderBy(path => Path.GetRelativePath(lease.RootPath, path), StringComparer.Ordinal))
                    {
                        var relative = Path.GetRelativePath(lease.RootPath, file).Replace(Path.DirectorySeparatorChar, '/');
                        await AddFileAsync(archive, file, workspaceName + "/" + relative, cancellationToken).ConfigureAwait(false);
                        count++;
                    }
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, packagePath);
            return new PackResult(packagePath, count);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<UnpackResult> UnpackAsync(
        string packagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        packagePath = Path.GetFullPath(packagePath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("DocRedock package (.drmdpkg) was not found.", packagePath);
        if (Directory.Exists(outputDirectory)) throw new IOException("Unpack output directory already exists.");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new IOException("Output directory has no parent.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            await using var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > 50_000) throw new InvalidDataException("Package exceeds the entry-count limit.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = ValidateEntry(entry, seen);
                if (relative.EndsWith("/", StringComparison.Ordinal)) continue;
                expanded = checked(expanded + entry.Length);
                if (expanded > 1_073_741_824) throw new InvalidDataException("Package exceeds the expanded-size limit.");
                var destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(staging, destination)) throw new InvalidDataException("Package entry escapes the output directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var markdownFiles = Directory.EnumerateFiles(staging, "*.md", SearchOption.TopDirectoryOnly).ToArray();
            var workspaceDirectories = Directory.EnumerateDirectories(staging, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".drmd", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (markdownFiles.Length != 1 || workspaceDirectories.Length != 1)
                throw new InvalidDataException("Package must contain exactly one Markdown file and one DocRedock sidecar.");
            var workspace = await RoundTripWorkspace.OpenAsync(workspaceDirectories[0], cancellationToken).ConfigureAwait(false);
            var verification = await workspace.VerifyAsync(markdownFiles[0], requireUnchangedProjection: false, cancellationToken).ConfigureAwait(false);
            if (!verification.IsValid)
                throw new WorkspaceIntegrityException("Unpacked workspace failed integrity verification: " +
                    string.Join("; ", verification.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
            Directory.Move(staging, outputDirectory);
            return new UnpackResult(
                outputDirectory,
                Path.Combine(outputDirectory, Path.GetFileName(markdownFiles[0])),
                Path.Combine(outputDirectory, Path.GetFileName(workspaceDirectories[0])),
                archive.Entries.Count);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    internal static async Task AddFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicTimestamp;
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    internal static string ValidateEntry(ZipArchiveEntry entry, ISet<string> seen)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(name) ||
            name.Split('/').Any(segment => segment is ".." or "."))
            throw new InvalidDataException("Package contains an unsafe entry path.");
        if (!seen.Add(name)) throw new InvalidDataException("Package contains a duplicate entry path.");
        if (entry.Length > 268_435_456) throw new InvalidDataException("Package entry exceeds the size limit.");
        if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > 100))
            throw new InvalidDataException("Package entry exceeds the compression-ratio limit.");
        return name;
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}

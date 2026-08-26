using System.IO.Compression;

namespace DocRedock.RoundTrip;

/// <summary>Physical representations of a DocRedock sidecar.</summary>
public enum SidecarForm { Directory, Zip }

/// <summary>
/// Owns a temporary extraction when a zip-form sidecar is opened.  Callers must
/// keep the lease alive for every operation that uses <see cref="RootPath"/>.
/// </summary>
public sealed class SidecarLease : IAsyncDisposable
{
    private readonly string? temporaryParent;

    internal SidecarLease(string originalPath, string rootPath, SidecarForm form, string? temporaryParent)
    {
        OriginalPath = originalPath;
        RootPath = rootPath;
        Form = form;
        this.temporaryParent = temporaryParent;
    }

    public string OriginalPath { get; }
    public string RootPath { get; }
    public SidecarForm Form { get; }
    public bool IsTemporary => temporaryParent is not null;

    public ValueTask DisposeAsync()
    {
        if (temporaryParent is not null && Directory.Exists(temporaryParent))
            Directory.Delete(temporaryParent, recursive: true);
        return ValueTask.CompletedTask;
    }
}

public static class SidecarContainer
{
    private const int EntryLimit = 50_000;
    private const long ExpandedSizeLimit = 1_073_741_824;

    public static SidecarForm Detect(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) return SidecarForm.Directory;
        if (!File.Exists(path) || !HasZipSignature(path))
            throw new InvalidDataException("Unrecognized DocRedock container.");

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Any(entry => entry.FullName.Equals("manifest.json", StringComparison.Ordinal)))
            return SidecarForm.Zip;
        if (IsBundleArchive(archive))
            throw new InvalidDataException("Unrecognized DocRedock container.");
        throw new InvalidDataException("Unrecognized DocRedock container.");
    }

    public static bool IsBundle(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !HasZipSignature(path)) return false;
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return IsBundleArchive(archive);
    }

    public static async Task<SidecarLease> OpenAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var form = Detect(path);
        if (form == SidecarForm.Directory)
            return new SidecarLease(path, path, form, null);

        var temporaryParent = Path.Combine(Path.GetTempPath(), "docredock-sidecar", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporaryParent, Path.GetFileNameWithoutExtension(path));
        Directory.CreateDirectory(root);
        try
        {
            await ExtractAsync(path, root, ct).ConfigureAwait(false);
            return new SidecarLease(path, root, form, temporaryParent);
        }
        catch
        {
            if (Directory.Exists(temporaryParent)) Directory.Delete(temporaryParent, recursive: true);
            throw;
        }
    }

    public static async Task<string> PackInPlaceAsync(string directoryPath, string markdownPath, CancellationToken ct = default)
    {
        directoryPath = Path.GetFullPath(directoryPath);
        markdownPath = Path.GetFullPath(markdownPath);
        EnsureDirectory(directoryPath);
        await VerifyDirectoryAsync(directoryPath, markdownPath, ct).ConfigureAwait(false);

        var parent = Path.GetDirectoryName(directoryPath) ?? throw new IOException("Sidecar path has no parent directory.");
        var temporaryZip = Path.Combine(parent, $".{Path.GetFileName(directoryPath)}.{Guid.NewGuid():N}.tmp");
        var backupDirectory = Path.Combine(parent, $".{Path.GetFileName(directoryPath)}.{Guid.NewGuid():N}.bak");
        try
        {
            await CreateZipAsync(directoryPath, temporaryZip, ct).ConfigureAwait(false);
            await VerifyZipAsync(temporaryZip, markdownPath, ct).ConfigureAwait(false);
            Directory.Move(directoryPath, backupDirectory);
            try
            {
                File.Move(temporaryZip, directoryPath);
            }
            catch
            {
                if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath) && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, directoryPath);
                throw;
            }
            TryDeleteDirectory(backupDirectory);
            return directoryPath;
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            if (Directory.Exists(backupDirectory) && !File.Exists(directoryPath) && !Directory.Exists(directoryPath))
                Directory.Move(backupDirectory, directoryPath);
        }
    }

    public static async Task<string> UnpackInPlaceAsync(string zipPath, string markdownPath, CancellationToken ct = default)
    {
        zipPath = Path.GetFullPath(zipPath);
        markdownPath = Path.GetFullPath(markdownPath);
        EnsureZip(zipPath);
        var parent = Path.GetDirectoryName(zipPath) ?? throw new IOException("Sidecar path has no parent directory.");
        var staging = Path.Combine(parent, $".{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(parent, $".{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.bak");
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(zipPath, staging, ct).ConfigureAwait(false);
            await VerifyDirectoryAsync(staging, markdownPath, ct).ConfigureAwait(false);
            File.Move(zipPath, backup);
            try
            {
                Directory.Move(staging, zipPath);
            }
            catch
            {
                if (!File.Exists(zipPath) && !Directory.Exists(zipPath) && File.Exists(backup)) File.Move(backup, zipPath);
                throw;
            }
            TryDeleteFile(backup);
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (File.Exists(backup) && !File.Exists(zipPath) && !Directory.Exists(zipPath)) File.Move(backup, zipPath);
        }
    }

    public static async Task<string> PackToAsync(string directoryPath, string markdownPath, string outputZipPath, CancellationToken ct = default)
    {
        directoryPath = Path.GetFullPath(directoryPath);
        markdownPath = Path.GetFullPath(markdownPath);
        outputZipPath = Path.GetFullPath(outputZipPath);
        EnsureDirectory(directoryPath);
        EnsureDoesNotExist(outputZipPath);
        await VerifyDirectoryAsync(directoryPath, markdownPath, ct).ConfigureAwait(false);
        try
        {
            await CreateZipAsync(directoryPath, outputZipPath, ct).ConfigureAwait(false);
            await VerifyZipAsync(outputZipPath, markdownPath, ct).ConfigureAwait(false);
            return outputZipPath;
        }
        catch
        {
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);
            throw;
        }
    }

    public static async Task<string> UnpackToAsync(string zipPath, string markdownPath, string outputDirectoryPath, CancellationToken ct = default)
    {
        zipPath = Path.GetFullPath(zipPath);
        markdownPath = Path.GetFullPath(markdownPath);
        outputDirectoryPath = Path.GetFullPath(outputDirectoryPath);
        EnsureZip(zipPath);
        EnsureDoesNotExist(outputDirectoryPath);
        var parent = Path.GetDirectoryName(outputDirectoryPath) ?? throw new IOException("Sidecar output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectoryPath)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(zipPath, staging, ct).ConfigureAwait(false);
            await VerifyDirectoryAsync(staging, markdownPath, ct).ConfigureAwait(false);
            Directory.Move(staging, outputDirectoryPath);
            return outputDirectoryPath;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static async Task CreateZipAsync(string directoryPath, string outputZipPath, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(outputZipPath) ?? throw new IOException("Sidecar output path has no parent directory.");
        Directory.CreateDirectory(parent);
        await using var stream = new FileStream(outputZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(directoryPath, path), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > EntryLimit) throw new InvalidDataException("Package exceeds the entry-count limit.");
            var relative = Path.GetRelativePath(directoryPath, file).Replace(Path.DirectorySeparatorChar, '/');
            await RoundTripPackage.AddFileAsync(archive, file, relative, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task VerifyZipAsync(string zipPath, string markdownPath, CancellationToken ct)
    {
        await using var lease = await OpenAsync(zipPath, ct).ConfigureAwait(false);
        await VerifyDirectoryAsync(lease.RootPath, markdownPath, ct).ConfigureAwait(false);
    }

    private static async Task VerifyDirectoryAsync(string directoryPath, string markdownPath, CancellationToken ct)
    {
        var workspace = await RoundTripWorkspace.OpenAsync(directoryPath, ct).ConfigureAwait(false);
        var verification = await workspace.VerifyAsync(markdownPath, requireUnchangedProjection: false, ct).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new WorkspaceIntegrityException("Workspace must verify before converting sidecar form.");
    }

    private static async Task ExtractAsync(string zipPath, string outputDirectory, CancellationToken ct)
    {
        await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > EntryLimit) throw new InvalidDataException("Package exceeds the entry-count limit.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = RoundTripPackage.ValidateEntry(entry, seen);
            if (relative.EndsWith("/", StringComparison.Ordinal)) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > ExpandedSizeLimit) throw new InvalidDataException("Package exceeds the expanded-size limit.");
            var destination = Path.GetFullPath(Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!RoundTripPackage.IsWithin(outputDirectory, destination)) throw new InvalidDataException("Package entry escapes the output directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
    }

    private static bool IsBundleArchive(ZipArchive archive)
    {
        var markdown = archive.Entries.Where(entry => IsRootFile(entry.FullName, ".md")).ToArray();
        var roots = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(name => name.Contains('/', StringComparison.Ordinal))
            .Select(name => name[..name.IndexOf('/', StringComparison.Ordinal)])
            .Where(name => name.EndsWith(".drmd", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return markdown.Length == 1 && roots.Length == 1;
    }

    private static bool IsRootFile(string name, string extension) =>
        !name.Contains('/', StringComparison.Ordinal) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool HasZipSignature(string path)
    {
        Span<byte> bytes = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(bytes) == 4 && bytes.SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new FileNotFoundException("Sidecar directory was not found.", path);
    }

    private static void EnsureZip(string path)
    {
        if (Detect(path) != SidecarForm.Zip) throw new InvalidDataException("Unrecognized DocRedock container.");
    }

    private static void EnsureDoesNotExist(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Sidecar output already exists.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

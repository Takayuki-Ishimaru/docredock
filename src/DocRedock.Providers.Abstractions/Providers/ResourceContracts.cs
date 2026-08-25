using System.Security.Cryptography;

namespace DocRedock.Providers.Abstractions.Providers;

public sealed record ResourceReference(string Value);
public sealed record ResourcePolicy(IReadOnlyList<string> AllowedRoots, long MaxBytes = 268_435_456);
public sealed record ResourceResolution(string Path, string Sha256, long Size, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface ILocalResourceResolver
{
    ValueTask<ResourceResolution> ResolveReadOnlyAsync(
        ResourceReference reference,
        ResourcePolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>Local-only resolver; URI schemes, UNC paths and root escapes are rejected.</summary>
public sealed class LocalResourceResolver : ILocalResourceResolver
{
    public async ValueTask<ResourceResolution> ResolveReadOnlyAsync(
        ResourceReference reference,
        ResourcePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(reference.Value)) throw new InvalidDataException("Resource path is empty.");
        if (Uri.TryCreate(reference.Value, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new UnauthorizedAccessException("Network and data URI resources are disabled.");
        if (reference.Value.StartsWith("\\\\", StringComparison.Ordinal) || reference.Value.StartsWith("//", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("UNC resources are disabled.");

        var path = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : reference.Value);
        var roots = policy.AllowedRoots.Select(Path.GetFullPath).ToArray();
        if (!roots.Any(root => IsWithin(root, path)))
            throw new UnauthorizedAccessException("Resource is outside the allowed roots.");

        RejectLinks(path, roots);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Resource was not found.", path);
        if (info.Length > policy.MaxBytes) throw new InvalidDataException("Resource exceeds the configured size limit.");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            stream.Position = 0;
            return new ResourceResolution(path, hash, info.Length, stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static void RejectLinks(string path, IReadOnlyList<string> roots)
    {
        var root = roots.Where(candidate => IsWithin(candidate, path)).OrderByDescending(candidate => candidate.Length).First();
        FileSystemInfo? current = new FileInfo(path);
        while (current is not null && IsWithin(root, current.FullName))
        {
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Symbolic links and reparse points are disabled.");
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }
}

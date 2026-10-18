using System.Text;

namespace DocRedock.RoundTrip;

/// <summary>Small, platform-neutral helpers for committing files atomically.</summary>
public static class AtomicFile
{
    public static Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
        => WriteCoreAsync(path, content, overwrite: true, cancellationToken);

    internal static Task WriteNewAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => WriteCoreAsync(path, content, overwrite: false, cancellationToken);

    private static async Task WriteCoreAsync(string path, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite) File.Move(temporaryPath, fullPath, overwrite: true);
            else DocRedock.Core.IO.ExclusiveFileSystem.MoveFile(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken = default)
        => WriteAsync(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), cancellationToken);

    public static Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => CopyCoreAsync(sourcePath, destinationPath, overwrite: true, cancellationToken);

    internal static Task CopyNewAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => CopyCoreAsync(sourcePath, destinationPath, overwrite: false, cancellationToken);

    private static async Task CopyCoreAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fullDestination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite) File.Move(temporaryPath, fullDestination, overwrite: true);
            else DocRedock.Core.IO.ExclusiveFileSystem.MoveFile(temporaryPath, fullDestination);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

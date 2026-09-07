namespace DocRedock.Cli;

/// <summary>
/// Thrown when a command's output path would land on one of its own input paths.
/// Derives from <see cref="IOException"/> so it flows through the existing generic
/// IOException handling in <see cref="CliApplication.RunAsync"/> and surfaces as
/// <see cref="ExitCode.InvalidInput"/> without any special-casing there.
/// </summary>
internal sealed class OutputCollidesWithInputException(string message) : IOException(message);

/// <summary>
/// Centralizes the "an output path must never land on one of this command's own
/// inputs" rule so every CLI command enforces it the same way, whether the output
/// goes through <see cref="StagedOutputTransaction"/> or a direct call such as
/// <c>SidecarContainer.PackToAsync</c>/<c>UnpackToAsync</c>.
///
/// The check always runs -- it is never gated by --force. --force means "replace a
/// stale PREVIOUS output"; it has never meant "it is fine to destroy this command's
/// own input", so the two must stay independent even though both are enforced near
/// each other in <see cref="StagedOutputTransaction"/>.
/// </summary>
internal static class OutputCollisionGuard
{
    // macOS and Windows file systems are case-insensitive by default; Linux is
    // case-sensitive. This is intentionally a separate comparer from
    // StagedOutputTransaction's own bookkeeping comparer (Windows-only, used purely
    // to de-duplicate staged paths within a single transaction): colliding with an
    // *input* document must also be caught on a case-insensitive macOS volume,
    // where "Report.DOCX" and "report.docx" are the same file.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Throws <see cref="OutputCollidesWithInputException"/> if any <paramref name="candidates"/>
    /// path resolves to the same location as any <paramref name="protectedInputs"/> path, or to the
    /// immediate parent directory of one. The parent-directory case covers a directory-shaped output
    /// (an unpack destination, a readable ".assets" folder) landing on the directory an input already
    /// lives in: committing such an output backs up and later deletes that whole directory, destroying
    /// the input even though no individual file name literally collided.
    /// </summary>
    public static void EnsureNoCollision(IEnumerable<string> candidates, IEnumerable<string> protectedInputs)
    {
        var inputs = protectedInputs
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(Normalize)
            .ToArray();
        if (inputs.Length == 0) return;

        var inputParents = inputs
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrEmpty(parent))
            .Select(parent => Normalize(parent!))
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var normalizedCandidate = Normalize(candidate);
            var collides = inputs.Any(input => PathsEqual(normalizedCandidate, input)) ||
                inputParents.Any(parent => PathsEqual(normalizedCandidate, parent));
            if (collides)
            {
                throw new OutputCollidesWithInputException(
                    "Output path must differ from the input path; refusing to overwrite the source document even with --force. " +
                    $"Colliding output: {candidate}");
            }

            // A hard link shares its target's (volume/device, file-index/inode) identity but is its
            // own directory entry: it is neither literally the same path nor a symlink whose target
            // resolves elsewhere, so PathsEqual above cannot see it. This check is deliberately last
            // and separate from PathsEqual -- it never changes which candidates collide, only how the
            // less obvious case is explained once one does.
            var identityCollides = inputs.Any(input => SameFileIdentity(normalizedCandidate, input)) ||
                inputParents.Any(parent => SameFileIdentity(normalizedCandidate, parent));
            if (identityCollides)
            {
                throw new OutputCollidesWithInputException(
                    "Output path must differ from the input path; refusing to overwrite the source document even with --force. " +
                    "The output refers to the same file as an input, e.g. through a hard link. " +
                    $"Colliding output: {candidate}");
            }
        }
    }

    private static bool PathsEqual(string normalizedA, string normalizedB)
    {
        if (PathComparer.Equals(normalizedA, normalizedB)) return true;
        return PathComparer.Equals(ResolveReal(normalizedA), ResolveReal(normalizedB));
    }

    // Best-effort, like ResolveReal below: a failure on either side (missing path, unsupported
    // platform/CPU architecture, permissions) simply means this extra check contributes nothing --
    // it can only ever recognize a real collision PathsEqual missed, never manufacture a false one.
    // Directories are compared too where the platform can identify them (Unix `stat`; Windows
    // reports no identity for a directory, see FileIdentity): that is what lets the parent-directory
    // rule above catch an output directory reached through a second path to the same directory
    // (a macOS firmlink such as /System/Volumes/Data/..., a bind mount) that ResolveReal cannot see.
    private static bool SameFileIdentity(string normalizedA, string normalizedB) =>
        Exists(normalizedA) && Exists(normalizedB) &&
        FileIdentity.TryGet(normalizedA, out var identityA) &&
        FileIdentity.TryGet(normalizedB, out var identityB) &&
        identityA == identityB;

    private static bool Exists(string normalizedPath) => File.Exists(normalizedPath) || Directory.Exists(normalizedPath);

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // Best-effort: only a path that already exists on disk can be resolved as a symlink, and a
    // fresh output target usually does not exist yet. Any failure here silently falls back to the
    // plain normalized path -- symlink resolution is a bonus check layered on top of the primary
    // literal-path comparison above, never a precondition for it.
    private static string ResolveReal(string normalizedFullPath)
    {
        try
        {
            if (File.Exists(normalizedFullPath))
            {
                var target = File.ResolveLinkTarget(normalizedFullPath, returnFinalTarget: true);
                if (target is not null) return Normalize(target.FullName);
            }
            else if (Directory.Exists(normalizedFullPath))
            {
                var target = Directory.ResolveLinkTarget(normalizedFullPath, returnFinalTarget: true);
                if (target is not null) return Normalize(target.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return normalizedFullPath;
    }
}

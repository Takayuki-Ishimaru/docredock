using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DocRedock.Tests.Cli;

/// <summary>
/// Creates a hard link for tests that need one, via the same kind of native call
/// <c>DocRedock.Cli.FileIdentity</c> itself relies on to detect one. Hard-link creation can fail
/// even when both paths are otherwise fine -- most commonly because the two paths sit on
/// different volumes/file systems, which never supports hard links -- so callers should skip
/// (not fail) a test when <see cref="TryCreate"/> returns <see langword="false"/>.
/// </summary>
internal static class HardLinkTestHelper
{
    public static bool TryCreate(string existingPath, string newPath)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CreateHardLinkW(newPath, existingPath, IntPtr.Zero);
            return LinkUnix(existingPath, newPath) == 0;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    // POSIX `link(2)` has the same signature and semantics on both macOS and Linux (glibc and
    // musl), unlike `stat`, so a single declaration covers both -- no per-OS entry point needed.
    [DllImport("libc", EntryPoint = "link")]
    private static extern int LinkUnix(string existingPath, string newPath);
}

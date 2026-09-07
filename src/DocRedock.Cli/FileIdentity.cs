using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DocRedock.Cli;

/// <summary>
/// A best-effort, cross-platform identifier for the underlying file entity a path resolves to --
/// the (volume/device, file-index/inode) pair the operating system uses to tell a hard link of an
/// existing file apart from a different file that merely has the same name or content. Two paths
/// that resolve to equal <see cref="FileIdentity"/> values are the same file on disk, however each
/// was reached (its original path, a hard link to it, or a symlink resolved down to its target).
///
/// <see cref="TryGet"/> never throws. A missing path, a permissions failure, a CPU architecture
/// other than x64/arm64, or a missing native entry point on an older C library all degrade to
/// <see langword="false"/>, so <see cref="OutputCollisionGuard"/> can fall back to its existing
/// plain-path/symlink comparison instead of failing the whole command outright.
/// </summary>
internal readonly record struct FileIdentity(ulong Device, ulong Index)
{
    private const int StatBufferSize = 256;

    private static bool IsSupportedArchitecture =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>
    /// Resolves <paramref name="path"/> to its <see cref="FileIdentity"/>. Returns
    /// <see langword="false"/> (with <paramref name="identity"/> left at its default value) on
    /// any failure, including a path that does not exist.
    /// </summary>
    public static bool TryGet(string path, out FileIdentity identity)
    {
        identity = default;
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindows(path, out identity);
            if (OperatingSystem.IsMacOS()) return TryGetMacOs(path, out identity);
            if (OperatingSystem.IsLinux()) return TryGetLinux(path, out identity);
            return false;
        }
        catch
        {
            // Identity comparison is a bonus safety check layered on top of
            // OutputCollisionGuard's primary literal-path/symlink comparison, never a
            // precondition for it: any unexpected native failure degrades to "unknown identity",
            // not a crash.
            identity = default;
            return false;
        }
    }

    // ----------------------------------------------------------------------------------------
    // Windows: kernel32!GetFileInformationByHandle. The identity is the classic NTFS "file ID" --
    // the volume serial number plus the 64-bit file index -- which is exactly what stays equal
    // across hard links to the same file and differs across unrelated files. Directories are out
    // of scope: reading a directory's file ID would need CreateFile with
    // FILE_FLAG_BACKUP_SEMANTICS, and directories cannot be hard-linked, so there is nothing to
    // gain from supporting them here.
    // ----------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindows(string path, out FileIdentity identity)
    {
        identity = default;
        if (!File.Exists(path)) return false;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) return false;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        identity = new FileIdentity(info.VolumeSerialNumber, index);
        return true;
    }

    // Field-for-field mirror of the Win32 BY_HANDLE_FILE_INFORMATION layout, with each FILETIME
    // kept as two DWORDs (rather than folded into one 8-byte field) so the default sequential
    // layout cannot insert 8-byte-alignment padding that the real, DWORD-only native struct does
    // not have.
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    // ----------------------------------------------------------------------------------------
    // macOS (arm64 and x64), both on the 64-bit-inode ABI: st_dev is an int32 at offset 0, st_ino
    // is a uint64 at offset 8. The 256-byte buffer is generous padding -- the real struct is
    // roughly 144 bytes on both architectures -- so an oversized read can never overrun what the
    // native call writes; only the two fields actually consumed need to be ABI-stable, and they
    // are.
    // ----------------------------------------------------------------------------------------

    [SupportedOSPlatform("macos")]
    private static bool TryGetMacOs(string path, out FileIdentity identity)
    {
        identity = default;
        if (!IsSupportedArchitecture) return false;
        var buffer = new byte[StatBufferSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacStatX64(path, buffer)
            : MacStatArm64(path, buffer);
        if (result != 0) return false;
        identity = new FileIdentity(BitConverter.ToUInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
        return true;
    }

    // The 64-bit-inode `stat` ABI is exported under two different symbol names depending on
    // architecture. macOS x64 keeps the plain `stat` symbol bound to the legacy 32-bit-inode
    // layout for binary compatibility, so the 64-bit layout used here lives under the `$INODE64`
    // suffix. macOS arm64 has no legacy 32-bit-inode ABI to stay compatible with, so plain `stat`
    // there is already the 64-bit layout. `path` marshals as a null-terminated UTF-8 string on
    // this platform, which matches what libc's `stat` expects.
    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "stat$INODE64")]
    private static extern int MacStatX64(string path, byte[] statBuffer);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "stat")]
    private static extern int MacStatArm64(string path, byte[] statBuffer);

    // ----------------------------------------------------------------------------------------
    // Linux (glibc and musl), x86_64 and aarch64: st_dev is a uint64 at offset 0, st_ino is a
    // uint64 at offset 8 -- the same offsets on both architectures. glibc versions before 2.33 do
    // not export a plain `stat` symbol (it used to be a compatibility macro around the versioned
    // `__xstat`); a missing entry point there falls back to `__xstat` with the ABI's `_STAT_VER`
    // constant (1 on x86_64, 0 on aarch64). musl always exports `stat` directly, so that fallback
    // never triggers on musl.
    // ----------------------------------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    private static bool TryGetLinux(string path, out FileIdentity identity)
    {
        identity = default;
        if (!IsSupportedArchitecture) return false;
        var buffer = new byte[StatBufferSize];
        int result;
        try
        {
            result = LinuxStat(path, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            var version = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0;
            result = LinuxXStat(version, path, buffer);
        }
        if (result != 0) return false;
        identity = new FileIdentity(BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8));
        return true;
    }

    [SupportedOSPlatform("linux")]
    [DllImport("libc", EntryPoint = "stat")]
    private static extern int LinuxStat(string path, byte[] statBuffer);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", EntryPoint = "__xstat")]
    private static extern int LinuxXStat(int version, string path, byte[] statBuffer);
}

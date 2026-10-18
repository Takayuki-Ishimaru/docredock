using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DocRedock.Core.IO;

/// <summary>Moves closed staging files/directories on the same filesystem without replacing any destination.
/// Unix File.Move/Directory.Move can check existence before a replacing rename, so they do not establish
/// ownership against concurrent writers. Unsupported exclusive rename operations fail without publishing.</summary>
public static class ExclusiveFileSystem
{
    public static void MoveFile(string sourcePath, string destinationPath) => Move(sourcePath, destinationPath, directory: false);
    public static void MoveDirectory(string sourcePath, string destinationPath) => Move(sourcePath, destinationPath, directory: true);

    private static void Move(string sourcePath, string destinationPath, bool directory)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (((File.GetAttributes(source) & FileAttributes.Directory) != 0) != directory)
            throw new IOException(directory ? "The staging source must be a directory." : "The staging source must be a file.");
        if (OperatingSystem.IsWindows())
        {
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite: false);
            return;
        }

        int result;
        try
        {
            if (OperatingSystem.IsMacOS()) result = RenameMac(source, destination, 4); // RENAME_EXCL
            else if (OperatingSystem.IsLinux()) result = RenameLinux(-100, source, -100, destination, 1); // AT_FDCWD, RENAME_NOREPLACE
            else throw new IOException("Exclusive staging moves are unsupported on this operating system.");
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new IOException("The operating system does not provide exclusive staging moves.", exception);
        }
        if (result == 0) return;

        var error = Marshal.GetLastPInvokeError();
        var cause = new Win32Exception(error);
        var message = $"Cannot move '{source}' to '{destination}' without replacing an existing output: {cause.Message}";
        if (error is 1 or 13) throw new UnauthorizedAccessException(message, cause); // EPERM, EACCES
        if (error == 20 || (error == 2 &&
            (!Directory.Exists(Path.GetDirectoryName(source)) || !Directory.Exists(Path.GetDirectoryName(destination)))))
            throw new DirectoryNotFoundException(message);
        if (error == 2)
        {
            if (directory) throw new DirectoryNotFoundException(message);
            throw new FileNotFoundException(message, source, cause);
        }
        throw new IOException(message, cause);
    }

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int RenameMac([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [SupportedOSPlatform("linux")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int RenameLinux(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}

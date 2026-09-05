using System.Text.Json.Serialization;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Api;

public sealed record CapabilityStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("action")] string? Action = null);
public sealed record CapabilityReport(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<CapabilityStatus> Capabilities);

/// <summary>Discovers executable local providers. An invalid explicit choice fails closed.</summary>
public static class PdfRasterizerFactory
{
    public static IPdfRasterizer? Discover(string? explicitPath = null, bool disable = false)
    {
        var path = ProviderPath(explicitPath, disable);
        if (path is null) return null;
        return Path.GetFileNameWithoutExtension(path).Equals("mutool", StringComparison.OrdinalIgnoreCase)
            ? new MutoolPdfRasterizer(path)
            : new PdftoppmPdfRasterizer(path);
    }

    public static CapabilityStatus Describe(string? explicitPath = null, bool disable = false)
    {
        var path = ProviderPath(explicitPath, disable);
        if (path is null)
            return new("pdf-rasterizer", "unavailable", Action: disable
                ? "Enable local rasterizer discovery or configure a path."
                : string.IsNullOrWhiteSpace(explicitPath)
                    ? "Install pdftoppm or mutool, or configure an executable path."
                    : "The configured PDF rasterizer is missing or not executable; correct its path or permissions.");
        return new("pdf-rasterizer", "ready", Path.GetFileNameWithoutExtension(path), path);
    }

    private static string? ProviderPath(string? explicitPath, bool disable) => disable ? null
        : !string.IsNullOrWhiteSpace(explicitPath) ? ResolveExecutable(explicitPath)
        : ResolveExecutable("pdftoppm") ?? ResolveExecutable("mutool");

    internal static string? ResolveExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(value) ? new[] { value + ".exe", value } : new[] { value };
        try
        {
            foreach (var name in names)
            {
                if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
                {
                    if (Executable(name)) return Path.GetFullPath(name);
                    continue;
                }
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Path.IsPathRooted(directory)) continue;
                    var candidate = Path.Combine(directory, name);
                    if (Executable(candidate)) return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static bool Executable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}

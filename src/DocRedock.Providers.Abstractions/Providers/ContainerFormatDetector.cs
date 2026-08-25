using System.IO.Compression;
using System.Text;

namespace DocRedock.Providers.Abstractions.Providers;

/// <summary>
/// BCL-only built-in probe for PDF and OOXML containers. It never extracts, executes,
/// or modifies the input; a separate adapter still performs actual document extraction.
/// </summary>
public sealed class ContainerFormatDetector : IFormatProbe
{
    public ProviderDescriptor Descriptor { get; } = new(
        "docredock.format.container", new Version(0, 2, 0), 1,
        new HashSet<string>(StringComparer.Ordinal) { "probe.pdf", "probe.docx", "probe.xlsx", "probe.pptx" },
        "MIT", "built-in", true);

    public ValueTask<ProbeResult> ProbeAsync(RewindableInput input, ProbeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = input.Stream;
        var magic = ReadPrefix(stream, 5);
        if (magic.SequenceEqual("%PDF-"u8))
            return ValueTask.FromResult(CreatePdf(context));
        if (!IsZipMagic(magic[..Math.Min(4, magic.Length)]))
            return ValueTask.FromResult(ProbeResult.Unsupported(Descriptor.ProviderId, "Input is neither PDF nor a ZIP-based OOXML package."));

        try
        {
            stream.Position -= magic.Length;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            var kind = entries.Contains("word/document.xml") ? "docx"
                : entries.Contains("xl/workbook.xml") ? "xlsx"
                : entries.Contains("ppt/presentation.xml") ? "pptx"
                : null;
            if (kind is null || !entries.Contains("[Content_Types].xml"))
                return ValueTask.FromResult(ProbeResult.Unsupported(Descriptor.ProviderId, "ZIP package does not contain the required OOXML parts."));
            var hasMacros = entries.Any(entry => entry.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase));
            var warnings = ExtensionMismatch(context.FileName, kind).ToList();
            if (hasMacros)
                warnings.Add(new ProbeWarning("MacroEnabled", "A VBA project is present; it will be preserved but never executed."));
            var evidence = new List<ProbeEvidence>
            {
                new("zip", "ZIP magic and OOXML required parts"),
                new("ooxml_part", kind),
            };
            if (hasMacros) evidence.Add(new("macro", "present"));
            return ValueTask.FromResult(new ProbeResult(Descriptor.ProviderId, 1.0, 100,
                evidence,
                warnings, false, false, true));
        }
        catch (InvalidDataException exception)
        {
            return ValueTask.FromResult(new ProbeResult(Descriptor.ProviderId, 0, 0, Array.Empty<ProbeEvidence>(),
                new[] { new ProbeWarning("MalformedZip", exception.Message) }, false, true, false));
        }
    }

    private static ProbeResult CreatePdf(ProbeContext context) => new(
        "docredock.format.container", 1.0, 100,
        new[] { new ProbeEvidence("magic", "%PDF-") }, ExtensionMismatch(context.FileName, "pdf"), false, false, true);

    private static IReadOnlyList<ProbeWarning> ExtensionMismatch(string? fileName, string detected)
    {
        var extension = fileName is null ? null : Path.GetExtension(fileName).TrimStart('.');
        var accepted = detected switch
        {
            "docx" => new[] { "docx", "docm" },
            "xlsx" => new[] { "xlsx", "xlsm" },
            "pptx" => new[] { "pptx", "pptm" },
            _ => new[] { detected },
        };
        return extension is null or "" || accepted.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Array.Empty<ProbeWarning>()
            : new[] { new ProbeWarning("ExtensionMismatch", $"File extension .{extension} does not match detected {detected} container.") };
    }

    private static byte[] ReadPrefix(Stream stream, int length)
    {
        var result = new byte[length];
        var total = 0;
        while (total < length)
        {
            var count = stream.Read(result, total, length - total);
            if (count == 0) break;
            total += count;
        }
        return total == length ? result : result[..total];
    }

    private static bool IsZipMagic(ReadOnlySpan<byte> magic) =>
        magic.SequenceEqual("PK\x03\x04"u8) || magic.SequenceEqual("PK\x05\x06"u8);
}

public sealed record ContainerSecurityLimits(
    int MaxEntries = 50_000,
    long MaxExpandedBytes = 1_073_741_824,
    long MaxSingleEntryBytes = 268_435_456,
    double MaxCompressionRatio = 100.0);
public sealed record SecurityDiagnostic(string Code, string Message);
public sealed record SecurityAssessment(bool IsAllowed, IReadOnlyList<SecurityDiagnostic> Diagnostics)
{
    public static SecurityAssessment Allowed { get; } = new(true, Array.Empty<SecurityDiagnostic>());
}

/// <summary>Read-only ZIP preflight. Call before probes/adapters that parse package payloads.</summary>
public static class ContainerSecurityGate
{
    public static SecurityAssessment Assess(RewindableInput input, ContainerSecurityLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        limits ??= new ContainerSecurityLimits();
        var diagnostics = new List<SecurityDiagnostic>();
        try
        {
            input.Reset();
            var magic = ReadPrefix(input.Stream, 4);
            if (!IsZipMagic(magic)) return SecurityAssessment.Allowed;
            input.Reset();
            using var zip = new ZipArchive(input.Stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > limits.MaxEntries)
                diagnostics.Add(new("ZipEntryLimitExceeded", $"ZIP has {zip.Entries.Count} entries; limit is {limits.MaxEntries}."));
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                if (IsUnsafePath(entry.FullName)) diagnostics.Add(new("ZipPathTraversal", $"Unsafe ZIP entry path: {entry.FullName}"));
                if (entry.Length > limits.MaxSingleEntryBytes)
                    diagnostics.Add(new("ZipEntrySizeExceeded", $"ZIP entry exceeds single-entry limit: {entry.FullName}"));
                try { expanded = checked(expanded + entry.Length); }
                catch (OverflowException) { diagnostics.Add(new("ZipExpandedSizeOverflow", "Expanded ZIP size overflowed Int64.")); }
                if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio))
                    diagnostics.Add(new("ZipCompressionRatioExceeded", $"ZIP entry exceeds compression-ratio limit: {entry.FullName}"));
            }
            if (expanded > limits.MaxExpandedBytes)
                diagnostics.Add(new("ZipExpandedSizeExceeded", $"ZIP expands to {expanded} bytes; limit is {limits.MaxExpandedBytes}."));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("MalformedZip", exception.Message));
        }
        finally { input.Reset(); }
        return diagnostics.Count == 0 ? SecurityAssessment.Allowed : new SecurityAssessment(false, diagnostics);
    }

    private static bool IsUnsafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\')) return true;
        return path.Replace('\\', '/').Split('/').Any(segment => segment is ".." or ".");
    }

    private static byte[] ReadPrefix(Stream stream, int length)
    {
        var result = new byte[length];
        var total = 0;
        while (total < length)
        {
            var count = stream.Read(result, total, length - total);
            if (count == 0) break;
            total += count;
        }

        return total == length ? result : result[..total];
    }

    private static bool IsZipMagic(ReadOnlySpan<byte> magic) =>
        magic.SequenceEqual("PK\x03\x04"u8) || magic.SequenceEqual("PK\x05\x06"u8);
}

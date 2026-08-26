using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocRedock.Worker;

public sealed record WorkerRequest(
    string? Id,
    string? Command,
    string? Path = null,
    string? Tsv = null,
    int? TimeoutMs = null,
    JsonElement? Arguments = null);

public sealed record WorkerResponse(string Id, bool Ok, string Code, object? Result = null, string? Error = null);
public sealed record OcrTsvRegion(string Text, int Page, int Left, int Top, int Width, int Height, double Confidence);
public sealed record OcrTsvSummary(string Text, int RegionCount, double? AverageConfidence, IReadOnlyList<OcrTsvRegion> Regions);
public sealed record ProbeSummary(string Format, bool IsSupported, bool IsZipPackage, IReadOnlyList<string> Evidence, long SizeBytes, string Sha256);
public sealed record PackageMetadata(string Format, long SizeBytes, string Sha256, int EntryCount, long ExpandedBytes, IReadOnlyList<string> Entries);

/// <summary>Bounded local JSON-lines worker. It never invokes a shell/network or discovers providers.</summary>
public static class WorkerHost
{
    private const long DefaultMaxBytes = 200 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return 0;
    }

    public static async ValueTask<WorkerResponse> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (line.Length > 4_194_304) return new("unknown", false, "TOO_LARGE", Error: "Request exceeds the worker protocol limit.");
        WorkerRequest? request;
        try { request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions); }
        catch (JsonException) { return new("unknown", false, "INVALID_JSON", Error: "Request is not valid JSON."); }
        if (request is null || string.IsNullOrWhiteSpace(request.Id)) return new(request?.Id ?? "unknown", false, "INVALID_REQUEST", Error: "Request id is required.");
        if (string.IsNullOrWhiteSpace(request.Command)) return new(request.Id, false, "INVALID_REQUEST", Error: "Command is required.");
        var timeout = request.TimeoutMs is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs.Value, 1, 300_000));
        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        try
        {
            var result = request.Command.Trim().ToLowerInvariant() switch
            {
                "ping" => (object)new { status = "ok", worker = "docredock-worker", protocol = 1 },
                "probe" => await ProbeAsync(request, token).ConfigureAwait(false),
                "extract_metadata" or "metadata" => await MetadataAsync(request, token).ConfigureAwait(false),
                "parse_ocr_tsv" or "ocr_tsv" => await ParseTsvAsync(request, token).ConfigureAwait(false),
                _ => throw new WorkerException("UNSUPPORTED_COMMAND", "Command is not supported.")
            };
            return new(request.Id, true, "OK", result);
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested) { return new(request.Id, false, "TIMEOUT", Error: "Operation timed out."); }
        catch (OperationCanceledException) { return new(request.Id, false, "CANCELLED", Error: "Operation was cancelled."); }
        catch (WorkerException exception) { return new(request.Id, false, exception.Code, Error: exception.Message); }
        catch (InvalidDataException exception) { return new(request.Id, false, "MALFORMED", Error: exception.Message); }
        catch (IOException) { return new(request.Id, false, "IO_ERROR", Error: "Input could not be read."); }
        catch { return new(request.Id, false, "INTERNAL", Error: "Worker failed without exposing input content."); }
    }

    private static ValueTask<object> ProbeAsync(WorkerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var bytes = ReadValidatedPath(request.Path, token); var evidence = new List<string>();
        if (bytes.Length >= 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return ValueTask.FromResult<object>(new ProbeSummary("pdf", true, false, ["pdf-magic"], bytes.LongLength, Sha256(bytes)));
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8)) return ValueTask.FromResult<object>(new ProbeSummary("unknown", false, false, ["unsupported-magic"], bytes.LongLength, Sha256(bytes)));
        using var input = new MemoryStream(bytes, writable: false); using var zip = new ZipArchive(input, ZipArchiveMode.Read); ValidateArchive(zip); var entries = zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal); var format = entries.Contains("word/document.xml") ? "docx" : entries.Contains("xl/workbook.xml") ? "xlsx" : entries.Contains("ppt/presentation.xml") ? "pptx" : "unknown"; evidence.Add("zip-magic"); if (format != "unknown") evidence.Add("ooxml-required-part"); return ValueTask.FromResult<object>(new ProbeSummary(format, format != "unknown", true, evidence, bytes.LongLength, Sha256(bytes)));
    }

    private static ValueTask<object> MetadataAsync(WorkerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var bytes = ReadValidatedPath(request.Path, token); var format = DetectFormat(bytes); var names = new List<string>(); var expanded = 0L;
        if (format is "docx" or "xlsx" or "pptx") { using var input = new MemoryStream(bytes, writable: false); using var zip = new ZipArchive(input, ZipArchiveMode.Read); ValidateArchive(zip); foreach (var entry in zip.Entries.OrderBy(x => x.FullName, StringComparer.Ordinal)) { names.Add(entry.FullName); expanded = checked(expanded + entry.Length); } }
        return ValueTask.FromResult<object>(new PackageMetadata(format, bytes.LongLength, Sha256(bytes), names.Count, expanded, names));
    }

    private static ValueTask<object> ParseTsvAsync(WorkerRequest request, CancellationToken token)
    {
        var tsv = request.Tsv; if (string.IsNullOrEmpty(tsv) && request.Path is not null) tsv = System.Text.Encoding.UTF8.GetString(ReadValidatedPath(request.Path, token));
        if (string.IsNullOrEmpty(tsv)) throw new WorkerException("INVALID_REQUEST", "TSV content or a local TSV path is required."); token.ThrowIfCancellationRequested(); var regions = new List<OcrTsvRegion>();
        foreach (var line in tsv.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')) { token.ThrowIfCancellationRequested(); if (line.Length == 0 || line.StartsWith("level\t", StringComparison.OrdinalIgnoreCase)) continue; var fields = line.Split('\t'); if (fields.Length < 12 || !int.TryParse(fields[0], out var level) || level != 5) continue; if (!int.TryParse(fields[1], out var page) || !int.TryParse(fields[6], out var left) || !int.TryParse(fields[7], out var top) || !int.TryParse(fields[8], out var width) || !int.TryParse(fields[9], out var height) || !double.TryParse(fields[10], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var confidence)) continue; if (!string.IsNullOrWhiteSpace(fields[11])) regions.Add(new(fields[11], page, left, top, width, height, confidence)); }
        var text = string.Join(" ", regions.Select(x => x.Text)); double? average = regions.Count == 0 ? null : regions.Average(x => x.Confidence); return ValueTask.FromResult<object>(new OcrTsvSummary(text, regions.Count, average, regions));
    }

    private static byte[] ReadValidatedPath(string? path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new WorkerException("PATH_INVALID", "A local input path is required."); var full = Path.GetFullPath(path); var root = Path.GetFullPath(Environment.GetEnvironmentVariable("DRMD_WORKER_ROOT") ?? Directory.GetCurrentDirectory()); var relative = Path.GetRelativePath(root, full); if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new WorkerException("PATH_INVALID", "Input path is outside the worker root."); if (!File.Exists(full)) throw new WorkerException("NOT_FOUND", "Input file was not found."); var info = new FileInfo(full); for (FileSystemInfo? current = info; current is not null && !StringComparer.Ordinal.Equals(current.FullName, root); current = current switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null }) if (current.LinkTarget is not null) throw new WorkerException("PATH_INVALID", "Symbolic links are not accepted."); if (info.Length > DefaultMaxBytes) throw new WorkerException("TOO_LARGE", "Input exceeds the worker size limit."); token.ThrowIfCancellationRequested(); return File.ReadAllBytes(full);
    }
    private static string DetectFormat(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith("%PDF-"u8)) return "pdf"; if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8)) return "unknown"; using var input = new MemoryStream(bytes, false); using var zip = new ZipArchive(input, ZipArchiveMode.Read); var names = zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal); return names.Contains("word/document.xml") ? "docx" : names.Contains("xl/workbook.xml") ? "xlsx" : names.Contains("ppt/presentation.xml") ? "pptx" : "zip";
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 50_000) throw new WorkerException("TOO_LARGE", "Package entry count exceeds the worker limit.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Split('/').Any(segment => segment is ".." or ".")) throw new WorkerException("PATH_INVALID", "Package contains an unsafe entry path.");
            expanded = checked(expanded + entry.Length);
            if (entry.Length > 268_435_456 || expanded > 1_073_741_824 || entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > 100)) throw new WorkerException("TOO_LARGE", "Package exceeds worker expansion limits.");
        }
    }
    private sealed class WorkerException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}

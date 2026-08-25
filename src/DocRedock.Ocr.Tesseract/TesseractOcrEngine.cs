using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Ocr.Tesseract;

/// <summary>
/// Local Tesseract provider. It deliberately starts only the explicitly named
/// executable and never uses a shell, URL, or implicit plugin loader.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly string executable;
    private readonly string? cacheDirectory;
    private readonly TimeSpan defaultTimeout;
    private readonly long maxOutputBytes;

    public TesseractOcrEngine(
        string executablePath = "tesseract",
        string? cacheDirectory = null,
        TimeSpan? defaultTimeout = null,
        long maxOutputBytes = 1_048_576)
    {
        executable = executablePath;
        this.cacheDirectory = cacheDirectory;
        this.defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        this.maxOutputBytes = Math.Max(4096, maxOutputBytes);
    }

    public ProviderDescriptor Descriptor { get; } = new(
        "docredock.ocr.tesseract",
        new Version(0, 2, 0),
        1,
        new HashSet<string>(StringComparer.Ordinal) { "ocr.text", "ocr.tsv", "ocr.jpn", "ocr.eng" },
        "Apache-2.0",
        "external-runtime",
        false);

    public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (input.Image is null || !input.Image.CanRead)
            return Failure(OcrProcessingStatus.Failed, "OcrInput.Image is not readable.", "InputStream");

        var languages = NormalizeLanguages(options.Languages);
        var executablePath = ResolveExecutable(executable);
        if (executablePath is null)
            return Failure(OcrProcessingStatus.Unavailable, $"Tesseract executable '{executable}' was not found.", "ExecutableUnavailable");

        var tempRoot = Path.Combine(Path.GetTempPath(), "docredock-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var extension = ExtensionForMediaType(input.MediaType);
        var inputPath = Path.Combine(tempRoot, "input" + extension);
        var outputBase = Path.Combine(tempRoot, "result");
        try
        {
            var imageBytes = await ReadImageAsync(input.Image, cancellationToken).ConfigureAwait(false);
            if (options.PixelBudget is { } pixelBudget &&
                (pixelBudget <= 0 || imageBytes.LongLength > pixelBudget))
                return Failure(OcrProcessingStatus.SkippedByBudget,
                    $"OCR input exceeds the configured {pixelBudget}-byte image budget.", "PixelBudgetExceeded");
            var cacheKey = CacheKey(imageBytes, languages, executablePath);
            var cached = await ReadCacheAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return new OcrAttemptResult(OcrProcessingStatus.Completed, cached, Array.Empty<OcrDiagnostic>());

            await File.WriteAllBytesAsync(inputPath, imageBytes, cancellationToken).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputBase);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(string.Join('+', languages));
            psi.ArgumentList.Add("tsv");

            var timeoutValue = options.Timeout ?? defaultTimeout;
            if (timeoutValue <= TimeSpan.Zero)
                return Failure(OcrProcessingStatus.Failed, "OCR timeout must be positive.", "InvalidTimeout");
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) return Failure(OcrProcessingStatus.Failed, "Tesseract process could not start.", "ProcessStart");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            using var killRegistration = timeout.Token.Register(() => TryKill(process));
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, maxOutputBytes, CancellationToken.None);
            var stderrTask = ReadLimitedAsync(process.StandardError, maxOutputBytes, CancellationToken.None);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return Failure(OcrProcessingStatus.Failed, "Tesseract timed out.", "Timeout");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return Failure(OcrProcessingStatus.Failed, error.Length == 0 ? $"Tesseract exited with code {process.ExitCode}." : error, "ProcessFailed");

            var tsvPath = outputBase + ".tsv";
            if (!File.Exists(tsvPath))
                return Failure(OcrProcessingStatus.Failed, "Tesseract did not produce TSV output.", "MissingTsv");
            if (new FileInfo(tsvPath).Length > maxOutputBytes)
                return Failure(OcrProcessingStatus.Failed, "Tesseract TSV output exceeded the configured limit.", "OutputLimit");
            var tsv = await File.ReadAllTextAsync(tsvPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var result = TsvParser.Parse(tsv);
            await WriteCacheAsync(cacheKey, result, cancellationToken).ConfigureAwait(false);
            return new OcrAttemptResult(OcrProcessingStatus.Completed, result,
                output.Length >= maxOutputBytes || error.Length >= maxOutputBytes
                    ? [new OcrDiagnostic("OutputTruncated", "Tesseract diagnostic output was capped.", DiagnosticSeverity.Warning)]
                    : Array.Empty<OcrDiagnostic>());
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<OcrResult?> ReadCacheAsync(string key, CancellationToken cancellationToken)
    {
        if (cacheDirectory is null) return null;
        var path = Path.Combine(Path.GetFullPath(cacheDirectory), key + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > maxOutputBytes) return null;
        try { return JsonSerializer.Deserialize<OcrResult>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)); }
        catch (JsonException) { return null; }
    }

    private async Task WriteCacheAsync(string key, OcrResult result, CancellationToken cancellationToken)
    {
        if (cacheDirectory is null) return;
        var directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<byte[]> ReadImageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var originalPosition = stream.CanSeek ? stream.Position : -1;
        try
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return memory.ToArray();
        }
        finally
        {
            if (originalPosition >= 0) stream.Position = originalPosition;
        }
    }

    private static string CacheKey(byte[] bytes, IReadOnlyList<string> languages, string executablePath)
    {
        var inputHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var descriptor = "docredock.ocr.tesseract/0.2.0\n" + executablePath + "\n" + string.Join('+', languages);
        var configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor))).ToLowerInvariant();
        return inputHash + "-" + configHash[..16];
    }

    private static string[] NormalizeLanguages(IReadOnlyList<string>? languages)
    {
        var selected = (languages is null || languages.Count == 0 ? ["jpn", "eng"] : languages)
            .SelectMany(language => language.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(language => language.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.Ordinal)
            .ToArray();
        return selected.Length == 0 ? ["eng", "jpn"] : selected;
    }

    private static string? ResolveExecutable(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(Path.GetFullPath(configured)) ? Path.GetFullPath(configured) : null;
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, configured);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ExtensionForMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/tiff" => ".tif",
        "image/bmp" => ".bmp",
        _ => ".bin",
    };

    private static OcrAttemptResult Failure(OcrProcessingStatus status, string message, string code) =>
        new(status, null, [new OcrDiagnostic(code, message, DiagnosticSeverity.Warning)]);

    private static async Task<string> ReadLimitedAsync(StreamReader reader, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        var count = 0L;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var take = (int)Math.Min(read, Math.Max(0, maxBytes - count));
            if (take > 0) builder.Append(buffer, 0, take);
            count += read;
            if (count >= maxBytes)
            {
                while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0) { }
                break;
            }
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* process already exited */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort cleanup */ }
    }
}

public static class TsvParser
{
    public static OcrResult Parse(string tsv)
    {
        ArgumentNullException.ThrowIfNull(tsv);
        var rows = new List<Row>();
        var sourceOrder = 0;
        foreach (var line in tsv.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith("level\t", StringComparison.OrdinalIgnoreCase)) continue;
            var values = line.Split('\t');
            if (values.Length < 12 || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level != 5) continue;
            if (!int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) ||
                !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var block) ||
                !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var paragraph) ||
                !int.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) ||
                !int.TryParse(values[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
                !int.TryParse(values[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
                !int.TryParse(values[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(values[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
                !double.TryParse(values[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)) continue;
            var recognizedText = values[11];
            if (string.IsNullOrWhiteSpace(recognizedText) || confidence < 0) continue;
            rows.Add(new Row(page, block, paragraph, lineNumber, left, top, width, height, recognizedText, confidence / 100.0, sourceOrder++));
        }

        var regions = rows.OrderBy(row => row.Page).ThenBy(row => row.Top).ThenBy(row => row.Left).ThenBy(row => row.SourceOrder)
            .Select(row => new OcrTextRegion(row.Text, new Geometry("image-pixels", row.Left, row.Top, row.Width, row.Height), Math.Clamp(row.Confidence, 0, 1)))
            .ToArray();
        var fullText = rows.GroupBy(row => (row.Page, row.Block, row.Paragraph, row.LineNumber))
            .OrderBy(group => group.Key.Page).ThenBy(group => group.Min(row => row.Top)).ThenBy(group => group.Min(row => row.Left))
            .Select(group => string.Join(" ", group.OrderBy(row => row.Left).ThenBy(row => row.SourceOrder).Select(row => row.Text)))
            .Where(line => line.Length > 0);
        return new OcrResult(string.Join("\n", fullText), regions);
    }

    private sealed record Row(int Page, int Block, int Paragraph, int LineNumber, int Left, int Top, int Width, int Height, string Text, double Confidence, int SourceOrder);
}

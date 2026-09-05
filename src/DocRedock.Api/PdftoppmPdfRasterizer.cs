using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Api;

/// <summary>Local page-at-a-time rasterization with bounded output and child-process lifetime.</summary>
public class PdftoppmPdfRasterizer : IPdfRasterizer
{
    private const int MaxPages = 200;
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private const long MaxTotalImageBytes = 256L * 1024 * 1024;
    private const int MaxDiagnosticCharacters = 32_768;
    private readonly string executable;

    public PdftoppmPdfRasterizer(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        this.executable = Path.GetFullPath(executable);
    }

    public virtual ProviderDescriptor Descriptor { get; } = new("docredock.pdf-rasterizer.pdftoppm",
        new Version(1, 0), 1, new HashSet<string>(StringComparer.Ordinal) { "rasterize.pdf" },
        "GPL-2.0-or-later", "system-runtime", false);

    public async ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath,
        IReadOnlyList<int> pageNumbers, PdfRasterizationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (pageNumbers.Count > MaxPages || pageNumbers.Any(page => page < 1))
            throw new InvalidDataException("PDF rasterization page budget exceeded.");
        if (options.Dpi is < 1 or > 600 || options.MaxPixelsPerPage <= 0 || options.MaxTotalPixels <= 0 ||
            options.Timeout is { } requestedTimeout && requestedTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Rasterization budgets must be positive and DPI must not exceed 600.");
        if (pageNumbers.Count == 0) return [];
        var source = new FileInfo(Path.GetFullPath(pdfPath));
        if (!source.Exists) throw new FileNotFoundException("PDF input was not found.", pdfPath);
        if (source.Length > 256L * 1024 * 1024) throw new InvalidDataException("PDF rasterizer input exceeds 256 MiB.");
        var limits = options with
        {
            MaxPixelsPerPage = Math.Min(options.MaxPixelsPerPage, 40_000_000),
            MaxTotalPixels = Math.Min(options.MaxTotalPixels, 200_000_000),
            Timeout = TimeSpan.FromSeconds(Math.Min(15, (options.Timeout ?? TimeSpan.FromSeconds(15)).TotalSeconds))
        };
        var root = Directory.CreateTempSubdirectory("docredock-raster-").FullName;
        try
        {
            // A private, ordinary filename prevents option/URI interpretation by the external tool.
            var privateInput = Path.Combine(root, "input.pdf");
            File.Copy(source.FullName, privateInput);
            var result = new List<RasterizedPdfPage>();
            long totalPixels = 0, totalBytes = 0;
            foreach (var page in pageNumbers.Distinct().Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = Path.Combine(root, "page-" + page.ToString(CultureInfo.InvariantCulture));
                var start = new ProcessStartInfo
                {
                    FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
                };
                ConfigureArguments(start, privateInput, output, [page], limits);
                await RunAsync(start, limits.Timeout!.Value, cancellationToken).ConfigureAwait(false);
                var path = OutputPath(output, page);
                var file = new FileInfo(path);
                if (!file.Exists || file.LinkTarget is not null)
                    throw new InvalidDataException($"PDF rasterizer did not produce an ordinary image for page {page}.");
                if (file.Length is < 45 or > MaxImageBytes || totalBytes + file.Length > MaxTotalImageBytes)
                    throw new InvalidDataException("PDF rasterizer output exceeded the image byte budget.");
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var header = new byte[33];
                await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var (width, height) = ReadPngDimensions(header);
                var pixels = checked((long)width * height);
                if (pixels > limits.MaxPixelsPerPage || pixels > limits.MaxTotalPixels - totalPixels)
                    throw new InvalidDataException("PDF rasterizer output exceeded the pixel budget.");
                // Length and dimensions are checked before allocating the complete image.
                var bytes = new byte[checked((int)input.Length)];
                header.CopyTo(bytes, 0);
                await input.ReadExactlyAsync(bytes.AsMemory(header.Length), cancellationToken).ConfigureAwait(false);
                result.Add(new RasterizedPdfPage(page, "image/png", bytes, width, height));
                totalPixels += pixels;
                totalBytes += bytes.Length;
                File.Delete(path);
            }
            return result;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    protected virtual void ConfigureArguments(ProcessStartInfo start, string pdfPath, string output,
        IReadOnlyList<int> pages, PdfRasterizationOptions options)
    {
        var maxDimension = MaximumDimension(options);
        foreach (var argument in new[]
        {
            "-png", "-singlefile", "-r", options.Dpi.ToString(CultureInfo.InvariantCulture),
            "-scale-to", maxDimension.ToString(CultureInfo.InvariantCulture),
            "-f", pages[0].ToString(CultureInfo.InvariantCulture),
            "-l", pages[0].ToString(CultureInfo.InvariantCulture), pdfPath, output
        }) start.ArgumentList.Add(argument);
    }

    protected virtual string OutputPath(string output, int page) => output + ".png";

    protected static int MaximumDimension(PdfRasterizationOptions options) =>
        Math.Max(1, (int)Math.Min(4096, Math.Floor(Math.Sqrt(Math.Min(options.MaxPixelsPerPage, options.MaxTotalPixels)))));

    private static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            !header[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("PDF rasterizer returned an invalid PNG header.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("PDF rasterizer returned invalid PNG dimensions.");
        return ((int)width, (int)height);
    }

    private static async Task RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        if (!process.Start()) throw new InvalidOperationException("PDF rasterizer could not start.");
        var stdout = DrainAsync(process.StandardOutput, lifetime.Token);
        var stderr = DrainAsync(process.StandardError, lifetime.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(lifetime.Token), stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"PDF rasterizer exited with code {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("PDF rasterization timed out; reduce the document size or check the configured provider.");
        }
        finally
        {
            Stop(process);
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var keep = Math.Min(count, MaxDiagnosticCharacters - captured.Length);
            if (keep > 0) captured.Append(buffer, 0, keep);
        }
        return captured.ToString();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}

/// <summary>Uses the same bounded process/output policy with MuPDF's explicit single-page arguments.</summary>
public sealed class MutoolPdfRasterizer(string executable) : PdftoppmPdfRasterizer(executable)
{
    public override ProviderDescriptor Descriptor { get; } = new("docredock.pdf-rasterizer.mutool",
        new Version(1, 0), 1, new HashSet<string>(StringComparer.Ordinal) { "rasterize.pdf" },
        "AGPL-3.0-or-later", "system-runtime", false);

    protected override void ConfigureArguments(ProcessStartInfo start, string pdfPath, string output,
        IReadOnlyList<int> pages, PdfRasterizationOptions options)
    {
        var dimension = MaximumDimension(options).ToString(CultureInfo.InvariantCulture);
        foreach (var argument in new[]
        {
            "draw", "-q", "-F", "png", "-r", options.Dpi.ToString(CultureInfo.InvariantCulture),
            "-w", dimension, "-h", dimension, "-o", output + ".png", pdfPath,
            pages[0].ToString(CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(argument);
    }
}

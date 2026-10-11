using System.Globalization;
using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Api;

/// <summary>Timing checks that run alone: next to the rest of the suite, running in parallel, CPU
/// contention stretched a one-second extraction past a five-second bound.</summary>
[CollectionDefinition("Timed PDF extraction", DisableParallelization = true)]
public sealed class TimedPdfExtractionCollection;

[Collection("Timed PDF extraction")]
public sealed class StrikethroughTimingTests
{
    [Fact]
    public void Pdf_many_underlined_shows_are_checked_in_near_linear_time()
    {
        // 16,000 shows, each with its own underline, on one tall page (more than visual inference's
        // work budget, which no longer turns decoration detection off). Measuring every line against
        // every show and every path took 7.5 s here; looking up only nearby ones takes under 1 s.
        var commands = new StringBuilder();
        for (var index = 0; index < 16000; index++)
        {
            double x = 20 + index % 20 * 70, y = 13000 - index / 20 * 8;
            commands.Append(CultureInfo.InvariantCulture, $"BT /F1 6 Tf 1 0 0 1 {x} {y} Tm (item{index % 10}) Tj ET\n0.3 w {x} {y - 0.6} m {x + 15} {y - 0.6} l S\n");
        }
        var content = commands.ToString();
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 1500 13100] /Contents 2 0 R >> endobj\n2 0 obj << /Length " +
            content.Length + " >> stream\n" + content + "\nendstream endobj\n%%EOF");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = PdfTextExtractor.Extract(pdf);
        watch.Stop();
        // Every underline is formatting: none is left as a drawing.
        Assert.Empty(result.VisualGraphs?.GetValueOrDefault(1)?.Paths ?? []);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}

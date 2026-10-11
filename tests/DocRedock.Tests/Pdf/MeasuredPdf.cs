using System.Globalization;
using System.Text;

namespace DocRedock.Tests.Pdf;

/// <summary>One-page PDFs whose fonts give their glyph widths, for tests of text placement.</summary>
internal static class MeasuredPdf
{
    /// <summary>Helvetica's advance widths for the codes 32-126, in thousandths of the font size.</summary>
    private static readonly int[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    /// <summary>A TrueType font dictionary that gives Helvetica's widths for the codes 32-126.</summary>
    public static string SimpleFont { get; } =
        "<< /Type /Font /Subtype /TrueType /BaseFont /Measured /FirstChar 32 /LastChar 126 /Widths [" + string.Join(" ", Helvetica) + "] >>";

    /// <summary>The page resources naming <see cref="SimpleFont"/> (object 4) /F1.</summary>
    public const string Resources = "<< /Font << /F1 4 0 R >> >>";

    /// <summary>How wide <paramref name="text"/> is in <see cref="SimpleFont"/> at <paramref name="size"/>.</summary>
    public static double Width(string text, double size) => text.Sum(Advance) / 1000.0 * size;

    /// <summary>Helvetica's width for <paramref name="character"/> (32-126), in thousandths of the font size.</summary>
    public static int Advance(char character) => Helvetica[character - 32];

    /// <summary>A number as a content stream writes it.</summary>
    public static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A page drawing <paramref name="content"/> with <paramref name="resources"/>; object 3 is
    /// its annotation when one is given, and <paramref name="objects"/> are written as numbered (by
    /// default <see cref="SimpleFont"/> as object 4).</summary>
    public static byte[] Page(string content, string resources = Resources, IReadOnlyDictionary<int, string>? objects = null, string? annotation = null)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        pdf.Append("1 0 obj << /Type /Page /MediaBox [0 0 595 842] /Resources ").Append(resources).Append(" /Contents 2 0 R")
            .Append(annotation is null ? "" : " /Annots [3 0 R]").Append(" >> endobj\n");
        pdf.Append("2 0 obj ").Append(Stream(content)).Append(" endobj\n");
        if (annotation is not null) pdf.Append("3 0 obj ").Append(annotation).Append(" endobj\n");
        foreach (var (id, body) in objects ?? new Dictionary<int, string> { [4] = SimpleFont })
            pdf.Append(id.ToString(CultureInfo.InvariantCulture)).Append(" 0 obj ").Append(body).Append(" endobj\n");
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }

    /// <summary>A stream object's body holding <paramref name="data"/>.</summary>
    public static string Stream(string data) => $"<< /Length {data.Length} >> stream\n{data}\nendstream";

    /// <summary>A ToUnicode CMap mapping two-byte codes to the characters given.</summary>
    public static string TwoByteCMap(params (int Code, char Character)[] entries) => Stream(
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        $"{entries.Length} beginbfchar\n" +
        string.Concat(entries.Select(entry => $"<{entry.Code:X4}> <{(int)entry.Character:X4}>\n")) +
        "endbfchar\nendcmap\nend\nend");
}

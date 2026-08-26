using System.Xml;

namespace DocRedock.Formats.OpenXml;

/// <summary>Minimal, safe, cached-data-only reader for DrawingML chart parts.</summary>
public sealed record OpenXmlChartSeries(string? Name, IReadOnlyList<string> Categories, IReadOnlyList<string> Values);
public sealed record OpenXmlChartData(string? Title, string? Type, IReadOnlyList<OpenXmlChartSeries> Series);

public static class OpenXmlChartReader
{
    private const int MaxChartPoints = 100_000;
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = false,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 16_777_216,
    };

    public static OpenXmlChartData? Read(byte[] bytes, Func<string, IReadOnlyList<string>>? referenceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var document = new XmlDocument { PreserveWhitespace = false };
        using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), SafeXml);
        document.Load(reader);

        var title = TextFrom(document.SelectSingleNode("//*[local-name()='title']"));
        var chartType = document.SelectNodes("//*[local-name()='barChart' or local-name()='lineChart' or local-name()='pieChart' or local-name()='doughnutChart' or local-name()='areaChart' or local-name()='scatterChart' or local-name()='bubbleChart' or local-name()='radarChart' or local-name()='stockChart' or local-name()='surfaceChart']")
            ?.OfType<XmlElement>().Select(element => NormalizeType(element.LocalName)).FirstOrDefault();
        var series = document.SelectNodes("//*[local-name()='ser']")?.OfType<XmlElement>()
            .Select(seriesElement => ReadSeries(seriesElement, referenceResolver)).ToArray() ?? [];
        return string.IsNullOrWhiteSpace(title) && series.Length == 0 ? null
            : new OpenXmlChartData(string.IsNullOrWhiteSpace(title) ? null : title, chartType, series);
    }

    private static OpenXmlChartSeries ReadSeries(XmlElement series, Func<string, IReadOnlyList<string>>? referenceResolver)
    {
        var nameElement = DirectChild(series, "tx");
        var categoryElement = DirectChild(series, "cat") ?? DirectChild(series, "xVal");
        var valueElement = DirectChild(series, "val") ?? DirectChild(series, "yVal");
        var name = TextFrom(nameElement);
        if (string.IsNullOrWhiteSpace(name)) name = ResolveFormula(nameElement, referenceResolver).FirstOrDefault() ?? string.Empty;
        var categories = PointValues(categoryElement);
        if (categories.Count == 0) categories = ResolveFormula(categoryElement, referenceResolver);
        var values = PointValues(valueElement);
        if (values.Count == 0) values = ResolveFormula(valueElement, referenceResolver);
        return new OpenXmlChartSeries(string.IsNullOrWhiteSpace(name) ? null : name, categories, values);
    }

    private static IReadOnlyList<string> ResolveFormula(XmlElement? element, Func<string, IReadOnlyList<string>>? referenceResolver)
    {
        if (element is null || referenceResolver is null) return [];
        var formula = element.SelectNodes(".//*[local-name()='f']")?.OfType<XmlElement>()
            .Select(item => item.InnerText.Trim()).FirstOrDefault(value => value.Length > 0);
        return formula is null ? [] : referenceResolver(formula);
    }

    private static IReadOnlyList<string> PointValues(XmlElement? element)
    {
        if (element is null) return [];
        var points = element.SelectNodes(".//*[local-name()='pt']")?.OfType<XmlElement>().ToArray() ?? [];
        if (points.Length == 0) return TextValues(element);

        var indexed = points.Select((point, position) =>
        {
            var index = int.TryParse(point.GetAttribute("idx"), out var parsed) && parsed >= 0 ? parsed : position;
            return (Index: index, Value: TextFrom(point));
        }).ToArray();
        var declaredCount = element.SelectNodes(".//*[local-name()='ptCount']")?.OfType<XmlElement>()
            .Select(item => int.TryParse(item.GetAttribute("val"), out var count) && count >= 0 ? count : 0)
            .DefaultIfEmpty(0).Max() ?? 0;
        var count = Math.Max(declaredCount, indexed.Max(item => item.Index) + 1);
        if (count > MaxChartPoints)
            throw new InvalidDataException($"Chart cache declares {count:N0} points, exceeding the {MaxChartPoints:N0} point limit.");
        var values = Enumerable.Repeat(string.Empty, count).ToArray();
        foreach (var point in indexed) values[point.Index] = point.Value;
        return values;
    }

    private static string TextFrom(XmlNode? node) => string.Concat(TextValues(node)).Trim();

    private static IReadOnlyList<string> TextValues(XmlNode? node) =>
        node?.SelectNodes(".//*[local-name()='t' or local-name()='v']")?.OfType<XmlElement>()
            .Select(element => element.InnerText.Trim()).Where(value => value.Length > 0).ToArray() ?? [];

    private static XmlElement? DirectChild(XmlElement element, string localName) =>
        element.ChildNodes.OfType<XmlElement>().FirstOrDefault(child => child.LocalName == localName);

    private static string NormalizeType(string localName) => localName switch
    {
        "barChart" => "bar",
        "lineChart" => "line",
        "pieChart" => "pie",
        "doughnutChart" => "doughnut",
        "areaChart" => "area",
        "scatterChart" => "scatter",
        "bubbleChart" => "bubble",
        "radarChart" => "radar",
        "stockChart" => "stock",
        "surfaceChart" => "surface",
        _ => localName,
    };
}

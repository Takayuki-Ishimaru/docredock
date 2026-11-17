using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocRedock.Core.Documents;

namespace DocRedock.Api.SourceReading;

public sealed partial class SourceReader
{
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".rels", ".txt", ".json", ".csv", ".vml", ".htm", ".html", ".css", ".svg", ".xsl", ".xsd", ".mml", ".md",
    };

    /// <summary>The zip entries of an Office file, opened after the package limits the export applies (entry count, entry
    /// size, compression ratio) have been checked.</summary>
    private static ZipArchive OpenPackage(string path)
    {
        DocumentService.PreflightOfficePackage(path);
        return ZipFile.OpenRead(path);
    }

    private SourceOverviewResult ReadOverview(string input, string path, DocumentFormatKind format, string name)
    {
        using var archive = OpenPackage(path);
        var types = ReadContentTypes(archive);
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        var parts = entries.Take(SourceOverviewResult.MaxListedParts)
            .Select(entry => new SourcePartInfo(entry.FullName, entry.Length, types.For(entry.FullName))).ToArray();
        IReadOnlyList<SourceSheetInfo>? sheets = format == DocumentFormatKind.Xlsx ? ReadSheets(archive) : null;
        IReadOnlyList<SourceSlideInfo>? slides = format == DocumentFormatKind.Pptx ? ReadSlides(archive) : null;
        var hint = format switch
        {
            DocumentFormatKind.Xlsx => "Read cells with --sheet NAME [--range A1:H40], or a part with --part NAME.",
            DocumentFormatKind.Pptx => "Read a slide with --slide N, or a part with --part NAME.",
            _ => "Read a part with --part NAME (for example word/document.xml), or a node with --node ID.",
        };
        var listed = entries.Length > parts.Length ? $" The list shows the first {parts.Length} of {entries.Length} parts." : "";
        return new SourceOverviewResult
        {
            Input = input, Format = name, Locator = Locator("overview"),
            Parts = parts, PartCount = entries.Length, Sheets = sheets, Slides = slides,
            Note = hint + listed,
        };
    }

    /// <summary>A zip part as text; a picture part is returned as the picture instead (with its text when asked for).</summary>
    private async Task<SourceReadResult> ReadPartAsync(string input, string path, DocumentFormatKind format, string name, string requested,
        int offset, int maxChars, SourceRequest request, CancellationToken cancellationToken)
    {
        using (var archive = OpenPackage(path))
        {
            var found = FindEntry(archive, requested);
            if (IsPicturePart(found.FullName, includeSvg: false))
            {
                if (request.Offset is not null || request.MaxChars is not null)
                    throw new SourceReadException("invalid_locator", $"Part '{found.FullName}' is a picture: --offset and --max-chars page text, and a picture is returned whole (as a file).");
                return await ReadPicturePartAsync(input, path, format, name, found, request.Ocr, cancellationToken).ConfigureAwait(false);
            }
        }
        if (request.Ocr)
            throw new SourceReadException("invalid_locator", "--ocr reads the text of a picture: name a picture part (such as word/media/image1.png) or use --image.");
        return ReadPart(input, path, name, requested, offset, maxChars);
    }

    private SourceTextResult ReadPart(string input, string path, string name, string requested, int offset, int maxChars)
    {
        using var archive = OpenPackage(path);
        var types = ReadContentTypes(archive);
        var entry = FindEntry(archive, requested);
        var contentType = types.For(entry.FullName);
        if (!IsTextPart(entry.FullName, contentType))
            throw new SourceReadException("binary_part",
                $"Part '{entry.FullName}' is binary ({entry.Length.ToString(CultureInfo.InvariantCulture)} bytes, {contentType ?? "unknown content type"}); only XML, .rels and text parts (and pictures, as pictures) can be read.");
        using var reader = OpenText(entry);
        var window = TextWindow.Read(reader, offset, maxChars);
        return new SourceTextResult
        {
            Input = input, Format = name, Locator = Locator("part", ("part", entry.FullName)),
            Part = entry.FullName, Content = window.Content, Offset = offset, NextOffset = window.NextOffset, TotalChars = window.Total,
            Note = window.NextOffset is { } next ? $"More text follows: read it with --offset {next.ToString(CultureInfo.InvariantCulture)}." : null,
        };
    }

    private SourceTextResult ReadSlide(string input, string path, int number, int offset, int maxChars)
    {
        using var archive = OpenPackage(path);
        var slides = ReadSlides(archive);
        if (slides.Count == 0) throw new SourceReadException("slide_out_of_range", "This presentation has no slides.");
        if (number > slides.Count)
            throw new SourceReadException("slide_out_of_range",
                $"This presentation has {slides.Count.ToString(CultureInfo.InvariantCulture)} slide(s); --slide must be 1 to {slides.Count.ToString(CultureInfo.InvariantCulture)}.");
        var slide = slides[number - 1];
        var entry = FindEntry(archive, slide.Part);
        string? notes = null;
        foreach (var relationship in ReadRelationships(archive, RelationshipsPathFor(entry.FullName)))
            if (!relationship.External && relationship.Type.EndsWith("/notesSlide", StringComparison.Ordinal) &&
                FindEntryOrNull(archive, ResolveTarget(entry.FullName, relationship.Target)) is { } notesEntry)
            {
                notes = notesEntry.FullName;
                break;
            }
        using var reader = OpenText(entry);
        var window = TextWindow.Read(reader, offset, maxChars);
        var more = window.NextOffset is { } next ? $"More text follows: read it with --offset {next.ToString(CultureInfo.InvariantCulture)}. " : "";
        var notesNote = notes is null ? "" : $"Speaker notes are in {notes} (read it with --part).";
        return new SourceTextResult
        {
            Input = input, Format = "pptx", Locator = Locator("slide", ("slide", number)),
            Part = entry.FullName, Slide = number, NotesPart = notes,
            Content = window.Content, Offset = offset, NextOffset = window.NextOffset, TotalChars = window.Total,
            Note = (more + notesNote).Trim() is { Length: > 0 } note ? note : null,
        };
    }

    private static StreamReader OpenText(ZipArchiveEntry entry) =>
        new(entry.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

    /// <summary>The entry a part name denotes, with or without the leading slash; names are compared exactly first, then
    /// without regard to case when that leaves one entry.</summary>
    private static ZipArchiveEntry FindEntry(ZipArchive archive, string name) =>
        FindEntryOrNull(archive, name) ?? throw new SourceReadException("unknown_part",
            $"Unknown part '{name}'. Parts include: {string.Join(", ", archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).Take(12).Select(entry => entry.FullName))}"
            + (archive.Entries.Count > 12 ? ", ... (run without a locator to list them all)." : "."));

    private static ZipArchiveEntry? FindEntryOrNull(ZipArchive archive, string name)
    {
        var wanted = name.Replace('\\', '/').TrimStart('/');
        var exact = archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, wanted, StringComparison.Ordinal));
        if (exact is not null) return exact;
        var folded = archive.Entries.Where(entry => string.Equals(entry.FullName, wanted, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return folded.Length == 1 ? folded[0] : null;
    }

    private static bool IsTextPart(string entryName, string? contentType)
    {
        if (TextExtensions.Contains(Path.GetExtension(entryName))) return true;
        return contentType is not null && (contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
            contentType.EndsWith("/xml", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ContentTypes(Dictionary<string, string> Defaults, Dictionary<string, string> Overrides)
    {
        public string? For(string entryName)
        {
            if (Overrides.TryGetValue("/" + entryName.TrimStart('/'), out var overridden)) return overridden;
            var extension = Path.GetExtension(entryName).TrimStart('.');
            return extension.Length > 0 && Defaults.TryGetValue(extension, out var type) ? type : null;
        }
    }

    private static ContentTypes ReadContentTypes(ZipArchive archive)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = FindEntryOrNull(archive, "[Content_Types].xml");
        if (entry is null) return new(defaults, overrides);
        try
        {
            var document = LoadXml(entry);
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "Default" && (string?)element.Attribute("Extension") is { } extension &&
                    (string?)element.Attribute("ContentType") is { } defaultType) defaults[extension] = defaultType;
                else if (element.Name.LocalName == "Override" && (string?)element.Attribute("PartName") is { } partName &&
                    (string?)element.Attribute("ContentType") is { } overrideType) overrides[partName] = overrideType;
            }
        }
        catch (XmlException) { }
        return new(defaults, overrides);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024,
        });
        return XDocument.Load(reader);
    }

    private sealed record Relationship(string Id, string Type, string Target, bool External);

    private static string RelationshipsPathFor(string partName)
    {
        var slash = partName.LastIndexOf('/');
        return (slash < 0 ? "" : partName[..(slash + 1)]) + "_rels/" + partName[(slash + 1)..] + ".rels";
    }

    private static List<Relationship> ReadRelationships(ZipArchive archive, string relsPath)
    {
        var result = new List<Relationship>();
        if (FindEntryOrNull(archive, relsPath) is not { } entry) return result;
        try
        {
            foreach (var element in LoadXml(entry).Descendants().Where(element => element.Name.LocalName == "Relationship"))
                if ((string?)element.Attribute("Id") is { } id && (string?)element.Attribute("Target") is { } target)
                    result.Add(new Relationship(id, (string?)element.Attribute("Type") ?? "", target,
                        string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)));
        }
        catch (XmlException) { }
        return result;
    }

    /// <summary>The name of the zip entry a relationship target denotes, relative to the part that holds the relationship.</summary>
    private static string ResolveTarget(string basePart, string target)
    {
        try { target = Uri.UnescapeDataString(target); }
        catch (UriFormatException) { }
        var combined = target.StartsWith('/') ? target
            : (basePart.LastIndexOf('/') is var slash and >= 0 ? basePart[..(slash + 1)] : "") + target;
        var segments = new List<string>();
        foreach (var segment in combined.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    /// <summary>The part the package's root relationships name as the document (<c>officeDocument</c>), or the usual name.</summary>
    private static string MainPart(ZipArchive archive, string fallback) =>
        ReadRelationships(archive, "_rels/.rels").FirstOrDefault(relationship => !relationship.External &&
            relationship.Type.EndsWith("/officeDocument", StringComparison.Ordinal)) is { } main
            ? ResolveTarget("", main.Target) : fallback;

    private static List<SourceSheetInfo> ReadSheets(ZipArchive archive)
    {
        var sheets = new List<SourceSheetInfo>();
        var workbook = MainPart(archive, "xl/workbook.xml");
        if (FindEntryOrNull(archive, workbook) is not { } entry) return sheets;
        var relationships = ReadRelationships(archive, RelationshipsPathFor(entry.FullName)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var element in LoadXml(entry).Descendants().Where(element => element.Name.LocalName == "sheet"))
        {
            var id = (string?)element.Attribute(XName.Get("id", RelationshipsNamespace));
            string? part = id is not null && relationships.TryGetValue(id, out var relationship) && !relationship.External
                ? ResolveTarget(entry.FullName, relationship.Target) : null;
            sheets.Add(new SourceSheetInfo(sheets.Count + 1, (string?)element.Attribute("name") ?? "", (string?)element.Attribute("state") ?? "visible", part));
        }
        return sheets;
    }

    private static List<SourceSlideInfo> ReadSlides(ZipArchive archive)
    {
        var slides = new List<SourceSlideInfo>();
        var presentation = MainPart(archive, "ppt/presentation.xml");
        if (FindEntryOrNull(archive, presentation) is not { } entry) return slides;
        var relationships = ReadRelationships(archive, RelationshipsPathFor(entry.FullName)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var element in LoadXml(entry).Descendants().Where(element => element.Name.LocalName == "sldId"))
            if ((string?)element.Attribute(XName.Get("id", RelationshipsNamespace)) is { } id &&
                relationships.TryGetValue(id, out var relationship) && !relationship.External)
                slides.Add(new SourceSlideInfo(slides.Count + 1, ResolveTarget(entry.FullName, relationship.Target)));
        return slides;
    }
}

/// <summary>A window of a text, counted in UTF-16 code units: where it starts, where the next one does and how long all of
/// the text is. A window never ends between the two halves of a surrogate pair.</summary>
internal static class TextWindow
{
    public readonly record struct Window(string Content, int? NextOffset, long Total);

    public static Window Read(TextReader reader, int offset, int maxChars)
    {
        var content = new StringBuilder(Math.Min(maxChars, 64 * 1024));
        var block = new char[16 * 1024];
        long position = 0;
        var end = (long)offset + maxChars;
        char? afterWindow = null;
        int read;
        while ((read = reader.Read(block, 0, block.Length)) > 0)
        {
            for (var index = 0; index < read; index++, position++)
            {
                if (position >= offset && position < end) content.Append(block[index]);
                else if (position == end) afterWindow = block[index];
            }
        }
        if (offset > position)
            throw new SourceReadException("offset_out_of_range",
                $"--offset {offset.ToString(CultureInfo.InvariantCulture)} is past the end of the text ({position.ToString(CultureInfo.InvariantCulture)} characters).");
        // Keep a surrogate pair whole: the low half that follows a window's last high half belongs to it.
        if (content.Length > 0 && char.IsHighSurrogate(content[^1]) && afterWindow is { } low && char.IsLowSurrogate(low))
        {
            content.Append(low);
        }
        var next = offset + content.Length;
        return new Window(content.ToString(), next < position ? next : null, position);
    }
}

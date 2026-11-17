using DocRedock.Core.Documents;

namespace DocRedock.Api.SourceReading;

/// <summary>Reads one piece of the original document - a sheet range, a slide, a page, a node, a zip part or a picture - so that a
/// reader of the Markdown export (an AI assistant, say) can check or complete what the export left out without reading the
/// whole file. Nothing is ever returned whole: text comes in windows of at most <see cref="DefaultMaxCharacters"/> characters
/// (more on request, up to <see cref="MaximumMaxCharacters"/>) with the offset of the next one, and cells are capped at
/// <see cref="MaxCells"/>. PDF gating (<c>--allow-pdf</c>) belongs to the caller, as it does for the export.</summary>
public sealed partial class SourceReader(DocumentService? service = null)
{
    /// <summary>The window of text returned when the request names none.</summary>
    public const int DefaultMaxCharacters = 20_000;

    /// <summary>The largest window a request can ask for; a larger one is cut to it and the note says so.</summary>
    public const int MaximumMaxCharacters = 200_000;

    /// <summary>The most non-empty cells a range read returns.</summary>
    public const int MaxCells = 2_000;

    private readonly DocumentService documents = service ?? new DocumentService(null, null, discoverPdfRasterizer: false);

    /// <summary>Reads what the request asks for, or lists what can be read when it names no locator.</summary>
    /// <exception cref="SourceReadException">The request cannot be answered as given.</exception>
    /// <exception cref="NotSupportedException">The file is not a document DocRedock reads (<see cref="DocumentSourceException"/>).</exception>
    public async Task<SourceReadResult> ReadAsync(SourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var locators = Locators(request);
        if (locators.Count > 1)
            throw new SourceReadException("conflicting_locators",
                $"Use only one locator; got {string.Join(" and ", locators.Select(name => "--" + name))}. Choose one of --part, --node, --sheet (with --range), --slide, --page, --image.");
        if (request.Range is not null && request.Sheet is null)
            throw new SourceReadException("invalid_locator", "--range needs --sheet: name the worksheet the range belongs to.");
        if (request.Offset is < 0) throw new SourceReadException("invalid_offset", "--offset must be 0 or more.");
        if (request.MaxChars is < 1) throw new SourceReadException("invalid_max_chars", "--max-chars must be 1 or more.");
        if (request.Slide is < 1) throw new SourceReadException("invalid_slide", "--slide must be 1 or more (slides are numbered from 1).");
        if (request.Page is < 1) throw new SourceReadException("invalid_page", "--page must be 1 or more (pages are numbered from 1).");
        var locator = locators.Count == 0 ? "overview" : locators[0] == "range" ? "sheet" : locators[0];
        if (locator is "overview" or "sheet" or "image" && (request.Offset is not null || request.MaxChars is not null))
            throw new SourceReadException("invalid_locator", locator == "sheet"
                ? "--offset and --max-chars page text; a sheet read returns cells (use --range to choose them)."
                : locator == "image"
                    ? "--offset and --max-chars page text; --image returns a picture, which is not text."
                    : "--offset and --max-chars apply to --part, --node, --slide and --page; give no locator for an overview.");
        if (request.Ocr && locator is not ("image" or "part"))
            throw new SourceReadException("invalid_locator", "--ocr reads the text of a picture: use it with --image (or --part naming a picture).");

        var path = Path.GetFullPath(request.Path);
        var format = await DocumentService.DetectFormatAsync(path, cancellationToken).ConfigureAwait(false);
        if (format is not (DocumentFormatKind.Docx or DocumentFormatKind.Xlsx or DocumentFormatKind.Pptx or DocumentFormatKind.Pdf))
            throw DocumentSourceException.ForUnreadable(path);
        var name = FormatName(format);
        ValidateLocatorFits(locator, format, name);

        var maxChars = Math.Min(request.MaxChars ?? DefaultMaxCharacters, MaximumMaxCharacters);
        var clamped = request.MaxChars > MaximumMaxCharacters
            ? $"--max-chars was cut to {MaximumMaxCharacters}; read the rest with --offset." : null;
        var offset = request.Offset ?? 0;
        SourceReadResult result = locator switch
        {
            "overview" => format == DocumentFormatKind.Pdf ? ReadPdfOverview(request.Path, path, cancellationToken)
                : await WithImagesAsync(ReadOverview(request.Path, path, format, name), path, format, cancellationToken).ConfigureAwait(false),
            "part" => await ReadPartAsync(request.Path, path, format, name, request.Part!, offset, maxChars, request, cancellationToken).ConfigureAwait(false),
            "image" => await ReadImageAsync(request.Path, path, format, name, request.Image!, request.Ocr, cancellationToken).ConfigureAwait(false),
            "slide" => ReadSlide(request.Path, path, request.Slide!.Value, offset, maxChars),
            "sheet" => ReadSheet(request.Path, path, request.Sheet!, request.Range, cancellationToken),
            "page" => ReadPdfPage(request.Path, path, request.Page!.Value, offset, maxChars, cancellationToken),
            _ => await ReadNodeAsync(request.Path, path, format, name, request.Node!, offset, maxChars, cancellationToken).ConfigureAwait(false),
        };
        return clamped is null ? result : result with { Note = result.Note is null ? clamped : clamped + " " + result.Note };
    }

    private static List<string> Locators(SourceRequest request)
    {
        var locators = new List<string>();
        if (request.Part is not null) locators.Add("part");
        if (request.Node is not null) locators.Add("node");
        if (request.Sheet is not null) locators.Add("sheet");
        if (request.Slide is not null) locators.Add("slide");
        if (request.Page is not null) locators.Add("page");
        if (request.Image is not null) locators.Add("image");
        // A range alone is reported by the caller of this method; with --sheet it is part of the sheet locator.
        if (request.Range is not null && request.Sheet is null) locators.Add("range");
        return locators;
    }

    private static void ValidateLocatorFits(string locator, DocumentFormatKind format, string name)
    {
        var (fits, hint) = locator switch
        {
            "part" => (format != DocumentFormatKind.Pdf, "A PDF has no zip parts: use --page N to read a page."),
            "sheet" => (format == DocumentFormatKind.Xlsx, "--sheet reads a worksheet of an .xlsx file."),
            "slide" => (format == DocumentFormatKind.Pptx, "--slide reads a slide of a .pptx file."),
            "page" => (format == DocumentFormatKind.Pdf, "--page reads a page of a PDF file."),
            // --image fits every format: a picture of a PDF is found by its id.
            _ => (true, ""),
        };
        if (!fits)
            throw new SourceReadException("locator_does_not_fit_format",
                $"--{locator} does not fit this {name} file. {hint}{FormatAdvice(format)}");
    }

    private static string FormatAdvice(DocumentFormatKind format) => format switch
    {
        DocumentFormatKind.Docx => " For a .docx use --part (for example word/document.xml), --node or --image.",
        DocumentFormatKind.Xlsx => " For an .xlsx use --sheet NAME [--range A1:H40], --part, --node or --image.",
        DocumentFormatKind.Pptx => " For a .pptx use --slide N, --part, --node or --image.",
        DocumentFormatKind.Pdf => " For a PDF use --page N, --node or --image.",
        _ => "",
    };

    private static string FormatName(DocumentFormatKind format) => format.ToString().ToLowerInvariant();

    private static Dictionary<string, object?> Locator(string kind, params (string Key, object? Value)[] values)
    {
        var locator = new Dictionary<string, object?> { ["kind"] = kind };
        foreach (var (key, value) in values) locator[key] = value;
        return locator;
    }
}

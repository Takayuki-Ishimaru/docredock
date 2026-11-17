using System.Globalization;
using DocRedock.Api;
using DocRedock.Api.SourceReading;
using DocRedock.Core.Documents;

namespace DocRedock.Cli;

public sealed partial class CliApplication
{
    internal const string SourceUsage = "Use source <file> [--part name | --node id | --sheet name [--range A1:H40] | --slide N | --page N | --image id [--ocr] [--output file]] [--offset N] [--max-chars N] [--json] [--allow-pdf].";

    private const string SourceHelp = """

        Reads one piece of the original document as it is stored, so that a Markdown export that needs attention (or lacks
        something) can be checked against the source without reading the whole file. Give at most one locator:
          (none)               An overview: the zip parts of an Office file, the sheet names of a workbook, the slides of a
                               presentation (with their part names), or the page count of a PDF; and the pictures (id, part, media
                               type, size, page, slide or sheet, name; at most 200).
          --part NAME          The raw text of a zip part, such as word/document.xml or /xl/worksheets/sheet1.xml, from --offset.
                               A picture part (word/media/image2.png) is returned as the picture, as --image does; other
                               binary parts (embedded files and the like) are refused.
          --node ID            A node id such as n_4L5SQ2FD7FAKPOC3 (from an AI package's source-index.json, package read
                               --with-sources, or the node_id of an export --json diagnostic): its kind, text, source anchor and,
                               for Word, the raw XML it came from.
          --sheet NAME [--range A1:H40]
                               Excel: the cells of a range (the used range when --range is omitted) with their displayed values,
                               formulas, merged ranges and hidden rows and columns; at most 2000 non-empty cells.
          --slide N            PowerPoint: the XML of slide N (1-based, in presentation order) and its notes part.
          --page N             PDF (needs --allow-pdf): the text lines of page N as extracted, with their positions and font sizes,
                               without the headings, tables and lists the export infers.
          --image ID [--ocr] [--output FILE]
                               A picture: ID is the id the Markdown export shows for it (img-0002 in "> 画像: ... (img-0002, ...)" and in
                               OCR table links; pdf-img-0005 for a picture of a PDF, which needs --allow-pdf), or the part name of a
                               picture such as word/media/image2.png. The reply gives its id, media type, size in bytes, pixel width
                               and height, where it is (part, and page, slide or sheet when known) and its alt text; the picture itself is
                               saved to FILE (--force replaces an existing one) or, without --output, to a new folder under the system
                               temporary directory (docredock-source/<guid>/<id>.<ext>), and saved_path says where. The bytes are
                               never printed. --ocr also reads the text of the picture with the OCR engine the export uses and adds
                               ocr: {engine, lines: [{text, x, y, w, h, confidence}]} (pixels from the upper left corner), or
                               ocr: {available: false, reason} when no engine can run. --offset and --max-chars do not apply.
        Text comes in windows: --offset N (characters, default 0) and --max-chars N (default 20000, at most 200000) choose one;
        next_offset says where the next one starts.
        Output
          --json               One JSON object: {"schema": "docredock.source.v1", "input", "format", "locator", the content,
                               "offset", "next_offset", "total_chars", "note", "raw_notice"}.
          --allow-pdf          Read PDF files (off by default, as for export).
        The content is the document as stored: it can include hidden text, comments and deleted tracked changes that the Markdown
        export leaves out.
        Exit codes: 0 ok, 2 invalid request (conflicting or unfitting locators, unknown part, sheet, slide, page, node or picture,
        invalid range, binary part), 4 unsupported or disabled file, 7 validation failed (a damaged or oversized package).
        """;

    private async Task<int> SourceAsync(Arguments args, CancellationToken token)
    {
        if (args.Positionals.Count != 1) return Invalid(SourceUsage);
        if (!TryReadInteger(args, "offset", 0, out var offset, out var problem) ||
            !TryReadInteger(args, "max-chars", 1, out var maxChars, out problem) ||
            !TryReadInteger(args, "slide", 1, out var slide, out problem) ||
            !TryReadInteger(args, "page", 1, out var page, out problem))
            return Invalid(problem!);
        var source = RequireExistingFile(args);
        if (new[] { ".doc", ".xls", ".ppt" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
        { errorCode = "legacy_office_format"; return Unsupported(text.LegacyOffice(Path.GetFileName(source))); }
        if (!ExperimentalFeatures.IsEnabled && !args.HasFlag("allow-pdf") && await DocumentService.DetectFormatAsync(source, token) == DocumentFormatKind.Pdf)
        { errorCode = "pdf_not_enabled"; return Unsupported(text.PdfDisabled(Path.GetFileName(source))); }
        var request = new SourceRequest(source)
        {
            Part = args.Option("part"), Node = args.Option("node"), Sheet = args.Option("sheet"), Range = args.Option("range"),
            Slide = slide, Page = page, Offset = offset, MaxChars = maxChars,
            Image = args.Option("image"), Ocr = args.HasFlag("ocr") || args.Option("ocr") is "on" or "auto",
        };
        SourceReadResult result;
        try { result = await new SourceReader(Service).ReadAsync(request, token); }
        catch (SourceReadException ex) { errorCode = ex.Code; return Invalid(ex.Message); }
        // The input is shown as it was given, not as the full path it was resolved to.
        result = result with { Input = args.Positionals[0] };
        if (result is SourceImageResult picture)
        {
            try { result = picture with { SavedPath = await SavePictureAsync(picture, args, token) }; }
            catch (SourceReadException ex) { errorCode = ex.Code; return Invalid(ex.Message); }
        }
        else if (args.Option("output") is not null)
        { errorCode = "invalid_locator"; return Invalid("--output saves a picture: use it with --image (or --part naming a picture)."); }
        if (args.HasFlag("json")) await output.WriteLineAsync(result.ToJson().ToJsonString(PackageJson));
        else await output.WriteAsync(result.ToText());
        return 0;
    }

    /// <summary>Writes the picture to <c>--output</c>, or to a new folder under the temporary directory, and returns the full path.
    /// The bytes go to the file only; they are never printed.</summary>
    private static async Task<string> SavePictureAsync(SourceImageResult picture, Arguments args, CancellationToken token)
    {
        string path;
        if (args.Option("output") is { } requested)
        {
            if (requested == "-") throw new SourceReadException("invalid_output", "--output needs a file: the picture's bytes are never printed.");
            path = Path.GetFullPath(requested);
            if (Directory.Exists(path)) throw new SourceReadException("invalid_output", $"--output '{requested}' is a folder; name the file to write.");
            if (File.Exists(path) && !args.HasFlag("force"))
                throw new SourceReadException("output_exists", $"--output '{requested}' already exists; use --force to replace it.");
        }
        else
        {
            var stem = picture.Id ?? Path.GetFileNameWithoutExtension(picture.Part ?? "image");
            path = Path.Combine(Path.GetTempPath(), "docredock-source", Guid.NewGuid().ToString("N"), stem + picture.Extension);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, picture.Content.ToArray(), token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SourceReadException("invalid_output", $"The picture could not be saved to '{path}': {exception.Message}");
        }
        return path;
    }

    /// <summary>An option that is a whole number of at least <paramref name="minimum"/>; null when it was not given.</summary>
    private static bool TryReadInteger(Arguments args, string name, int minimum, out int? value, out string? problem)
    {
        value = null;
        problem = null;
        if (args.Option(name) is not { } given) return true;
        if (int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum)
        {
            value = number;
            return true;
        }
        problem = $"--{name} must be a whole number of {minimum.ToString(CultureInfo.InvariantCulture)} or more.";
        return false;
    }
}

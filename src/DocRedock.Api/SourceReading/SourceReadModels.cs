using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DocRedock.Api.SourceReading;

/// <summary>What to read from a document: its path and at most one locator (<see cref="Part"/>, <see cref="Node"/>,
/// <see cref="Sheet"/> with <see cref="Range"/>, <see cref="Slide"/>, <see cref="Page"/> or <see cref="Image"/>). With no locator the reply is an
/// overview of what can be read. <see cref="Offset"/> and <see cref="MaxChars"/> page the text of a part, node, slide or page.</summary>
public sealed record SourceRequest(string Path)
{
    /// <summary>A zip part of an Office file, such as <c>word/document.xml</c> (with or without the leading slash).</summary>
    public string? Part { get; init; }

    /// <summary>A node id as written in an AI package's <c>source-index.json</c> or in the diagnostics of an export.</summary>
    public string? Node { get; init; }

    /// <summary>A worksheet name (xlsx).</summary>
    public string? Sheet { get; init; }

    /// <summary>A cell range such as <c>A1:H40</c> of <see cref="Sheet"/>; the used range when omitted.</summary>
    public string? Range { get; init; }

    /// <summary>A slide number, 1-based in presentation order (pptx).</summary>
    public int? Slide { get; init; }

    /// <summary>A page number, 1-based (pdf).</summary>
    public int? Page { get; init; }

    /// <summary>A picture: an asset id such as <c>img-0002</c> (as the export prints it) or the zip part name of the picture, such
    /// as <c>word/media/image2.png</c>. The reply is the picture itself, not text.</summary>
    public string? Image { get; init; }

    /// <summary>Also read the text of the picture (<see cref="Image"/>, or a picture <see cref="Part"/>) with the OCR engine.</summary>
    public bool Ocr { get; init; }

    /// <summary>The first character to return (UTF-16 code units, default 0).</summary>
    public int? Offset { get; init; }

    /// <summary>The most characters to return (default <see cref="SourceReader.DefaultMaxCharacters"/>).</summary>
    public int? MaxChars { get; init; }
}

/// <summary>A request that cannot be answered as given: conflicting or unfitting locators, an unknown part, sheet, slide,
/// page or node, an invalid range or a part that is not text. <see cref="Code"/> is a short machine-readable reason.</summary>
public sealed class SourceReadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One piece of a document as it is stored, found with a <see cref="SourceRequest"/>. Every result carries
/// <see cref="RawNotice"/>: the piece is the document as stored, not the Markdown export of it.</summary>
public abstract record SourceReadResult
{
    public const string SchemaName = "docredock.source.v1";

    public const string RawNoticeText =
        "This is the document as stored. It can include hidden text, comments and deleted tracked changes that the Markdown export leaves out.";

    /// <summary>The path of the document as it was given.</summary>
    public required string Input { get; init; }

    /// <summary>"docx", "xlsx", "pptx" or "pdf".</summary>
    public required string Format { get; init; }

    /// <summary>The kind of locator ("overview", "part", "node", "sheet", "slide", "page" or "image") and its values.</summary>
    public required IReadOnlyDictionary<string, object?> Locator { get; init; }

    /// <summary>What to do next or what was left out, in a sentence; null when there is nothing to add.</summary>
    public string? Note { get; init; }

    public string RawNotice => RawNoticeText;

    /// <summary>The result as one JSON object: <c>schema</c>, <c>input</c>, <c>format</c>, <c>locator</c>, the content fields,
    /// <c>note</c> and <c>raw_notice</c>.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["schema"] = SchemaName,
            ["input"] = Input,
            ["format"] = Format,
            ["locator"] = LocatorJson(),
        };
        AddContent(json);
        json["note"] = Note;
        json["raw_notice"] = RawNotice;
        return json;
    }

    /// <summary>The result for a person: a few header lines, then the content.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(Format).Append(": ").Append(Input).Append('\n');
        text.Append("locator: ").Append(DescribeLocator()).Append('\n');
        AppendText(text);
        if (Note is not null) text.Append("note: ").Append(Note).Append('\n');
        text.Append("notice: ").Append(RawNotice).Append('\n');
        return text.ToString();
    }

    private protected abstract void AddContent(JsonObject json);

    private protected abstract void AppendText(StringBuilder text);

    private JsonObject LocatorJson()
    {
        var locator = new JsonObject();
        foreach (var (key, value) in Locator)
            locator[key] = value switch
            {
                null => null,
                int number => number,
                _ => value.ToString(),
            };
        return locator;
    }

    // "sheet Sheet1 range=A1:H40": the kind, then its first value on its own and the rest as name=value.
    private string DescribeLocator()
    {
        var kind = Convert.ToString(Locator["kind"], CultureInfo.InvariantCulture)!;
        return string.Join(" ", Locator.Select(pair => pair.Key == "kind" ? kind
            : pair.Key == kind ? Convert.ToString(pair.Value, CultureInfo.InvariantCulture)
            : $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
    }

    internal static JsonArray ToArray<T>(IEnumerable<T> items, Func<T, JsonNode?> convert) => new([.. items.Select(convert)]);
}

/// <summary>One zip part of an Office file.</summary>
public sealed record SourcePartInfo(string Name, long Size, string? ContentType);

/// <summary>A worksheet of a workbook, in workbook order.</summary>
public sealed record SourceSheetInfo(int Number, string Name, string State, string? Part);

/// <summary>A slide of a presentation, in presentation order.</summary>
public sealed record SourceSlideInfo(int Number, string Part);

/// <summary>One picture of a document: the id the export prints for it, where it is stored and where it is shown.</summary>
public sealed record SourceImageInfo(string Id, string? Part, string MediaType, long Size, int? Page, int? Slide, string? Sheet, string? Name)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id, ["part"] = Part, ["media_type"] = MediaType, ["size"] = Size };
        if (Page is { } page) json["page"] = page;
        if (Slide is { } slide) json["slide"] = slide;
        if (Sheet is not null) json["sheet"] = Sheet;
        json["name"] = Name;
        return json;
    }

    internal string ToLine() =>
        string.Join("  ", new[]
        {
            Id, Part, MediaType, Size.ToString(CultureInfo.InvariantCulture) + " bytes",
            Page is { } page ? "page " + page.ToString(CultureInfo.InvariantCulture) : null,
            Slide is { } slide ? "slide " + slide.ToString(CultureInfo.InvariantCulture) : null,
            Sheet is not null ? "sheet " + Sheet : null,
            Name,
        }.Where(item => !string.IsNullOrEmpty(item)));
}

/// <summary>What can be read from a document: its zip parts and, for a workbook, its sheets, for a presentation, its slides
/// and for a PDF its page count; and the pictures it holds.</summary>
public sealed record SourceOverviewResult : SourceReadResult
{
    /// <summary>The most parts an overview lists.</summary>
    public const int MaxListedParts = 500;

    /// <summary>The most pictures an overview lists.</summary>
    public const int MaxListedImages = 200;

    public IReadOnlyList<SourcePartInfo> Parts { get; init; } = [];

    /// <summary>How many parts the file holds, which may be more than <see cref="Parts"/> lists.</summary>
    public int PartCount { get; init; }

    public IReadOnlyList<SourceSheetInfo>? Sheets { get; init; }

    public IReadOnlyList<SourceSlideInfo>? Slides { get; init; }

    public int? PageCount { get; init; }

    /// <summary>The pictures of the document (at most <see cref="MaxListedImages"/>); null when they could not be listed.</summary>
    public IReadOnlyList<SourceImageInfo>? Images { get; init; }

    /// <summary>How many pictures the document holds, which may be more than <see cref="Images"/> lists.</summary>
    public int ImageCount { get; init; }

    private protected override void AddContent(JsonObject json)
    {
        if (PageCount is { } pages) json["page_count"] = pages;
        if (Sheets is { } sheets)
        {
            json["sheet_count"] = sheets.Count;
            json["sheets"] = ToArray(sheets, sheet => new JsonObject
            {
                ["number"] = sheet.Number, ["name"] = sheet.Name, ["state"] = sheet.State, ["part"] = sheet.Part,
            });
        }
        if (Slides is { } slides)
        {
            json["slide_count"] = slides.Count;
            json["slides"] = ToArray(slides, slide => new JsonObject { ["number"] = slide.Number, ["part"] = slide.Part });
        }
        if (Images is { } images)
        {
            json["image_count"] = ImageCount;
            json["images_truncated"] = ImageCount > images.Count;
            json["images"] = ToArray(images, image => image.ToJson());
        }
        if (PageCount is null)
        {
            json["part_count"] = PartCount;
            json["parts"] = ToArray(Parts, part => new JsonObject { ["name"] = part.Name, ["size"] = part.Size, ["content_type"] = part.ContentType });
        }
    }

    private protected override void AppendText(StringBuilder text)
    {
        if (PageCount is { } pages) text.Append("pages: ").Append(pages.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (Sheets is { } sheets)
        {
            text.Append("sheets: ").Append(sheets.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var sheet in sheets)
                text.Append("  ").Append(sheet.Number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(sheet.Name)
                    .Append(sheet.State == "visible" ? "" : $" ({sheet.State})").Append("  ").Append(sheet.Part).Append('\n');
        }
        if (Slides is { } slides)
        {
            text.Append("slides: ").Append(slides.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var slide in slides)
                text.Append("  ").Append(slide.Number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(slide.Part).Append('\n');
        }
        if (Images is { } images)
        {
            text.Append("images: ").Append(ImageCount.ToString(CultureInfo.InvariantCulture)).Append(ImageCount > images.Count ? $" (first {images.Count} listed)" : "").Append('\n');
            foreach (var image in images) text.Append("  ").Append(image.ToLine()).Append('\n');
        }
        if (PageCount is not null) return;
        text.Append("parts: ").Append(PartCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var part in Parts)
            text.Append("  ").Append(part.Name).Append("  ").Append(part.Size.ToString(CultureInfo.InvariantCulture)).Append(" bytes  ")
                .Append(part.ContentType ?? "(no content type)").Append('\n');
    }
}

/// <summary>The node a <c>--node</c> request found: its kind, text and where it came from in the document.</summary>
public sealed record SourceNodeInfo(
    string Id, string Kind, string Layer, string? Text, bool TextTruncated,
    string? AnchorFormat, string? AnchorPart, IReadOnlyList<KeyValuePair<string, string>> AnchorLocators,
    string? RawSlicePart, long? RawSliceStart, long? RawSliceEnd, string? RawSliceSha256, string? RawSliceKind, bool? RawSliceHashMatches);

/// <summary>A window of text from a part, node, slide or PDF page. <see cref="NextOffset"/> is where the next window starts
/// (null when this one reaches the end) and <see cref="TotalChars"/> is the length of all of the text.</summary>
public sealed record SourceTextResult : SourceReadResult
{
    public string? Part { get; init; }

    public int? Slide { get; init; }

    public string? NotesPart { get; init; }

    public int? Page { get; init; }

    public SourceNodeInfo? Node { get; init; }

    public required string Content { get; init; }

    public int Offset { get; init; }

    public int? NextOffset { get; init; }

    public long TotalChars { get; init; }

    private protected override void AddContent(JsonObject json)
    {
        if (Part is not null) json["part"] = Part;
        if (Slide is { } slide) json["slide"] = slide;
        if (NotesPart is not null) json["notes_part"] = NotesPart;
        if (Page is { } page) json["page"] = page;
        if (Node is { } node)
        {
            json["node"] = new JsonObject
            {
                ["id"] = node.Id,
                ["kind"] = node.Kind,
                ["layer"] = node.Layer,
                ["text"] = node.Text,
                ["text_truncated"] = node.TextTruncated,
                ["anchor"] = node.AnchorFormat is null ? null : new JsonObject
                {
                    ["format"] = node.AnchorFormat,
                    ["part_uri"] = node.AnchorPart,
                    ["locators"] = ToArray(node.AnchorLocators, pair => new JsonObject { ["kind"] = pair.Key, ["value"] = pair.Value }),
                },
                ["raw_slice"] = node.RawSlicePart is null ? null : new JsonObject
                {
                    ["part_uri"] = node.RawSlicePart,
                    ["start_offset"] = node.RawSliceStart,
                    ["end_offset"] = node.RawSliceEnd,
                    ["sha256"] = node.RawSliceSha256,
                    ["kind"] = node.RawSliceKind,
                    ["sha256_matches"] = node.RawSliceHashMatches,
                },
            };
        }
        json["content"] = Content;
        json["offset"] = Offset;
        json["next_offset"] = NextOffset;
        json["total_chars"] = TotalChars;
    }

    private protected override void AppendText(StringBuilder text)
    {
        if (Part is not null) text.Append("part: ").Append(Part).Append('\n');
        if (Slide is not null && NotesPart is not null) text.Append("notes part: ").Append(NotesPart).Append('\n');
        if (Node is { } node)
        {
            text.Append("node: ").Append(node.Id).Append(" (").Append(node.Kind).Append(", ").Append(node.Layer).Append(")\n");
            if (node.AnchorFormat is not null)
                text.Append("anchor: ").Append(node.AnchorFormat).Append(' ').Append(node.AnchorPart)
                    .Append(node.AnchorLocators.Count == 0 ? "" : " " + string.Join(", ", node.AnchorLocators.Select(pair => $"{pair.Key}={pair.Value}"))).Append('\n');
            if (node.RawSlicePart is not null)
                text.Append("raw slice: ").Append(node.RawSlicePart).Append(" bytes ")
                    .Append(node.RawSliceStart?.ToString(CultureInfo.InvariantCulture)).Append('-').Append(node.RawSliceEnd?.ToString(CultureInfo.InvariantCulture))
                    .Append(node.RawSliceHashMatches == false ? " (sha256 does not match the file)" : "").Append('\n');
            if (node.Text is not null) text.Append("text: ").Append(node.Text).Append(node.TextTruncated ? " [...]" : "").Append('\n');
        }
        var end = Offset + Content.Length;
        text.Append("chars ").Append(Offset.ToString(CultureInfo.InvariantCulture)).Append('-').Append(end.ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(TotalChars.ToString(CultureInfo.InvariantCulture))
            .Append(NextOffset is { } next ? $"; next_offset {next.ToString(CultureInfo.InvariantCulture)}" : "; end").Append('\n');
        text.Append("----\n").Append(Content);
        if (!Content.EndsWith('\n')) text.Append('\n');
        text.Append("----\n");
    }
}

/// <summary>One non-empty cell: its displayed value, its formula and the merged range it belongs to.</summary>
public sealed record SourceCell(string Address, string? Value, string? RawValue, string? Formula, string? MergedRange);

/// <summary>The cells of a range of a worksheet (at most <see cref="SourceReader.MaxCells"/> non-empty ones).</summary>
public sealed record SourceCellsResult : SourceReadResult
{
    public required string Sheet { get; init; }

    public required string SheetState { get; init; }

    /// <summary>The range that was read, such as "A1:H40"; null when the sheet has no cells.</summary>
    public string? Range { get; init; }

    public string? UsedRange { get; init; }

    public IReadOnlyList<SourceCell> Cells { get; init; } = [];

    public bool Truncated { get; init; }

    public IReadOnlyList<string> MergedRanges { get; init; } = [];

    public IReadOnlyList<int> HiddenRows { get; init; } = [];

    public IReadOnlyList<string> HiddenColumns { get; init; } = [];

    private protected override void AddContent(JsonObject json)
    {
        json["sheet"] = Sheet;
        json["sheet_state"] = SheetState;
        json["range"] = Range;
        json["used_range"] = UsedRange;
        json["cell_count"] = Cells.Count;
        json["truncated"] = Truncated;
        json["cells"] = ToArray(Cells, cell =>
        {
            var item = new JsonObject { ["address"] = cell.Address, ["value"] = cell.Value };
            if (cell.RawValue is not null) item["raw_value"] = cell.RawValue;
            if (cell.Formula is not null) item["formula"] = cell.Formula;
            if (cell.MergedRange is not null) item["merged_range"] = cell.MergedRange;
            return item;
        });
        json["merged_ranges"] = ToArray(MergedRanges, range => (JsonNode?)range);
        json["hidden_rows"] = ToArray(HiddenRows, row => (JsonNode?)row);
        json["hidden_columns"] = ToArray(HiddenColumns, column => (JsonNode?)column);
    }

    private protected override void AppendText(StringBuilder text)
    {
        text.Append("sheet: ").Append(Sheet).Append(SheetState == "visible" ? "" : $" ({SheetState})")
            .Append("; range: ").Append(Range ?? "(empty)").Append("; used range: ").Append(UsedRange ?? "(none)").Append('\n');
        foreach (var cell in Cells)
        {
            text.Append(cell.Address).Append(": ").Append(cell.Value ?? "");
            if (cell.RawValue is not null) text.Append("  [raw ").Append(cell.RawValue).Append(']');
            if (cell.Formula is not null) text.Append("  [formula =").Append(cell.Formula).Append(']');
            if (cell.MergedRange is not null) text.Append("  [merged ").Append(cell.MergedRange).Append(']');
            text.Append('\n');
        }
        text.Append(Cells.Count.ToString(CultureInfo.InvariantCulture)).Append(" non-empty cell(s)").Append(Truncated ? " (truncated)" : "").Append('\n');
        if (MergedRanges.Count > 0) text.Append("merged ranges: ").Append(string.Join(", ", MergedRanges)).Append('\n');
        if (HiddenRows.Count > 0) text.Append("hidden rows: ").Append(string.Join(", ", HiddenRows)).Append('\n');
        if (HiddenColumns.Count > 0) text.Append("hidden columns: ").Append(string.Join(", ", HiddenColumns)).Append('\n');
    }
}

/// <summary>One line of text an OCR engine read in a picture, with where it is in the picture (pixels from its upper left corner
/// when <see cref="SourceImageOcr.CoordinateSpace"/> is "image-pixels").</summary>
public sealed record SourceOcrLine(string Text, double? X, double? Y, double? Width, double? Height, double? Confidence);

/// <summary>The text of a picture as the OCR engine of the export read it. When no engine is available (or it failed),
/// <see cref="Available"/> is false and <see cref="Reason"/> says why; the read of the picture itself still succeeds.</summary>
public sealed record SourceImageOcr(bool Available, string? Reason, string? Engine, string? CoordinateSpace, IReadOnlyList<SourceOcrLine> Lines)
{
    public static SourceImageOcr NotAvailable(string reason, string? engine = null) => new(false, reason, engine, null, []);

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["available"] = Available };
        if (Reason is not null) json["reason"] = Reason;
        if (!Available) return json;
        json["engine"] = Engine;
        json["coordinate_space"] = CoordinateSpace;
        json["lines"] = SourceReadResult.ToArray(Lines, line =>
        {
            var item = new JsonObject { ["text"] = line.Text, ["x"] = line.X, ["y"] = line.Y, ["w"] = line.Width, ["h"] = line.Height };
            if (line.Confidence is { } confidence) item["confidence"] = confidence;
            return item;
        });
        return json;
    }
}

/// <summary>A picture found with <c>--image</c> (or a picture part): what it is and where it is. <see cref="Content"/> holds the
/// bytes of the file as stored; they are never part of the JSON or the text, a caller saves them (<see cref="SavedPath"/>) or
/// hands them to a client that can show an image.</summary>
public sealed record SourceImageResult : SourceReadResult
{
    /// <summary>The id the export prints for the picture; null for a picture part the export does not use.</summary>
    public string? Id { get; init; }

    /// <summary>The zip part the picture is stored in, without a leading slash; null for a PDF's picture.</summary>
    public string? Part { get; init; }

    public required string MediaType { get; init; }

    public required long Size { get; init; }

    public int? PixelWidth { get; init; }

    public int? PixelHeight { get; init; }

    public int? Page { get; init; }

    public int? Slide { get; init; }

    public string? Sheet { get; init; }

    /// <summary>The alt text or name the document gives the picture.</summary>
    public string? AltText { get; init; }

    /// <summary>Other parts that hold exactly the same picture (the export writes them once).</summary>
    public IReadOnlyList<string> AliasParts { get; init; } = [];

    /// <summary>The file name extension for the picture, with the dot (".png").</summary>
    public required string Extension { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>Where the caller saved the picture, if it did.</summary>
    public string? SavedPath { get; init; }

    public SourceImageOcr? Ocr { get; init; }

    private protected override void AddContent(JsonObject json)
    {
        var image = new JsonObject { ["id"] = Id, ["part"] = Part, ["media_type"] = MediaType, ["size"] = Size, ["width"] = PixelWidth, ["height"] = PixelHeight };
        if (Page is { } page) image["page"] = page;
        if (Slide is { } slide) image["slide"] = slide;
        if (Sheet is not null) image["sheet"] = Sheet;
        image["alt_text"] = AltText;
        if (AliasParts.Count > 0) image["alias_parts"] = ToArray(AliasParts, part => (JsonNode?)part);
        json["image"] = image;
        json["saved_path"] = SavedPath;
        if (Ocr is { } ocr) json["ocr"] = ocr.ToJson();
    }

    private protected override void AppendText(StringBuilder text)
    {
        text.Append("image: ").Append(Id ?? "(not used by the export)").Append(", ").Append(MediaType).Append(", ")
            .Append(Size.ToString(CultureInfo.InvariantCulture)).Append(" bytes");
        if (PixelWidth is { } width && PixelHeight is { } height)
            text.Append(", ").Append(width.ToString(CultureInfo.InvariantCulture)).Append('x').Append(height.ToString(CultureInfo.InvariantCulture)).Append(" px");
        text.Append('\n');
        if (Part is not null) text.Append("part: ").Append(Part).Append('\n');
        if (Page is { } page) text.Append("page: ").Append(page.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (Slide is { } slide) text.Append("slide: ").Append(slide.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (Sheet is not null) text.Append("sheet: ").Append(Sheet).Append('\n');
        if (AltText is not null) text.Append("alt text: ").Append(AltText).Append('\n');
        text.Append("saved: ").Append(SavedPath ?? "(not saved)").Append('\n');
        if (Ocr is not { } ocr) return;
        if (!ocr.Available)
        {
            text.Append("ocr: not available: ").Append(ocr.Reason).Append('\n');
            return;
        }
        text.Append("ocr: ").Append(ocr.Engine).Append(", ").Append(ocr.Lines.Count.ToString(CultureInfo.InvariantCulture)).Append(" line(s)")
            .Append(ocr.Reason is null ? "" : "; " + ocr.Reason).Append('\n');
        foreach (var line in ocr.Lines)
        {
            text.Append("  ");
            if (line.X is { } x && line.Y is { } y && line.Width is { } w && line.Height is { } h)
                text.Append(FormattableString.Invariant($"[x={x:0.##} y={y:0.##} w={w:0.##} h={h:0.##}] "));
            text.Append(line.Text).Append('\n');
        }
    }
}

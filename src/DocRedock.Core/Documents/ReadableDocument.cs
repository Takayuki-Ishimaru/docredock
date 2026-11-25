using System.Text.Json;

namespace DocRedock.Core.Documents;

/// <summary>A format-neutral presentation. Text is source text, never serialized Markdown or HTML.</summary>
public sealed record ReadableDocument(
    string Title,
    string Language,
    DocumentFormatKind SourceFormat,
    IReadOnlyList<ReadableBlock> Blocks,
    string? SourceName = null)
{
    public IReadOnlyDictionary<string, StyleDescriptor>? Styles { get; init; }
    public JsonElement? WorkbookLayout { get; init; }
    public IReadOnlyList<ReadableNotice> Notices { get; init; } = [];
}

public sealed record ReadableNotice(string Code, string Message, string? ObjectId = null);

public sealed record ReadableSourceOrigin(
    string? PartitionId = null,
    string? SheetName = null,
    string? Range = null,
    string? ObjectId = null,
    IReadOnlyList<string>? NodeIds = null,
    SourceAnchor? Anchor = null);

public sealed record ReadableContent(IReadOnlyList<TextRun> Runs)
{
    public static ReadableContent Text(string text) => new([new TextRun(text)]);
    public string PlainText => string.Concat(Runs.Select(run => run.Text));
}

public abstract record ReadableBlock
{
    public ReadableSourceOrigin? Origin { get; init; }
}

public sealed record ReadableSection(string Id, ReadableContent Title, IReadOnlyList<ReadableBlock> Blocks,
    int HeadingLevel = 2) : ReadableBlock;
public sealed record ReadableHeading(int Level, ReadableContent Content) : ReadableBlock;
public sealed record ReadableParagraph(ReadableContent Content) : ReadableBlock;
public sealed record ReadableList(IReadOnlyList<ReadableListItem> Items) : ReadableBlock;
public sealed record ReadableListItem(ReadableContent Content, int Level = 0, bool Ordered = false, int? Number = null)
{
    public ReadableSourceOrigin? Origin { get; init; }
}
public sealed record ReadableQuote(IReadOnlyList<ReadableBlock> Blocks) : ReadableBlock;
public sealed record ReadableCode(string Text, string? Language = null) : ReadableBlock;
public sealed record ReadableDetails(string Kind, ReadableContent Summary, IReadOnlyList<ReadableBlock> Blocks,
    bool InitiallyOpen = false) : ReadableBlock;
public sealed record ReadablePageBreak : ReadableBlock;
public sealed record ReadableFixedPage(int Number, string ImageReference, double WidthPoints, double HeightPoints,string? Text=null);
public sealed record ReadableOfficePages(string Engine, IReadOnlyList<ReadableFixedPage> Pages) : ReadableBlock;
public sealed record ReadableSourceDrawingScene(SourceDrawingScene Scene) : ReadableBlock
{
    public IReadOnlyDictionary<string, ReadableBlock> ObjectBlocks { get; init; } = new Dictionary<string, ReadableBlock>();
    public IReadOnlyDictionary<string, IReadOnlyList<ReadableBlock>> ChartData { get; init; } = new Dictionary<string, IReadOnlyList<ReadableBlock>>();
}
public sealed record ReadableImage(string Reference, string AltText, double? WidthPixels = null,
    double? HeightPixels = null) : ReadableBlock;
public sealed record ReadableDiagram(string? MermaidSource, string AltText, IReadOnlyList<ReadableBlock> FallbackBlocks,
    VisualGraph? Graph = null) : ReadableBlock;

public sealed record ReadableFormula(string? Expression, string DisplayValue, bool HasCachedValue,
    bool IsSharedCopy = false);
public sealed record ReadableColumn(int SourceIndex, double? WidthPixels = null);
public sealed record ReadableTableRow(IReadOnlyList<ReadableTableCell> Cells, int? SourceRow = null,
    double? HeightPixels = null);
public sealed record ReadableTableCell(ReadableContent Content, int RowSpan = 1, int ColSpan = 1,
    int? SourceColumn = null)
{
    public string? StyleId { get; init; }
    public ReadableFormula? Formula { get; init; }
    public ReadableContent? SourceContent { get; init; }
    public bool IsNumeric { get; init; }
    public ReadableSourceOrigin? Origin { get; init; }
    public IReadOnlyList<ReadableBlock> Children { get; init; } = [];
}
public sealed record ReadableTable(IReadOnlyList<ReadableColumn> Columns, IReadOnlyList<ReadableTableRow> Rows,
    int HeaderRowCount = 1) : ReadableBlock
{
    /// <summary>Sparse source grid when the readable table uses address/value records.</summary>
    public ReadableTable? SourceGrid { get; init; }
}

/// <summary>A worksheet's fixed source grid and DrawingML scene, independent of semantic diagram layout.</summary>
public sealed record ReadableWorksheetScene(ReadableTable? Grid, WorkbookDrawingScene Scene,
    bool IncludeHiddenDimensions = false) : ReadableBlock;

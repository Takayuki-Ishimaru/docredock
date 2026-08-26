using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocRedock.Core.Documents;

public enum DocumentFormatKind { Unknown, Docx, Xlsx, Pptx, Pdf, Markdown }
public enum ContentLayer { Body, Furniture, Derived, Hidden, Metadata }
public enum DocumentContentPolicy { Visible, Complete, Sanitized }

public static class DocumentContentPolicyRules
{
    public static DocumentContentPolicy Parse(string? value) => (value ?? "visible").Trim().ToLowerInvariant() switch
    {
        "visible" => DocumentContentPolicy.Visible,
        "complete" => DocumentContentPolicy.Complete,
        "sanitized" => DocumentContentPolicy.Sanitized,
        _ => throw new ArgumentException("Content policy must be visible, complete, or sanitized.", nameof(value)),
    };

    public static string Name(DocumentContentPolicy policy) => policy.ToString().ToLowerInvariant();

    public static bool Includes(DocumentNode node, DocumentContentPolicy policy)
    {
        if (policy == DocumentContentPolicy.Complete) return true;
        if (node.Layer is ContentLayer.Hidden or ContentLayer.Metadata ||
            node.Kind is NodeKind.Comment or NodeKind.Revision or NodeKind.SpeakerNotes) return false;
        return policy != DocumentContentPolicy.Sanitized ||
            (node.Layer is not (ContentLayer.Furniture or ContentLayer.Derived) &&
             node.Kind is not (NodeKind.Header or NodeKind.Footer or NodeKind.Footnote or NodeKind.Endnote or NodeKind.ImageText or NodeKind.Annotation));
    }
}

public static class ExperimentalFeatures
{
    public const string EnvironmentVariable = "DOCREDOCK_ENABLE_EXPERIMENTAL";
    public static bool IsEnabled => StringComparer.Ordinal.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1");
}
public enum NodeKind
{
    Document, Section, Page, Paragraph, Heading, List, ListItem, Quote, CodeBlock, Link,
    Footnote, Endnote, Comment, Revision, Table, TableRow, TableCell, Image, ImageText,
    TextBox, Shape, Connector, Group, Chart, ChartSeries, Diagram, Slide, SpeakerNotes,
    Workbook, Worksheet, Range, Cell, Formula, NamedRange, PdfPage, Annotation, Header,
    Footer, PageBreak, Unknown, Passthrough
}
public enum NodeEditability { EditableInPlace, EditableWithConstraints, AnnotationOnly, Passthrough, RenderOnly, Protected }
public enum EvidenceKind { Native, AltText, Ocr, UserCorrectedOcr, LayoutInferred, TableInferred, VisionDescribed, Generated }
public enum RawSliceKind { XmlElement, XmlFragment, ZipEntryPayload, BinaryAsset }

public sealed record DocumentGraph(
    string SchemaVersion,
    string DocumentId,
    DocumentFormatKind Format,
    IReadOnlyList<DocumentPartition> Partitions,
    IReadOnlyDictionary<string, StyleDescriptor>? Styles = null,
    IReadOnlyDictionary<string, AssetDescriptor>? Assets = null,
    IReadOnlyDictionary<string, JsonElement>? FormatExtensions = null,
    GraphCapabilities? Capabilities = null)
{
    public const string CurrentSchemaVersion = "1.1";
    [JsonIgnore]
    public IEnumerable<DocumentNode> Nodes => Partitions.SelectMany(partition => partition.Nodes);
    public DocumentNode? FindNode(string id) => Nodes.FirstOrDefault(node => StringComparer.Ordinal.Equals(node.Id, id));
}

public sealed record DocumentPartition(string Id, int Order, IReadOnlyList<DocumentNode> Nodes, string? SourcePartUri = null);

public sealed record DocumentNode(
    string Id,
    NodeKind Kind,
    string? ParentId,
    int Order,
    ContentLayer Layer,
    NodeContent Content,
    SourceAnchor? Source = null,
    RawSliceRef? RawSlice = null,
    Geometry? Geometry = null,
    string? StyleId = null,
    NodeEditability Editability = NodeEditability.EditableInPlace,
    IReadOnlyList<ProvenanceItem>? Provenance = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

[
    System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind"),
    System.Text.Json.Serialization.JsonDerivedType(typeof(TextNodeContent), "text"),
    System.Text.Json.Serialization.JsonDerivedType(typeof(RichTextNodeContent), "rich_text"),
    System.Text.Json.Serialization.JsonDerivedType(typeof(TableNodeContent), "table"),
    System.Text.Json.Serialization.JsonDerivedType(typeof(ReferenceNodeContent), "reference"),
    System.Text.Json.Serialization.JsonDerivedType(typeof(EmptyNodeContent), "empty")
]
public abstract record NodeContent;
public sealed record TextNodeContent(string Text) : NodeContent;
public sealed record RichTextNodeContent(IReadOnlyList<TextRun> Runs) : NodeContent;
public sealed record TableNodeContent(IReadOnlyList<IReadOnlyList<TableCell>> Rows) : NodeContent;
public sealed record ReferenceNodeContent(string Reference, string? AltText = null) : NodeContent;
public sealed record EmptyNodeContent : NodeContent;

/// <summary>
/// A single table cell. <see cref="ColSpan"/>/<see cref="RowSpan"/> mirror OOXML's
/// <c>w:gridSpan</c>/<c>w:vMerge</c> (and the equivalent merged-region concept in other
/// formats): both default to 1 for an ordinary cell. A vertical-merge continuation cell
/// (the OOXML placeholder that visually inherits its content from the cell above) is
/// represented with <see cref="RowSpan"/> 0 and an empty <see cref="Text"/>; the merge's
/// origin cell carries the real, accumulated span count. The implicit conversion from
/// <see cref="string"/> keeps plain-text table construction (Xlsx/Pptx/tests) unchanged.
/// </summary>
public sealed record TableCell(string Text, int ColSpan = 1, int RowSpan = 1)
{
    public static implicit operator TableCell(string text) => new(text);
}

/// <summary>
/// The inline units DRMD can round-trip for rich Word paragraphs.  Line breaks and tabs
/// retain their textual representation in <see cref="TextRun.Text"/> as well, so older
/// consumers that concatenate runs do not silently drop them.
/// </summary>
public enum TextRunKind { Text, LineBreak, Tab }

/// <summary>
/// A deliberately portable inline-format subset.  It represents direct Word run
/// properties (<c>w:rPr</c>) rather than arbitrary Word character formatting.
/// Fields, revision markup, and drawing runs remain outside this contract.
/// <see cref="LinkTarget"/>, <see cref="Color"/>, and <see cref="HighlightColor"/> are
/// read-only decorations for the one-way readable projection: the shared
/// <c>DocRedockInlineMarkdown</c> serializer/parser used by the round-trippable
/// <c>rich-text=inline-v1</c> block contract intentionally ignores them, so they never
/// participate in F1 edit/restore matching and cannot desynchronize a round trip.
/// </summary>
public sealed record TextRun(
    string Text,
    string? StyleId = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false,
    bool Code = false,
    TextRunKind Kind = TextRunKind.Text,
    string? LinkTarget = null,
    string? Color = null,
    string? HighlightColor = null);

public sealed record StyleDescriptor(string Id, string? Name = null, IReadOnlyDictionary<string, string>? Properties = null);
public sealed record AssetDescriptor(string Id, string Sha256, string MediaType, string? FileName = null);
public sealed record GraphCapabilities
{
    public GraphCapabilities() { }
    public GraphCapabilities(IEnumerable<string>? values) =>
        Values = values is null ? [] : new SortedSet<string>(values, StringComparer.Ordinal);

    public SortedSet<string> Values { get; init; } = new(StringComparer.Ordinal);
    public bool Supports(string capability) => Values.Contains(capability);
}

public sealed record ProvenanceItem(
    EvidenceKind Evidence,
    double? Confidence = null,
    string? Engine = null,
    string? EngineVersion = null,
    string? DerivedFromNodeId = null,
    int? PageNumber = null,
    Geometry? Bbox = null,
    TextRange? CharacterSpan = null);

public sealed record RawSliceRef(string PartUri, long StartOffset, long EndOffset, string Sha256, RawSliceKind Kind)
{
    public bool Contains(long offset) => offset >= StartOffset && offset < EndOffset;
}

public sealed record Geometry(string CoordinateSpace, double X, double Y, double Width, double Height, double RotationDegrees = 0);
public sealed record TextRange(int Start, int End)
{
    public bool IsValid => Start >= 0 && End >= Start;
}

public sealed record SourceAnchor(
    string Format,
    string PartUri,
    IReadOnlyList<AnchorLocator> Locators,
    int? OriginalOrdinal = null,
    string? StructuralFingerprint = null);

public sealed record AnchorLocator(string Kind, string Value);

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed record PptxShapeRecord(
    string SlideId,
    string ShapeId,
    string? Name,
    string Text,
    bool IsTable,
    IReadOnlyList<string> ImageRelationshipIds,
    Geometry? Geometry,
    IReadOnlyList<IReadOnlyList<TableCell>>? TableRows = null,
    string Role = "other",
    IReadOnlyList<string>? Paragraphs = null,
    IReadOnlyList<PptxTextParagraph>? ParagraphDetails = null,
    string? Description = null,
    string ShapeType = "shape",
    IReadOnlyList<string>? ChartRelationshipIds = null,
    IReadOnlyList<string>? DiagramRelationshipIds = null,
    string? ConnectorStartId = null,
    string? ConnectorEndId = null,
    bool IsHidden = false);
public sealed record PptxTextRun(string Text, bool Bold = false, bool Italic = false,
    bool Underline = false, string? FontName = null, double? FontSize = null, bool Strike = false);
public sealed record PptxTextParagraph(string Text, int Level = 0, bool IsBullet = false,
    string? BulletCharacter = null, IReadOnlyList<PptxTextRun>? Runs = null,
    bool IsOrdered = false, int? ListNumber = null);
public sealed record PptxSlideRecord(string SlideId, string PartUri, IReadOnlyList<PptxShapeRecord> Shapes, string? NotesText,
    IReadOnlyList<PptxTextParagraph>? NotesDetails = null, bool IsHidden = false);
public sealed record PptxExtractionResult(DocumentGraph Graph, IReadOnlyList<PptxSlideRecord> Slides, IReadOnlyDictionary<string, string> PartSha256, IReadOnlyList<string> Warnings);
public sealed record PptxShapeTextEdit(string SlideId, string ShapeId, string Text);
public sealed record PptxPatchPlan(IReadOnlyList<PptxShapeTextEdit> Edits, IReadOnlySet<string> DirtyParts);
public sealed record PptxRestoreResult(byte[] Bytes, bool IsByteIdentical, PptxPatchPlan Plan, IReadOnlyList<string> Warnings);

/// <summary>BCL-only PPTX extractor and existing-shape text patcher.</summary>
public sealed class PptxAdapter
{
    private const string PresentationRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = false };

    public PptxExtractionResult Extract(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = ReadAll(source); var package = Open(bytes);
        var slides = ReadSlides(package);
        var partitions = new List<DocumentPartition>();
        foreach (var slide in slides)
        {
            var nodes = new List<DocumentNode>(); var order = 0;
            foreach (var shape in slide.Shapes)
            {
                var extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["shape_id"] = JsonSerializer.SerializeToElement(shape.ShapeId), ["shape_name"] = JsonSerializer.SerializeToElement(shape.Name), ["shape_role"] = JsonSerializer.SerializeToElement(shape.Role) };
                extension["hidden_slide"] = JsonSerializer.SerializeToElement(slide.IsHidden);
                extension["hidden_object"] = JsonSerializer.SerializeToElement(shape.IsHidden);
                if (shape.Paragraphs is not null) extension["paragraphs"] = JsonSerializer.SerializeToElement(shape.Paragraphs);
                if (shape.ParagraphDetails is not null) extension["paragraph_details"] = JsonSerializer.SerializeToElement(shape.ParagraphDetails);
                if (shape.IsTable) extension["is_table"] = JsonSerializer.SerializeToElement(true);
                extension["shape_type"] = JsonSerializer.SerializeToElement(shape.ShapeType);
                if (shape.ChartRelationshipIds is { Count: > 0 }) extension["chart_relationships"] = JsonSerializer.SerializeToElement(shape.ChartRelationshipIds);
                if (shape.ImageRelationshipIds.Count > 0)
                {
                    extension["image_relationships"] = JsonSerializer.SerializeToElement(shape.ImageRelationshipIds);
                    extension["image_relationship"] = JsonSerializer.SerializeToElement(shape.ImageRelationshipIds[0]);
                }
                var kind = shape.IsTable ? NodeKind.Table
                    : shape.ImageRelationshipIds.Count > 0 ? NodeKind.Image
                    : shape.ChartRelationshipIds is { Count: > 0 } ? NodeKind.Chart
                    : shape.DiagramRelationshipIds is { Count: > 0 } ? NodeKind.Diagram
                    : StringComparer.Ordinal.Equals(shape.ShapeType, "connector") ? NodeKind.Connector
                    : NodeKind.Shape;
                var chartData = kind == NodeKind.Chart && shape.ChartRelationshipIds is { Count: > 0 }
                    ? ResolveChart(package, slide.PartUri, shape.ChartRelationshipIds[0]) : null;
                var diagramTexts = kind == NodeKind.Diagram && shape.DiagramRelationshipIds is { Count: > 0 }
                    ? ResolveDiagramTexts(package, slide.PartUri, shape.DiagramRelationshipIds[0]) : null;
                if (chartData is not null)
                {
                    if (!string.IsNullOrWhiteSpace(chartData.Title)) extension["chart_title"] = JsonSerializer.SerializeToElement(chartData.Title);
                    if (!string.IsNullOrWhiteSpace(chartData.Type)) extension["chart_type"] = JsonSerializer.SerializeToElement(chartData.Type);
                    if (chartData.Series.Count > 0) extension["chart_series"] = JsonSerializer.SerializeToElement(chartData.Series);
                }
                if (diagramTexts is { Count: > 0 }) extension["diagram_items"] = JsonSerializer.SerializeToElement(diagramTexts);
                NodeContent content = kind switch
                {
                    NodeKind.Image => new ReferenceNodeContent(ResolveImageReference(package, slide.PartUri, shape.ImageRelationshipIds[0]), shape.Description ?? shape.Name),
                    NodeKind.Table => new TableNodeContent(shape.TableRows ?? []),
                    NodeKind.Chart when chartData is not null => new TextNodeContent(string.IsNullOrWhiteSpace(chartData.Title) ? "図" : chartData.Title),
                    NodeKind.Diagram when diagramTexts is { Count: > 0 } => new TextNodeContent(string.Join("\n", diagramTexts)),
                    _ => CreateShapeTextContent(shape),
                };
                var editability = kind == NodeKind.Shape ? NodeEditability.EditableWithConstraints : NodeEditability.Protected;
                var layer = slide.IsHidden || shape.IsHidden || (kind == NodeKind.Shape && string.IsNullOrWhiteSpace(shape.Text))
                    ? ContentLayer.Hidden
                    : IsFurnitureRole(shape.Role) ? ContentLayer.Furniture : ContentLayer.Body;
                nodes.Add(new($"n_{Hash(slide.SlideId + ":" + shape.ShapeId)[..16]}", kind, null, order++, layer,
                    content, new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)]),
                    Geometry: shape.Geometry, Editability: editability, Extensions: extension));
            }
            if (!string.IsNullOrWhiteSpace(slide.NotesText))
            {
                var notesExtension = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["hidden_content_type"] = JsonSerializer.SerializeToElement("pptx-notes"),
                    ["hidden_slide"] = JsonSerializer.SerializeToElement(slide.IsHidden)
                };
                if (slide.NotesDetails is { Count: > 0 }) notesExtension["paragraph_details"] = JsonSerializer.SerializeToElement(slide.NotesDetails);
                nodes.Add(new($"n_{Hash(slide.SlideId + ":notes")[..16]}", NodeKind.SpeakerNotes, null, order,
                    slide.IsHidden ? ContentLayer.Hidden : ContentLayer.Metadata, new TextNodeContent(slide.NotesText),
                    new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("slide_id", slide.SlideId)]),
                    Editability: NodeEditability.Protected, Extensions: notesExtension));
            }
            partitions.Add(new DocumentPartition(slide.SlideId, partitions.Count, nodes, slide.PartUri));
        }
        var graph = new DocumentGraph("1.1", "doc_" + Hash(bytes)[..16], DocumentFormatKind.Pptx, partitions);
        return new(graph, slides, package.ToDictionary(x => x.Key, x => Hash(x.Value), StringComparer.Ordinal), Array.Empty<string>());
    }

    public PptxPatchPlan CreatePatchPlan(IEnumerable<PptxShapeTextEdit> edits)
    {
        var list = edits.ToArray();
        var dirty = list.Select(x => SlidePartFromId(x.SlideId)).ToHashSet(StringComparer.Ordinal);
        return new(list, dirty);
    }

    public PptxPatchPlan CreatePatchPlan(DocumentGraph baseline, DocumentGraph edited)
    {
        var baselineNotes = baseline.Nodes.Where(x => x.Kind == NodeKind.SpeakerNotes).ToDictionary(x => x.Id, x => (x.Content as TextNodeContent)?.Text ?? string.Empty);
        var editedNotes = edited.Nodes.Where(x => x.Kind == NodeKind.SpeakerNotes).ToDictionary(x => x.Id, x => (x.Content as TextNodeContent)?.Text ?? string.Empty);
        if (baselineNotes.Any(x => !editedNotes.TryGetValue(x.Key, out var text) || !StringComparer.Ordinal.Equals(text, x.Value)) || editedNotes.Any(x => !baselineNotes.ContainsKey(x.Key)))
            throw new InvalidOperationException("Speaker notes edits are protected and require a dedicated notes patch provider.");
        var before = Shapes(baseline).ToDictionary(x => (x.Slide, x.Shape), x => x.Text);
        var editedShapes = Shapes(edited).ToArray();
        var added = editedShapes.Where(x => !before.ContainsKey((x.Slide, x.Shape))).ToArray();
        if (added.Length > 0) throw new InvalidOperationException("New PPTX shapes require a template insertion provider and are not silently ignored.");
        var edits = editedShapes.Where(x => before.TryGetValue((x.Slide, x.Shape), out var text) && !StringComparer.Ordinal.Equals(text, x.Text))
            .Select(x => new PptxShapeTextEdit(x.Slide, x.Shape, x.Text)).ToArray();
        return CreatePatchPlan(edits);
    }

    public PptxRestoreResult Restore(Stream original, PptxPatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(plan);
        var source = ReadAll(original); if (plan.Edits.Count == 0) return new(source, true, plan, Array.Empty<string>());
        var package = Open(source);
        foreach (var group in plan.Edits.GroupBy(x => x.SlideId, StringComparer.Ordinal))
        {
            var part = SlidePartFromId(group.Key);
            if (!package.TryGetValue(part, out var xml)) throw new InvalidDataException($"Slide not found: {group.Key}");
            package[part] = PatchSlide(xml, group);
        }
        return new(WritePackage(package), false, plan, Array.Empty<string>());
    }

    private static IEnumerable<(string Slide, string Shape, string Text)> Shapes(DocumentGraph graph) => graph.Partitions.SelectMany(partition => partition.Nodes.Where(node => node.Kind == NodeKind.Shape).Select(node => (
        partition.Id, node.Source?.Locators.FirstOrDefault(x => x.Kind == "shape_id")?.Value ?? node.Id, ShapeText(node.Content))));
    private static string ShapeText(NodeContent content) => content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => string.Empty,
    };
    private static string SlidePartFromId(string slideId) => slideId.StartsWith("slide", StringComparison.OrdinalIgnoreCase) ? "ppt/slides/" + slideId.ToLowerInvariant() + ".xml" : "ppt/slides/slide" + slideId + ".xml";
    private sealed record Relationship(string Id, string Target, string Type);

    private static List<PptxSlideRecord> ReadSlides(Dictionary<string, byte[]> package)
    {
        var result = new List<PptxSlideRecord>();
        var resolverCache = new Dictionary<string, PlaceholderBulletResolver>(StringComparer.Ordinal);
        if (!package.TryGetValue("ppt/presentation.xml", out var presentation)) throw new InvalidDataException("Presentation part missing.");
        var presentationRels = ReadRelationships(package, "ppt/_rels/presentation.xml.rels");
        using var reader = XmlReader.Create(new MemoryStream(presentation), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId")
        {
            var id = reader.GetAttribute("id") ?? (result.Count + 1).ToString(); var rid = reader.GetAttribute("id", PresentationRelNs) ?? reader.GetAttribute("r:id") ?? "";
            if (!presentationRels.TryGetValue(rid, out var target) || !package.TryGetValue(target.Target, out var slideBytes)) continue;
            var slideId = "slide" + (result.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bulletResolver = BuildBulletResolver(package, target.Target, resolverCache);
            var shapes = ReadShapes(slideBytes, slideId, out _, bulletResolver);
            var (notes, notesDetails) = ReadNotesParagraphs(package, target.Target);
            result.Add(new(slideId, target.Target, shapes, notes, notesDetails, IsSlideHidden(slideBytes)));
            _ = id;
        }
        if (result.Count == 0)
            foreach (var part in package.Keys.Where(x => x.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && x.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
            {
                var slideId = Path.GetFileNameWithoutExtension(part);
                var bulletResolver = BuildBulletResolver(package, part, resolverCache);
                var (notes, notesDetails) = ReadNotesParagraphs(package, part);
                result.Add(new(slideId, part, ReadShapes(package[part], slideId, out _, bulletResolver), notes, notesDetails, IsSlideHidden(package[part])));
            }
        return result;
    }

    private static bool IsSlideHidden(byte[] bytes)
    {
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sld") continue;
            var show = reader.GetAttribute("show");
            return show is "0" || bool.TryParse(show, out var parsed) && !parsed;
        }
        return false;
    }

    private readonly record struct AffineTransform(double M11, double M12, double M21, double M22, double Tx, double Ty)
    {
        public static AffineTransform Identity => new(1, 0, 0, 1, 0, 0);
        public (double X, double Y) Apply(double x, double y) =>
            (M11 * x + M12 * y + Tx, M21 * x + M22 * y + Ty);

        public static AffineTransform operator *(AffineTransform left, AffineTransform right) => new(
            left.M11 * right.M11 + left.M12 * right.M21,
            left.M11 * right.M12 + left.M12 * right.M22,
            left.M21 * right.M11 + left.M22 * right.M21,
            left.M21 * right.M12 + left.M22 * right.M22,
            left.M11 * right.Tx + left.M12 * right.Ty + left.Tx,
            left.M21 * right.Tx + left.M22 * right.Ty + left.Ty);

        public static AffineTransform Translation(double x, double y) => new(1, 0, 0, 1, x, y);
        public static AffineTransform Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
        public static AffineTransform Rotation(double degrees)
        {
            var radians = degrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new(cos, -sin, sin, cos, 0, 0);
        }
    }

    private sealed class GroupFrame
    {
        public AffineTransform Transform { get; set; } = AffineTransform.Identity;
    }

    private static AffineTransform ParseGroupTransform(XmlReader source)
    {
        var offX = 0d; var offY = 0d; var extX = 0d; var extY = 0d;
        var childX = 0d; var childY = 0d; var childWidth = 0d; var childHeight = 0d;
        var hasExt = false; var hasChildExt = false;
        var rotation = 0d; var flipH = false; var flipV = false;
        using var subtree = source.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;
            if (subtree.LocalName == "xfrm")
            {
                rotation = ParseDouble(subtree.GetAttribute("rot")) / 60000.0;
                flipH = IsOn(subtree.GetAttribute("flipH"));
                flipV = IsOn(subtree.GetAttribute("flipV"));
            }
            else if (subtree.LocalName == "off")
            {
                offX = ParseDouble(subtree.GetAttribute("x"));
                offY = ParseDouble(subtree.GetAttribute("y"));
            }
            else if (subtree.LocalName == "ext")
            {
                extX = ParseDouble(subtree.GetAttribute("cx"));
                extY = ParseDouble(subtree.GetAttribute("cy"));
                hasExt = true;
            }
            else if (subtree.LocalName == "chOff")
            {
                childX = ParseDouble(subtree.GetAttribute("x"));
                childY = ParseDouble(subtree.GetAttribute("y"));
            }
            else if (subtree.LocalName == "chExt")
            {
                childWidth = ParseDouble(subtree.GetAttribute("cx"));
                childHeight = ParseDouble(subtree.GetAttribute("cy"));
                hasChildExt = true;
            }
        }

        // chExt may be omitted in malformed/degenerate files. Preserve finite child
        // coordinates in that case instead of introducing an infinite scale.
        var scaleX = !hasExt || !hasChildExt || childWidth == 0 ? 1 : extX / childWidth;
        var scaleY = !hasExt || !hasChildExt || childHeight == 0 ? 1 : extY / childHeight;
        if (!double.IsFinite(scaleX)) scaleX = 1;
        if (!double.IsFinite(scaleY)) scaleY = 1;
        var mapped = AffineTransform.Translation(offX, offY) *
                     AffineTransform.Scale(scaleX, scaleY) *
                     AffineTransform.Translation(-childX, -childY);
        var centerX = offX + extX / 2;
        var centerY = offY + extY / 2;
        var orientation = AffineTransform.Translation(centerX, centerY) *
                          AffineTransform.Rotation(rotation) *
                          (flipH ? AffineTransform.Scale(-1, 1) : AffineTransform.Identity) *
                          (flipV ? AffineTransform.Scale(1, -1) : AffineTransform.Identity) *
                          AffineTransform.Translation(-centerX, -centerY);
        return orientation * mapped;
    }

    private static AffineTransform ShapeOrientation(Geometry geometry, bool flipH, bool flipV)
    {
        var centerX = geometry.X + geometry.Width / 2;
        var centerY = geometry.Y + geometry.Height / 2;
        return AffineTransform.Translation(centerX, centerY) *
               AffineTransform.Rotation(geometry.RotationDegrees) *
               (flipH ? AffineTransform.Scale(-1, 1) : AffineTransform.Identity) *
               (flipV ? AffineTransform.Scale(1, -1) : AffineTransform.Identity) *
               AffineTransform.Translation(-centerX, -centerY);
    }

    private static Geometry TransformGeometry(Geometry geometry, AffineTransform transform)
    {
        var p1 = transform.Apply(geometry.X, geometry.Y);
        var p2 = transform.Apply(geometry.X + geometry.Width, geometry.Y);
        var p3 = transform.Apply(geometry.X + geometry.Width, geometry.Y + geometry.Height);
        var p4 = transform.Apply(geometry.X, geometry.Y + geometry.Height);
        var minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        var minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        var maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        var maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        var angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X) * 180 / Math.PI;
        if (!double.IsFinite(angle)) angle = 0;
        return geometry with
        {
            X = minX, Y = minY,
            Width = Math.Max(0, maxX - minX), Height = Math.Max(0, maxY - minY),
            RotationDegrees = angle,
        };
    }

    private static List<PptxShapeRecord> ReadShapes(byte[] bytes, string slideId, out string? notes, PlaceholderBulletResolver bulletResolver)
    {
        notes = null; var result = new List<PptxShapeRecord>(); var groupStack = new Stack<GroupFrame>(); using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "grpSp") { groupStack.Push(new GroupFrame()); continue; }
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "grpSp") { if (groupStack.Count > 0) groupStack.Pop(); continue; }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "grpSpPr" && groupStack.Count > 0) { groupStack.Peek().Transform = ParseGroupTransform(reader); continue; }
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("sp" or "graphicFrame" or "pic" or "cxnSp")) continue;
            var parentTransform = groupStack.Reverse().Aggregate(AffineTransform.Identity, (current, frame) => current * frame.Transform);
            var shapeType = reader.LocalName switch { "cxnSp" => "connector", "graphicFrame" => "graphic-frame", "pic" => "picture", _ => "shape" };
            using var subtree = reader.ReadSubtree(); var shapeId = ""; string? name = null; string? description = null; var text = new StringBuilder(); var imageRels = new List<string>(); var chartRels = new List<string>(); var diagramRels = new List<string>(); var isTable = false; var shapeHidden = false; Geometry? geometry = null; double? pendingRotation = null; var flipH = false; var flipV = false; string? placeholderType = null; string? placeholderIdx = null; string? connectorStartId = null; string? connectorEndId = null;
            var paragraphs = new List<string>(); var paragraphDetails = new List<PptxTextParagraph>(); StringBuilder? paragraph = null; var inTableCell = false;
            var paragraphRuns = new List<PptxTextRun>(); var paragraphLevel = 0; var paragraphBullet = false; string? paragraphBulletCharacter = null; var paragraphBulletSpecified = false; var paragraphOrdered = false; int? paragraphListNumber = null;
            var runBold = false; var runItalic = false; var runUnderline = false; var runStrike = false; string? runFont = null; double? runSize = null;
            var tableRows = new List<IReadOnlyList<TableCell>>(); List<TableCell>? tableRow = null; StringBuilder? tableCell = null; var tcGridSpan = 1; var tcRowSpan = 1; var tcHMerge = false; var tcVMerge = false;
            var autoNumCounters = new Dictionary<int, int>();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "cNvPr") { shapeId = subtree.GetAttribute("id") ?? ""; name = subtree.GetAttribute("name"); description = subtree.GetAttribute("descr"); shapeHidden = IsOn(subtree.GetAttribute("hidden")); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "ph") { placeholderType = subtree.GetAttribute("type") ?? "body"; placeholderIdx = subtree.GetAttribute("idx"); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "stCxn") connectorStartId = subtree.GetAttribute("id");
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "endCxn") connectorEndId = subtree.GetAttribute("id");
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tr") tableRow = [];
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tc")
                {
                    tableCell = new StringBuilder(); inTableCell = true;
                    tcGridSpan = ParseIntOr1(subtree.GetAttribute("gridSpan")); tcRowSpan = ParseIntOr1(subtree.GetAttribute("rowSpan"));
                    tcHMerge = IsOn(subtree.GetAttribute("hMerge")); tcVMerge = IsOn(subtree.GetAttribute("vMerge"));
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "p" && !inTableCell)
                {
                    paragraph = new StringBuilder(); paragraphRuns = []; paragraphLevel = 0; paragraphBullet = false; paragraphBulletCharacter = null; paragraphBulletSpecified = false; paragraphOrdered = false; paragraphListNumber = null;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "pPr" && !inTableCell)
                {
                    paragraphLevel = ParseInt(subtree.GetAttribute("lvl"));
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "buChar" && !inTableCell)
                {
                    paragraphBullet = true; paragraphBulletCharacter = subtree.GetAttribute("char") ?? "•"; paragraphBulletSpecified = true; paragraphOrdered = false;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "buAutoNum" && !inTableCell)
                {
                    paragraphBullet = true; paragraphBulletSpecified = true; paragraphOrdered = true;
                    var startAt = ParseIntNullable(subtree.GetAttribute("startAt"));
                    var nextNumber = autoNumCounters.TryGetValue(paragraphLevel, out var current) ? current + 1 : startAt ?? 1;
                    autoNumCounters[paragraphLevel] = nextNumber; paragraphListNumber = nextNumber;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "buNone" && !inTableCell) { paragraphBullet = false; paragraphBulletSpecified = true; paragraphOrdered = false; }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "r" && !inTableCell)
                {
                    runBold = false; runItalic = false; runUnderline = false; runStrike = false; runFont = null; runSize = null;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "rPr" && !inTableCell)
                {
                    runBold = IsOn(subtree.GetAttribute("b")); runItalic = IsOn(subtree.GetAttribute("i"));
                    runUnderline = !string.IsNullOrWhiteSpace(subtree.GetAttribute("u")) && !StringComparer.OrdinalIgnoreCase.Equals(subtree.GetAttribute("u"), "none");
                    runStrike = IsStrike(subtree.GetAttribute("strike"));
                    runFont = null; runSize = ParseDoubleNullable(subtree.GetAttribute("sz"));
                }
                else if (subtree.NodeType == XmlNodeType.Element && (subtree.LocalName is "latin" or "ea" or "cs") && !inTableCell)
                    runFont = subtree.GetAttribute("typeface") ?? runFont;
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "br" && !inTableCell)
                {
                    paragraph ??= new StringBuilder(); paragraph.Append('\n'); paragraphRuns.Add(new PptxTextRun("\n"));
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t")
                {
                    var value = subtree.ReadElementContentAsString(); text.Append(value); tableCell?.Append(value); paragraph?.Append(value);
                    if (!inTableCell && paragraph is not null) paragraphRuns.Add(new(value, runBold, runItalic, runUnderline, runFont, runSize, runStrike));
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tbl") isTable = true;
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "chart")
                {
                    var rid = subtree.GetAttribute("id", PresentationRelNs) ?? subtree.GetAttribute("r:id");
                    if (rid is not null) chartRels.Add(rid);
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "relIds")
                {
                    var rid = subtree.GetAttribute("dm", PresentationRelNs) ?? subtree.GetAttribute("r:dm");
                    if (rid is not null) diagramRels.Add(rid);
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "blip") { var rid = subtree.GetAttribute("embed", PresentationRelNs) ?? subtree.GetAttribute("r:embed"); if (rid is not null) imageRels.Add(rid); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "xfrm") { pendingRotation = ParseDoubleNullable(subtree.GetAttribute("rot")); flipH = IsOn(subtree.GetAttribute("flipH")); flipV = IsOn(subtree.GetAttribute("flipV")); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "off") { var x = ParseDouble(subtree.GetAttribute("x")); var y = ParseDouble(subtree.GetAttribute("y")); geometry = new Geometry("pptx-emu", x, y, 0, 0, (pendingRotation ?? 0) / 60000.0); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "ext")
                {
                    var width = ParseDouble(subtree.GetAttribute("cx")); var height = ParseDouble(subtree.GetAttribute("cy"));
                    geometry = geometry is null ? new Geometry("pptx-emu", 0, 0, width, height, (pendingRotation ?? 0) / 60000.0)
                        : geometry with { Width = width, Height = height };
                }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "tc")
                {
                    if (!tcHMerge) tableRow?.Add(new TableCell(tcVMerge ? string.Empty : tableCell?.ToString() ?? string.Empty, tcGridSpan, tcVMerge ? 0 : tcRowSpan));
                    tableCell = null; inTableCell = false;
                }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "tr") { if (tableRow is not null) tableRows.Add(tableRow); tableRow = null; }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "p" && !inTableCell && paragraph is not null)
                {
                    if (!paragraphBulletSpecified && placeholderType is not null &&
                        bulletResolver.Resolve(placeholderType, placeholderIdx, paragraphLevel) is { } inherited)
                    {
                        paragraphBullet = inherited.IsBullet; paragraphBulletCharacter = inherited.Char;
                    }
                    paragraphs.Add(paragraph.ToString());
                    paragraphDetails.Add(new PptxTextParagraph(paragraph.ToString(), paragraphLevel, paragraphBullet, paragraphBulletCharacter, paragraphRuns.ToArray(), paragraphOrdered, paragraphListNumber));
                    paragraph = null;
                }
            }
            if (geometry is not null) geometry = TransformGeometry(geometry, parentTransform * ShapeOrientation(geometry, flipH, flipV));
            var role = InferRole(placeholderType, name);
            var paragraphText = paragraphs.Count == 0 ? text.ToString().TrimEnd('\r', '\n') : string.Join('\n', paragraphs);
            result.Add(new(slideId, shapeId, name, paragraphText, isTable, imageRels, geometry, tableRows, role, paragraphs, paragraphDetails,
                string.IsNullOrWhiteSpace(description) ? null : description, shapeType, chartRels, diagramRels, connectorStartId, connectorEndId, shapeHidden));
        }
        ResolveConnectorLabels(result);
        return result;
    }

    private static void ResolveConnectorLabels(List<PptxShapeRecord> shapes)
    {
        if (!shapes.Any(shape => StringComparer.Ordinal.Equals(shape.ShapeType, "connector"))) return;
        var labels = shapes.ToDictionary(shape => shape.ShapeId, shape => FirstLine(shape.Text), StringComparer.Ordinal);
        for (var index = 0; index < shapes.Count; index++)
        {
            var shape = shapes[index];
            if (!StringComparer.Ordinal.Equals(shape.ShapeType, "connector") || shape.ConnectorStartId is null || shape.ConnectorEndId is null) continue;
            if (!labels.TryGetValue(shape.ConnectorStartId, out var start) || !labels.TryGetValue(shape.ConnectorEndId, out var end)) continue;
            if (start.Length == 0 || end.Length == 0) continue;
            shapes[index] = shape with { Text = $"{start} → {end}" };
        }
    }

    private static string FirstLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var newline = normalized.IndexOf('\n');
        return (newline >= 0 ? normalized[..newline] : normalized).Trim();
    }

    private static NodeContent CreateShapeTextContent(PptxShapeRecord shape)
    {
        var details = shape.ParagraphDetails;
        if (details is null || details.Count == 0 || !details.SelectMany(item => item.Runs ?? []).Any(run => run.Bold || run.Italic || run.Underline || run.Strike))
            return new TextNodeContent(shape.Text);
        var runs = new List<TextRun>();
        foreach (var paragraph in details)
        {
            if (runs.Count > 0) runs.Add(new TextRun("\n", Kind: TextRunKind.LineBreak));
            foreach (var run in paragraph.Runs ?? [])
                runs.Add(new TextRun(run.Text, Bold: run.Bold, Italic: run.Italic, Underline: run.Underline, Strike: run.Strike));
        }
        return new RichTextNodeContent(runs);
    }

    private static (string? Text, IReadOnlyList<PptxTextParagraph>? Details) ReadNotesParagraphs(Dictionary<string, byte[]> package, string slidePart)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return (null, null);
        var rels = slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels";
        var relationships = ReadRelationships(package, rels);
        var notes = relationships.Values.FirstOrDefault(x => x.Type.Contains("notesSlide", StringComparison.OrdinalIgnoreCase));
        if (notes is null || !package.TryGetValue(notes.Target, out var bytes)) return (null, null);
        // Notes slides use the same <p:sp>/<p:ph>/<p:txBody> shape schema as ordinary slides, so
        // the same paragraph/run reader recovers bold runs and buChar bullets (P10) instead of the
        // old flat <a:t> concatenation. Only the "body" placeholder is real notes content: the
        // sldImg/sldNum placeholders on a real notes slide are template furniture with no text.
        var shapes = ReadShapes(bytes, "notes", out _, PlaceholderBulletResolver.Empty);
        var body = shapes.FirstOrDefault(shape => StringComparer.OrdinalIgnoreCase.Equals(shape.Role, "body"))
            ?? shapes.FirstOrDefault(shape => !string.IsNullOrWhiteSpace(shape.Text));
        if (body is not null) return (body.Text, body.ParagraphDetails);
        // Fallback for a notes part that does not follow the standard placeholder shape: recover at
        // least the flattened text, matching the previous (pre-P10) behavior.
        var flat = ReadAllRunText(bytes);
        return (string.IsNullOrEmpty(flat) ? null : flat, null);
    }

    private static string ReadAllRunText(byte[] bytes)
    {
        var text = new StringBuilder(); using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") text.Append(reader.ReadElementContentAsString());
        return text.ToString();
    }

    // --------------------------------------------------------------- P05: master/layout bullet inheritance --
    // A placeholder paragraph with no local buChar/buNone/buAutoNum inherits its bullet from the
    // slide layout's matching <p:ph> (matched by idx, falling back to type) and, if that layout
    // placeholder's <a:lstStyle> says nothing for the paragraph's level either, from the slide
    // master's p:txStyles category (titleStyle/bodyStyle/otherStyle) for that level. This mirrors
    // ECMA-376 placeholder inheritance generically; it is not specific to any one fixture.
    private static PlaceholderBulletResolver BuildBulletResolver(Dictionary<string, byte[]> package, string slidePart, Dictionary<string, PlaceholderBulletResolver> cache)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return PlaceholderBulletResolver.Empty;
        var slideRels = ReadRelationships(package, slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels");
        var layoutRel = slideRels.Values.FirstOrDefault(x => x.Type.Contains("slideLayout", StringComparison.OrdinalIgnoreCase));
        if (layoutRel is null) return PlaceholderBulletResolver.Empty;
        if (cache.TryGetValue(layoutRel.Target, out var cached)) return cached;
        var resolver = PlaceholderBulletResolver.Empty;
        if (package.TryGetValue(layoutRel.Target, out var layoutBytes))
        {
            var layoutPh = ParseLayoutPlaceholderBullets(layoutBytes);
            var layoutSlash = layoutRel.Target.LastIndexOf('/');
            var masterStyles = new Dictionary<string, Dictionary<int, BulletInfo>>(StringComparer.OrdinalIgnoreCase);
            if (layoutSlash >= 0)
            {
                var layoutRels = ReadRelationships(package, layoutRel.Target[..layoutSlash] + "/_rels/" + layoutRel.Target[(layoutSlash + 1)..] + ".rels");
                var masterRel = layoutRels.Values.FirstOrDefault(x => x.Type.Contains("slideMaster", StringComparison.OrdinalIgnoreCase));
                if (masterRel is not null && package.TryGetValue(masterRel.Target, out var masterBytes)) masterStyles = ParseMasterTxStyles(masterBytes);
            }
            resolver = new PlaceholderBulletResolver(layoutPh, masterStyles);
        }
        cache[layoutRel.Target] = resolver;
        return resolver;
    }

    private static bool IsLevelElement(string localName, out int level)
    {
        level = 0;
        if (localName.Length < 6 || !localName.StartsWith("lvl", StringComparison.Ordinal) || !localName.EndsWith("pPr", StringComparison.Ordinal)) return false;
        if (!int.TryParse(localName.AsSpan(3, localName.Length - 6), out var number) || number < 1) return false;
        level = number - 1;
        return true;
    }

    private static Dictionary<(string Type, string? Idx), Dictionary<int, BulletInfo>> ParseLayoutPlaceholderBullets(byte[] bytes)
    {
        var result = new Dictionary<(string, string?), Dictionary<int, BulletInfo>>();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sp") continue;
            using var spReader = reader.ReadSubtree();
            string? phType = null; string? phIdx = null; var levels = new Dictionary<int, BulletInfo>();
            while (spReader.Read())
            {
                if (spReader.NodeType == XmlNodeType.Element && spReader.LocalName == "ph")
                { phType = (spReader.GetAttribute("type") ?? "body").ToLowerInvariant(); phIdx = spReader.GetAttribute("idx"); }
                else if (spReader.NodeType == XmlNodeType.Element && IsLevelElement(spReader.LocalName, out var level))
                {
                    using var lvlReader = spReader.ReadSubtree();
                    BulletInfo? info = null;
                    while (lvlReader.Read())
                    {
                        if (lvlReader.NodeType != XmlNodeType.Element) continue;
                        if (lvlReader.LocalName == "buNone") info = new BulletInfo(false, null);
                        else if (lvlReader.LocalName == "buChar") info = new BulletInfo(true, lvlReader.GetAttribute("char") ?? "•");
                        else if (lvlReader.LocalName == "buAutoNum") info = new BulletInfo(true, null);
                    }
                    if (info is { } resolved) levels[level] = resolved;
                }
            }
            if (phType is not null) result[(phType, phIdx)] = levels;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<int, BulletInfo>> ParseMasterTxStyles(byte[] bytes)
    {
        var result = new Dictionary<string, Dictionary<int, BulletInfo>>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("titleStyle" or "bodyStyle" or "otherStyle")) continue;
            var category = reader.LocalName; using var styleReader = reader.ReadSubtree(); var levels = new Dictionary<int, BulletInfo>();
            while (styleReader.Read())
            {
                if (styleReader.NodeType != XmlNodeType.Element || !IsLevelElement(styleReader.LocalName, out var level)) continue;
                using var lvlReader = styleReader.ReadSubtree();
                BulletInfo? info = null;
                while (lvlReader.Read())
                {
                    if (lvlReader.NodeType != XmlNodeType.Element) continue;
                    if (lvlReader.LocalName == "buNone") info = new BulletInfo(false, null);
                    else if (lvlReader.LocalName == "buChar") info = new BulletInfo(true, lvlReader.GetAttribute("char") ?? "•");
                    else if (lvlReader.LocalName == "buAutoNum") info = new BulletInfo(true, null);
                }
                if (info is { } resolved) levels[level] = resolved;
            }
            result[category] = levels;
        }
        return result;
    }

    private readonly record struct BulletInfo(bool IsBullet, string? Char);

    private sealed class PlaceholderBulletResolver(Dictionary<(string Type, string? Idx), Dictionary<int, BulletInfo>> layoutPh, Dictionary<string, Dictionary<int, BulletInfo>> masterStyles)
    {
        public static readonly PlaceholderBulletResolver Empty = new([], []);

        public BulletInfo? Resolve(string placeholderType, string? placeholderIdx, int level)
        {
            var type = placeholderType.ToLowerInvariant();
            var byIdx = placeholderIdx is not null && layoutPh.TryGetValue((type, placeholderIdx), out var idxMatch) ? idxMatch : null;
            var layoutMatch = byIdx ?? layoutPh.FirstOrDefault(entry => entry.Key.Type == type).Value;
            if (layoutMatch is not null && layoutMatch.TryGetValue(level, out var fromLayout)) return fromLayout;
            var category = type is "title" or "ctrtitle" ? "titleStyle" : type is "body" or "obj" or "text" ? "bodyStyle" : "otherStyle";
            return masterStyles.TryGetValue(category, out var byCategory) && byCategory.TryGetValue(level, out var fromMaster) ? fromMaster : null;
        }
    }

    // ------------------------------------------------------------------------------- P06: charts --
    private static OpenXmlChartData? ResolveChart(Dictionary<string, byte[]> package, string slidePart, string relationshipId)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return null;
        var relationships = ReadRelationships(package, slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels");
        return relationships.TryGetValue(relationshipId, out var relationship) && package.TryGetValue(relationship.Target, out var bytes)
            ? OpenXmlChartReader.Read(bytes) : null;
    }

    // ---------------------------------------------------------------------------- P07: SmartArt --
    private static IReadOnlyList<string>? ResolveDiagramTexts(Dictionary<string, byte[]> package, string slidePart, string relationshipId)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return null;
        var relationships = ReadRelationships(package, slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels");
        return relationships.TryGetValue(relationshipId, out var relationship) && package.TryGetValue(relationship.Target, out var bytes)
            ? ParseDiagramTexts(bytes) : null;
    }

    private static IReadOnlyList<string> ParseDiagramTexts(byte[] bytes)
    {
        var result = new List<string>();
        using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "pt") continue;
            using var subtree = reader.ReadSubtree(); var text = new StringBuilder();
            while (subtree.Read())
                // dgm:t (the text-run wrapper) and a:t (the run itself) share the local name "t";
                // ReadElementContentAsString throws on dgm:t because it has element children, so only
                // the DrawingML-namespaced a:t (the real text) is read here. Nodes without a dgm:t at
                // all (the doc/parTrans/sibTrans points) simply contribute nothing and are skipped.
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t" && StringComparer.Ordinal.Equals(subtree.NamespaceURI, DrawingNs))
                    text.Append(subtree.ReadElementContentAsString());
            var value = text.ToString().Trim();
            if (value.Length > 0) result.Add(value);
        }
        return result;
    }

    private static string ResolveImageReference(Dictionary<string, byte[]> package, string slidePart, string relationshipId)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return relationshipId;
        var rels = slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels";
        var relationships = ReadRelationships(package, rels);
        return relationships.TryGetValue(relationshipId, out var relationship) &&
            relationship.Type.Contains("image", StringComparison.OrdinalIgnoreCase)
            ? relationship.Target
            : relationshipId;
    }

    private static byte[] PatchSlide(byte[] bytes, IEnumerable<PptxShapeTextEdit> edits)
    {
        var document = new XmlDocument { PreserveWhitespace = true }; using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml); document.Load(reader); var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("p", "http://schemas.openxmlformats.org/presentationml/2006/main"); ns.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        foreach (var edit in edits)
        {
            var shape = document.SelectSingleNode($"//p:sp[.//p:cNvPr[@id='{EscapeXPath(edit.ShapeId)}']] | //p:graphicFrame[.//p:cNvPr[@id='{EscapeXPath(edit.ShapeId)}']]", ns); if (shape is null) continue;
            var body = shape.SelectSingleNode("./p:txBody", ns);
            if (body is null) continue;
            var paragraphs = body.SelectNodes("./a:p", ns)!;
            var lines = edit.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            if (paragraphs.Count == 0)
            {
                AppendParagraph(document, body, lines[0], ns);
                paragraphs = body.SelectNodes("./a:p", ns)!;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                XmlElement target;
                if (i < paragraphs.Count) target = (XmlElement)paragraphs[i]!;
                else
                {
                    target = (XmlElement)paragraphs[paragraphs.Count - 1]!.CloneNode(true);
                    body.AppendChild(target);
                    paragraphs = body.SelectNodes("./a:p", ns)!;
                }
                ReplaceParagraphText(document, target, lines[i], ns);
            }
            for (var i = paragraphs.Count - 1; i >= lines.Length; i--) body.RemoveChild(paragraphs[i]!);
        }
        using var output = new MemoryStream(); using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false })) document.Save(writer); return output.ToArray();
    }
    private static string InferRole(string? placeholderType, string? name)
    {
        var value = placeholderType?.Trim().ToLowerInvariant();
        if (value is "title" or "ctrtitle") return "title";
        if (value is "subtitle") return "subtitle";
        if (value is "body" or "obj" or "text") return "body";
        if (value is "ftr" or "footer") return "footer";
        if (value is "dt" or "date") return "date";
        if (value is "sldnum" or "slidenum" or "slide-number") return "slide-number";
        var fallback = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (fallback.Contains("title", StringComparison.Ordinal)) return "title";
        if (fallback.Contains("subtitle", StringComparison.Ordinal) || fallback.Contains("sub-title", StringComparison.Ordinal)) return "subtitle";
        if (fallback.Contains("footer", StringComparison.Ordinal)) return "footer";
        if (fallback.Contains("slide number", StringComparison.Ordinal) || fallback.Contains("slide-number", StringComparison.Ordinal)) return "slide-number";
        if (fallback.Equals("date", StringComparison.Ordinal) || fallback.StartsWith("date ", StringComparison.Ordinal)) return "date";
        if (fallback.Contains("body", StringComparison.Ordinal) || fallback.Contains("content", StringComparison.Ordinal)) return "body";
        return "other";
    }
    private static bool IsFurnitureRole(string role) => role is "footer" or "date" or "slide-number" or "sldnum" or "ftr";
    private static void AppendParagraph(XmlDocument document, XmlNode body, string text, XmlNamespaceManager ns)
    {
        var paragraph = document.CreateElement("a", "p", ns.LookupNamespace("a")!);
        var run = document.CreateElement("a", "r", ns.LookupNamespace("a")!);
        var t = document.CreateElement("a", "t", ns.LookupNamespace("a")!); t.InnerText = text;
        run.AppendChild(t); paragraph.AppendChild(run); body.AppendChild(paragraph);
    }
    private static void ReplaceParagraphText(XmlDocument document, XmlElement paragraph, string text, XmlNamespaceManager ns)
    {
        var texts = paragraph.SelectNodes(".//a:t", ns)!;
        if (texts.Count == 0)
        {
            var run = document.CreateElement("a", "r", ns.LookupNamespace("a")!);
            var t = document.CreateElement("a", "t", ns.LookupNamespace("a")!); t.InnerText = text; run.AppendChild(t);
            var end = paragraph.SelectSingleNode("./a:endParaRPr", ns);
            paragraph.InsertBefore(run, end);
            return;
        }
        // Keep the original a:r/a:rPr boundaries.  Text growth is assigned to the
        // final run, matching the DOCX character-map policy, so each run keeps its
        // source font, size, language, color, and other presentation formatting.
        var originalLengths = Enumerable.Range(0, texts.Count).Select(index => texts[index]!.InnerText.Length).ToArray();
        var offset = 0;
        for (var index = 0; index < texts.Count; index++)
        {
            var length = index == texts.Count - 1
                ? text.Length - offset
                : Math.Min(originalLengths[index], Math.Max(0, text.Length - offset));
            texts[index]!.InnerText = text.Substring(offset, length);
            offset += length;
        }
    }
    private static string EscapeXPath(string value) => value.Replace("'", "&apos;", StringComparison.Ordinal);
    private static double ParseDouble(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static double? ParseDoubleNullable(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    private static int ParseInt(string? value) => int.TryParse(value, out var result) ? result : 0;
    private static int ParseIntOr1(string? value) => int.TryParse(value, out var result) && result > 0 ? result : 1;
    private static int? ParseIntNullable(string? value) => int.TryParse(value, out var result) ? result : null;
    private static bool IsOn(string? value) => value is not null && !StringComparer.OrdinalIgnoreCase.Equals(value, "0") &&
        !StringComparer.OrdinalIgnoreCase.Equals(value, "off") && !StringComparer.OrdinalIgnoreCase.Equals(value, "false");
    // OOXML a:rPr@strike is an enum (noStrike/sngStrike/dblStrike/...); anything other than an
    // explicit "noStrike" (or an absent attribute) means the run is struck through (P14-3).
    private static bool IsStrike(string? value) => value is not null && !StringComparer.OrdinalIgnoreCase.Equals(value, "noStrike");
    private static byte[] ReadAll(Stream stream) { using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static Dictionary<string, byte[]> Open(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var zip = new ZipArchive(stream, ZipArchiveMode.Read); var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries) { if (entry.FullName.Contains("..", StringComparison.Ordinal) || entry.FullName.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe ZIP entry path."); using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); result[entry.FullName] = output.ToArray(); }
        return result;
    }
    private static byte[] WritePackage(Dictionary<string, byte[]> parts)
    {
        using var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) foreach (var part in parts.OrderBy(x => x.Key, StringComparer.Ordinal)) { var entry = zip.CreateEntry(part.Key, CompressionLevel.Optimal); using var output = entry.Open(); output.Write(part.Value); }
        return stream.ToArray();
    }
    private static Dictionary<string, Relationship> ReadRelationships(Dictionary<string, byte[]> package, string path)
    {
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal); if (!package.TryGetValue(path, out var bytes)) return result; using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml); while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship") { var id = reader.GetAttribute("Id"); var target = reader.GetAttribute("Target"); var type = reader.GetAttribute("Type") ?? ""; if (id is null || target is null) continue; var basePath = path[..path.LastIndexOf("/_rels/", StringComparison.Ordinal)]; var resolved = target.StartsWith("/", StringComparison.Ordinal) ? NormalizePartPath(target) : NormalizePartPath(basePath + "/" + target); result[id] = new(id, resolved, type); }
        return result;
    }

    private static string NormalizePartPath(string value)
    {
        var stack = new List<string>();
        foreach (var segment in value.Replace("\\", "/", StringComparison.Ordinal).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(segment);
        }
        return string.Join('/', stack);
    }
}

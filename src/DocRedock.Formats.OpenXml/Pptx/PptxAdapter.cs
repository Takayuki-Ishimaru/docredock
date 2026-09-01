using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using DocRedock.Core.Documents;
using DocRedock.VisualInference;
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
    bool IsHidden = false,
    string? ShapePreset = null,
    string? ConnectorHeadArrow = null,
    string? ConnectorTailArrow = null,
    IReadOnlyList<VisualPoint>? ConnectorPathPoints = null);
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
    public TimeSpan? VisualInferenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public PptxExtractionResult Extract(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ReadAll(source); var package = Open(bytes);
        var slides = ReadSlides(package);
        var partitions = new List<DocumentPartition>();
        var visualDiagnostics = new List<VisualDiagnostic>();
        foreach (var slide in slides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodes = new List<DocumentNode>(); var order = 0;
            var visualGraphs = BuildVisualGraphs(slide, out var visualLabelShapeIds, VisualInferenceTimeout, cancellationToken);
            visualDiagnostics.AddRange(visualGraphs.SelectMany(graph => graph.Diagnostics ?? []));
            var visual = visualGraphs.FirstOrDefault();
            var visualConnectorShapeIds = visualGraphs.SelectMany(graph => graph.Edges).Where(edge => edge.SourceId is not null && edge.TargetId is not null)
                .Select(edge => edge.SourceNodeId).Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
            var visualNodeShapeIds = visualGraphs.SelectMany(graph => graph.Nodes).Select(node => node.SourceNodeId)
                .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
            visualNodeShapeIds.UnionWith(slide.Shapes.Where(shape => StringComparer.Ordinal.Equals(shape.ShapeType, "connector"))
                .SelectMany(shape => new[] { shape.ConnectorStartId, shape.ConnectorEndId })
                .Where(id => id is not null).Cast<string>());
            foreach (var shape in slide.Shapes)
            {
                var extension = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["shape_id"] = JsonSerializer.SerializeToElement(shape.ShapeId), ["shape_name"] = JsonSerializer.SerializeToElement(shape.Name), ["shape_role"] = JsonSerializer.SerializeToElement(shape.Role) };
                extension["hidden_slide"] = JsonSerializer.SerializeToElement(slide.IsHidden);
                extension["hidden_object"] = JsonSerializer.SerializeToElement(shape.IsHidden);
                if (shape.Paragraphs is not null) extension["paragraphs"] = JsonSerializer.SerializeToElement(shape.Paragraphs);
                if (shape.ParagraphDetails is not null) extension["paragraph_details"] = JsonSerializer.SerializeToElement(shape.ParagraphDetails);
                if (shape.IsTable) extension["is_table"] = JsonSerializer.SerializeToElement(true);
                extension["shape_type"] = JsonSerializer.SerializeToElement(shape.ShapeType);
                if (!string.IsNullOrWhiteSpace(shape.ShapePreset)) extension["shape_preset"] = JsonSerializer.SerializeToElement(shape.ShapePreset);
                if (visualLabelShapeIds.Contains(shape.ShapeId)) extension["visual_edge_label"] = JsonSerializer.SerializeToElement(true);
                if (visualConnectorShapeIds.Contains(shape.ShapeId)) extension["visual_graph_edge"] = JsonSerializer.SerializeToElement(true);
                if (visualNodeShapeIds.Contains(shape.ShapeId)) extension["visual_graph_node"] = JsonSerializer.SerializeToElement(true);
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
            foreach (var visualGraph in visualGraphs)
            {
                var visualExtensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["visual_graph"] = JsonSerializer.SerializeToElement(visualGraph),
                    ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
                    ["visual_graph_member_shape_ids"] = JsonSerializer.SerializeToElement(
                        visualGraph.Nodes.Select(node => node.SourceNodeId).Where(id => id is not null).Cast<string>()
                            .Concat(visualGraph.Edges.Select(edge => edge.SourceNodeId).Where(id => id is not null).Cast<string>())
                            .Concat((visualGraph.SourceItems ?? [])
                                .Where(item => item.Disposition is VisualDisposition.ProjectedNode or
                                    VisualDisposition.ProjectedEdge or VisualDisposition.SuppressedDuplicate)
                                .SelectMany(item => item.SourceAnchor?.Locators ?? [])
                                .Where(locator => locator.Kind is "shape_id" or "connector")
                                .Select(locator => locator.Value))
                            .Concat(visualLabelShapeIds)
                            .Distinct(StringComparer.Ordinal).ToArray())
                };
                nodes.Add(new($"n_{Hash(slide.SlideId + ":visual:" + visualGraph.Id)[..16]}", NodeKind.Diagram, null, order++, ContentLayer.Derived,
                    new TextNodeContent("Visual flow"), new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("slide_id", slide.SlideId)]),
                    Editability: NodeEditability.Protected, Extensions: visualExtensions));
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
        var warnings = visualDiagnostics
            .Concat(slides.SelectMany(slide => slide.Shapes.Where(shape => shape.DiagramRelationshipIds is { Count: > 0 })
                .Select(shape => new VisualDiagnostic("VisualSemanticProjectionPartial", "SmartArt text was retained, but no native SmartArt topology was available.", shape.ShapeId, Fallback: "text list retained"))))
            .Select(d => $"{d.Code}: {d.Message}")
            .OrderBy(warning => warning, StringComparer.Ordinal).ToArray();
        return new(graph, slides, package.ToDictionary(x => x.Key, x => Hash(x.Value), StringComparer.Ordinal), warnings);
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
            using var subtree = reader.ReadSubtree(); var shapeId = ""; string? name = null; string? description = null; var text = new StringBuilder(); var imageRels = new List<string>(); var chartRels = new List<string>(); var diagramRels = new List<string>(); var isTable = false; var shapeHidden = false; Geometry? geometry = null; double? pendingRotation = null; var flipH = false; var flipV = false; string? placeholderType = null; string? placeholderIdx = null; string? connectorStartId = null; string? connectorEndId = null; string? shapePreset = null;
            string? connectorHeadArrow = null; string? connectorTailArrow = null;
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
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "headEnd") connectorHeadArrow = subtree.GetAttribute("type");
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tailEnd") connectorTailArrow = subtree.GetAttribute("type");
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "prstGeom") shapePreset = subtree.GetAttribute("prst");
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
            IReadOnlyList<VisualPoint>? connectorPathPoints = null;
            if (geometry is not null)
            {
                var shapeTransform = parentTransform * ShapeOrientation(geometry, flipH, flipV);
                if (StringComparer.Ordinal.Equals(shapeType, "connector"))
                {
                    var startPoint = shapeTransform.Apply(geometry.X, geometry.Y);
                    var endPoint = shapeTransform.Apply(geometry.X + geometry.Width, geometry.Y + geometry.Height);
                    connectorPathPoints =
                    [
                        new VisualPoint(startPoint.X, startPoint.Y),
                        new VisualPoint(endPoint.X, endPoint.Y),
                    ];
                }
                geometry = TransformGeometry(geometry, shapeTransform);
            }
            var role = InferRole(placeholderType, name);
            var paragraphText = paragraphs.Count == 0 ? text.ToString().TrimEnd('\r', '\n') : string.Join('\n', paragraphs);
            result.Add(new(slideId, shapeId, name, paragraphText, isTable, imageRels, geometry, tableRows, role, paragraphs, paragraphDetails,
                string.IsNullOrWhiteSpace(description) ? null : description, shapeType, chartRels, diagramRels, connectorStartId, connectorEndId, shapeHidden, shapePreset,
                connectorHeadArrow, connectorTailArrow, connectorPathPoints));
        }
        if (!result.Any(shape => StringComparer.Ordinal.Equals(shape.Role, "title")))
        {
            var inferredTitle = result
                .Select((shape, index) => (Shape: shape, Index: index))
                .Where(candidate => candidate.Shape.Role is "other" or "body" &&
                    IsTitleLike(candidate.Shape.ParagraphDetails ?? [], candidate.Shape.Geometry))
                .OrderBy(candidate => candidate.Shape.Geometry?.Y ?? double.MaxValue)
                .ThenBy(candidate => candidate.Shape.Geometry?.X ?? double.MaxValue)
                .FirstOrDefault();
            if (inferredTitle.Shape is not null)
                result[inferredTitle.Index] = inferredTitle.Shape with { Role = "title" };
        }
        ResolveConnectorLabels(result);
        return result;
    }

    private static void ResolveConnectorLabels(List<PptxShapeRecord> shapes)
    {
        if (!shapes.Any(shape => StringComparer.Ordinal.Equals(shape.ShapeType, "connector"))) return;
        // Malformed presentations can repeat cNvPr ids. Keep the first occurrence instead of
        // exposing Dictionary's raw duplicate-key exception during ordinary extraction.
        var labels = shapes.GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => FirstLine(group.First().Text), StringComparer.Ordinal);
        for (var index = 0; index < shapes.Count; index++)
        {
            var shape = shapes[index];
            if (!StringComparer.Ordinal.Equals(shape.ShapeType, "connector") || shape.ConnectorStartId is null || shape.ConnectorEndId is null) continue;
            if (!labels.TryGetValue(shape.ConnectorStartId, out var start) || !labels.TryGetValue(shape.ConnectorEndId, out var end)) continue;
            if (start.Length == 0 || end.Length == 0) continue;
            shapes[index] = shape with { Text = $"{start} → {end}" };
        }
    }

    private static IReadOnlyList<VisualGraph> BuildVisualGraphs(PptxSlideRecord slide, out HashSet<string> edgeLabelShapeIds,
        TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        var graph = BuildVisualGraph(slide, out edgeLabelShapeIds, inferenceTimeout, cancellationToken);
        if (graph is null) return [];
        var resolvedEdges = graph.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null).ToArray();
        if (resolvedEdges.Length == 0 || resolvedEdges.Length != graph.Edges.Count) return [graph];
        var parent = graph.Nodes.Select((_, index) => index).ToArray();
        int Find(int index) { while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; } return index; }
        void Union(int left, int right) { left = Find(left); right = Find(right); if (left != right) parent[Math.Min(left, right)] = Math.Max(left, right); }
        foreach (var edge in resolvedEdges)
        {
            var source = Array.FindIndex(graph.Nodes.ToArray(), node => node.Id == edge.SourceId);
            var target = Array.FindIndex(graph.Nodes.ToArray(), node => node.Id == edge.TargetId);
            if (source >= 0 && target >= 0) Union(source, target);
        }
        var groups = graph.Nodes.Select((node, index) => (node, index)).GroupBy(item => Find(item.index))
            .OrderBy(group => group.Min(item => item.node.Id), StringComparer.Ordinal).ToArray();
        if (groups.Length <= 1) return [graph];
        var result = new List<VisualGraph>(groups.Length);
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var nodeIds = groups[groupIndex].Select(item => item.node.Id).ToHashSet(StringComparer.Ordinal);
            var edges = graph.Edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null && nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId)).ToArray();
            var clusterId = $"{slide.SlideId}:cluster:{groupIndex:D3}";
            edges = edges.Select(edge => edge with
            {
                Evidence = edge.Evidence is null
                    ? null
                    : edge.Evidence with { ClusterId = clusterId }
            }).ToArray();
            var edgeIds = edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
            var sourceItems = graph.SourceItems?.Where(item =>
                item.ProjectedNodeId is null && item.ProjectedEdgeId is null ||
                item.ProjectedNodeId is not null && nodeIds.Contains(item.ProjectedNodeId) ||
                item.ProjectedEdgeId is not null && edgeIds.Contains(item.ProjectedEdgeId)).ToArray();
            var partition = graph with
            {
                Id = graph.Id + ":cluster:" + groupIndex.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                Nodes = groups[groupIndex].Select(item => item.node).ToArray(),
                Edges = edges,
                SourceItems = sourceItems,
            };
            result.Add(partition with { Quality = VisualGraphValidator.ComputeQuality(partition) });
        }
        return result;
    }

    private static VisualGraph? BuildVisualGraph(PptxSlideRecord slide, out HashSet<string> edgeLabelShapeIds,
        TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        edgeLabelShapeIds = labels;
        var connectors = slide.Shapes.Where(shape =>
            StringComparer.Ordinal.Equals(shape.ShapeType, "connector") && !shape.IsHidden).ToArray();
        var detachedArrowheads = AssociateDetachedArrowheads(connectors, slide.Shapes);
        var connectorDirections = connectors.ToDictionary(
            connector => connector.ShapeId,
            connector => detachedArrowheads.TryGetValue(connector.ShapeId, out var association)
                ? association.Direction
                : ConnectorDirection(connector),
            StringComparer.Ordinal);
        var directional = slide.Shapes.Where(shape => !shape.IsHidden && IsDirectionalShape(shape)).ToArray();
        // Textless directional shapes are still recognized visual content.  When no connector
        // exists there is no safe semantic edge to invent, so retain their absolute geometry
        // as deterministic fallback paths instead of silently dropping them.
        if (connectors.Length == 0 && directional.Length == 0) return null;
        var drawable = slide.Shapes.Where(shape => !shape.IsHidden &&
                !StringComparer.Ordinal.Equals(shape.ShapeType, "connector") &&
                !shape.IsTable && shape.Geometry is not null &&
                (!string.IsNullOrWhiteSpace(FirstLine(shape.Text)) || IsDirectionalShape(shape)))
            .OrderBy(shape => shape.ShapeId, StringComparer.Ordinal).ToArray();
        var byShapeId = drawable.GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Pre-classify small text shapes resting on a connector shaft as edge-label candidates,
        // the same way DocxAdapter.BuildDocxVisualGraph splits sharedNodeRecords (real shapes)
        // from labelRecords (IsTextBox shapes) before either ever reaches the shared inference
        // document. PPTX shapes carry no equally reliable structural "this is definitely a
        // label" marker, so the split here is geometric instead -- see
        // ClassifyEdgeLabelCandidateShapeIds below. Excluding these shapes from the node-
        // primitive pool (not from `drawable`/`byShapeId`, which still feed AssignEdgeLabels and
        // the source-item ledger further down) is what stops a label sitting mid-shaft from
        // being clustered onto the connector and misread by FindIntermediateNodeIds as a real
        // node blocking its own endpoints.
        var edgeLabelCandidateShapeIds = ClassifyEdgeLabelCandidateShapeIds(byShapeId, connectors);

        // Normalize the complete slide into one primitive document. Clustering and inference
        // are deliberately executed exactly once; connector projection below only consumes
        // these shared results.
        var canvasId = "pptx:" + slide.SlideId;
        var nodeShapeByVisualId = byShapeId.Values
            .Where(shape => !edgeLabelCandidateShapeIds.Contains(shape.ShapeId))
            .GroupBy(shape => "v_" + SafeVisualId(shape.ShapeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var inferenceNodes = nodeShapeByVisualId
            .Select(item =>
            {
                var shape = item.Value;
                var geometry = shape.Geometry!;
                var sourceAnchor = new SourceAnchor("pptx", slide.PartUri,
                    [new AnchorLocator("shape_id", shape.ShapeId)]);
                return (VisualPrimitive)new VisualNodePrimitive(item.Key, canvasId, sourceAnchor,
                    new VisualRect(geometry.X, geometry.Y, geometry.Width, geometry.Height),
                    PrimitiveBoundary(shape.ShapePreset), VisualLabel(shape),
                    Aliases: [new VisualIdentityAlias("shape_id", shape.ShapeId)],
                    IsHidden: shape.IsHidden);
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var inferenceConnectors = connectors
            .OrderBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(connector =>
            {
                var connectorAnchor = new SourceAnchor("pptx", slide.PartUri,
                    [new AnchorLocator("shape_id", connector.ShapeId)]);
                var direction = connectorDirections[connector.ShapeId];
                var path = new VisualConnectorPath(
                    ConnectorPoints(connector),
                    StartArrowhead: new ArrowheadEvidence(
                        direction is ConnectionDirection.Reverse or ConnectionDirection.Bidirectional,
                        Kind: connector.ConnectorHeadArrow, Confidence: 1),
                    EndArrowhead: new ArrowheadEvidence(
                        direction is ConnectionDirection.Forward or ConnectionDirection.Bidirectional,
                        Kind: connector.ConnectorTailArrow, Confidence: 1));
                return (VisualPrimitive)new VisualConnectorPrimitive(
                    connector.ShapeId, canvasId, connectorAnchor, path,
                    connector.ConnectorStartId, connector.ConnectorEndId,
                    Aliases: [new VisualIdentityAlias("shape_id", connector.ShapeId)],
                    IsHidden: connector.IsHidden);
            })
            .ToArray();
        var inferencePrimitives = inferenceNodes.Concat(inferenceConnectors).ToArray();
        var primitiveBounds = inferencePrimitives
            .Select(item => item.Bounds)
            .Where(item => item is not null)
            .Cast<VisualRect>()
            .ToArray();
        var primitiveDocument = new VisualPrimitiveDocument(
            "pptx:" + slide.SlideId,
            DocumentFormatKind.Pptx,
            [new VisualCanvas(canvasId, slide.PartUri, slide.SlideId,
                Math.Max(1, primitiveBounds.Select(item => item.Right).DefaultIfEmpty(1).Max()),
                Math.Max(1, primitiveBounds.Select(item => item.Bottom).DefaultIfEmpty(1).Max()),
                "pptx-emu",
                new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("slide_id", slide.SlideId)]))],
            inferencePrimitives);
        using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (inferenceTimeout is { } timeout)
        {
            if (timeout <= TimeSpan.Zero) inferenceCts.Cancel();
            else inferenceCts.CancelAfter(timeout);
        }
        var inferenceToken = inferenceCts.Token;
        var clusterer = new DiagramClusterer();
        var clusters = clusterer.Cluster(primitiveDocument);
        SoftConnectionResult inference;
        try
        {
            inferenceToken.ThrowIfCancellationRequested();
            inference = new SoftConnectionEngine().Infer(
                primitiveDocument, clusters, new SoftConnectionOptions(VisualInferenceContext.Current), inferenceToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            inference = new SoftConnectionResult([], [], [], Diagnostics:
                [new VisualExtractionDiagnostic("VisualInferenceTimeout",
                    "PPTX visual inference exceeded its configured time budget; all connectors remain fallback geometry.", slide.SlideId)]);
        }
        var inferredByConnector = inference.Resolved
            .GroupBy(item => item.ConnectorId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var paths = directional.Where(shape => shape.Geometry is not null)
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(shape => new VisualPath(
                "path_" + SafeVisualId(shape.ShapeId),
                RectanglePath(shape.Geometry!),
                shape.Geometry,
                new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)]),
                Confidence: 0.9,
                IsFallback: true,
                SourceNodeId: shape.ShapeId))
            .ToList();
        if (connectors.Length == 0)
        {
            var fallbackItems = new List<VisualSourceItem>();
            var seenDirectional = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in directional)
            {
                var itemId = "directional:" + SafeVisualId(shape.ShapeId);
                if (!seenDirectional.Add(shape.ShapeId))
                {
                    fallbackItems.Add(new VisualSourceItem(itemId + ":duplicate", VisualSourceItemKind.DirectionalShape,
                        VisualDisposition.SuppressedDuplicate, DuplicateOfSourceItemId: itemId,
                        Reason: "duplicate directional shape ID"));
                    continue;
                }
                fallbackItems.Add(new VisualSourceItem(itemId, VisualSourceItemKind.DirectionalShape,
                    VisualDisposition.VisualFallback, FallbackPathId: "path_" + SafeVisualId(shape.ShapeId),
                    Reason: "textless directional shape has no safe semantic endpoints",
                    SourceAnchor: new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)])));
            }
            var fallbackGraph = new VisualGraph(
                "pptx_" + SafeVisualId(slide.SlideId),
                [],
                [],
                [new VisualDiagnostic("VisualDirectionalShapeFallback", "Textless directional shapes were retained as geometry paths.",
                    Fallback: "directional shape geometry", Format: "pptx", PartUri: slide.PartUri,
                    PartitionId: slide.SlideId, SourceObjectId: directional.FirstOrDefault()?.ShapeId,
                    SourceObjectType: "directional-shape", Confidence: 0.9)],
                "LR",
                Paths: paths,
                SourceItems: fallbackItems);
            return fallbackGraph with { Quality = VisualGraphValidator.ComputeQuality(fallbackGraph) };
        }
        var edges = new List<VisualEdge>();
        var diagnostics = clusterer.Diagnostics
            .Concat(inference.Diagnostics ?? [])
            .Select(item => new VisualDiagnostic(item.Code, item.Message, item.PrimitiveId,
                Fallback: item.Code == "VisualInferenceTimeout" ? "connectors retained as visual fallback" : null,
                Remedy: item.Code == "VisualInferenceTimeout" ? "increase VisualInferenceTimeout or simplify the diagram" : null,
                Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                SourceObjectId: item.PrimitiveId,
                SourceObjectType: item.Code == "VisualInferenceTimeout" ? "inference" : "visual-primitive"))
            .ToList();
        var connectedShapeIds = new HashSet<string>(StringComparer.Ordinal);

        // Resolve every connector first, then reserve all selected endpoints before looking
        // for edge labels. This prevents a label assignment from consuming an endpoint that
        // belongs to a later connector.
        var projectedConnectors = new Dictionary<string,
            (string Start, string End, VisualEdgeResolution Resolution, VisualConnectionEvidence Evidence)>(
            StringComparer.Ordinal);
        var reservedEndpointShapeIds = connectors
            .SelectMany(connector => new[] { connector.ConnectorStartId, connector.ConnectorEndId })
            .Where(id => id is not null && byShapeId.ContainsKey(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connector in connectors.OrderBy(item => item.ShapeId, StringComparer.Ordinal))
        {
            inferredByConnector.TryGetValue(connector.ShapeId, out var pair);
            if (pair?.SourceId is null || pair.TargetId is null ||
                !nodeShapeByVisualId.TryGetValue(pair.SourceId, out var sourceShape) ||
                !nodeShapeByVisualId.TryGetValue(pair.TargetId, out var targetShape))
                continue;

            var physicalStart = pair.Direction == ConnectionDirection.Reverse
                ? targetShape.ShapeId : sourceShape.ShapeId;
            var physicalEnd = pair.Direction == ConnectionDirection.Reverse
                ? sourceShape.ShapeId : targetShape.ShapeId;
            if (StringComparer.Ordinal.Equals(physicalStart, physicalEnd)) continue;
            var evidence = ConnectionEvidence(pair, inference);
            projectedConnectors[connector.ShapeId] = (
                physicalStart,
                physicalEnd,
                pair.IsNative ? VisualEdgeResolution.NativeConnection : VisualEdgeResolution.GeometryInferred,
                evidence);
            reservedEndpointShapeIds.Add(physicalStart);
            reservedEndpointShapeIds.Add(physicalEnd);
        }
        var unresolvedConnectorIds = connectors
            .Where(connector => !projectedConnectors.ContainsKey(connector.ShapeId))
            .Select(connector => connector.ShapeId).ToHashSet(StringComparer.Ordinal);
        // Candidate retention and shaft intersection share one deterministic budget. Keep both
        // result sets staged so timeout or exhaustion cannot expose a partially analysed diagram.
        const long maxUnresolvedConnectorWorkItems = 250_000;
        long unresolvedConnectorWorkItems = 0;
        var unresolvedAnalysisBudgetExceeded = false;
        bool TrySpendUnresolvedAnalysis(long amount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inferenceToken.IsCancellationRequested) return false;
            if (amount < 0 || unresolvedConnectorWorkItems > maxUnresolvedConnectorWorkItems - amount)
            {
                unresolvedAnalysisBudgetExceeded = true;
                return false;
            }
            unresolvedConnectorWorkItems += amount;
            return true;
        }

        var stagedCandidateNodeShapeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in inference.Candidates)
        {
            if (!TrySpendUnresolvedAnalysis(1)) break;
            if (unresolvedConnectorIds.Contains(candidate.ConnectorId) && !candidate.IsHardRejected &&
                nodeShapeByVisualId.TryGetValue(candidate.NodeId, out var candidateShape))
                stagedCandidateNodeShapeIds.Add(candidateShape.ShapeId);
        }
        var stagedShaftNodeShapeIds = new HashSet<string>(StringComparer.Ordinal);
        if (!unresolvedAnalysisBudgetExceeded && !inferenceToken.IsCancellationRequested)
        {
            foreach (var connector in connectors.Where(item => unresolvedConnectorIds.Contains(item.ShapeId)))
            {
                if (!TrySpendUnresolvedAnalysis(1)) break;
                var points = ConnectorPoints(connector);
                if (points.Count < 2) continue;
                foreach (var shape in nodeShapeByVisualId.Values)
                {
                    var comparisonCost = Math.Max(1, points.Count - 1);
                    if (!TrySpendUnresolvedAnalysis(comparisonCost)) break;
                    var geometry = shape.Geometry!;
                    var bounds = new VisualRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
                    var tolerance = Math.Max(1, Math.Min(bounds.Width, bounds.Height) * .05);
                    if (Enumerable.Range(1, points.Count - 1).Any(index =>
                            GeometryMath.DistanceToSegmentRect(points[index - 1], points[index], bounds) <= tolerance))
                        stagedShaftNodeShapeIds.Add(shape.ShapeId);
                }
                if (unresolvedAnalysisBudgetExceeded || inferenceToken.IsCancellationRequested) break;
            }
        }
        var unresolvedNodeShapeIds = new HashSet<string>(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (inferenceToken.IsCancellationRequested)
        {
            if (!diagnostics.Any(item => item.Code == "VisualInferenceTimeout"))
                diagnostics.Add(new VisualDiagnostic("VisualInferenceTimeout",
                    "PPTX visual inference exceeded its configured time budget; all connectors remain fallback geometry.",
                    slide.SlideId, Fallback: "connectors retained as visual fallback",
                    Remedy: "increase VisualInferenceTimeout or simplify the diagram",
                    Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                    SourceObjectId: slide.SlideId, SourceObjectType: "inference", Confidence: 0));
        }
        else if (unresolvedAnalysisBudgetExceeded)
        {
            diagnostics.Add(new VisualDiagnostic("VisualInferenceBudgetExceeded",
                "PPTX unresolved-connector analysis exceeded its deterministic work budget; candidate and shaft-only node retention was skipped.",
                slide.SlideId, Fallback: "connectors and implicated shapes retained as visual fallback",
                Remedy: "simplify the slide or split dense diagrams across slides",
                Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                SourceObjectId: slide.SlideId, SourceObjectType: "unresolved-connector-analysis", Confidence: 0));
        }
        else
        {
            unresolvedNodeShapeIds.UnionWith(stagedCandidateNodeShapeIds);
            unresolvedNodeShapeIds.UnionWith(stagedShaftNodeShapeIds);
        }

        var assignedLabels = AssignEdgeLabels(connectors, projectedConnectors, drawable,
            reservedEndpointShapeIds, out var unresolvedLabelConnectorIds);
        foreach (var assignment in assignedLabels.Values)
            labels.Add(assignment.ShapeId);

        foreach (var (connector, connectorIndex) in connectors.OrderBy(shape => shape.ShapeId, StringComparer.Ordinal).Select((shape, index) => (shape, index)))
        {
            if (!projectedConnectors.TryGetValue(connector.ShapeId, out var projected))
            {
                diagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved", "A PPTX connector could not be uniquely associated with source and target shapes.", connector.ShapeId,
                    Fallback: "connector retained as visual fallback", Remedy: "snap both connector endpoints to shapes or make geometry unambiguous",
                    Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                    SourceObjectId: connector.ShapeId, SourceObjectType: "connector", Confidence: 0));
                edges.Add(new VisualEdge("e_" + connectorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null,
                    VisualEdgeResolution.Unresolved, connector.ShapeId, Direction: "directed",
                    Geometry: connector.Geometry, Confidence: 0,
                    SourceAnchor: new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", connector.ShapeId)]),
                    EdgeDirection: VisualEdgeDirection.Directed));
                continue;
            }
            string? start = projected.Start;
            string? end = projected.End;
            var resolution = projected.Resolution;
            VisualConnectionEvidence? connectionEvidence = projected.Evidence;
            if (start is not null && end is not null && StringComparer.Ordinal.Equals(start, end))
            {
                start = null;
                end = null;
                resolution = VisualEdgeResolution.Unresolved;
                connectionEvidence = null;
                diagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved", "A PPTX connector cannot connect a shape to itself.", connector.ShapeId,
                    Fallback: "connector retained as visual fallback", Remedy: "connect two distinct shapes",
                    Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                    SourceObjectId: connector.ShapeId, SourceObjectType: "connector", Confidence: 0));
            }
            var label = assignedLabels.TryGetValue(connector.ShapeId, out var assignedLabel)
                ? assignedLabel.Text
                : null;
            if (unresolvedLabelConnectorIds.Contains(connector.ShapeId)) diagnostics.Add(new VisualDiagnostic("VisualEdgeLabelUnresolved", "Nearby text could not be uniquely assigned to a connector edge.", connector.ShapeId,
                Fallback: "text retained independently", Remedy: "place the label closer to one connector",
                Format: "pptx", PartUri: slide.PartUri, PartitionId: slide.SlideId,
                SourceObjectId: connector.ShapeId, SourceObjectType: "connector", Confidence: 0));
            if (start is not null) connectedShapeIds.Add(start);
            if (end is not null) connectedShapeIds.Add(end);
            var connectorAnchor = new SourceAnchor("pptx", slide.PartUri,
                [new AnchorLocator("shape_id", connector.ShapeId)]);
            var connectorDirection = connectorDirections[connector.ShapeId];
            // A native stCxn/endCxn pair is an ordered semantic relation even when Office
            // omits arrowhead decoration. Free geometry without an arrowhead stays undirected.
            if (resolution == VisualEdgeResolution.NativeConnection && connectorDirection == ConnectionDirection.Unknown)
                connectorDirection = ConnectionDirection.Forward;
            var edgeSource = connectorDirection == ConnectionDirection.Reverse ? end : start;
            var edgeTarget = connectorDirection == ConnectionDirection.Reverse ? start : end;
            var baseEvidence = connectionEvidence ?? new VisualConnectionEvidence("pptx-connector", "Unresolved", 0);
            var evidenceCodes = detachedArrowheads.ContainsKey(connector.ShapeId)
                ? (baseEvidence.EvidenceCodes ?? []).Concat(["DetachedArrowhead"])
                    .Distinct(StringComparer.Ordinal).ToArray()
                : baseEvidence.EvidenceCodes;
            edges.Add(new VisualEdge("e_" + connectorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                edgeSource is null ? null : "v_" + SafeVisualId(edgeSource), edgeTarget is null ? null : "v_" + SafeVisualId(edgeTarget), label, resolution, connector.ShapeId,
                Direction: connectorDirection switch { ConnectionDirection.Reverse => "reverse", ConnectionDirection.Bidirectional => "bidirectional", ConnectionDirection.Unknown => "undirected", _ => "directed" }, Geometry: connector.Geometry,
                Confidence: resolution == VisualEdgeResolution.NativeConnection ? 1 : 0.8,
                SourceAnchor: connectorAnchor, EdgeDirection: connectorDirection switch { ConnectionDirection.Bidirectional => VisualEdgeDirection.Undirected, ConnectionDirection.Unknown => VisualEdgeDirection.Undirected, _ => VisualEdgeDirection.Directed },
                Evidence: baseEvidence with
                {
                    ArrowheadEvidence = connectorDirection switch { ConnectionDirection.Reverse => "start", ConnectionDirection.Bidirectional => "both", ConnectionDirection.Unknown => "none", _ => "end" },
                    EvidenceCodes = evidenceCodes
                }));
        }
        if (edges.Any(edge => edge.SourceId is null || edge.TargetId is null))
            diagnostics.Add(new VisualDiagnostic("VisualSemanticProjectionPartial", "Recognized connector relationships could not be projected as a flowchart.",
                Fallback: "shape text and connector diagnostics retained", Format: "pptx", PartUri: slide.PartUri,
                PartitionId: slide.SlideId, SourceObjectId: connectors.FirstOrDefault()?.ShapeId,
                SourceObjectType: "connector", Confidence: 0));
        // A slide often contains title, footer, and explanatory text boxes alongside a flow.
        // Resolved endpoints and nodes touched/candidated by unresolved connectors belong to
        // the topology; unrelated slide furniture remains outside it.
        var visualNodeShapeIds = new HashSet<string>(connectedShapeIds, StringComparer.Ordinal);
        visualNodeShapeIds.UnionWith(unresolvedNodeShapeIds);
        var nodes = byShapeId.Values.Where(shape => visualNodeShapeIds.Contains(shape.ShapeId) && !labels.Contains(shape.ShapeId))
            .OrderBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(shape => new VisualNode("v_" + SafeVisualId(shape.ShapeId), VisualLabel(shape), VisualKind(shape.ShapePreset), shape.ShapeId,
                Geometry: shape.Geometry,
                SourceAnchor: new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)]))).ToArray();

        // The source ledger makes every recognized shape/connector/label auditable. A
        // duplicate shape ID is suppressed instead of creating an invalid graph node.
        var sourceItems = new List<VisualSourceItem>();
        var shapeSourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shape in drawable)
        {
            var sourceItemId = "shape:" + SafeVisualId(shape.ShapeId);
            if (shapeSourceIds.TryGetValue(shape.ShapeId, out var firstSourceId))
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId + ":duplicate:" + sourceItems.Count,
                    IsDirectionalShape(shape) ? VisualSourceItemKind.DirectionalShape : VisualSourceItemKind.Shape,
                    VisualDisposition.SuppressedDuplicate, DuplicateOfSourceItemId: firstSourceId,
                    Reason: "duplicate source shape ID",
                    SourceAnchor: new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)])));
                continue;
            }
            shapeSourceIds[shape.ShapeId] = sourceItemId;
            var sourceAnchor = new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)]);
            if (labels.Contains(shape.ShapeId))
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.TextLabel,
                    VisualDisposition.IgnoredDecorative, Reason: "attached as connector edge label", SourceAnchor: sourceAnchor));
            }
            else if (visualNodeShapeIds.Contains(shape.ShapeId) && nodes.Any(node => node.SourceNodeId == shape.ShapeId))
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId,
                    IsDirectionalShape(shape) ? VisualSourceItemKind.DirectionalShape : VisualSourceItemKind.Shape,
                    VisualDisposition.ProjectedNode,
                    ProjectedNodeId: nodes.Single(node => node.SourceNodeId == shape.ShapeId).Id,
                    SourceAnchor: sourceAnchor));
            }
            else if (IsDirectionalShape(shape))
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.DirectionalShape,
                    VisualDisposition.VisualFallback, FallbackPathId: "path_" + SafeVisualId(shape.ShapeId),
                    Reason: "unconnected directional shape geometry", SourceAnchor: sourceAnchor));
            }
            else
            {
                sourceItems.Add(new VisualSourceItem(sourceItemId, VisualSourceItemKind.Shape,
                    VisualDisposition.IgnoredDecorative, Reason: "unconnected from recognized visual graph", SourceAnchor: sourceAnchor));
            }
        }
        foreach (var (edge, index) in edges.Select((edge, index) => (edge, index)))
        {
            var edgeAnchor = new SourceAnchor("pptx", slide.PartUri,
                [new AnchorLocator("connector", edge.SourceNodeId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            sourceItems.Add(new VisualSourceItem("connector:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VisualSourceItemKind.Connector,
                edge.SourceId is not null && edge.TargetId is not null
                    ? VisualDisposition.ProjectedEdge
                    : VisualDisposition.DiagnosticOnly,
                ProjectedEdgeId: edge.SourceId is not null && edge.TargetId is not null ? edge.Id : null,
                DiagnosticCode: edge.SourceId is null || edge.TargetId is null ? "VisualConnectorUnresolved" : null,
                Reason: edge.SourceId is null || edge.TargetId is null ? "connector endpoints unresolved" : null,
                SourceAnchor: edgeAnchor));
        }
        foreach (var (connectorId, association) in detachedArrowheads.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var edgeIndex = edges.FindIndex(item => StringComparer.Ordinal.Equals(item.SourceNodeId, connectorId));
            sourceItems.Add(new VisualSourceItem("arrowhead:" + SafeVisualId(association.Head.ShapeId),
                VisualSourceItemKind.DirectionalShape,
                VisualDisposition.SuppressedDuplicate,
                DuplicateOfSourceItemId: "connector:" + edgeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Reason: "detached arrowhead consumed as directional evidence for its one-to-one connector",
                SourceAnchor: new SourceAnchor("pptx", slide.PartUri,
                    [new AnchorLocator("shape_id", association.Head.ShapeId)])));
        }
        var graph = new VisualGraph("pptx_" + SafeVisualId(slide.SlideId), nodes, edges, diagnostics,
            InferDirection(nodes, byShapeId), Paths: paths, SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };

        static IReadOnlyDictionary<string, (PptxShapeRecord Head, ConnectionDirection Direction)> AssociateDetachedArrowheads(
            IReadOnlyList<PptxShapeRecord> connectorShapes,
            IReadOnlyList<PptxShapeRecord> allShapes)
        {
            var heads = allShapes.Where(shape => !shape.IsHidden && shape.Geometry is not null &&
                    !StringComparer.Ordinal.Equals(shape.ShapeType, "connector") &&
                    string.IsNullOrWhiteSpace(FirstLine(shape.Text)) &&
                    shape.ShapePreset is not null &&
                    (StringComparer.OrdinalIgnoreCase.Equals(shape.ShapePreset, "triangle") ||
                     StringComparer.OrdinalIgnoreCase.Equals(shape.ShapePreset, "rtTriangle")))
                .OrderBy(shape => shape.ShapeId, StringComparer.Ordinal).ToArray();
            var candidates = new List<(string ConnectorId, PptxShapeRecord Head,
                ConnectionDirection Direction, double Distance)>();
            foreach (var connector in connectorShapes.Where(item => ConnectorDirection(item) == ConnectionDirection.Unknown)
                         .OrderBy(item => item.ShapeId, StringComparer.Ordinal))
            {
                var points = ConnectorPoints(connector);
                if (points.Count < 2) continue;
                var shaft = points[^1] - points[0];
                var tangent = shaft.Normalize();
                if (tangent.Length == 0) continue;
                foreach (var head in heads)
                {
                    var geometry = head.Geometry!;
                    var center = new VisualPoint(geometry.X + geometry.Width / 2, geometry.Y + geometry.Height / 2);
                    foreach (var endpoint in new[]
                    {
                        (Point: points[0], Tangent: new VisualVector(-tangent.X, -tangent.Y), Direction: ConnectionDirection.Reverse),
                        (Point: points[^1], Tangent: tangent, Direction: ConnectionDirection.Forward),
                    })
                    {
                        var offset = center - endpoint.Point;
                        var distance = offset.Length;
                        var proximity = Math.Max(Math.Max(geometry.Width, geometry.Height) * 2.5, shaft.Length * .20);
                        if (distance <= 1e-9 || distance > proximity) continue;
                        var alignment = VisualVector.Dot(offset.Normalize(), endpoint.Tangent);
                        if (alignment < Math.Cos(30 * Math.PI / 180)) continue;
                        candidates.Add((connector.ShapeId, head, endpoint.Direction, distance));
                    }
                }
            }

            var result = new Dictionary<string, (PptxShapeRecord Head, ConnectionDirection Direction)>(StringComparer.Ordinal);
            var usedHeads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connectorGroup in candidates.GroupBy(item => item.ConnectorId, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var ranked = connectorGroup.OrderBy(item => item.Distance)
                    .ThenBy(item => item.Head.ShapeId, StringComparer.Ordinal).ToArray();
                var best = ranked[0];
                if (ranked.Length > 1 && ranked[1].Distance <= best.Distance + Math.Max(1, best.Distance * .15))
                    continue;
                var headRanked = candidates.Where(item => item.Head.ShapeId == best.Head.ShapeId)
                    .OrderBy(item => item.Distance).ThenBy(item => item.ConnectorId, StringComparer.Ordinal).ToArray();
                if (headRanked[0].ConnectorId != best.ConnectorId ||
                    headRanked.Length > 1 && headRanked[1].Distance <= best.Distance + Math.Max(1, best.Distance * .15) ||
                    !usedHeads.Add(best.Head.ShapeId))
                    continue;
                result[best.ConnectorId] = (best.Head, best.Direction);
            }
            return result;
        }

        static bool IsDirectionalShape(PptxShapeRecord shape) =>
            shape.Geometry is not null && shape.ShapePreset is not null &&
            (shape.ShapePreset.Contains("arrow", StringComparison.OrdinalIgnoreCase) ||
             shape.ShapePreset.Contains("chevron", StringComparison.OrdinalIgnoreCase) ||
             shape.ShapePreset.Contains("flowChart", StringComparison.OrdinalIgnoreCase));

        static string VisualLabel(PptxShapeRecord shape)
        {
            var text = FirstLine(shape.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            return !string.IsNullOrWhiteSpace(shape.Name) ? shape.Name!.Trim() : shape.ShapePreset ?? "Directional shape";
        }

        static IReadOnlyList<VisualPathPoint> RectanglePath(Geometry geometry) =>
        [
            new(geometry.X, geometry.Y),
            new(geometry.X + geometry.Width, geometry.Y),
            new(geometry.X + geometry.Width, geometry.Y + geometry.Height),
            new(geometry.X, geometry.Y + geometry.Height),
            new(geometry.X, geometry.Y),
        ];

        static VisualBoundaryKind PrimitiveBoundary(string? preset) =>
            preset?.ToLowerInvariant() switch
            {
                "ellipse" => VisualBoundaryKind.Ellipse,
                "diamond" or "flowchartdecision" => VisualBoundaryKind.Diamond,
                "roundrect" or "flowchartterminator" => VisualBoundaryKind.RoundedRectangle,
                "parallelogram" or "flowchartdata" => VisualBoundaryKind.Parallelogram,
                _ => VisualBoundaryKind.Rectangle,
            };

        // Deterministic, fixture-independent edge-label pre-classification (see the call site
        // in BuildVisualGraph above). A shape qualifies as an edge-label candidate only when
        // ALL of the following hold:
        //   1. It is never a native connector endpoint (an explicit stCxn/endCxn glue-point
        //      reference is authoritative and must never be second-guessed by geometry).
        //   2. Its min-dimension is smaller than half the slide's own median text-bearing-shape
        //      min-dimension -- relative to this slide, not a fixed EMU constant, so a diagram
        //      whose real nodes are all uniformly small never trips this against its own nodes
        //      (every node then sits at ~1x the median, never under half of it).
        //   3. It rests strictly between one connector's two endpoints (the same >2%/<98% shaft
        //      band FindIntermediateNodeIds itself exempts), not at either end -- a real small
        //      terminal node (e.g. a start/end marker smaller than the diagram's process boxes)
        //      legitimately sits AT an endpoint, so distance-to-shaft alone cannot separate it
        //      from a label; only the strictly-interior band is unambiguous.
        //   4. It sits within its own min-dimension of that connector's shaft, using
        //      DistanceToSegmentRect -- the same rect-to-segment measure and the same
        //      candidate-relative tolerance DiagramClusterer.Touches uses, so "close enough to
        //      merge into the connector's cluster" and "close enough to read as its label"
        //      agree with each other.
        // A shape failing any bound stays a normal node candidate; this method only ever narrows
        // the node-primitive pool, so it can turn a false intermediate-node block into a
        // resolved edge but can never fabricate a relation geometry does not otherwise support.
        static HashSet<string> ClassifyEdgeLabelCandidateShapeIds(
            IReadOnlyDictionary<string, PptxShapeRecord> byShapeId, IReadOnlyList<PptxShapeRecord> connectors)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (connectors.Count == 0) return result;
            var textBearing = byShapeId.Values
                .Where(shape => shape.Geometry is not null && !string.IsNullOrWhiteSpace(FirstLine(shape.Text)))
                .ToArray();
            // A median needs at least two independent samples; with 0 or 1 text-bearing shapes
            // there is no "typical size" on this slide to compare against.
            if (textBearing.Length < 2) return result;
            var nativeEndpointShapeIds = connectors
                .SelectMany(connector => new[] { connector.ConnectorStartId, connector.ConnectorEndId })
                .Where(id => id is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
            var minDimensions = textBearing
                .Select(shape => Math.Min(shape.Geometry!.Width, shape.Geometry!.Height))
                .OrderBy(value => value).ToArray();
            var middle = minDimensions.Length / 2;
            var median = minDimensions.Length % 2 == 1
                ? minDimensions[middle]
                : (minDimensions[middle - 1] + minDimensions[middle]) / 2;
            var sizeThreshold = median * 0.5;
            if (sizeThreshold <= 0) return result;
            var shafts = connectors.Select(ConnectorPoints)
                .Where(points => points.Count == 2).ToArray();
            if (shafts.Length == 0) return result;
            foreach (var shape in textBearing)
            {
                if (nativeEndpointShapeIds.Contains(shape.ShapeId)) continue;
                var geometry = shape.Geometry!;
                var minDimension = Math.Min(geometry.Width, geometry.Height);
                if (minDimension >= sizeThreshold) continue;
                var rect = new VisualRect(geometry.X, geometry.Y, geometry.Width, geometry.Height);
                var proximityThreshold = Math.Max(1, minDimension);
                foreach (var shaft in shafts)
                {
                    GeometryMath.DistanceToSegment(rect.Center, shaft[0], shaft[1], out var projection);
                    if (projection is <= .02 or >= .98) continue;
                    if (GeometryMath.DistanceToSegmentRect(shaft[0], shaft[1], rect) > proximityThreshold) continue;
                    result.Add(shape.ShapeId);
                    break;
                }
            }
            return result;
        }

        static VisualConnectionEvidence ConnectionEvidence(
            ConnectionPairCandidate pair, SoftConnectionResult inference)
        {
            var physicalStartId = pair.Direction == ConnectionDirection.Reverse
                ? pair.TargetId : pair.SourceId;
            var physicalEndId = pair.Direction == ConnectionDirection.Reverse
                ? pair.SourceId : pair.TargetId;
            var selected = inference.Candidates
                .Where(candidate => candidate.ConnectorId == pair.ConnectorId &&
                    (candidate.IsStart && candidate.NodeId == physicalStartId ||
                     !candidate.IsStart && candidate.NodeId == physicalEndId))
                .ToArray();
            var margin = selected.Select(candidate => candidate.Features.CandidateMargin)
                .DefaultIfEmpty(1).Min();
            return new VisualConnectionEvidence(
                pair.IsNative ? "native-connection" : "soft-geometry",
                pair.Confidence.ToString(),
                pair.Score,
                SecondBestScore: Math.Max(0, pair.Score - margin),
                CandidateMargin: margin,
                BoundaryDistanceNormalized: selected
                    .Select(candidate => candidate.Features.BoundaryDistanceNormalized)
                    .DefaultIfEmpty(0).Max(),
                RayIntersects: selected.All(candidate => candidate.Features.RayIntersects),
                RayFirstHit: selected.All(candidate => candidate.Features.RayFirstHit),
                AngularDeviationDegrees: selected
                    .Select(candidate => candidate.Features.AngularDeviationDegrees)
                    .DefaultIfEmpty(0).Max(),
                PerpendicularOffsetNormalized: selected
                    .Select(candidate => candidate.Features.PerpendicularOffsetNormalized)
                    .DefaultIfEmpty(0).Max(),
                IntermediateNodeCount: selected
                    .Select(candidate => candidate.Features.IntermediateNodeCount)
                    .DefaultIfEmpty(0).Max(),
                ArrowheadEvidence: "none",
                ClusterId: pair.ClusterId,
                RejectedCandidateIds: pair.RejectedCandidateIds);
        }
    }

    private static IReadOnlyList<VisualPoint> ConnectorPoints(PptxShapeRecord connector)
    {
        if (connector.ConnectorPathPoints is { Count: >= 2 } points)
            return [points[0], points[^1]];
        return ConnectorPoints(connector.Geometry);
    }

    private static IReadOnlyList<VisualPoint> ConnectorPoints(Geometry? geometry)
    {
        if (geometry is not { } line) return [];
        var horizontal = line.Width >= line.Height;
        var first = horizontal
            ? new VisualPoint(line.X, line.Y + line.Height / 2)
            : new VisualPoint(line.X + line.Width / 2, line.Y);
        var second = horizontal
            ? new VisualPoint(line.X + line.Width, line.Y + line.Height / 2)
            : new VisualPoint(line.X + line.Width / 2, line.Y + line.Height);
        if (Math.Abs(Math.Abs(line.RotationDegrees) - 180) < 1)
            (first, second) = (second, first);
        return [first, second];
    }

    private static (PptxShapeRecord Start, PptxShapeRecord End, VisualConnectionEvidence Evidence)? InferGeometryEndpoints(
        PptxShapeRecord connector, IReadOnlyList<PptxShapeRecord> candidates)
    {
        if (connector.Geometry is not { } line || candidates.Count < 2 || VisualInferenceContext.Current == VisualInferenceMode.NativeOnly) return null;        var horizontal = line.Width >= line.Height;
        var first = horizontal
            ? new VisualPoint(line.X, line.Y + line.Height / 2)
            : new VisualPoint(line.X + line.Width / 2, line.Y);
        var second = horizontal
            ? new VisualPoint(line.X + line.Width, line.Y + line.Height / 2)
            : new VisualPoint(line.X + line.Width / 2, line.Y + line.Height);
        if (Math.Abs(Math.Abs(line.RotationDegrees) - 180) < 1)
            (first, second) = (second, first);
        var startCandidates = candidates.Where(candidate => candidate.Geometry is not null)
            .Select(candidate => (Shape: candidate, Distance: DistanceToBox((first.X, first.Y), candidate.Geometry!)))
            .OrderBy(item => item.Distance).ThenBy(item => item.Shape.ShapeId, StringComparer.Ordinal).Take(2).ToArray();
        if (startCandidates.Length < 2 || startCandidates[0].Distance > Math.Max(1, Math.Max(line.Width, line.Height)) * .75 ||
            Math.Abs(startCandidates[1].Distance - startCandidates[0].Distance) <= 1e-6)
            return null;
        var start = startCandidates[0].Shape;
        var endCandidates = candidates.Where(candidate => candidate.Geometry is not null && candidate.ShapeId != start.ShapeId)
            .Select(candidate => (Shape: candidate, Distance: DistanceToBox((second.X, second.Y), candidate.Geometry!)))
            .OrderBy(item => item.Distance).ThenBy(item => item.Shape.ShapeId, StringComparer.Ordinal).Take(2).ToArray();
        if (endCandidates.Length < 1 || endCandidates[0].Distance > Math.Max(1, Math.Max(line.Width, line.Height)) * .75 ||
            endCandidates.Length > 1 && Math.Abs(endCandidates[1].Distance - endCandidates[0].Distance) <= 1e-6)
            return null;
        var end = endCandidates[0].Shape;
        if (start.ShapeId == end.ShapeId) return null;
        var scale = Math.Max(1, Math.Max(line.Width, line.Height));
        var score = Math.Max(0, 1 - startCandidates[0].Distance / scale);
        var evidence = new VisualConnectionEvidence(
            "soft-geometry", score >= .8 ? "High" : "Medium", score,
            CandidateMargin: Math.Max(0, endCandidates[0].Distance - startCandidates[0].Distance),
            ArrowheadEvidence: ConnectorDirection(connector) switch
            {
                ConnectionDirection.Reverse => "start",
                ConnectionDirection.Bidirectional => "both",
                _ => "end",
            },
            ClusterId: "slide:" + connector.SlideId);
        return (start, end, evidence);
    }
    private static double DistanceToBox((double X, double Y) point, Geometry box)
    {
        var dx = Math.Max(Math.Max(box.X - point.X, 0), point.X - (box.X + box.Width));
        var dy = Math.Max(Math.Max(box.Y - point.Y, 0), point.Y - (box.Y + box.Height));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record AssignedEdgeLabel(string Text, string ShapeId);

    private static IReadOnlyDictionary<string, AssignedEdgeLabel> AssignEdgeLabels(
        IReadOnlyList<PptxShapeRecord> connectors,
        IReadOnlyDictionary<string, (string Start, string End, VisualEdgeResolution Resolution, VisualConnectionEvidence Evidence)> projectedConnectors,
        IReadOnlyList<PptxShapeRecord> candidates,
        IReadOnlySet<string> reservedEndpointShapeIds,
        out IReadOnlySet<string> unresolvedConnectorIds)
    {
        var choices = new List<EdgeLabelCandidate>();
        var labelsById = new Dictionary<string, AssignedEdgeLabel>(StringComparer.Ordinal);
        var connectorIdsByLabel = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var text = FirstLine(candidate.Text);
            if (candidate.Geometry is null || text.Length == 0 || text.Length > 80 ||
                reservedEndpointShapeIds.Contains(candidate.ShapeId))
                continue;

            foreach (var connector in connectors)
            {
                if (!projectedConnectors.ContainsKey(connector.ShapeId)) continue;
                var shaft = ConnectorPoints(connector);
                if (shaft.Count != 2) continue;
                var rect = new VisualRect(candidate.Geometry.X, candidate.Geometry.Y,
                    candidate.Geometry.Width, candidate.Geometry.Height);
                GeometryMath.DistanceToSegment(rect.Center, shaft[0], shaft[1], out var projection);
                if (projection is <= .05 or >= .95) continue;
                var distance = GeometryMath.DistanceToSegmentRect(shaft[0], shaft[1], rect);
                var shaftLength = (shaft[1] - shaft[0]).Length;
                var labelMinorDimension = Math.Min(candidate.Geometry.Width, candidate.Geometry.Height);
                var tolerance = Math.Max(1, Math.Max(labelMinorDimension * 1.5, shaftLength * .08));
                if (distance > tolerance) continue;

                var proximityScore = 1 - distance / tolerance;
                var midpointScore = 1 - Math.Abs(projection - .5) * 2;
                var score = proximityScore * .75 + midpointScore * .25;
                choices.Add(new EdgeLabelCandidate(candidate.ShapeId, connector.ShapeId, score));
                labelsById[candidate.ShapeId] = new AssignedEdgeLabel(text, candidate.ShapeId);
                if (!connectorIdsByLabel.TryGetValue(candidate.ShapeId, out var connectorIds))
                    connectorIdsByLabel[candidate.ShapeId] = connectorIds = new HashSet<string>(StringComparer.Ordinal);
                connectorIds.Add(connector.ShapeId);
            }
        }

        var resolved = EdgeLabelAssigner.Assign(choices);
        var assignments = new Dictionary<string, AssignedEdgeLabel>(StringComparer.Ordinal);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (labelId, connectorIds) in connectorIdsByLabel)
        {
            if (resolved.TryGetValue(labelId, out var connectorId))
                assignments[connectorId] = labelsById[labelId];
            else
                unresolved.UnionWith(connectorIds);
        }
        unresolvedConnectorIds = unresolved;
        return assignments;
    }

    private static ConnectionDirection ConnectorDirection(PptxShapeRecord connector)
    {
        var head = !string.IsNullOrWhiteSpace(connector.ConnectorHeadArrow) && !StringComparer.OrdinalIgnoreCase.Equals(connector.ConnectorHeadArrow, "none");
        var tail = !string.IsNullOrWhiteSpace(connector.ConnectorTailArrow) && !StringComparer.OrdinalIgnoreCase.Equals(connector.ConnectorTailArrow, "none");
        return (head, tail) switch { (true, true) => ConnectionDirection.Bidirectional, (true, false) => ConnectionDirection.Reverse, (false, true) => ConnectionDirection.Forward, _ => ConnectionDirection.Unknown };
    }

    private static VisualNodeKind VisualKind(string? preset) => preset?.ToLowerInvariant() switch
    {
        "rect" or "flowchartprocess" => VisualNodeKind.Process,
        "roundrect" or "flowchartterminator" => VisualNodeKind.Terminator,
        "diamond" or "flowchartdecision" => VisualNodeKind.Decision,
        "parallelogram" or "flowchartdata" => VisualNodeKind.Data,
        "ellipse" => VisualNodeKind.Terminator,
        _ => VisualNodeKind.Generic,
    };
    private static string InferDirection(IReadOnlyList<VisualNode> nodes, IReadOnlyDictionary<string, PptxShapeRecord> byShapeId)
    {
        var geometries = nodes.Select(node => byShapeId.TryGetValue(node.SourceNodeId ?? "", out var shape) ? shape.Geometry : null).Where(geometry => geometry is not null).Cast<Geometry>().ToArray();
        if (geometries.Length < 2) return "LR";
        var x = geometries.Max(item => item.X) - geometries.Min(item => item.X);
        var y = geometries.Max(item => item.Y) - geometries.Min(item => item.Y);
        return y > x ? "TD" : "LR";
    }
    private static string SafeVisualId(string value) => new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

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
        if (fallback.Contains("subtitle", StringComparison.Ordinal) || fallback.Contains("sub-title", StringComparison.Ordinal)) return "subtitle";
        if (fallback.Contains("title", StringComparison.Ordinal)) return "title";
        if (fallback.Contains("footer", StringComparison.Ordinal)) return "footer";
        if (fallback.Contains("slide number", StringComparison.Ordinal) || fallback.Contains("slide-number", StringComparison.Ordinal)) return "slide-number";
        if (fallback.Equals("date", StringComparison.Ordinal) || fallback.StartsWith("date ", StringComparison.Ordinal)) return "date";
        if (fallback.Contains("body", StringComparison.Ordinal) || fallback.Contains("content", StringComparison.Ordinal)) return "body";
        return "other";
    }
    private static bool IsFurnitureRole(string role) => role is "footer" or "date" or "slide-number" or "sldnum" or "ftr";

    private static bool IsTitleLike(IReadOnlyList<PptxTextParagraph> paragraphs, Geometry? geometry) =>
        geometry is { Y: <= 1_200_000 } && paragraphs.Count == 1 &&
        paragraphs[0].Runs is { Count: > 0 } runs && runs.All(run => run.Bold);
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

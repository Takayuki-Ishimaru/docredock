using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Rtmd.Core.Documents;

namespace Rtmd.Formats.OpenXml.Pptx;

public sealed record PptxShapeRecord(
    string SlideId,
    string ShapeId,
    string? Name,
    string Text,
    bool IsTable,
    IReadOnlyList<string> ImageRelationshipIds,
    Geometry? Geometry,
    IReadOnlyList<IReadOnlyList<string>>? TableRows = null,
    string Role = "other",
    IReadOnlyList<string>? Paragraphs = null,
    IReadOnlyList<PptxTextParagraph>? ParagraphDetails = null);
public sealed record PptxTextRun(string Text, bool Bold = false, bool Italic = false,
    bool Underline = false, string? FontName = null, double? FontSize = null);
public sealed record PptxTextParagraph(string Text, int Level = 0, bool IsBullet = false,
    string? BulletCharacter = null, IReadOnlyList<PptxTextRun>? Runs = null);
public sealed record PptxSlideRecord(string SlideId, string PartUri, IReadOnlyList<PptxShapeRecord> Shapes, string? NotesText);
public sealed record PptxExtractionResult(DocumentGraph Graph, IReadOnlyList<PptxSlideRecord> Slides, IReadOnlyDictionary<string, string> PartSha256, IReadOnlyList<string> Warnings);
public sealed record PptxShapeTextEdit(string SlideId, string ShapeId, string Text);
public sealed record PptxPatchPlan(IReadOnlyList<PptxShapeTextEdit> Edits, IReadOnlySet<string> DirtyParts);
public sealed record PptxRestoreResult(byte[] Bytes, bool IsByteIdentical, PptxPatchPlan Plan, IReadOnlyList<string> Warnings);

/// <summary>BCL-only PPTX extractor and existing-shape text patcher.</summary>
public sealed class PptxAdapter
{
    private const string PresentationRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
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
                if (shape.Paragraphs is not null) extension["paragraphs"] = JsonSerializer.SerializeToElement(shape.Paragraphs);
                if (shape.ParagraphDetails is not null) extension["paragraph_details"] = JsonSerializer.SerializeToElement(shape.ParagraphDetails);
                if (shape.IsTable) extension["is_table"] = JsonSerializer.SerializeToElement(true);
                if (shape.ImageRelationshipIds.Count > 0)
                {
                    extension["image_relationships"] = JsonSerializer.SerializeToElement(shape.ImageRelationshipIds);
                    extension["image_relationship"] = JsonSerializer.SerializeToElement(shape.ImageRelationshipIds[0]);
                }
                var kind = shape.IsTable ? NodeKind.Table : shape.ImageRelationshipIds.Count > 0 ? NodeKind.Image : NodeKind.Shape;
                NodeContent content = kind switch
                {
                    NodeKind.Image => new ReferenceNodeContent(ResolveImageReference(package, slide.PartUri, shape.ImageRelationshipIds[0]), shape.Name),
                    NodeKind.Table => new TableNodeContent(shape.TableRows ?? []),
                    _ => CreateShapeTextContent(shape),
                };
                var editability = kind == NodeKind.Shape ? NodeEditability.EditableWithConstraints : NodeEditability.Protected;
                var layer = kind == NodeKind.Shape && string.IsNullOrWhiteSpace(shape.Text) ? ContentLayer.Hidden : ContentLayer.Body;
                nodes.Add(new($"n_{Hash(slide.SlideId + ":" + shape.ShapeId)[..16]}", kind, null, order++, layer,
                    content, new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("shape_id", shape.ShapeId)]),
                    Geometry: shape.Geometry, Editability: editability, Extensions: extension));
            }
            if (!string.IsNullOrWhiteSpace(slide.NotesText)) nodes.Add(new($"n_{Hash(slide.SlideId + ":notes")[..16]}", NodeKind.SpeakerNotes, null, order, ContentLayer.Furniture, new TextNodeContent(slide.NotesText), new SourceAnchor("pptx", slide.PartUri, [new AnchorLocator("slide_id", slide.SlideId)]), Editability: NodeEditability.Protected));
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
        partition.Id, node.Source?.Locators.FirstOrDefault(x => x.Kind == "shape_id")?.Value ?? node.Id, (node.Content as TextNodeContent)?.Text ?? string.Empty)));
    private static string SlidePartFromId(string slideId) => slideId.StartsWith("slide", StringComparison.OrdinalIgnoreCase) ? "ppt/slides/" + slideId.ToLowerInvariant() + ".xml" : "ppt/slides/slide" + slideId + ".xml";
    private sealed record Relationship(string Id, string Target, string Type);

    private static List<PptxSlideRecord> ReadSlides(Dictionary<string, byte[]> package)
    {
        var result = new List<PptxSlideRecord>();
        if (!package.TryGetValue("ppt/presentation.xml", out var presentation)) throw new InvalidDataException("Presentation part missing.");
        var presentationRels = ReadRelationships(package, "ppt/_rels/presentation.xml.rels");
        using var reader = XmlReader.Create(new MemoryStream(presentation), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId")
        {
            var id = reader.GetAttribute("id") ?? (result.Count + 1).ToString(); var rid = reader.GetAttribute("id", PresentationRelNs) ?? reader.GetAttribute("r:id") ?? "";
            if (!presentationRels.TryGetValue(rid, out var target) || !package.TryGetValue(target.Target, out var slideBytes)) continue;
            var slideId = "slide" + (result.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); var shapes = ReadShapes(slideBytes, slideId, out _);
            var notes = ReadNotes(package, target.Target);
            result.Add(new(slideId, target.Target, shapes, notes));
            _ = id;
        }
        if (result.Count == 0)
            foreach (var part in package.Keys.Where(x => x.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && x.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
            { var slideId = Path.GetFileNameWithoutExtension(part); result.Add(new(slideId, part, ReadShapes(package[part], slideId, out _), ReadNotes(package, part))); }
        return result;
    }

    private static List<PptxShapeRecord> ReadShapes(byte[] bytes, string slideId, out string? notes)
    {
        notes = null; var result = new List<PptxShapeRecord>(); using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("sp" or "graphicFrame" or "pic")) continue;
            using var subtree = reader.ReadSubtree(); var shapeId = ""; string? name = null; var text = new StringBuilder(); var imageRels = new List<string>(); var isTable = false; Geometry? geometry = null; string? placeholderType = null;
            var paragraphs = new List<string>(); var paragraphDetails = new List<PptxTextParagraph>(); StringBuilder? paragraph = null; var inTableCell = false;
            var paragraphRuns = new List<PptxTextRun>(); var paragraphLevel = 0; var paragraphBullet = false; string? paragraphBulletCharacter = null;
            var runBold = false; var runItalic = false; var runUnderline = false; string? runFont = null; double? runSize = null;
            var tableRows = new List<IReadOnlyList<string>>(); List<string>? tableRow = null; StringBuilder? tableCell = null;
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "cNvPr") { shapeId = subtree.GetAttribute("id") ?? ""; name = subtree.GetAttribute("name"); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "ph") placeholderType = subtree.GetAttribute("type") ?? "body";
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tr") tableRow = [];
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tc") { tableCell = new StringBuilder(); inTableCell = true; }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "p" && !inTableCell)
                {
                    paragraph = new StringBuilder(); paragraphRuns = []; paragraphLevel = 0; paragraphBullet = false; paragraphBulletCharacter = null;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "pPr" && !inTableCell)
                {
                    paragraphLevel = ParseInt(subtree.GetAttribute("lvl"));
                }
                else if (subtree.NodeType == XmlNodeType.Element && (subtree.LocalName is "buChar" or "buAutoNum") && !inTableCell)
                {
                    paragraphBullet = true; paragraphBulletCharacter = subtree.GetAttribute("char") ?? "•";
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "buNone" && !inTableCell) paragraphBullet = false;
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "r" && !inTableCell)
                {
                    runBold = false; runItalic = false; runUnderline = false; runFont = null; runSize = null;
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "rPr" && !inTableCell)
                {
                    runBold = IsOn(subtree.GetAttribute("b")); runItalic = IsOn(subtree.GetAttribute("i"));
                    runUnderline = !string.IsNullOrWhiteSpace(subtree.GetAttribute("u")) && !StringComparer.OrdinalIgnoreCase.Equals(subtree.GetAttribute("u"), "none");
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
                    if (!inTableCell && paragraph is not null) paragraphRuns.Add(new(value, runBold, runItalic, runUnderline, runFont, runSize));
                }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "tbl") isTable = true;
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "blip") { var rid = subtree.GetAttribute("embed", PresentationRelNs) ?? subtree.GetAttribute("r:embed"); if (rid is not null) imageRels.Add(rid); }
                else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "off") { var x = ParseDouble(subtree.GetAttribute("x")); var y = ParseDouble(subtree.GetAttribute("y")); geometry = new Geometry("pptx-emu", x, y, 0, 0); }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "tc") { tableRow?.Add(tableCell?.ToString() ?? string.Empty); tableCell = null; inTableCell = false; }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "tr") { if (tableRow is not null) tableRows.Add(tableRow); tableRow = null; }
                else if (subtree.NodeType == XmlNodeType.EndElement && subtree.LocalName == "p" && !inTableCell && paragraph is not null)
                {
                    paragraphs.Add(paragraph.ToString());
                    paragraphDetails.Add(new PptxTextParagraph(paragraph.ToString(), paragraphLevel, paragraphBullet, paragraphBulletCharacter, paragraphRuns.ToArray()));
                    paragraph = null;
                }
            }
            var role = InferRole(placeholderType, name);
            var paragraphText = paragraphs.Count == 0 ? text.ToString().TrimEnd('\r', '\n') : string.Join('\n', paragraphs);
            result.Add(new(slideId, shapeId, name, paragraphText, isTable, imageRels, geometry, tableRows, role, paragraphs, paragraphDetails));
        }
        return result;
    }

    private static NodeContent CreateShapeTextContent(PptxShapeRecord shape)
    {
        var details = shape.ParagraphDetails;
        if (details is null || details.Count == 0 || !details.SelectMany(item => item.Runs ?? []).Any(run => run.Bold || run.Italic || run.Underline))
            return new TextNodeContent(shape.Text);
        var runs = new List<TextRun>();
        foreach (var paragraph in details)
        {
            if (runs.Count > 0) runs.Add(new TextRun("\n", Kind: TextRunKind.LineBreak));
            foreach (var run in paragraph.Runs ?? [])
                runs.Add(new TextRun(run.Text, Bold: run.Bold, Italic: run.Italic, Underline: run.Underline));
        }
        return new RichTextNodeContent(runs);
    }

    private static string? ReadNotes(Dictionary<string, byte[]> package, string slidePart)
    {
        var slash = slidePart.LastIndexOf('/');
        if (slash < 0) return null;
        var rels = slidePart[..slash] + "/_rels/" + slidePart[(slash + 1)..] + ".rels";
        var relationships = ReadRelationships(package, rels);
        var notes = relationships.Values.FirstOrDefault(x => x.Type.Contains("notesSlide", StringComparison.OrdinalIgnoreCase));
        if (notes is null || !package.TryGetValue(notes.Target, out var bytes)) return null;
        var text = new StringBuilder(); using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml);
        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") text.Append(reader.ReadElementContentAsString());
        return text.ToString();
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
        var fallback = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (fallback.Contains("title", StringComparison.Ordinal)) return "title";
        if (fallback.Contains("subtitle", StringComparison.Ordinal) || fallback.Contains("sub-title", StringComparison.Ordinal)) return "subtitle";
        if (fallback.Contains("body", StringComparison.Ordinal) || fallback.Contains("content", StringComparison.Ordinal)) return "body";
        return "other";
    }
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
    private static bool IsOn(string? value) => value is not null && !StringComparer.OrdinalIgnoreCase.Equals(value, "0") &&
        !StringComparer.OrdinalIgnoreCase.Equals(value, "off") && !StringComparer.OrdinalIgnoreCase.Equals(value, "false");
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
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal); if (!package.TryGetValue(path, out var bytes)) return result; using var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml); while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship") { var id = reader.GetAttribute("Id"); var target = reader.GetAttribute("Target"); var type = reader.GetAttribute("Type") ?? ""; if (id is null || target is null) continue; var basePath = path[..path.LastIndexOf("/_rels/", StringComparison.Ordinal)]; result[id] = new(id, NormalizePartPath(basePath + "/" + target), type); }
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

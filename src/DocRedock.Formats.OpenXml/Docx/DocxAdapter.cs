using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Common;
using DocRedock.Providers.Abstractions.Providers;

namespace DocRedock.Formats.OpenXml.Docx;

/// <summary>
/// Built-in DOCX extractor and F1 paragraph patcher. It parses XML with a secure reader,
/// retains original top-level XML slices, and only splices blocks selected by graph diff.
/// </summary>
public sealed class DocxAdapter : IFormatProbe
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

    public ProviderDescriptor Descriptor { get; } = new(
        "docredock.docx.openxml", new Version(0, 2, 0), 1,
        new HashSet<string>(StringComparer.Ordinal) { "extract.text", "extract.images", "restore.byte_identical", "restore.text_in_place", "restore.insert_node", "restore.delete_node", "preserve.raw_xml_slice", "preserve.unknown_parts" },
        "MIT", "built-in", true);

    public ValueTask<ProbeResult> ProbeAsync(RewindableInput input, ProbeContext context, CancellationToken cancellationToken)
    {
        try
        {
            input.Reset();
            using var archive = new ZipArchive(input.Stream, ZipArchiveMode.Read, leaveOpen: true);
            var valid = archive.GetEntry("[Content_Types].xml") is not null && archive.GetEntry("word/document.xml") is not null;
            var warnings = context.FileName is { } name && !Path.GetExtension(name).Equals(".docx", StringComparison.OrdinalIgnoreCase)
                ? new[] { new ProbeWarning("ExtensionMismatch", "DOCX package does not match the file extension.") }
                : Array.Empty<ProbeWarning>();
            return ValueTask.FromResult(valid
                ? new ProbeResult(Descriptor.ProviderId, 1, 200, [new("ooxml_part", "word/document.xml")], warnings, false, false, true)
                : ProbeResult.Unsupported(Descriptor.ProviderId, "Required DOCX package parts are missing."));
        }
        catch (InvalidDataException exception)
        {
            return ValueTask.FromResult(new ProbeResult(Descriptor.ProviderId, 0, 0, Array.Empty<ProbeEvidence>(),
                [new ProbeWarning("MalformedZip", exception.Message)], false, true, false));
        }
        finally { input.Reset(); }
    }

    public async ValueTask<DocxExtractionResult> ExtractAsync(string sourcePath, DocxExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        sourcePath = Path.GetFullPath(sourcePath);
        var diagnostics = new List<Diagnostic>();
        await using var input = File.OpenRead(sourcePath);
        if (options.StrictSecurity)
        {
            await using var rewindable = new RewindableInput(input);
            var assessment = ContainerSecurityGate.Assess(rewindable);
            if (!assessment.IsAllowed)
                throw new InvalidDataException("DOCX package failed security preflight: " + string.Join("; ", assessment.Diagnostics.Select(item => item.Code)));
        }
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var document = RequireEntry(archive, "word/document.xml");
        var documentBytes = await ReadEntryAsync(document, cancellationToken).ConfigureAwait(false);
        var slices = XmlSliceScanner.FindWordBodyBlocks(documentBytes, "/word/document.xml");
        var doc = SafeXml.LoadDocument(documentBytes);
        var relationships = ReadRelationships(archive, "word/_rels/document.xml.rels", cancellationToken);
        var numberingInfo = ReadNumberingInfo(archive, cancellationToken);
        var listCounters = new Dictionary<(int NumId, int Ilvl), int>();
        var nodes = new List<DocumentNode>();
        var sliceMap = new Dictionary<string, RawSliceRef>(StringComparer.Ordinal);
        var runMaps = new Dictionary<string, DocxRunCharacterMap>(StringComparer.Ordinal);
        var ordinal = 0;
        var bodyElements = doc.Root?.Element(W + "body")?.Elements().ToArray() ?? Array.Empty<XElement>();
        var blockByOrdinal = slices.Blocks.OrderBy(slice => slice.Start).ToArray();
        var blockIndex = 0;
        var landscapeSectionStarts = FindLandscapeSectionStarts(doc, bodyElements);
        for (var elementIndex = 0; elementIndex < bodyElements.Length; elementIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (landscapeSectionStarts.Contains(elementIndex))
                AddSectionOrientationMarker(nodes, "/word/document.xml", ordinal++, "landscape");
            var element = bodyElements[elementIndex];
            if (element.Name == W + "p")
            {
                var slice = blockByOrdinal.ElementAtOrDefault(blockIndex++);
                var paragraphOrder = ordinal++;
                AddParagraph(element, "/word/document.xml", slice?.Reference, paragraphOrder, nodes, sliceMap, runMaps, relationships, ContentLayer.Body, numberingInfo, listCounters);
            }
            else if (element.Name == W + "tbl")
            {
                var slice = blockByOrdinal.ElementAtOrDefault(blockIndex++);
                var tableOrder = ordinal++;
                AddTable(element, "/word/document.xml", slice?.Reference, tableOrder, nodes, sliceMap, ref ordinal);
            }
        }
        if (options.IncludeFurniture)
            ordinal = await AddRelatedTextPartsAsync(archive, relationships, "header", NodeKind.Header, ContentLayer.Furniture, nodes, ordinal, cancellationToken).ConfigureAwait(false);
        if (options.IncludeFurniture)
            ordinal = await AddRelatedTextPartsAsync(archive, relationships, "footer", NodeKind.Footer, ContentLayer.Furniture, nodes, ordinal, cancellationToken).ConfigureAwait(false);
        if (options.IncludeFootnotes && archive.GetEntry("word/footnotes.xml") is { } footnotes)
            AddFootnotes(await ReadEntryAsync(footnotes, cancellationToken).ConfigureAwait(false), nodes, ref ordinal);
        if (options.IncludeFootnotes && archive.GetEntry("word/endnotes.xml") is { } endnotes)
            AddEndnotes(await ReadEntryAsync(endnotes, cancellationToken).ConfigureAwait(false), nodes, ref ordinal);
        if (options.IncludeFootnotes && archive.GetEntry("word/comments.xml") is { } comments)
            AddComments(await ReadEntryAsync(comments, cancellationToken).ConfigureAwait(false), nodes, ref ordinal);

        var sourceHash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var hasDocumentProtection = doc.Descendants(W + "documentProtection").Any() ||
            await HasDocumentProtectionAsync(archive, cancellationToken).ConfigureAwait(false);
        var hasTrackedRevisions = doc.Descendants(W + "ins").Any() || doc.Descendants(W + "del").Any();
        var index = new DocxSourceIndex(sourcePath, sourceHash, sliceMap, runMaps, slices.BodyEndTagStart,
            archive.GetEntry("word/vbaProject.bin") is not null,
            archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)),
            hasDocumentProtection,
            hasTrackedRevisions);
        if (index.HasMacro) diagnostics.Add(new("MacroPresent", "DOCX contains a macro project; it was not executed.", DiagnosticSeverity.Warning));
        if (index.HasSignature) diagnostics.Add(new("SignaturePresent", "DOCX contains package signatures; edited restore is strict-rejected.", DiagnosticSeverity.Warning));
        if (index.HasDocumentProtection) diagnostics.Add(new("DocumentProtected", "DOCX has document protection; protected edits are strict-rejected.", DiagnosticSeverity.Warning));
        if (index.HasTrackedRevisions) diagnostics.Add(new("TrackedRevisionsPresent", "DOCX contains tracked revisions; edits crossing revision markup are strict-rejected.", DiagnosticSeverity.Warning));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_" + sourceHash[..16], DocumentFormatKind.Docx,
            [new DocumentPartition("part-0001", 0, nodes, "/word/document.xml")], Capabilities: new(new HashSet<string>(StringComparer.Ordinal)
            { "extract.text", "extract.images", "restore.byte_identical", "restore.text_in_place", "restore.insert_node", "restore.delete_node", "preserve.raw_xml_slice", "preserve.unknown_parts" }));
        return new(graph, index, diagnostics);
    }

    public async ValueTask<DocxRestoreResult> RestoreAsync(
        DocxExtractionResult baselineExport, DocumentGraph editedGraph, string outputPath,
        DiffOptions? diffOptions = null, DocxRestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineExport);
        return await RestoreAsync(baselineExport.SourceIndex.SourcePath, baselineExport.Graph, editedGraph, outputPath, diffOptions, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DocxRestoreResult> RestoreAsync(
        string sourcePath, DocumentGraph baselineGraph, DocumentGraph editedGraph, string outputPath,
        DiffOptions? diffOptions = null, DocxRestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var diagnostics = new List<Diagnostic>();
        var diff = new DocumentGraphDiffEngine().Compare(baselineGraph, editedGraph, diffOptions);
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(sourcePath, outputPath)) throw new ArgumentException("Output path must differ from the original source path.", nameof(outputPath));
        if (File.Exists(outputPath)) throw new IOException("DOCX restore output already exists.");
        if (!diff.DirtySet.HasOriginalMutations)
        {
            await CopyAtomicallyAsync(sourcePath, outputPath, cancellationToken).ConfigureAwait(false);
            var report = new FidelityReport(FidelityLevel.F0, PackagePreservationLevel.ByteIdentical, diagnostics);
            return new(true, diff, report, diagnostics);
        }

        var extraction = await ExtractAsync(sourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(extraction.Diagnostics);
        if (options.Strict && (extraction.SourceIndex.HasMacro || extraction.SourceIndex.HasSignature || extraction.SourceIndex.HasDocumentProtection))
            return Failure("ProtectedPackage", "Strict restore refuses edits to macro, signed, or protected packages.");

        var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var additions = new List<byte[]>();
        foreach (var operation in diff.PatchSet.Operations.Where(operation => operation.MutatesOriginal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Kind == PatchOperationKind.InsertNode)
            {
                if (!options.AllowInsertParagraph || operation.After is null || operation.After.Kind is not (NodeKind.Paragraph or NodeKind.Heading or NodeKind.ListItem or NodeKind.CodeBlock))
                    return Failure("UnsupportedInsert", "Only paragraph, heading, and list-item insertions are supported by DOCX F1 restore.", operation.NodeId);
                additions.Add(CreateParagraphXml(TextOf(operation.After), operation.After.Kind == NodeKind.Heading, operation.After.Kind == NodeKind.ListItem, operation.After.Kind == NodeKind.CodeBlock));
                continue;
            }
            if (operation.Before is null || operation.Before.Kind is not (NodeKind.Paragraph or NodeKind.Heading or NodeKind.ListItem or NodeKind.CodeBlock or NodeKind.Table) || operation.Before.RawSlice is null)
                return Failure("UnsupportedPatch", "Only anchored paragraph, list-item, and table changes are supported by DOCX F1 restore.", operation.NodeId);
            var slice = operation.Before.RawSlice;
            if (!StringComparer.Ordinal.Equals(slice.PartUri, "/word/document.xml"))
                return Failure("ProtectedBoundary", "Edit crosses a non-body DOCX part boundary.", operation.NodeId);
            var original = await ReadDocumentSliceAsync(sourcePath, slice, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(SafeXml.Sha256(original), slice.Sha256))
                return Failure("SliceHashMismatch", "Original XML slice no longer matches baseline hash.", operation.NodeId);
            if (operation.Kind == PatchOperationKind.ExplicitDelete) changes[operation.NodeId] = Array.Empty<byte>();
            else if (operation.After is not null)
                changes[operation.NodeId] = operation.Before.Kind == NodeKind.Table
                    ? ReplaceTableCells(original, operation.After.Content)
                    : ReplaceParagraphContent(original, operation.After.Content);
        }

        var documentBytes = await ReadZipEntryAsync(sourcePath, "word/document.xml", cancellationToken).ConfigureAwait(false);
        var replacements = baselineGraph.Nodes.Where(node => node.RawSlice is not null && changes.ContainsKey(node.Id))
            .Select(node => (Slice: node.RawSlice!, Data: changes[node.Id])).OrderBy(item => item.Slice.StartOffset).ToArray();
        var patchedDocument = SpliceDocument(documentBytes, replacements, additions);
        await WritePatchedPackageAsync(sourcePath, outputPath, patchedDocument, cancellationToken).ConfigureAwait(false);
        diagnostics.Add(new("PatchedDocumentXml", "Changed DOCX blocks were regenerated; unrelated package payloads were copied verbatim.", DiagnosticSeverity.Information, PartUri: "/word/document.xml"));
        var fidelity = new FidelityReport(FidelityLevel.F1, PackagePreservationLevel.SlicePreserving, diagnostics,
            diff.DirtySet.Nodes.Where(node => node.MutatesOriginal).Select(node => node.NodeId).ToArray());
        return new(true, diff, fidelity, diagnostics);

        DocxRestoreResult Failure(string code, string message, string? nodeId = null)
        {
            diagnostics.Add(new(code, message, DiagnosticSeverity.Error, nodeId));
            return new(false, diff, new(FidelityLevel.FX, PackagePreservationLevel.Unsupported, diagnostics), diagnostics);
        }
    }

    private static void AddParagraph(XElement paragraph, string partUri, RawSliceRef? slice, int order,
        ICollection<DocumentNode> nodes, IDictionary<string, RawSliceRef> sliceMap, IDictionary<string, DocxRunCharacterMap> runMaps,
        IReadOnlyDictionary<string, string> relationships, ContentLayer layer,
        NumberingInfo numberingInfo, IDictionary<(int NumId, int Ilvl), int> listCounters)
    {
        var paraId = (string?)paragraph.Attribute(W14 + "paraId");
        var anchor = new SourceAnchor("docx", partUri,
            paraId is null ? [new("body_child_ordinal", order.ToString(System.Globalization.CultureInfo.InvariantCulture))] : [new("w14_para_id", paraId)], order);
        var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
        var text = ParagraphText(paragraph);
        var style = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
        // Word commonly stores list semantics through a paragraph style (ListBullet /
        // ListNumber) while other producers emit an explicit w:numPr.  Treat both as
        // list items so real-world documents do not lose their list structure on export.
        var isList = paragraph.Element(W + "pPr")?.Element(W + "numPr") is not null ||
                     IsListStyle(style);
        var headingLevel = HeadingLevel(style);
        // A character-level CodeChar run is inline code, not a code block.  Only
        // paragraph styles classify the whole paragraph as a Markdown code block.
        var isCode = IsCodeStyle(style);
        var kind = isList ? NodeKind.ListItem : headingLevel > 0 ? NodeKind.Heading : isCode ? NodeKind.CodeBlock : NodeKind.Paragraph;
        var map = BuildRunMap(id, paragraph);
        var projectionLayer = string.IsNullOrWhiteSpace(text) ? ContentLayer.Hidden : layer;
        var richRuns = ExtractRichTextRuns(paragraph, relationships);
        // Preserve the simple text projection for ordinary paragraphs: it keeps existing
        // graph clients compatible while only opting into rich text when the OOXML contains
        // a supported direct run property or an inline break/tab.
        NodeContent content = richRuns is { Count: > 0 } && richRuns.Any(IsRichRun)
            ? new RichTextNodeContent(richRuns)
            : new TextNodeContent(text);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (headingLevel > 0) extensions["heading_level"] = JsonSerializer.SerializeToElement(headingLevel);
        if (isList)
        {
            extensions["list_level"] = JsonSerializer.SerializeToElement(ListLevel(paragraph));
            var (isOrdered, orderedNumber) = ResolveListNumbering(paragraph, style, numberingInfo, listCounters);
            if (isOrdered && orderedNumber is { } number)
            {
                extensions["list_format"] = JsonSerializer.SerializeToElement("ordered");
                extensions["list_number"] = JsonSerializer.SerializeToElement(number);
            }
        }
        if (isCode) extensions["code_style"] = JsonSerializer.SerializeToElement(style);
        var node = new DocumentNode(id, kind, null, order, projectionLayer, content, anchor, slice, StyleId: style,
            Editability: NodeEditability.EditableInPlace, Provenance: [new(EvidenceKind.Native)], Extensions: extensions);
        nodes.Add(node);
        if (slice is not null) sliceMap[id] = slice;
        runMaps[id] = map;

        foreach (var link in paragraph.Descendants(W + "hyperlink"))
        {
            var relationshipId = (string?)link.Attribute(R + "id");
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var linkAnchor = anchor with { Locators = [new("hyperlink", relationshipId)] };
            var linkId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, linkAnchor);
            nodes.Add(new(linkId, NodeKind.Link, id, order, layer, new ReferenceNodeContent(target, ParagraphText(link)), linkAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)]));
        }
        foreach (var blip in paragraph.Descendants(A + "blip"))
        {
            var relationshipId = (string?)blip.Attribute(R + "embed");
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var imageAnchor = anchor with { Locators = [new("image_relationship", relationshipId)] };
            var imageId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, imageAnchor);
            var docPr = blip.Ancestors().SelectMany(ancestor => ancestor.Elements(WP + "docPr")).FirstOrDefault()
                ?? blip.Ancestors().Descendants(WP + "docPr").FirstOrDefault();
            var description = FirstNonEmptyAttribute(docPr, "descr", "title", "name");
            nodes.Add(new(imageId, NodeKind.Image, id, order, layer, new ReferenceNodeContent(target, description), imageAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)]));
        }
        foreach (var textBox in paragraph.Descendants(W + "txbxContent"))
        {
            var boxAnchor = anchor with { Locators = [new("textbox", textBox.Ancestors().Count().ToString(System.Globalization.CultureInfo.InvariantCulture))] };
            var boxId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, boxAnchor);
            nodes.Add(new(boxId, NodeKind.TextBox, id, order, layer, new TextNodeContent(ParagraphText(textBox)), boxAnchor,
                Editability: NodeEditability.EditableWithConstraints, Provenance: [new(EvidenceKind.Native)]));
        }
        // D18: an explicit page break (w:br type="page") gets its own PageBreak marker node so
        // readable can render a chapter separator (---) instead. ParagraphText/ExtractRichTextRuns/
        // BuildRunMap all exclude type="page" breaks (IsPageBreakElement) so the owning paragraph's
        // own text/rich-runs no longer also render it as a bare "<br>" line — the marker is now the
        // sole representation. Only type="page" is excluded; an ordinary w:br (no type, or any type
        // other than "page") still becomes a real line break exactly as before.
        var pageBreakIndex = 0;
        foreach (var pageBreak in paragraph.Descendants(W + "br"))
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals((string?)pageBreak.Attribute(W + "type"), "page")) continue;
            var breakAnchor = anchor with { Locators = [new("page_break", (pageBreakIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture))] };
            var breakId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, breakAnchor);
            nodes.Add(new(breakId, NodeKind.PageBreak, id, order, layer, new TextNodeContent("page-break"), breakAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)]));
        }
    }

    private static string? FirstNonEmptyAttribute(XElement? element, params string[] names)
    {
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace((string?)element?.Attribute(name))) return (string)element!.Attribute(name)!;
        return null;
    }

    // Builds one Table node per w:tbl, resolving w:gridSpan (horizontal merge, D07) and w:vMerge
    // (vertical merge, D07) into TableCell.ColSpan/RowSpan instead of a flat string grid. This
    // keeps the extracted shape byte-for-byte faithful to the physical tr/tc layout (row count
    // and per-row cell count are unchanged from today), so ReplaceTableCells's same-shape F1
    // restore path is unaffected; only the readable serializer needs to know about spans.
    // A nested w:tbl inside a cell (D08) is *not* folded into that cell's own text (ParagraphText
    // already skips w:tbl subtrees) — it is instead extracted as its own sibling Table node,
    // ordered immediately after this table by consuming further slots from the shared ordinal.
    private static void AddTable(XElement table, string partUri, RawSliceRef? slice, int order,
        ICollection<DocumentNode> nodes, IDictionary<string, RawSliceRef> sliceMap, ref int ordinal)
    {
        var anchor = new SourceAnchor("docx", partUri, [new("body_child_ordinal", order.ToString(System.Globalization.CultureInfo.InvariantCulture))], order);
        var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
        var grid = new List<List<TableCell>>();
        // Tracks, per logical grid column, which (row, cell) in `grid` is the still-open vMerge
        // origin so a later continuation cell can add itself to that origin's RowSpan count.
        var openVerticalMerges = new Dictionary<int, (int RowIndex, int CellIndex)>();
        foreach (var row in table.Elements(W + "tr"))
        {
            var rowCells = new List<TableCell>();
            var gridColumn = 0;
            foreach (var tc in row.Elements(W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                var gridSpan = ParsePositiveInt((string?)tcPr?.Element(W + "gridSpan")?.Attribute(W + "val")) ?? 1;
                var vMerge = tcPr?.Element(W + "vMerge");
                // A vMerge element with no w:val (or w:val="continue") marks a placeholder cell
                // that inherits the cell above; only w:val="restart" starts a new merge region.
                var isContinuation = vMerge is not null && !StringComparer.OrdinalIgnoreCase.Equals((string?)vMerge.Attribute(W + "val"), "restart");
                var text = ParagraphText(tc);
                if (isContinuation)
                {
                    if (openVerticalMerges.TryGetValue(gridColumn, out var origin))
                    {
                        var originCell = grid[origin.RowIndex][origin.CellIndex];
                        grid[origin.RowIndex][origin.CellIndex] = originCell with { RowSpan = originCell.RowSpan + 1 };
                    }
                    rowCells.Add(new TableCell(text, gridSpan, 0));
                }
                else
                {
                    rowCells.Add(new TableCell(text, gridSpan, 1));
                    if (vMerge is not null) openVerticalMerges[gridColumn] = (grid.Count, rowCells.Count - 1);
                    else openVerticalMerges.Remove(gridColumn);
                }
                gridColumn += gridSpan;
            }
            grid.Add(rowCells);
        }
        nodes.Add(new(id, NodeKind.Table, null, order, ContentLayer.Body, new TableNodeContent(grid), anchor, slice,
            Editability: NodeEditability.EditableWithConstraints, Provenance: [new(EvidenceKind.Native)]));
        if (slice is not null) sliceMap[id] = slice;

        // Direct tc children only: a deeper nested table is discovered when we recurse into this
        // one, so scanning only one level down here avoids visiting (and re-adding) it twice.
        foreach (var tc in table.Elements(W + "tr").Elements(W + "tc"))
            foreach (var nestedTable in tc.Elements(W + "tbl"))
            {
                var nestedOrder = ordinal++;
                AddTable(nestedTable, partUri, null, nestedOrder, nodes, sliceMap, ref ordinal);
            }
    }

    private static int? ParsePositiveInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    private static async Task<int> AddRelatedTextPartsAsync(ZipArchive archive, IReadOnlyDictionary<string, string> relationships, string relationshipFragment,
        NodeKind kind, ContentLayer layer, ICollection<DocumentNode> nodes, int ordinal, CancellationToken cancellationToken)
    {
        foreach (var target in relationships.Values.Where(target => target.Contains(relationshipFragment, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal))
        {
            var entry = archive.GetEntry(NormalizeEntryName(target));
            if (entry is null) continue;
            var partUri = "/" + entry.FullName;
            var xml = SafeXml.LoadDocument(await ReadEntryAsync(entry, cancellationToken).ConfigureAwait(false));
            foreach (var paragraph in xml.Descendants(W + "p"))
            {
                var anchor = new SourceAnchor("docx", partUri, [new("part_paragraph_ordinal", ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))], ordinal);
                var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
                nodes.Add(new(id, kind, null, ordinal++, layer, new TextNodeContent(ParagraphText(paragraph)), anchor,
                    Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)]));
            }
        }
        return ordinal;
    }

    // D05: a w:sectPr describes the section that ENDS at the paragraph holding it (or, for the
    // final section, the trailing body-level w:sectPr) — so the *next* body element index is
    // where that section's successor starts. Landscape sections in this fixture always contain
    // at least one paragraph, so an out-of-range start (an empty trailing section) is dropped
    // rather than special-cased.
    private static HashSet<int> FindLandscapeSectionStarts(XDocument doc, XElement[] bodyElements)
    {
        var result = new HashSet<int>();
        var sectionStart = 0;
        for (var index = 0; index < bodyElements.Length; index++)
        {
            if (bodyElements[index].Name != W + "p") continue;
            var sectPr = bodyElements[index].Element(W + "pPr")?.Element(W + "sectPr");
            if (sectPr is null) continue;
            if (IsLandscape(sectPr) && sectionStart < bodyElements.Length) result.Add(sectionStart);
            sectionStart = index + 1;
        }
        var trailingSectPr = doc.Root?.Element(W + "body")?.Element(W + "sectPr");
        if (trailingSectPr is not null && IsLandscape(trailingSectPr) && sectionStart < bodyElements.Length)
            result.Add(sectionStart);
        return result;
    }

    private static bool IsLandscape(XElement sectPr) =>
        StringComparer.OrdinalIgnoreCase.Equals((string?)sectPr.Element(W + "pgSz")?.Attribute(W + "orient"), "landscape");

    // Emits a machine-readable `<!-- section:landscape -->` marker (D05) reusing NodeKind.Section
    // with a section_orientation extension. Xlsx/Pptx never set that extension, so their existing
    // Section-as-heading rendering in ReadableMarkdownSerializer is unaffected.
    private static void AddSectionOrientationMarker(ICollection<DocumentNode> nodes, string partUri, int order, string orientation)
    {
        var anchor = new SourceAnchor("docx", partUri, [new("section_break", order.ToString(System.Globalization.CultureInfo.InvariantCulture))], order);
        var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["section_orientation"] = JsonSerializer.SerializeToElement(orientation),
        };
        nodes.Add(new(id, NodeKind.Section, null, order, ContentLayer.Body, new TextNodeContent("section-break"), anchor,
            Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)], Extensions: extensions));
    }

    private static void AddFootnotes(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal)
    {
        var xml = SafeXml.LoadDocument(bytes);
        foreach (var footnote in xml.Descendants(W + "footnote"))
        {
            var noteId = (string?)footnote.Attribute(W + "id") ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = new SourceAnchor("docx", "/word/footnotes.xml", [new("footnote_id", noteId)], ordinal);
            var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
            nodes.Add(new(id, NodeKind.Footnote, null, ordinal++, ContentLayer.Body, new TextNodeContent(ParagraphText(footnote)), anchor,
                Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)]));
        }
    }

    // D16: word/endnotes.xml mirrors word/footnotes.xml but was never read at all, so an
    // endnote's text disappeared entirely rather than merely losing its number.
    private static void AddEndnotes(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal)
    {
        var xml = SafeXml.LoadDocument(bytes);
        foreach (var endnote in xml.Descendants(W + "endnote"))
        {
            var noteId = (string?)endnote.Attribute(W + "id") ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = new SourceAnchor("docx", "/word/endnotes.xml", [new("endnote_id", noteId)], ordinal);
            var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
            nodes.Add(new(id, NodeKind.Endnote, null, ordinal++, ContentLayer.Body, new TextNodeContent(ParagraphText(endnote)), anchor,
                Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)]));
        }
    }

    // D17: word/comments.xml was never read, so a reviewer comment's text disappeared entirely.
    // w:author is captured as a comment_author extension so readable can label who wrote it.
    private static void AddComments(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal)
    {
        var xml = SafeXml.LoadDocument(bytes);
        foreach (var comment in xml.Descendants(W + "comment"))
        {
            var commentId = (string?)comment.Attribute(W + "id") ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = new SourceAnchor("docx", "/word/comments.xml", [new("comment_id", commentId)], ordinal);
            var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
            var author = (string?)comment.Attribute(W + "author");
            var extensions = string.IsNullOrWhiteSpace(author)
                ? null
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["comment_author"] = JsonSerializer.SerializeToElement(author) };
            nodes.Add(new(id, NodeKind.Comment, null, ordinal++, ContentLayer.Body, new TextNodeContent(ParagraphText(comment)), anchor,
                Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)], Extensions: extensions));
        }
    }

    // A paragraph can host two other kinds of opaque OOXML content besides its own runs: a
    // nested table cell's w:tbl (D08), and a legacy VML text box's w:txbxContent (D14). Both
    // already surface as their own DocumentNode (a sibling Table node, or a TextBox node), so
    // paragraph/cell text extraction must not descend into them a second time or the same text
    // is emitted twice. RelevantDescendants is a subtree-skipping stand-in for XContainer's own
    // (non-skippable) Descendants().
    private static readonly XName[] OpaqueParagraphContainers = [W + "tbl", W + "txbxContent"];

    private static IEnumerable<XElement> RelevantDescendants(XContainer container)
    {
        foreach (var child in container.Elements())
        {
            if (OpaqueParagraphContainers.Contains(child.Name)) continue;
            yield return child;
            foreach (var descendant in RelevantDescendants(child)) yield return descendant;
        }
    }

    // D18 (coordinator-adjudicated): only w:br type="page" is excluded here — an explicit page
    // break already gets its own PageBreak marker node (see AddParagraph), so it must not also
    // contribute a "\n"/LineBreak to the owning paragraph's own text. w:cr and a plain w:br
    // (no type, or any type other than "page" — e.g. ordinary Shift+Enter line wrapping) are
    // untouched and keep contributing a line break exactly as before.
    private static bool IsPageBreakElement(XElement element) =>
        element.Name == W + "br" && StringComparer.OrdinalIgnoreCase.Equals((string?)element.Attribute(W + "type"), "page");

    private static string ParagraphText(XContainer container)
    {
        var output = new StringBuilder();
        foreach (var element in RelevantDescendants(container))
        {
            if (element.Name == W + "t") output.Append(element.Value);
            else if (element.Name is var name && (name == W + "br" || name == W + "cr"))
            {
                if (IsPageBreakElement(element)) continue;
                output.Append('\n');
            }
            else if (element.Name == W + "tab") output.Append('\t');
        }
        return output.ToString();
    }

    private static IReadOnlyList<TextRun> ExtractRichTextRuns(XElement paragraph, IReadOnlyDictionary<string, string> relationships)
    {
        var runs = new List<TextRun>();
        foreach (var run in RelevantDescendants(paragraph).Where(element => element.Name == W + "r"))
        {
            var properties = ReadRunProperties(run);
            var linkTarget = ResolveEnclosingHyperlinkTarget(run, relationships);
            foreach (var child in run.Elements())
            {
                if (child.Name == W + "t")
                    runs.Add(new TextRun(child.Value, properties.StyleId, properties.Bold, properties.Italic, properties.Underline, properties.Strike, properties.Code,
                        LinkTarget: linkTarget, Color: properties.Color, HighlightColor: properties.HighlightColor));
                else if (child.Name is var name && (name == W + "br" || name == W + "cr"))
                {
                    if (IsPageBreakElement(child)) continue; // D18: represented solely by its own PageBreak marker node.
                    runs.Add(new TextRun("\n", properties.StyleId, properties.Bold, properties.Italic, properties.Underline, properties.Strike, properties.Code, TextRunKind.LineBreak,
                        LinkTarget: linkTarget, Color: properties.Color, HighlightColor: properties.HighlightColor));
                }
                else if (child.Name == W + "tab")
                    runs.Add(new TextRun("\t", properties.StyleId, properties.Bold, properties.Italic, properties.Underline, properties.Strike, properties.Code, TextRunKind.Tab,
                        LinkTarget: linkTarget, Color: properties.Color, HighlightColor: properties.HighlightColor));
            }
        }
        return runs;
    }

    // Only an *external* hyperlink (w:hyperlink with a relationship id) resolves to a URL here.
    // An internal bookmark reference (w:anchor, no r:id) intentionally yields null: it already
    // flows through as plain paragraph text with no separate Link node (see AddParagraph), and
    // D12-2 requires that text stay unique rather than gaining a second, markdown-link rendering.
    private static string? ResolveEnclosingHyperlinkTarget(XElement run, IReadOnlyDictionary<string, string> relationships)
    {
        var relationshipId = (string?)run.Ancestors(W + "hyperlink").FirstOrDefault()?.Attribute(R + "id");
        return relationshipId is not null && relationships.TryGetValue(relationshipId, out var target) ? target : null;
    }

    private static bool IsRichRun(TextRun run) =>
        run.Kind != TextRunKind.Text || run.Bold || run.Italic || run.Underline || run.Strike || run.Code || run.StyleId is not null ||
        run.LinkTarget is not null || run.Color is not null || run.HighlightColor is not null;

    private static (string? StyleId, bool Bold, bool Italic, bool Underline, bool Strike, bool Code, string? Color, string? HighlightColor) ReadRunProperties(XElement run)
    {
        var properties = run.Element(W + "rPr");
        var styleId = (string?)properties?.Element(W + "rStyle")?.Attribute(W + "val");
        var bold = IsEnabled(properties?.Element(W + "b"));
        var italic = IsEnabled(properties?.Element(W + "i"));
        var underline = IsEnabled(properties?.Element(W + "u"), "none");
        var strike = IsEnabled(properties?.Element(W + "strike"));
        var fonts = properties?.Element(W + "rFonts");
        var fontNames = new[] { (string?)fonts?.Attribute(W + "ascii"), (string?)fonts?.Attribute(W + "hAnsi"), (string?)fonts?.Attribute(W + "eastAsia") };
        var code = styleId?.Contains("code", StringComparison.OrdinalIgnoreCase) == true ||
                   fontNames.Any(IsMonospaceFont);
        // Direct character color/highlight (D15). These are readable-only decorations: they are
        // deliberately outside the round-trippable rich-text contract (see TextRun's doc comment).
        var colorValue = (string?)properties?.Element(W + "color")?.Attribute(W + "val");
        var color = colorValue is not null && !StringComparer.OrdinalIgnoreCase.Equals(colorValue, "auto") ? colorValue : null;
        var highlightValue = (string?)properties?.Element(W + "highlight")?.Attribute(W + "val");
        var highlight = highlightValue is not null && !StringComparer.OrdinalIgnoreCase.Equals(highlightValue, "none") ? highlightValue : null;
        return (styleId, bold, italic, underline, strike, code, color, highlight);
    }

    private static bool IsEnabled(XElement? element, string disabledValue = "0")
    {
        if (element is null) return false;
        var value = (string?)element.Attribute(W + "val");
        return !StringComparer.OrdinalIgnoreCase.Equals(value, disabledValue) &&
               !StringComparer.OrdinalIgnoreCase.Equals(value, "false");
    }

    private static bool IsMonospaceFont(string? fontName) => fontName is not null &&
        (fontName.Contains("consolas", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("courier", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("menlo", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("monaco", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("source code", StringComparison.OrdinalIgnoreCase));
    private static DocxRunCharacterMap BuildRunMap(string nodeId, XElement paragraph)
    {
        var spans = new List<RunCharacterSpan>();
        var start = 0;
        var ordinal = 0;
        foreach (var run in RelevantDescendants(paragraph).Where(element => element.Name == W + "r"))
        {
            var text = string.Concat(run.Elements().Select(element => element.Name == W + "t" ? element.Value :
                element.Name is var name && (name == W + "br" || name == W + "cr") ? (IsPageBreakElement(element) ? string.Empty : "\n") :
                element.Name == W + "tab" ? "\t" : string.Empty));
            spans.Add(new(start, start + text.Length, ordinal++, text));
            start += text.Length;
        }
        return new(nodeId, spans);
    }

    private static IReadOnlyDictionary<string, string> ReadRelationships(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return new Dictionary<string, string>();
        var xml = SafeXml.LoadDocument(ReadEntryAsync(entry, cancellationToken).GetAwaiter().GetResult());
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        return xml.Descendants(rel + "Relationship")
            .Where(item => (string?)item.Attribute("Id") is not null && (string?)item.Attribute("Target") is not null)
            .ToDictionary(item => (string)item.Attribute("Id")!, item => ResolveTarget("word/document.xml", (string)item.Attribute("Target")!), StringComparer.Ordinal);
    }

    private static string ResolveTarget(string baseEntry, string target)
    {
        // External hyperlink targets are data, not files to open. Preserve the URI
        // verbatim so the graph can expose the link without dereferencing it.
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.IsAbsoluteUri)
            return target;
        var baseSegments = baseEntry.Split('/')[..^1].ToList();
        foreach (var part in target.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (baseSegments.Count > 0) baseSegments.RemoveAt(baseSegments.Count - 1); continue; }
            baseSegments.Add(part);
        }
        return string.Join('/', baseSegments);
    }

    private static async Task<bool> HasDocumentProtectionAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var settings = archive.GetEntry("word/settings.xml");
        if (settings is null) return false;
        var xml = SafeXml.LoadDocument(await ReadEntryAsync(settings, cancellationToken).ConfigureAwait(false));
        return xml.Descendants(W + "documentProtection").Any();
    }

    private static string NormalizeEntryName(string name) => name.TrimStart('/');
    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string name) => archive.GetEntry(name) ?? throw new InvalidDataException($"DOCX required part '{name}' is missing.");
    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadZipEntryAsync(string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        return await ReadEntryAsync(RequireEntry(archive, entryName), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadDocumentSliceAsync(string sourcePath, RawSliceRef slice, CancellationToken cancellationToken)
    {
        var document = await ReadZipEntryAsync(sourcePath, NormalizeEntryName(slice.PartUri), cancellationToken).ConfigureAwait(false);
        if (slice.StartOffset < 0 || slice.EndOffset > document.Length || slice.EndOffset < slice.StartOffset)
            throw new InvalidDataException("Stored DOCX XML slice range is invalid.");
        return document.AsSpan((int)slice.StartOffset, checked((int)(slice.EndOffset - slice.StartOffset))).ToArray();
    }

    private static byte[] ReplaceParagraphContent(byte[] originalSlice, NodeContent content)
    {
        return content switch
        {
            TextNodeContent text => ReplaceParagraphText(originalSlice, text.Text),
            RichTextNodeContent rich => ReplaceParagraphRichText(originalSlice, rich.Runs),
            _ => throw new InvalidDataException("An edited DOCX paragraph must retain text or supported rich text content.")
        };
    }

    private static byte[] ReplaceParagraphText(byte[] originalSlice, string text)
    {
        var fragment = Encoding.UTF8.GetString(originalSlice);
        var wrapper = $"<drmd:root xmlns:drmd=\"urn:drmd\" xmlns:w=\"{W}\" xmlns:w14=\"{W14}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\" xmlns:wp=\"{WP}\">{fragment}</drmd:root>";
        var paragraph = SafeXml.LoadDocument(SafeXml.Utf8(wrapper)).Root?.Elements().SingleOrDefault()
            ?? throw new InvalidDataException("DOCX paragraph slice is empty.");
        RejectUnsupportedPlainTextParagraph(paragraph);
        var textElements = paragraph.Descendants(W + "t").ToArray();
        if (textElements.Length == 0)
        {
            paragraph.Add(new XElement(W + "r", new XElement(W + "t", text)));
        }
        else
        {
            // Preserve existing run/text boundaries where possible. This is the minimal
            // character-offset-map policy: original character spans remain assigned to
            // their original formatting runs; growth is assigned to the final run.
            var originalLengths = textElements.Select(element => element.Value.Length).ToArray();
            var offset = 0;
            for (var index = 0; index < textElements.Length; index++)
            {
                var length = index == textElements.Length - 1
                    ? text.Length - offset
                    : Math.Min(originalLengths[index], Math.Max(0, text.Length - offset));
                var replacement = text.Substring(offset, length);
                textElements[index].Value = replacement;
                if (!StringComparer.Ordinal.Equals(replacement, replacement.Trim())) textElements[index].SetAttributeValue(XNamespace.Xml + "space", "preserve");
                offset += length;
            }
        }
        return SafeXml.Utf8(paragraph.ToString(SaveOptions.DisableFormatting));
    }

    private static byte[] ReplaceParagraphRichText(byte[] originalSlice, IReadOnlyList<TextRun> runs)
    {
        var paragraph = LoadParagraphSlice(originalSlice);
        RejectUnsupportedRichTextParagraph(paragraph);
        var templates = BuildOriginalRunTemplates(paragraph);

        // Paragraph properties stay untouched.  We replace only the direct w:r children,
        // preserving surrounding bookmark/comment anchors and the paragraph's layout,
        // numbering, alignment, and style properties.  Each replacement run starts from
        // the original run properties at the same character position, so unprojected
        // typography (font family, size, color, language, kerning, etc.) survives an edit.
        paragraph.Elements(W + "r").Remove();
        var insertionPoint = paragraph.Element(W + "pPr");
        var templateIndex = 0;
        foreach (var run in runs)
        {
            var matchedIndex = FindMatchingTemplate(templates, templateIndex, run);
            if (matchedIndex >= 0) templateIndex = matchedIndex;
            if (run.Kind == TextRunKind.Text)
            {
                var groupEnd = templateIndex;
                while (groupEnd < templates.Count && Matches(templates[groupEnd], run)) groupEnd++;
                if (groupEnd == templateIndex && templateIndex < templates.Count) groupEnd++;
                var textOffset = 0;
                for (var index = templateIndex; index < groupEnd && textOffset < run.Text.Length; index++)
                {
                    var template = templates[index];
                    var length = index == groupEnd - 1
                        ? run.Text.Length - textOffset
                        : Math.Min(template.Length, run.Text.Length - textOffset);
                    var replacement = CreateRichRun(run with { Text = run.Text.Substring(textOffset, length) }, template.Properties);
                    Insert(replacement);
                    textOffset += length;
                }
                if (textOffset < run.Text.Length)
                    Insert(CreateRichRun(run with { Text = run.Text[textOffset..] }, templates.Count == 0 ? null : templates[Math.Min(templateIndex, templates.Count - 1)].Properties));
                templateIndex = groupEnd;
                continue;
            }

            var specialTemplate = templateIndex < templates.Count ? templates[templateIndex] : templates.LastOrDefault();
            Insert(CreateRichRun(run, specialTemplate?.Properties));
            if (templateIndex < templates.Count) templateIndex++;
        }
        return SafeXml.Utf8(paragraph.ToString(SaveOptions.DisableFormatting));

        void Insert(XElement replacement)
        {
            if (insertionPoint is null) paragraph.AddFirst(replacement);
            else insertionPoint.AddAfterSelf(replacement);
            insertionPoint = replacement;
        }
    }

    private sealed record OriginalRunTemplate(int Length, XElement? Properties, string? StyleId, bool Bold, bool Italic, bool Underline, bool Strike, bool Code, TextRunKind Kind);

    private static IReadOnlyList<OriginalRunTemplate> BuildOriginalRunTemplates(XElement paragraph)
    {
        var result = new List<OriginalRunTemplate>();
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var properties = run.Element(W + "rPr");
            var formatting = ReadRunProperties(run);
            foreach (var child in run.Elements())
            {
                var length = child.Name == W + "t" ? child.Value.Length
                    : child.Name is var name && (name == W + "br" || name == W + "cr" || name == W + "tab") ? 1
                    : 0;
                if (length == 0) continue;
                var kind = child.Name == W + "tab" ? TextRunKind.Tab
                    : child.Name is var childName && (childName == W + "br" || childName == W + "cr") ? TextRunKind.LineBreak
                    : TextRunKind.Text;
                result.Add(new OriginalRunTemplate(length, properties is null ? null : new XElement(properties), formatting.StyleId,
                    formatting.Bold, formatting.Italic, formatting.Underline, formatting.Strike, formatting.Code, kind));
            }
        }
        return result;
    }

    private static int FindMatchingTemplate(IReadOnlyList<OriginalRunTemplate> templates, int start, TextRun run)
    {
        for (var index = start; index < templates.Count; index++)
            if (Matches(templates[index], run)) return index;
        return -1;
    }

    private static bool Matches(OriginalRunTemplate template, TextRun run) =>
        template.Kind == run.Kind && template.StyleId == run.StyleId && template.Bold == run.Bold && template.Italic == run.Italic &&
        template.Underline == run.Underline && template.Strike == run.Strike && template.Code == run.Code;

    private static XElement LoadParagraphSlice(byte[] originalSlice)
    {
        var fragment = Encoding.UTF8.GetString(originalSlice);
        var wrapper = $"<drmd:root xmlns:drmd=\"urn:drmd\" xmlns:w=\"{W}\" xmlns:w14=\"{W14}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\" xmlns:wp=\"{WP}\">{fragment}</drmd:root>";
        return SafeXml.LoadDocument(SafeXml.Utf8(wrapper)).Root?.Elements().SingleOrDefault()
            ?? throw new InvalidDataException("DOCX paragraph slice is empty.");
    }

    private static void RejectUnsupportedRichTextParagraph(XElement paragraph)
    {
        if (paragraph.Descendants(W + "fldChar").Any() || paragraph.Descendants(W + "instrText").Any())
            throw new InvalidDataException("A field boundary cannot be edited in strict DOCX restore.");
        if (paragraph.Descendants(W + "hyperlink").Any() || paragraph.Descendants(W + "drawing").Any() || paragraph.Descendants(W + "object").Any())
            throw new InvalidDataException("Rich-text editing does not support paragraphs containing hyperlinks, drawings, or embedded objects.");
        if (paragraph.Descendants(W + "ins").Any() || paragraph.Descendants(W + "del").Any())
            throw new InvalidDataException("Rich-text editing does not support tracked revisions.");
    }

    private static void RejectUnsupportedPlainTextParagraph(XElement paragraph)
    {
        if (paragraph.Descendants(W + "fldChar").Any() || paragraph.Descendants(W + "instrText").Any())
            throw new InvalidDataException("A field boundary cannot be edited in strict DOCX restore.");
        if (paragraph.Descendants(W + "hyperlink").Any() || paragraph.Descendants(W + "drawing").Any() || paragraph.Descendants(W + "object").Any())
            throw new InvalidDataException("Text editing does not support paragraphs containing hyperlinks, drawings, or embedded objects.");
        if (paragraph.Descendants(W + "ins").Any() || paragraph.Descendants(W + "del").Any())
            throw new InvalidDataException("Text editing does not support tracked revisions.");
    }

    private static XElement CreateRichRun(TextRun run, XElement? originalProperties = null)
    {
        var element = new XElement(W + "r");
        var properties = CreateRunProperties(run, originalProperties);
        if (properties is not null) element.Add(properties);
        switch (run.Kind)
        {
            case TextRunKind.Text:
                {
                    var text = new XElement(W + "t", run.Text);
                    if (!StringComparer.Ordinal.Equals(run.Text, run.Text.Trim())) text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                    element.Add(text);
                    break;
                }
            case TextRunKind.LineBreak:
                element.Add(new XElement(W + "br"));
                break;
            case TextRunKind.Tab:
                element.Add(new XElement(W + "tab"));
                break;
            default:
                throw new InvalidDataException($"Unsupported rich text run kind '{run.Kind}'.");
        }
        return element;
    }

    private static XElement? CreateRunProperties(TextRun run, XElement? originalProperties = null)
    {
        var properties = originalProperties is null ? new XElement(W + "rPr") : new XElement(originalProperties);
        properties.Elements(W + "rStyle").Remove();
        properties.Elements(W + "b").Remove();
        properties.Elements(W + "bCs").Remove();
        properties.Elements(W + "i").Remove();
        properties.Elements(W + "iCs").Remove();
        properties.Elements(W + "u").Remove();
        properties.Elements(W + "strike").Remove();
        properties.Elements(W + "dstrike").Remove();
        if (run.StyleId is not null) properties.Add(new XElement(W + "rStyle", new XAttribute(W + "val", run.StyleId)));
        if (run.Bold) properties.Add(new XElement(W + "b"));
        if (run.Italic) properties.Add(new XElement(W + "i"));
        if (run.Underline) properties.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
        if (run.Strike) properties.Add(new XElement(W + "strike"));
        // Word has no semantic inline-code element.  Preserve a code character style when
        // supplied; otherwise emit the conservative portable monospace equivalent.
        if (run.Code && (run.StyleId is null || !run.StyleId.Contains("code", StringComparison.OrdinalIgnoreCase)))
        {
            var fonts = properties.Element(W + "rFonts");
            var fontNames = new[] { (string?)fonts?.Attribute(W + "ascii"), (string?)fonts?.Attribute(W + "hAnsi"), (string?)fonts?.Attribute(W + "eastAsia") };
            if (!fontNames.Any(IsMonospaceFont))
            {
                fonts?.Remove();
                properties.Add(new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", "Consolas"),
                    new XAttribute(W + "hAnsi", "Consolas"),
                    new XAttribute(W + "eastAsia", "Consolas")));
            }
        }
        return properties.HasElements ? properties : null;
    }

    private static byte[] CreateParagraphXml(string text, bool heading, bool listItem, bool codeBlock)
    {
        var paragraph = new XElement(W + "p");
        var properties = new XElement(W + "pPr");
        if (heading) properties.Add(new XElement(W + "pStyle", new XAttribute(W + "val", "Heading1")));
        if (codeBlock) properties.Add(new XElement(W + "pStyle", new XAttribute(W + "val", "Code")));
        if (listItem) properties.Add(new XElement(W + "numPr"));
        if (properties.HasElements) paragraph.Add(properties);
        var value = new XElement(W + "t", text);
        if (!StringComparer.Ordinal.Equals(text, text.Trim())) value.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        paragraph.Add(new XElement(W + "r", value));
        return SafeXml.Utf8(paragraph.ToString(SaveOptions.DisableFormatting));
    }

    private static int HeadingLevel(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return 0;
        // Word allows Heading styles beyond 6 (e.g. "Heading 7"); Markdown only has six levels,
        // so WriteHeading clamps the value. Recognizing the style at all (D02) beats losing its
        // heading-ness entirely just because GFM cannot represent it 1:1.
        var match = System.Text.RegularExpressions.Regex.Match(style, @"heading\s*(?<level>[1-9][0-9]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["level"].Value, out var level) ? level : 0;
    }

    private static bool IsCodeStyle(string? style) => style is not null &&
        (style.Contains("code", StringComparison.OrdinalIgnoreCase) || style.Contains("preformatted", StringComparison.OrdinalIgnoreCase) ||
         style.Contains("source", StringComparison.OrdinalIgnoreCase) || style.Contains("monospace", StringComparison.OrdinalIgnoreCase));

    private static int ListLevel(XElement paragraph)
    {
        var ilvl = paragraph.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val")?.Value;
        return int.TryParse(ilvl, out var level) ? level : 0;
    }

    private static bool IsListStyle(string? style) => style is not null &&
        (style.Contains("listbullet", StringComparison.OrdinalIgnoreCase) ||
         style.Contains("listnumber", StringComparison.OrdinalIgnoreCase) ||
         style.Contains("listparagraph", StringComparison.OrdinalIgnoreCase));

    // numbering.xml/styles.xml resolution for D10: distinguishing an ordered list (numFmt
    // decimal/decimalZero/lowerRoman/... ) from a bullet list (numFmt bullet/none), which
    // ListLevel/IsListStyle alone cannot do since both look identical at the paragraph level.
    private readonly record struct NumberingInfo(
        IReadOnlyDictionary<string, int> StyleNumIds,
        IReadOnlyDictionary<int, int> AbstractNumIdsByNumId,
        IReadOnlyDictionary<(int AbstractNumId, int Ilvl), string> FormatsByLevel)
    {
        public static readonly NumberingInfo Empty = new(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, int>(),
            new Dictionary<(int, int), string>());
    }

    private static NumberingInfo ReadNumberingInfo(ZipArchive archive, CancellationToken cancellationToken)
    {
        var styleNumIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (archive.GetEntry("word/styles.xml") is { } stylesEntry)
        {
            var stylesXml = SafeXml.LoadDocument(ReadEntryAsync(stylesEntry, cancellationToken).GetAwaiter().GetResult());
            foreach (var style in stylesXml.Root?.Elements(W + "style") ?? Enumerable.Empty<XElement>())
            {
                var styleId = (string?)style.Attribute(W + "styleId");
                var numId = ParsePositiveInt((string?)style.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val"));
                if (styleId is not null && numId is not null) styleNumIds[styleId] = numId.Value;
            }
        }

        var abstractNumIds = new Dictionary<int, int>();
        var formatsByLevel = new Dictionary<(int, int), string>();
        if (archive.GetEntry("word/numbering.xml") is { } numberingEntry)
        {
            var numberingXml = SafeXml.LoadDocument(ReadEntryAsync(numberingEntry, cancellationToken).GetAwaiter().GetResult());
            foreach (var num in numberingXml.Root?.Elements(W + "num") ?? Enumerable.Empty<XElement>())
            {
                var numId = ParsePositiveInt((string?)num.Attribute(W + "numId"));
                var abstractNumId = ParsePositiveInt((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val"));
                if (numId is not null && abstractNumId is not null) abstractNumIds[numId.Value] = abstractNumId.Value;
            }
            foreach (var abstractNum in numberingXml.Root?.Elements(W + "abstractNum") ?? Enumerable.Empty<XElement>())
            {
                var abstractNumId = ParsePositiveInt((string?)abstractNum.Attribute(W + "abstractNumId"));
                if (abstractNumId is null) continue;
                foreach (var level in abstractNum.Elements(W + "lvl"))
                {
                    var ilvlText = (string?)level.Attribute(W + "ilvl");
                    var numFmt = (string?)level.Element(W + "numFmt")?.Attribute(W + "val");
                    if (numFmt is not null && int.TryParse(ilvlText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ilvl))
                        formatsByLevel[(abstractNumId.Value, ilvl)] = numFmt;
                }
            }
        }
        return new NumberingInfo(styleNumIds, abstractNumIds, formatsByLevel);
    }

    // Resolves whether a list paragraph is an ordered (numbered) item and, if so, its sequence
    // number. The counter is keyed by (numId, ilvl) and persists across intervening non-list
    // paragraphs, matching Word's own numbering semantics (D10: a numbered list continues across
    // an interrupting paragraph rather than restarting).
    private static (bool IsOrdered, int? Number) ResolveListNumbering(
        XElement paragraph, string? style, NumberingInfo numberingInfo, IDictionary<(int NumId, int Ilvl), int> counters)
    {
        var numPr = paragraph.Element(W + "pPr")?.Element(W + "numPr");
        var numId = ParsePositiveInt((string?)numPr?.Element(W + "numId")?.Attribute(W + "val"))
            ?? (style is not null && numberingInfo.StyleNumIds.TryGetValue(style, out var styleNumId) ? styleNumId : (int?)null);
        if (numId is null) return (false, null);
        var ilvlText = (string?)numPr?.Element(W + "ilvl")?.Attribute(W + "val");
        var ilvl = int.TryParse(ilvlText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedIlvl) ? parsedIlvl : 0;
        if (!numberingInfo.AbstractNumIdsByNumId.TryGetValue(numId.Value, out var abstractNumId) ||
            !numberingInfo.FormatsByLevel.TryGetValue((abstractNumId, ilvl), out var numFmt) ||
            StringComparer.OrdinalIgnoreCase.Equals(numFmt, "bullet") || StringComparer.OrdinalIgnoreCase.Equals(numFmt, "none"))
            return (false, null);
        var key = (numId.Value, ilvl);
        var next = counters.TryGetValue(key, out var current) ? current + 1 : 1;
        counters[key] = next;
        return (true, next);
    }

    private static byte[] ReplaceTableCells(byte[] originalSlice, NodeContent content)
    {
        if (content is not TableNodeContent edited) throw new InvalidDataException("An edited DOCX table must retain table cell content.");
        var fragment = Encoding.UTF8.GetString(originalSlice);
        var wrapper = $"<drmd:root xmlns:drmd=\"urn:drmd\" xmlns:w=\"{W}\" xmlns:w14=\"{W14}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\" xmlns:wp=\"{WP}\">{fragment}</drmd:root>";
        var table = SafeXml.LoadDocument(SafeXml.Utf8(wrapper)).Root?.Elements().SingleOrDefault()
            ?? throw new InvalidDataException("DOCX table slice is empty.");
        if (table.Descendants(W + "fldChar").Any()) throw new InvalidDataException("A field boundary cannot be edited in a table.");
        var rows = table.Elements(W + "tr").ToArray();
        if (rows.Length != edited.Rows.Count) throw new InvalidDataException("DOCX table row count changed; F1 table structure edits are not supported.");
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var cells = rows[rowIndex].Elements(W + "tc").ToArray();
            if (cells.Length != edited.Rows[rowIndex].Count) throw new InvalidDataException("DOCX table cell count changed; F1 table structure edits are not supported.");
            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++) ReplaceCellText(cells[cellIndex], edited.Rows[rowIndex][cellIndex].Text);
        }
        return SafeXml.Utf8(table.ToString(SaveOptions.DisableFormatting));
    }

    private static void ReplaceCellText(XElement cell, string text)
    {
        var textElements = cell.Descendants(W + "t").ToArray();
        if (textElements.Length == 0)
        {
            var paragraph = cell.Element(W + "p") ?? new XElement(W + "p");
            if (paragraph.Parent is null) cell.Add(paragraph);
            paragraph.Add(new XElement(W + "r", new XElement(W + "t", text)));
            return;
        }
        textElements[0].Value = text;
        if (!StringComparer.Ordinal.Equals(text, text.Trim())) textElements[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var element in textElements.Skip(1)) element.Value = string.Empty;
    }

    private static string TextOf(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => throw new InvalidDataException($"Node '{node.Id}' does not contain editable text.")
    };

    private static byte[] SpliceDocument(byte[] original, IReadOnlyList<(RawSliceRef Slice, byte[] Data)> replacements, IReadOnlyList<byte[]> additions)
    {
        var output = new MemoryStream(original.Length + additions.Sum(item => item.Length));
        long copied = 0;
        foreach (var replacement in replacements)
        {
            if (replacement.Slice.StartOffset < copied || replacement.Slice.EndOffset > original.LongLength)
                throw new InvalidDataException("DOCX patch slices overlap or exceed document.xml.");
            output.Write(original, (int)copied, checked((int)(replacement.Slice.StartOffset - copied)));
            output.Write(replacement.Data);
            copied = replacement.Slice.EndOffset;
        }
        // Locate the body close tag from a fresh scanner so additions cannot accidentally cross an XML boundary.
        if (additions.Count > 0)
        {
            var bodyEnd = XmlSliceScanner.FindWordBodyBlocks(original, "/word/document.xml").BodyEndTagStart;
            if (bodyEnd < copied) throw new InvalidDataException("Cannot insert a paragraph after a patched body boundary.");
            output.Write(original, (int)copied, bodyEnd - (int)copied);
            foreach (var addition in additions) output.Write(addition);
            copied = bodyEnd;
        }
        output.Write(original, (int)copied, original.Length - (int)copied);
        return output.ToArray();
    }

    private static async Task WritePatchedPackageAsync(string sourcePath, string outputPath, byte[] patchedDocument, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new IOException("Output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            using (var input = new ZipArchive(source, ZipArchiveMode.Read))
            await using (var destination = File.Create(temporary))
            using (var output = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var entry in input.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var copied = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    await using var target = copied.Open();
                    if (StringComparer.Ordinal.Equals(entry.FullName, "word/document.xml"))
                        await target.WriteAsync(patchedDocument, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        await using var origin = entry.Open();
                        await origin.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            File.Move(temporary, outputPath);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static async Task CopyAtomicallyAsync(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new IOException("Output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var input = File.OpenRead(sourcePath);
            await using (var output = File.Create(temporary)) await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, outputPath);
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }
}

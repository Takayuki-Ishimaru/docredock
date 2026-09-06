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
using DocRedock.VisualInference;

namespace DocRedock.Formats.OpenXml.Docx;

/// <summary>
/// Built-in DOCX extractor and F1 paragraph patcher. It parses XML with a secure reader,
/// retains original top-level XML slices, and only splices blocks selected by graph diff.
/// </summary>
public sealed class DocxAdapter : IFormatProbe
{
    /// <summary>Optional wall-clock budget for the DOCX visual inference pass.</summary>
    public TimeSpan? VisualInferenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace WPG = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly HashSet<string> SupportedMarkupNamespaces = new(StringComparer.Ordinal)
    {
        W.NamespaceName, A.NamespaceName, WP.NamespaceName, V.NamespaceName, W14.NamespaceName, WPS.NamespaceName, WPG.NamespaceName,
    };

    // See BuildDocxVisualGraph's orphan-shape label demotion pass: a shape absent from the
    // engine's Candidates (never a plausible connector endpoint) is treated as a possible
    // mislabeled edge-label shape when it sits within this many multiples of the node
    // minor-axis median of a resolved connector's segment. 1.0x is calibrated to accept the
    // contract regression fixture's YES label (~0.55x away) while rejecting R0_FIX_04's
    // ambiguous TARGET_A/TARGET_B rival candidates (~1.5x+ away) -- though those are already
    // excluded up front because the engine lists them as real Candidates.
    private const double OrphanShapeLabelRadiusFactor = 1.0;

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
        var hiddenStyles = ReadHiddenStyles(archive, cancellationToken);
        var listCounters = new Dictionary<(int NumId, int Ilvl), int>();
        var nodes = new List<DocumentNode>();
        var sliceMap = new Dictionary<string, RawSliceRef>(StringComparer.Ordinal);
        var runMaps = new Dictionary<string, DocxRunCharacterMap>(StringComparer.Ordinal);
        var ordinal = 0;
        var originalBodyElements = (doc.Root?.Element(W + "body")?.Elements() ?? Enumerable.Empty<XElement>()).ToArray();
        var blockLedger = slices.Blocks.OrderBy(slice => slice.Start).ToArray();
        var ledgerIndex = 0;
        var ledgerAligned = true;
        var bodyEntries = new List<(XElement Element, RawSliceRef? Slice, DocxContentControl? Control, XElement? Original)>();
        var alternateMirrors = new Dictionary<long, DocxAlternateMirror>();
        var alternateOrdinal = 0;
        // The text boxes the scanner cut, grouped by the block they sit in. A projected block finds
        // its own boxes here by the start offset of the slice it was handed.
        var textBoxLedger = slices.TextBoxes.ToLookup(item => item.HostBlockStart);
        // A block-level content control (w:sdt) is a wrapper, not content: the paragraphs and
        // tables it holds live under w:sdtContent. Unwrapping it first - recursively, and on the
        // *original* tree, which is the tree XmlSliceScanner walked - keeps that content in its
        // body position and lets each unwrapped block claim its own slice, so an edit inside a
        // control rewrites only that block. Markup-compatibility resolution then runs per block:
        // a body-level mc:AlternateContent claims no slice of its own but owns the run of slices
        // the scanner cut inside its branches, and hands the selected branch's blocks theirs.
        foreach (var originalElement in originalBodyElements)
        foreach (var (originalBlock, control) in ExpandContentControls(originalElement, null))
        {
            RawSliceRef? slice = null;
            if (ledgerAligned && IsSliceableBlock(originalBlock))
            {
                // Scanner and projection must enumerate the same blocks in the same order for a
                // slice to address the element it was cut from. If they ever disagree, stop handing
                // slices out: the blocks then project read-only instead of risking a splice into
                // the wrong element.
                var ledgerBlock = blockLedger.ElementAtOrDefault(ledgerIndex);
                if (ledgerBlock is not null && ledgerBlock.AlternateContent < 0 &&
                    StringComparer.Ordinal.Equals(ledgerBlock.LocalName, originalBlock.Name.LocalName))
                {
                    slice = ledgerBlock.Reference;
                    ledgerIndex++;
                }
                else ledgerAligned = false;
            }
            var selected = SelectSupportedAlternateContentBlocks(originalBlock)
                .SelectMany(candidate => ExpandContentControls(candidate, control)).ToArray();
            var selectedSlices = new RawSliceRef?[selected.Length];
            // The element in the *original* tree each projected block was resolved from. It is the
            // only place mc:Choice/@Requires still resolves, so it - not the resolved clone - is
            // what says which branch a text box inside the block came out of.
            var selectedOriginals = new XElement?[selected.Length];
            var isAlternateContent = originalBlock.Name == MC + "AlternateContent";
            if (selected.Length > 0)
            {
                selectedSlices[0] = slice;
                // Outside a fork the projection is a resolved clone of this one block, so the
                // original is this element; inside one, AssignAlternateContentSlices says which
                // branch each projected block was resolved from.
                if (!isAlternateContent) selectedOriginals[0] = originalBlock;
            }
            if (isAlternateContent)
            {
                var alternateIndex = alternateOrdinal++;
                var group = new List<XmlSliceScanner.BlockSlice>();
                while (ledgerIndex < blockLedger.Length && blockLedger[ledgerIndex].AlternateContent == alternateIndex)
                    group.Add(blockLedger[ledgerIndex++]);
                if (ledgerAligned && group.Count > 0 &&
                    !AssignAlternateContentSlices(originalBlock, alternateIndex, group, selected, selectedSlices, selectedOriginals, alternateMirrors, hiddenStyles))
                    ledgerAligned = false;
            }
            for (var selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
                bodyEntries.Add((selected[selectedIndex].Element, selectedSlices[selectedIndex], selected[selectedIndex].Control, selectedOriginals[selectedIndex]));
        }
        // A ledger that was not fully consumed means the two walks disagreed somewhere earlier than
        // the name check caught it: drop every slice rather than trust any of them, and say so
        // instead of quietly projecting a whole document read-only.
        if (!ledgerAligned || ledgerIndex != blockLedger.Length)
        {
            diagnostics.Add(new("DocxSliceLedgerMismatch",
                "DOCX block slices did not line up with the parsed body; in-place edits are disabled for this document.",
                DiagnosticSeverity.Warning, PartUri: "/word/document.xml"));
            for (var entryIndex = 0; entryIndex < bodyEntries.Count; entryIndex++)
                bodyEntries[entryIndex] = (bodyEntries[entryIndex].Element, null, bodyEntries[entryIndex].Control, bodyEntries[entryIndex].Original);
            alternateMirrors.Clear();
        }
        var bodyElements = bodyEntries.Select(entry => entry.Element).ToArray();
        var landscapeSectionStarts = FindLandscapeSectionStarts(doc, bodyElements);
        for (var elementIndex = 0; elementIndex < bodyElements.Length; elementIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (landscapeSectionStarts.Contains(elementIndex))
                AddSectionOrientationMarker(nodes, "/word/document.xml", ordinal++, "landscape");
            // Resolve Markup Compatibility once at the block boundary. A supported Choice
            // replaces the AlternateContent container; its Fallback is never traversed too,
            // preventing duplicate DrawingML/VML textbox and image projections.
            var entry = bodyEntries[elementIndex];
            var element = entry.Element;
            if (element.Name == W + "p")
            {
                var paragraphOrder = ordinal++;
                var textBoxes = BindHostTextBoxes(entry.Original, entry.Slice, textBoxLedger, alternateMirrors, hiddenStyles);
                AddParagraph(element, "/word/document.xml", entry.Slice, paragraphOrder, nodes, sliceMap, runMaps, relationships, ContentLayer.Body, numberingInfo, listCounters, hiddenStyles, entry.Control, textBoxSlices: textBoxes);
            }
            else if (element.Name == W + "tbl")
            {
                var tableOrder = ordinal++;
                AddTable(element, "/word/document.xml", entry.Slice, tableOrder, nodes, sliceMap, ref ordinal, hiddenStyles, entry.Control);
            }
            else if (IsMathRoot(element))
            {
                // OOXML lets an equation stand as a block-level sibling of w:p. Give it its own
                // Paragraph node rather than folding it into an unrelated neighbouring block.
                AddMathParagraph(element, "/word/document.xml", entry.Slice, ordinal++, nodes, sliceMap, entry.Control);
            }
        }
        // Word stores floating nodes and connectors in separate paragraphs surprisingly often.
        // Build one document-level visual canvas so a flow is reconstructed across paragraph
        // boundaries; unresolved primitives remain represented by the visual fallback/diagnostic
        // data carried by the derived Diagram node.
        AddDocumentVisualGraph(bodyElements, nodes, ref ordinal, hiddenStyles, VisualInferenceTimeout, cancellationToken);
        if (options.IncludeFurniture)
            ordinal = await AddRelatedTextPartsAsync(archive, relationships, "header", NodeKind.Header, ContentLayer.Furniture, nodes, ordinal, hiddenStyles, cancellationToken).ConfigureAwait(false);
        if (options.IncludeFurniture)
            ordinal = await AddRelatedTextPartsAsync(archive, relationships, "footer", NodeKind.Footer, ContentLayer.Furniture, nodes, ordinal, hiddenStyles, cancellationToken).ConfigureAwait(false);
        if (options.IncludeFootnotes && archive.GetEntry("word/footnotes.xml") is { } footnotes)
            AddFootnotes(await ReadEntryAsync(footnotes, cancellationToken).ConfigureAwait(false), nodes, ref ordinal, hiddenStyles);
        if (options.IncludeFootnotes && archive.GetEntry("word/endnotes.xml") is { } endnotes)
            AddEndnotes(await ReadEntryAsync(endnotes, cancellationToken).ConfigureAwait(false), nodes, ref ordinal, hiddenStyles);
        if (options.IncludeFootnotes && archive.GetEntry("word/comments.xml") is { } comments)
            AddComments(await ReadEntryAsync(comments, cancellationToken).ConfigureAwait(false), nodes, ref ordinal, hiddenStyles);

        var sourceHash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var hasDocumentProtection = doc.Descendants(W + "documentProtection").Any() ||
            await HasDocumentProtectionAsync(archive, cancellationToken).ConfigureAwait(false);
        var hasTrackedRevisions = doc.Descendants(W + "ins").Any() || doc.Descendants(W + "del").Any();
        var index = new DocxSourceIndex(sourcePath, sourceHash, sliceMap, runMaps, slices.BodyEndTagStart,
            archive.GetEntry("word/vbaProject.bin") is not null,
            archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)),
            hasDocumentProtection,
            hasTrackedRevisions,
            alternateMirrors);
        if (index.HasMacro) diagnostics.Add(new("MacroPresent", "DOCX contains a macro project; it was not executed.", DiagnosticSeverity.Warning));
        if (index.HasSignature) diagnostics.Add(new("SignaturePresent", "DOCX contains package signatures; edited restore is strict-rejected.", DiagnosticSeverity.Warning));
        if (index.HasDocumentProtection) diagnostics.Add(new("DocumentProtected", "DOCX has document protection; protected edits are strict-rejected.", DiagnosticSeverity.Warning));
        if (index.HasTrackedRevisions) diagnostics.Add(new("TrackedRevisionsPresent", "DOCX contains tracked revisions; edits crossing revision markup are strict-rejected.", DiagnosticSeverity.Warning));
        // One document-level notice, not one per equation: OMML is structured markup that only
        // approximates as linear text, so the reader is told once that layout may have changed.
        var linearizedEquations = bodyElements.Sum(CountMathRoots);
        if (linearizedEquations > 0)
            diagnostics.Add(new("DocxMathLinearized",
                $"{linearizedEquations} equation(s) were converted to linear text; layout may differ from the original.",
                DiagnosticSeverity.Warning, PartUri: "/word/document.xml"));
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
        // The blocks of an mc:AlternateContent branch the extractor did not project. They carry no
        // node, so they cannot come out of the diff; the same edit is applied to them here so Word
        // shows it whichever branch it resolves.
        var mirrored = new List<(RawSliceRef Slice, byte[] Data)>();
        var mirrors = extraction.SourceIndex.AlternateMirrors;
        var additions = new List<byte[]>();
        // A text box's slice sits *inside* its host block's slice, so the two can only be spliced
        // one at a time. Every slice an operation claims is kept here to catch that up front.
        var claimed = new List<(RawSliceRef Slice, string NodeId)>();
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
            if (operation.Before is null || operation.Before.Kind is not (NodeKind.Paragraph or NodeKind.Heading or NodeKind.ListItem or NodeKind.CodeBlock or NodeKind.Table or NodeKind.TextBox) || operation.Before.RawSlice is null)
                return Failure("UnsupportedPatch", "Only anchored paragraph, list-item, text-box, and table changes are supported by DOCX F1 restore.", operation.NodeId);
            var slice = operation.Before.RawSlice;
            if (!StringComparer.Ordinal.Equals(slice.PartUri, "/word/document.xml"))
                return Failure("ProtectedBoundary", "Edit crosses a non-body DOCX part boundary.", operation.NodeId);
            if (claimed.FirstOrDefault(item => Overlaps(item.Slice, slice)) is { NodeId: not null } clash)
                return Failure("OverlappingEdits", "Edit the text box and its host paragraph in separate restores.",
                    slice.EndOffset - slice.StartOffset <= clash.Slice.EndOffset - clash.Slice.StartOffset ? operation.NodeId : clash.NodeId);
            claimed.Add((slice, operation.NodeId));
            var original = await ReadDocumentSliceAsync(sourcePath, slice, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(SafeXml.Sha256(original), slice.Sha256))
                return Failure("SliceHashMismatch", "Original XML slice no longer matches baseline hash.", operation.NodeId);
            var mirror = mirrors is not null && mirrors.TryGetValue(slice.StartOffset, out var found) ? found : null;
            if (operation.Kind == PatchOperationKind.ExplicitDelete)
            {
                // Dropping a w:txbxContent leaves a shape with no body, which is not what the node
                // stands for: the box is a place to put text, not a body position that can go away.
                if (operation.Before.Kind == NodeKind.TextBox)
                    return Failure("UnsupportedDelete", "A DOCX text box cannot be deleted by F1 restore; only its text can be replaced.", operation.NodeId);
                changes[operation.NodeId] = Array.Empty<byte>();
                foreach (var companion in mirror?.Slices ?? []) mirrored.Add((companion, Array.Empty<byte>()));
            }
            else if (operation.After is not null)
            {
                var edits = default(DocxParagraphEditResult);
                changes[operation.NodeId] = operation.Before.Kind switch
                {
                    NodeKind.Table => ReplaceTableCells(original, operation.After.Content, out edits),
                    NodeKind.TextBox => ReplaceTextBoxContent(original, operation.After.Content, out edits),
                    _ => ReplaceParagraphContent(original, operation.After.Content, out edits),
                };
                // OMML is structured markup that only projects as one linear string. An edit that
                // no longer contains that string has retyped the equation, so the original markup
                // is dropped rather than left stranded beside the new text - and said so here.
                if (edits.ReplacedEquations > 0)
                    diagnostics.Add(new("DocxMathReplaced",
                        $"{edits.ReplacedEquations} equation(s) on paragraph {operation.NodeId} were replaced by plain text; OMML cannot be rebuilt from an edited linear form.",
                        DiagnosticSeverity.Warning, operation.NodeId, "/word/document.xml"));
                // An inline control's wrapper can only be kept when the edit stayed on one side of
                // its boundary. An edit that rewrote its text together with the text around it
                // leaves nothing to put back inside, so the control's content becomes plain runs.
                if (edits.UnwrappedContainers > 0)
                    diagnostics.Add(new("DocxInlineContainerUnwrapped",
                        $"{edits.UnwrappedContainers} inline content control(s) on paragraph {operation.NodeId} were unwrapped because their text was edited together with the surrounding text.",
                        DiagnosticSeverity.Warning, operation.NodeId, "/word/document.xml"));
                if (mirror is not null)
                    foreach (var companion in mirror.Slices)
                    {
                        var companionOriginal = await ReadDocumentSliceAsync(sourcePath, companion, cancellationToken).ConfigureAwait(false);
                        if (!StringComparer.Ordinal.Equals(SafeXml.Sha256(companionOriginal), companion.Sha256)) continue;
                        mirrored.Add((companion, operation.Before.Kind switch
                        {
                            NodeKind.Table => ReplaceTableCells(companionOriginal, operation.After.Content, out _),
                            NodeKind.TextBox => ReplaceTextBoxContent(companionOriginal, operation.After.Content, out _),
                            _ => ReplaceParagraphContent(companionOriginal, operation.After.Content, out _),
                        }));
                    }
            }
            // Nothing in the other branches said the same thing, so the edit reaches only the
            // branch Word resolves the way this build does.
            if (mirror is { Slices.Count: 0 })
                diagnostics.Add(new("DocxAlternateContentFallbackStale",
                    $"Block {operation.NodeId} was patched, but the unselected branch of AlternateContent #{mirror.AlternateContentOrdinal} was left unchanged.",
                    DiagnosticSeverity.Information, operation.NodeId, "/word/document.xml"));
        }

        var documentBytes = await ReadZipEntryAsync(sourcePath, "word/document.xml", cancellationToken).ConfigureAwait(false);
        var replacements = baselineGraph.Nodes.Where(node => node.RawSlice is not null && changes.ContainsKey(node.Id))
            .Select(node => (Slice: node.RawSlice!, Data: changes[node.Id]))
            .Concat(mirrored).OrderBy(item => item.Slice.StartOffset).ToArray();
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
        NumberingInfo numberingInfo, IDictionary<(int NumId, int Ilvl), int> listCounters,
        DocxHiddenStyles hiddenStyles, DocxContentControl? contentControl = null, bool includeVisualGraph = true,
        IReadOnlyList<DocxTextBoxBinding?>? textBoxSlices = null)
    {
        var paraId = (string?)paragraph.Attribute(W14 + "paraId");
        var anchor = new SourceAnchor("docx", partUri,
            paraId is null ? [new("body_child_ordinal", order.ToString(System.Globalization.CultureInfo.InvariantCulture))] : [new("w14_para_id", paraId)], order);
        var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
        var text = ParagraphText(paragraph, hiddenStyles);
        var hiddenText = HiddenParagraphText(paragraph, hiddenStyles);
        var style = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
        // Word commonly stores list semantics through a paragraph style (ListBullet /
        // ListNumber) while other producers emit an explicit w:numPr.  Treat both as
        // list items so real-world documents do not lose their list structure on export.
        var isList = paragraph.Element(W + "pPr")?.Element(W + "numPr") is not null ||
                     IsListStyle(style);
        var headingLevel = HeadingLevel(style);
        var isDocumentTitle = IsTitleStyle(style);
        // A character-level CodeChar run is inline code, not a code block.  Only
        // paragraph styles classify the whole paragraph as a Markdown code block.
        var isCode = IsCodeStyle(style);
        var kind = isList ? NodeKind.ListItem : headingLevel > 0 || isDocumentTitle ? NodeKind.Heading : isCode ? NodeKind.CodeBlock : NodeKind.Paragraph;
        var map = BuildRunMap(id, paragraph, hiddenStyles);
        var projectionLayer = string.IsNullOrWhiteSpace(text) ? ContentLayer.Hidden : layer;
        var richRuns = ExtractRichTextRuns(paragraph, relationships, hiddenStyles);
        // Preserve the simple text projection for ordinary paragraphs: it keeps existing
        // graph clients compatible while only opting into rich text when the OOXML contains
        // a supported direct run property or an inline break/tab.
        NodeContent content = richRuns is { Count: > 0 } && richRuns.Any(IsRichRun)
            ? new RichTextNodeContent(richRuns)
            : new TextNodeContent(text);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (headingLevel > 0) extensions["heading_level"] = JsonSerializer.SerializeToElement(headingLevel);
        if (isDocumentTitle) extensions["document_title"] = JsonSerializer.SerializeToElement(true);
        if (isList)
        {
            extensions["list_level"] = JsonSerializer.SerializeToElement(ListLevel(paragraph, style));
            var (isOrdered, orderedNumber) = ResolveListNumbering(paragraph, style, numberingInfo, listCounters);
            if (isOrdered && orderedNumber is { } number)
            {
                extensions["list_format"] = JsonSerializer.SerializeToElement("ordered");
                extensions["list_number"] = JsonSerializer.SerializeToElement(number);
            }
        }
        if (isCode) extensions["code_style"] = JsonSerializer.SerializeToElement(style);
        AddContentControlExtensions(extensions, contentControl);
        var equations = RelevantDescendants(paragraph).Where(IsMathRoot).ToArray();
        if (equations.Length > 0) extensions["math_linear"] = JsonSerializer.SerializeToElement(true);
        // An equation is opaque markup that only projects as one linear string, so the F1 patcher
        // keeps it as an anchor and rewrites the runs around it (see ReplaceParagraphRuns). That
        // needs the equation to sit directly under its w:p; one buried in another inline container
        // gives the surrounding runs no stable place to reattach, so such a paragraph stays
        // read-only rather than losing its equation on the next edit.
        var restorable = equations.All(element => element.Parent?.Name == W + "p");
        var effectiveSlice = restorable ? slice : null;
        var node = new DocumentNode(id, kind, null, order, projectionLayer, content, anchor, effectiveSlice, StyleId: style,
            Editability: restorable ? NodeEditability.EditableInPlace : NodeEditability.Protected,
            Provenance: [new(EvidenceKind.Native)], Extensions: extensions);
        nodes.Add(node);
        if (effectiveSlice is not null) sliceMap[id] = effectiveSlice;
        runMaps[id] = map;
        if (!string.IsNullOrWhiteSpace(hiddenText))
        {
            var hiddenAnchor = anchor with
            {
                Locators = anchor.Locators.Concat([new AnchorLocator("hidden_content", "w:vanish-or-deletion")]).ToArray()
            };
            var hiddenId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, hiddenAnchor);
            var hiddenExtensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["hidden_content_type"] = JsonSerializer.SerializeToElement("docx-hidden-text")
            };
            nodes.Add(new(hiddenId, NodeKind.Annotation, id, order, ContentLayer.Hidden,
                new TextNodeContent(hiddenText), hiddenAnchor, Editability: NodeEditability.Protected,
                Provenance: [new(EvidenceKind.Native)], Extensions: hiddenExtensions));
        }

        foreach (var link in paragraph.Descendants(W + "hyperlink"))
        {
            var relationshipId = (string?)link.Attribute(R + "id");
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var linkAnchor = anchor with { Locators = [new("hyperlink", relationshipId)] };
            var linkId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, linkAnchor);
            nodes.Add(new(linkId, NodeKind.Link, id, order, layer, new ReferenceNodeContent(target, ParagraphText(link, hiddenStyles)), linkAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)]));
        }
        foreach (var (blip, visualIndex) in paragraph.Descendants(A + "blip").Select((item, index) => (item, index)))
        {
            var relationshipId = (string?)blip.Attribute(R + "embed");
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var imageAnchor = anchor with { Locators = [new("image_relationship", relationshipId), new("visual_index", visualIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))] };
            var imageId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, imageAnchor);
            var docPr = blip.Ancestors().SelectMany(ancestor => ancestor.Elements(WP + "docPr")).FirstOrDefault()
                ?? blip.Ancestors().Descendants(WP + "docPr").FirstOrDefault();
            var description = FirstNonEmptyAttribute(docPr, "descr", "title", "name");
            var imageLayer = IsHiddenContentElement(blip, hiddenStyles) ? ContentLayer.Hidden : layer;
            nodes.Add(new(imageId, NodeKind.Image, id, order, imageLayer, new ReferenceNodeContent(target, description), imageAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)]));
        }
        foreach (var (imageData, visualIndex) in paragraph.Descendants(V + "imagedata").Select((item, index) => (item, index)))
        {
            var relationshipId = (string?)imageData.Attribute(R + "id");
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var imageAnchor = anchor with { Locators = [new("vml_image_relationship", relationshipId), new("visual_index", visualIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))] };
            var imageId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, imageAnchor);
            var shape = imageData.Ancestors(V + "shape").FirstOrDefault();
            var description = FirstNonEmptyAttribute(shape, "alt", "title", "id");
            var imageLayer = IsHiddenContentElement(imageData, hiddenStyles) ? ContentLayer.Hidden : layer;
            var imageExtensions = imageLayer == ContentLayer.Hidden
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["hidden_content_type"] = JsonSerializer.SerializeToElement("docx-hidden-vml-image"),
                }
                : null;
            nodes.Add(new(imageId, NodeKind.Image, id, order, imageLayer,
                new ReferenceNodeContent(target, description), imageAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)],
                Extensions: imageExtensions));
        }
        // Only the outer boxes are addressable, so only they consume a binding; the index the node
        // id is built from still counts every box, keeping ids stable across this change.
        var outerTextBoxIndex = 0;
        foreach (var (textBox, textboxIndex) in paragraph.Descendants(W + "txbxContent").Select((item, index) => (item, index)))
        {
            var isOuterTextBox = !textBox.Ancestors(W + "txbxContent").Any();
            var binding = isOuterTextBox
                ? (textBoxSlices ?? []).ElementAtOrDefault(outerTextBoxIndex++)
                : null;
            // The ancestor depth is not an identity: sibling textboxes have the same depth.
            // Use the source-order index so repeated visual objects receive stable unique IDs.
            var shapeId = TextBoxShapeId(textBox);
            var locators = new List<AnchorLocator>
            {
                new("textbox", textboxIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            if (shapeId is not null) locators.Insert(0, new AnchorLocator("shape_id", shapeId));
            var boxAnchor = anchor with { Locators = locators };
            var boxId = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, boxAnchor);
            var boxExtensions = shapeId is null
                ? null
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["shape_id"] = JsonSerializer.SerializeToElement(shapeId),
                };
            // A bound box owns the bytes of its w:txbxContent, so an edit to its text splices only
            // that element and leaves the shape, the drawing anchor and the host paragraph around
            // it byte-identical. Without a binding - a box in a header, or one the two walks
            // disagree about - it projects exactly as it used to: text, but no way back.
            nodes.Add(new(boxId, NodeKind.TextBox, id, order, layer, new TextNodeContent(TextBoxText(textBox, hiddenStyles)), boxAnchor,
                binding?.Slice,
                Editability: binding is null ? NodeEditability.EditableWithConstraints : NodeEditability.EditableInPlace,
                Provenance: [new(EvidenceKind.Native)], Extensions: boxExtensions));
            if (binding is not null) sliceMap[boxId] = binding.Slice;
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

    private static IReadOnlyList<XElement> SelectSupportedAlternateContentBlocks(XElement source)
    {
        if (source.Name == MC + "AlternateContent")
        {
            var selected = SelectMarkupChoice(source);
            return selected is null ? Array.Empty<XElement>()
                : selected.Elements().SelectMany(SelectSupportedAlternateContentBlocks).ToArray();
        }
        return source.Descendants(MC + "AlternateContent").Any()
            ? [new XElement(source.Name, source.Attributes(), source.Nodes().Select(ResolveMarkupNode))]
            : [new XElement(source)];
    }

    /// <summary>Rebuilds one node with every mc:AlternateContent under it replaced by the branch
    /// this build supports. The walk is over the *original* tree on purpose: mc:Choice/@Requires
    /// names markup by prefix, and a detached clone no longer has the xmlns declaration w:document
    /// made - which is where Word declares every one of them - so a choice resolved on a clone
    /// would read as unsupported and silently fall back.</summary>
    private static object? ResolveMarkupNode(XNode node)
    {
        if (node is not XElement element) return node;
        if (element.Name == MC + "AlternateContent")
            return SelectMarkupChoice(element) is { } selected ? selected.Nodes().Select(ResolveMarkupNode).ToArray() : null;
        return new XElement(element.Name, element.Attributes(), element.Nodes().Select(ResolveMarkupNode));
    }

    private static XElement? SelectMarkupChoice(XElement alternate) =>
        alternate.Elements(MC + "Choice").FirstOrDefault(IsSupportedMarkupChoice) ?? alternate.Element(MC + "Fallback");

    /// <summary>Whether every namespace prefix an mc:Choice demands is one this build understands.</summary>
    private static bool IsSupportedMarkupChoice(XElement choice)
    {
        var requires = ((string?)choice.Attribute("Requires") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return requires.All(prefix =>
        {
            var ns = choice.GetNamespaceOfPrefix(prefix);
            return ns is not null && SupportedMarkupNamespaces.Contains(ns.NamespaceName);
        });
    }

    /// <summary>The branch label <see cref="XmlSliceScanner"/> tagged the branch this extractor
    /// selects, so the two walks address the same slices.</summary>
    private static string? SelectedBranchLabel(XElement alternate)
    {
        var index = 0;
        foreach (var choice in alternate.Elements(MC + "Choice"))
        {
            if (IsSupportedMarkupChoice(choice)) return "Choice#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            index++;
        }
        return alternate.Element(MC + "Fallback") is null ? null : "Fallback";
    }

    /// <summary>The mc:Choice / mc:Fallback children of one fork, labelled the way
    /// <see cref="XmlSliceScanner"/> labelled them.</summary>
    private static IEnumerable<(string Label, XElement Branch)> AlternateBranches(XElement alternate)
    {
        var choiceIndex = 0;
        foreach (var child in alternate.Elements())
        {
            if (child.Name == MC + "Choice")
                yield return ("Choice#" + (choiceIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture), child);
            else if (child.Name == MC + "Fallback") yield return ("Fallback", child);
        }
    }

    /// <summary>What one mc:Choice / mc:Fallback holds at body positions, in the order and with the
    /// content-control transparency <see cref="XmlSliceScanner"/> recorded: the sliceable blocks it
    /// owns directly, and the nested forks that stand for a body position of their own.</summary>
    private static IReadOnlyList<XElement> BranchChildren(XElement branch) =>
        branch.Elements().SelectMany(child => ExpandContentControls(child, null))
            .Select(item => item.Element).ToArray();

    /// <summary>One block the extractor projected out of an mc:AlternateContent: the original
    /// element it came from, the slice the scanner cut for it, and the same-position, same-text
    /// blocks of every branch that was not taken - at each level of a nested fork - which an F1
    /// edit is mirrored into. <paramref name="HasSiblingBranch"/> says the block came out of a fork
    /// that had somewhere to mirror to, so an empty companion list is worth reporting.</summary>
    private sealed record DocxAlternateProjection(XElement Element, RawSliceRef Slice,
        List<RawSliceRef> Companions, bool HasSiblingBranch);

    /// <summary>Hands the slices cut inside one body-level mc:AlternateContent to the blocks the
    /// selected branch projected, and records - per selected block - the same-position, same-text
    /// blocks of the other branches so an edit can be mirrored into them. A fork nested inside a
    /// branch is resolved the same way one level down, so the walk descends into the branch that
    /// fork selects and a block only reachable through two choices still gets its slice, with
    /// companions collected from the branches passed over at *both* levels. The slices of a branch
    /// that was not selected are consumed either way: they address real markup, just markup no node
    /// stands for. Returns false when the two walks disagree, which disables slicing document-wide.</summary>
    private static bool AssignAlternateContentSlices(
        XElement alternate, int alternateIndex, IReadOnlyList<XmlSliceScanner.BlockSlice> group,
        IReadOnlyList<(XElement Element, DocxContentControl? Control)> selected, RawSliceRef?[] selectedSlices,
        XElement?[] selectedOriginals, IDictionary<long, DocxAlternateMirror> mirrors, DocxHiddenStyles hiddenStyles)
    {
        var slicesByPath = group.GroupBy(item => item.Branch ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => (IReadOnlyList<XmlSliceScanner.BlockSlice>)item.OrderBy(entry => entry.Start).ToArray(), StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!ValidateAlternateBranchPaths(alternate, null, slicesByPath, paths)) return false;
        // A slice the scanner cut on a path the parse never walked means the two disagree about the
        // shape of the fork, so no slice inside it can be trusted.
        if (slicesByPath.Keys.Any(path => !paths.Contains(path))) return false;
        var projected = ResolveAlternateContentBlocks(alternate, null, slicesByPath, hiddenStyles);
        var blockIndex = 0;
        for (var index = 0; index < selected.Count && blockIndex < projected.Count; index++)
        {
            var element = selected[index].Element;
            if (!IsSliceableBlock(element)) continue;
            var projection = projected[blockIndex];
            if (!StringComparer.Ordinal.Equals(element.Name.LocalName, projection.Element.Name.LocalName)) return false;
            selectedSlices[index] = projection.Slice;
            selectedOriginals[index] = projection.Element;
            if (projection.HasSiblingBranch) mirrors[projection.Slice.StartOffset] = new(alternateIndex, projection.Companions);
            blockIndex++;
        }
        return true;
    }

    /// <summary>Checks, for one fork and every fork nested in it, that the blocks the parse sees on
    /// a branch are exactly the slices the scanner cut for that branch path, and collects the paths
    /// walked so a slice on an unknown path can be caught.</summary>
    private static bool ValidateAlternateBranchPaths(XElement alternate, string? prefix,
        IReadOnlyDictionary<string, IReadOnlyList<XmlSliceScanner.BlockSlice>> slicesByPath, ISet<string> paths)
    {
        foreach (var (label, branch) in AlternateBranches(alternate))
        {
            var path = prefix is null ? label : prefix + "/" + label;
            paths.Add(path);
            var children = BranchChildren(branch);
            var blocks = children.Where(IsSliceableBlock).ToArray();
            var branchSlices = slicesByPath.TryGetValue(path, out var found) ? found : [];
            if (branchSlices.Count != blocks.Length) return false;
            for (var index = 0; index < blocks.Length; index++)
                if (!StringComparer.Ordinal.Equals(branchSlices[index].LocalName, blocks[index].Name.LocalName)) return false;
            foreach (var nested in children.Where(child => child.Name == MC + "AlternateContent"))
                if (!ValidateAlternateBranchPaths(nested, path, slicesByPath, paths)) return false;
        }
        return true;
    }

    /// <summary>The blocks one fork projects, in document order, each already carrying the
    /// companions the branches it passed over offer. Recursing before mirroring means an inner
    /// fork's own counterpart and the outer fork's counterpart both end up on the same block.</summary>
    private static List<DocxAlternateProjection> ResolveAlternateContentBlocks(XElement alternate, string? prefix,
        IReadOnlyDictionary<string, IReadOnlyList<XmlSliceScanner.BlockSlice>> slicesByPath, DocxHiddenStyles hiddenStyles)
    {
        var branches = AlternateBranches(alternate).ToArray();
        var projections = new Dictionary<string, List<DocxAlternateProjection>>(StringComparer.Ordinal);
        foreach (var (label, branch) in branches)
            projections[label] = ProjectAlternateBranch(branch, prefix is null ? label : prefix + "/" + label, slicesByPath, hiddenStyles);
        if (SelectedBranchLabel(alternate) is not { } selectedLabel ||
            !projections.TryGetValue(selectedLabel, out var chosen)) return [];
        if (branches.Length <= 1) return chosen;
        foreach (var (label, other) in projections)
        {
            if (StringComparer.Ordinal.Equals(label, selectedLabel)) continue;
            for (var index = 0; index < chosen.Count && index < other.Count; index++)
            {
                if (!StringComparer.Ordinal.Equals(chosen[index].Element.Name.LocalName, other[index].Element.Name.LocalName)) continue;
                if (!StringComparer.Ordinal.Equals(ParagraphText(chosen[index].Element, hiddenStyles),
                        ParagraphText(other[index].Element, hiddenStyles))) continue;
                chosen[index].Companions.Add(other[index].Slice);
                chosen[index].Companions.AddRange(other[index].Companions);
            }
        }
        return chosen.Select(item => item with { HasSiblingBranch = true }).ToList();
    }

    /// <summary>What one branch stands for at body positions: its own sliceable blocks paired with
    /// the slices cut for its path, and - for a nested fork - whatever that fork projects.</summary>
    private static List<DocxAlternateProjection> ProjectAlternateBranch(XElement branch, string path,
        IReadOnlyDictionary<string, IReadOnlyList<XmlSliceScanner.BlockSlice>> slicesByPath, DocxHiddenStyles hiddenStyles)
    {
        var result = new List<DocxAlternateProjection>();
        var branchSlices = slicesByPath.TryGetValue(path, out var found) ? found : [];
        var sliceIndex = 0;
        foreach (var child in BranchChildren(branch))
        {
            if (IsSliceableBlock(child))
            {
                if (sliceIndex < branchSlices.Count) result.Add(new(child, branchSlices[sliceIndex].Reference, [], false));
                sliceIndex++;
                continue;
            }
            if (child.Name == MC + "AlternateContent")
                result.AddRange(ResolveAlternateContentBlocks(child, path, slicesByPath, hiddenStyles));
        }
        return result;
    }

    /// <summary>The slice one projected text box is addressed by, plus the same-position, same-text
    /// boxes of the paragraph-level branches that were not taken.</summary>
    private sealed record DocxTextBoxBinding(RawSliceRef Slice, IReadOnlyList<RawSliceRef> Companions,
        int AlternateOrdinal, bool HasSiblingBranch);

    /// <summary>One text box the projection kept, before its slice is known: the original
    /// w:txbxContent, the paragraph-level fork path it came out of, and its counterparts in the
    /// branches that fork passed over.</summary>
    private sealed record DocxTextBoxProjection(XElement Box, string? Path, List<XElement> Companions,
        int AlternateOrdinal, bool HasSiblingBranch);

    /// <summary>Pairs the text boxes one projected block still shows with the slices the scanner cut
    /// for them, in projection order, so a caller can hand the k-th w:txbxContent of the resolved
    /// block the bytes it came from. The scanner numbered every outer box in the host in document
    /// order - the branches it did not take included - so the walk here is over the *original*
    /// block, resolving each mc:AlternateContent exactly as the projection did; a box the two walks
    /// disagree about is left unbound and stays read-only. Mirrors for the branches passed over are
    /// registered on the way through.</summary>
    private static IReadOnlyList<DocxTextBoxBinding?> BindHostTextBoxes(
        XElement? original, RawSliceRef? hostSlice, ILookup<int, XmlSliceScanner.TextBoxSlice> textBoxLedger,
        IDictionary<long, DocxAlternateMirror> mirrors, DocxHiddenStyles hiddenStyles)
    {
        if (original is null || hostSlice is null) return [];
        var recorded = textBoxLedger[(int)hostSlice.StartOffset].OrderBy(item => item.Start).ToArray();
        if (recorded.Length == 0) return [];
        var outer = original.Descendants(W + "txbxContent")
            .Where(box => !box.Ancestors(W + "txbxContent").Any()).ToArray();
        if (outer.Length != recorded.Length) return [];
        var ordinals = new Dictionary<XElement, int>();
        for (var index = 0; index < outer.Length; index++) ordinals[outer[index]] = index;
        var alternateOrdinals = new Dictionary<XElement, int>();
        var alternateIndex = 0;
        foreach (var element in original.Descendants(MC + "AlternateContent")) alternateOrdinals[element] = alternateIndex++;

        var bindings = new List<DocxTextBoxBinding?>();
        foreach (var projection in CollectProjectedTextBoxes(original, null, -1, false, alternateOrdinals, hiddenStyles))
        {
            if (!ordinals.TryGetValue(projection.Box, out var ordinal) ||
                recorded[ordinal].OrdinalInHost != ordinal ||
                !StringComparer.Ordinal.Equals(recorded[ordinal].InlineAlternatePath, projection.Path))
            { bindings.Add(null); continue; }
            var companions = new List<RawSliceRef>();
            var bound = true;
            foreach (var companion in projection.Companions)
            {
                if (!ordinals.TryGetValue(companion, out var companionOrdinal)) { bound = false; break; }
                companions.Add(recorded[companionOrdinal].Reference);
            }
            if (!bound) { bindings.Add(null); continue; }
            var binding = new DocxTextBoxBinding(recorded[ordinal].Reference, companions,
                projection.AlternateOrdinal, projection.HasSiblingBranch);
            if (binding.HasSiblingBranch) mirrors[binding.Slice.StartOffset] = new(binding.AlternateOrdinal, companions);
            bindings.Add(binding);
        }
        return bindings;
    }

    /// <summary>The outer text boxes a block still shows once every mc:AlternateContent under it is
    /// resolved, in document order, each carrying the same-position, same-text boxes of the branches
    /// that fork passed over. A box inside a box is not walked into: only the outer one is
    /// addressable, exactly as <see cref="XmlSliceScanner"/> recorded it.</summary>
    private static List<DocxTextBoxProjection> CollectProjectedTextBoxes(XContainer container, string? path,
        int alternateOrdinal, bool hasSiblingBranch, IReadOnlyDictionary<XElement, int> alternateOrdinals,
        DocxHiddenStyles hiddenStyles)
    {
        var result = new List<DocxTextBoxProjection>();
        foreach (var child in container.Elements())
        {
            if (child.Name == MC + "AlternateContent")
            {
                if (!alternateOrdinals.TryGetValue(child, out var ordinal)) continue;
                var branches = AlternateBranches(child).ToArray();
                var projections = new Dictionary<string, List<DocxTextBoxProjection>>(StringComparer.Ordinal);
                foreach (var (label, branch) in branches)
                    projections[label] = CollectProjectedTextBoxes(branch,
                        ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + label,
                        ordinal, branches.Length > 1, alternateOrdinals, hiddenStyles);
                if (SelectedBranchLabel(child) is not { } selectedLabel ||
                    !projections.TryGetValue(selectedLabel, out var chosen)) continue;
                if (branches.Length > 1)
                    foreach (var (label, other) in projections)
                    {
                        if (StringComparer.Ordinal.Equals(label, selectedLabel)) continue;
                        for (var index = 0; index < chosen.Count && index < other.Count; index++)
                        {
                            if (!StringComparer.Ordinal.Equals(TextBoxText(chosen[index].Box, hiddenStyles),
                                    TextBoxText(other[index].Box, hiddenStyles))) continue;
                            chosen[index].Companions.Add(other[index].Box);
                            chosen[index].Companions.AddRange(other[index].Companions);
                        }
                    }
                result.AddRange(branches.Length > 1
                    ? chosen.Select(item => item with { HasSiblingBranch = true })
                    : chosen);
                continue;
            }
            if (child.Name == W + "txbxContent")
            {
                result.Add(new(child, path, [], alternateOrdinal, hasSiblingBranch));
                continue;
            }
            result.AddRange(CollectProjectedTextBoxes(child, path, alternateOrdinal, hasSiblingBranch, alternateOrdinals, hiddenStyles));
        }
        return result;
    }

    private static void AddDocumentVisualGraph(IReadOnlyList<XElement> bodyElements, ICollection<DocumentNode> nodes, ref int ordinal,
        DocxHiddenStyles hiddenStyles, TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        var visualRoot = new XElement(W + "p", bodyElements.Select(element => new XElement(element)));
        var anchor = new SourceAnchor("docx", "/word/document.xml", [new AnchorLocator("visual_graph", "document")], ordinal);
        if (BuildDocxVisualGraph(visualRoot, anchor, "doc_document", hiddenStyles, inferenceTimeout, cancellationToken) is not { } visualGraph) return;

        if (visualGraph.HasTopology && nodes is List<DocumentNode> nodeList)
        {
            // Body suppression is keyed by the actual source shape IDs consumed by the
            // visual graph. Text is deliberately not used: a diagram label may be repeated
            // by an unrelated textbox elsewhere in the document.
            var labelShapeIds = (visualGraph.SourceItems ?? [])
                .Where(item => item.Kind == VisualSourceItemKind.TextLabel && item.Disposition == VisualDisposition.ProjectedNode)
                .SelectMany(item => item.SourceAnchor?.Locators ?? [])
                .Where(locator => locator.Kind == "shape_id" && !string.IsNullOrWhiteSpace(locator.Value))
                .Select(locator => locator.Value)
                .ToHashSet(StringComparer.Ordinal);
            var memberShapeIds = (visualGraph.SourceItems ?? [])
                .Where(item => item.Kind != VisualSourceItemKind.TextLabel || item.Reason == "attached as connector label")
                .SelectMany(item => item.SourceAnchor?.Locators ?? [])
                .Where(locator => locator.Kind == "shape_id" && !string.IsNullOrWhiteSpace(locator.Value))
                .Select(locator => locator.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var visualNode in visualGraph.Nodes)
            {
                foreach (var locator in visualNode.SourceAnchor?.Locators ?? [])
                    if (locator.Kind == "shape_id" && !string.IsNullOrWhiteSpace(locator.Value) && !labelShapeIds.Contains(locator.Value)) memberShapeIds.Add(locator.Value);
            }
            for (var i = 0; i < nodeList.Count; i++)
            {
                var source = nodeList[i];
                if (source.Kind != NodeKind.TextBox || source.Extensions is null ||
                    !source.Extensions.TryGetValue("shape_id", out var shapeId) || shapeId.ValueKind != JsonValueKind.String ||
                    !memberShapeIds.Contains(shapeId.GetString()!)) continue;
                var extensions = new Dictionary<string, JsonElement>(source.Extensions, StringComparer.Ordinal)
                {
                    ["visual_graph_member"] = JsonSerializer.SerializeToElement(true),
                };
                nodeList[i] = source with { Extensions = extensions };
            }
        }

        var componentIndex = 0;
        foreach (var component in SplitVisualGraphs(visualGraph))
        {
            var diagramAnchor = anchor with { Locators = anchor.Locators.Concat([new AnchorLocator("visual_graph", componentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))]).ToArray() };
            var componentShapeIds = component.Nodes
                .SelectMany(node => node.SourceAnchor?.Locators ?? [])
                .Where(locator => locator.Kind == "shape_id" && !string.IsNullOrWhiteSpace(locator.Value))
                .Select(locator => locator.Value)
                .Concat((component.SourceItems ?? [])
                    .Where(item => item.Kind != VisualSourceItemKind.TextLabel || item.Reason == "attached as connector label")
                    .SelectMany(item => item.SourceAnchor?.Locators ?? [])
                    .Where(locator => locator.Kind == "shape_id" && !string.IsNullOrWhiteSpace(locator.Value))
                    .Select(locator => locator.Value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var diagramExtensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(component),
                ["visual_graph_member_shape_ids"] = JsonSerializer.SerializeToElement(componentShapeIds),
            };
            if (component.HasTopology)
                diagramExtensions["visual_fallback_suppressed"] = JsonSerializer.SerializeToElement(true);
            nodes.Add(new DocumentNode(
                NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, diagramAnchor), NodeKind.Diagram,
                null, ordinal++, ContentLayer.Derived, new TextNodeContent("DOCX visual graph"), diagramAnchor,
                Editability: NodeEditability.Passthrough, Provenance: [new(EvidenceKind.Native)], Extensions: diagramExtensions));
            componentIndex++;
        }
    }

    private static IEnumerable<VisualGraph> SplitVisualGraphs(VisualGraph graph)
    {
        var nodes = (graph.Nodes ?? []).Where(node => node is not null).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var edges = (graph.Edges ?? []).Where(edge => edge is not null).ToArray();
        // Unresolved geometry may be the only evidence that otherwise disconnected components
        // belong to one visual. Keep the complete graph so candidate nodes and the ambiguity
        // diagnostic survive together, matching XLSX and PPTX behavior.
        if (edges.Any(edge => edge.SourceId is null || edge.TargetId is null)) return [graph];
        var remaining = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var groups = new List<HashSet<string>>();
        while (remaining.Count > 0)
        {
            var seed = remaining.OrderBy(id => id, StringComparer.Ordinal).First();
            var group = new HashSet<string>(StringComparer.Ordinal) { seed };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var edge in edges)
                    if (edge.SourceId is not null && edge.TargetId is not null &&
                        (group.Contains(edge.SourceId) || group.Contains(edge.TargetId)) &&
                        (group.Add(edge.SourceId) | group.Add(edge.TargetId))) changed = true;
            }
            groups.Add(group);
            remaining.ExceptWith(group);
        }
        var connectedGroups = groups.Count(ids => edges.Any(edge => edge.SourceId is not null && edge.TargetId is not null && ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)));
        // The inference engine's cluster IDs are authoritative. Do not merge two independently
        // solved diagrams merely because an ambiguous projection happened to connect their nodes.
        var evidenceGroups = edges.Where(edge => !string.IsNullOrWhiteSpace(edge.Evidence?.ClusterId))
            .GroupBy(edge => edge.Evidence!.ClusterId!, StringComparer.Ordinal)
            .Where(group => group.Any(edge => edge.SourceId is not null && edge.TargetId is not null))
            .Select(group => group.SelectMany(edge => new[] { edge.SourceId!, edge.TargetId! }).ToHashSet(StringComparer.Ordinal))
            .ToArray();
        if (evidenceGroups.Length >= 2)
        {
            var clusteredIds = evidenceGroups.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);
            groups = evidenceGroups.ToList();
            groups.AddRange(nodes.Where(node => !clusteredIds.Contains(node.Id)).Select(node => new HashSet<string>(StringComparer.Ordinal) { node.Id }));
            connectedGroups = evidenceGroups.Length;
        }
        if (connectedGroups < 2) return [graph];
        return groups.Select((ids, index) =>
        {
            var id = graph.Id + "_cluster_" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            var componentNodes = nodes.Where(node => ids.Contains(node.Id)).ToArray();
            var componentEdges = edges.Where(edge => edge.SourceId is not null && edge.TargetId is not null && ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)).ToArray();
            var componentPaths = (graph.Paths ?? [])
                .Where(path => path.SourceNodeId is null || ids.Contains(path.SourceNodeId) ||
                    componentEdges.Any(edge => edge.Path?.SequenceEqual(path.Points ?? []) == true))
                .ToArray();
            var componentNodeIds = componentNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            var componentEdgeIds = componentEdges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
            var componentPathIds = componentPaths.Select(path => path.Id).ToHashSet(StringComparer.Ordinal);
            var componentAnchors = componentNodes.SelectMany(node => node.SourceAnchor?.Locators ?? [])
                .Concat(componentEdges.SelectMany(edge => edge.SourceAnchor?.Locators ?? []))
                .Concat(componentPaths.SelectMany(path => path.SourceAnchor?.Locators ?? []))
                // The graph-level visual_graph locator is shared by every component and
                // must not pull unrelated source items into this component's ledger.
                .Where(locator => locator.Kind != "visual_graph")
                .Select(locator => locator.Kind + "=" + locator.Value)
                .ToHashSet(StringComparer.Ordinal);
            var componentDiagnostics = (graph.Diagnostics ?? [])
                .Where(diagnostic => diagnostic.SourceObjectId is null ||
                    componentEdges.Any(edge => edge.SourceAnchor?.Locators.Any(locator => locator.Value == diagnostic.SourceObjectId) == true))
                .ToArray();
            var componentDiagnosticCodes = componentDiagnostics.Select(diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);
            var componentItems = (graph.SourceItems ?? [])
                .Where(item =>
                    item.ProjectedNodeId is not null && componentNodeIds.Contains(item.ProjectedNodeId) ||
                    item.ProjectedEdgeId is not null && componentEdgeIds.Contains(item.ProjectedEdgeId) ||
                    item.FallbackPathId is not null && componentPathIds.Contains(item.FallbackPathId) ||
                    item.Disposition == VisualDisposition.DiagnosticOnly && item.DiagnosticCode is not null && componentDiagnosticCodes.Contains(item.DiagnosticCode) ||
                    item.SourceAnchor?.Locators.Any(locator => componentAnchors.Contains(locator.Kind + "=" + locator.Value)) == true)
                .ToArray();
            return graph with { Id = id, Nodes = componentNodes, Edges = componentEdges,
                Paths = componentPaths, Diagnostics = componentDiagnostics, SourceItems = componentItems };
        }).ToArray();
    }

    private static VisualGraph? BuildDocxVisualGraph(XElement paragraph, SourceAnchor anchor, string sourceNodeId,
        DocxHiddenStyles hiddenStyles, TimeSpan? inferenceTimeout, CancellationToken cancellationToken)
    {
        static bool IsVmlConnector(XElement item)
        {
            if (item.Name != V + "shape" && item.Name != V + "line") return false;
            var type = ((string?)item.Attribute("type") ?? string.Empty).ToLowerInvariant();
            return item.Name == V + "line" || type.Contains("line") || item.Attribute("from") is not null || item.Attribute("to") is not null;
        }

        var shapeElements = paragraph.Descendants().Where(item =>
            item.Name == A + "sp" ||
            (item.Name == WPS + "wsp" && !string.Equals(
                (string?)item.Descendants(A + "prstGeom").FirstOrDefault()?.Attribute("prst"), "line", StringComparison.OrdinalIgnoreCase)) ||
            (item.Name == V + "shape" && !IsVmlConnector(item)) ||
            (item.Name == V + "rect" || item.Name == V + "roundrect" || item.Name == V + "oval")).ToArray();
        var connectorElements = paragraph.Descendants(A + "cxnSp")
            .Select(item => (Element: item, Vml: false))
            .Concat(paragraph.Descendants(WPS + "wsp")
                .Where(item => string.Equals((string?)item.Descendants(A + "prstGeom").FirstOrDefault()?.Attribute("prst"), "line", StringComparison.OrdinalIgnoreCase))
                .Select(item => (Element: item, Vml: false)))
            .Concat(paragraph.Descendants(V + "shape").Where(IsVmlConnector).Select(item => (Element: item, Vml: true)))
            .Concat(paragraph.Descendants(V + "line").Select(item => (Element: item, Vml: true)))
            .ToArray();
        if (shapeElements.Length == 0 && connectorElements.Length == 0) return null;
        // Textbox content is already projected as ordinary DocumentNodes. Without any
        // connector, a textbox-only canvas adds no visual semantics and would duplicate
        // that same text in the visual-fallback section.
        if (connectorElements.Length == 0 && shapeElements.All(item => item.Descendants(W + "txbxContent").Any()))
            return null;

        // Build the semantic node set once per unique source shape ID. Duplicate IDs are
        // preserved in the source ledger as suppressed duplicates rather than becoming
        // duplicate VisualNode IDs that would invalidate promotion.
        var shapeRecords = shapeElements.Select((shape, index) =>
        {
            var shapeId = (string?)shape.Attribute("id") ??
                (string?)shape.Descendants().FirstOrDefault(item => item.Name.LocalName == "cNvPr")?.Attribute("id") ?? index.ToString();
            var nodeAnchor = anchor with { Locators = [new AnchorLocator("shape_id", shapeId)] };
            var id = NodeIdGenerator.CreateForSource("docx-visual", DocumentFormatKind.Docx, nodeAnchor);
            var geometry = ReadVisualGeometry(shape);
            var label = TextBoxText(shape, hiddenStyles).Trim();
            if (label.Length == 0) label = "Shape " + shapeId;
            return (shapeId, PrimitiveId: "docx-primitive-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), Geometry: geometry,
                IsTextBox: shape.Name == A + "sp" && shape.Descendants(W + "txbxContent").Any(),
                Node: new VisualNode(id, label, VisualNodeKind.Generic, sourceNodeId,
                    Geometry: geometry, SourceAnchor: nodeAnchor,
                    Group: (string?)shape.Ancestors(A + "grpSp").FirstOrDefault()?.Descendants(A + "cNvPr").FirstOrDefault()?.Attribute("id") ??
                        (string?)shape.Ancestors(WPG + "wgp").FirstOrDefault()?.Descendants(A + "cNvPr").FirstOrDefault()?.Attribute("id")));
        }).ToArray();
        var lookup = shapeRecords.GroupBy(item => item.shapeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var nodes = new List<VisualNode>();
        var sourceItems = new List<VisualSourceItem>();
        var shapeSourceItems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in shapeRecords)
        {
            var sourceItemId = "shape:" + record.shapeId;
            if (shapeSourceItems.TryGetValue(record.shapeId, out var firstSourceItem))
            {
                sourceItems.Add(new VisualSourceItem("shape:" + record.shapeId + ":duplicate:" + sourceItems.Count,
                    VisualSourceItemKind.Shape, VisualDisposition.SuppressedDuplicate,
                    DuplicateOfSourceItemId: firstSourceItem, Reason: "duplicate source shape ID",
                    SourceAnchor: record.Node.SourceAnchor));
                continue;
            }
            shapeSourceItems[record.shapeId] = sourceItemId;
            nodes.Add(record.Node);
            sourceItems.Add(new VisualSourceItem(sourceItemId,
                record.IsTextBox ? VisualSourceItemKind.TextLabel : VisualSourceItemKind.Shape,
                VisualDisposition.ProjectedNode, ProjectedNodeId: record.Node.Id, SourceAnchor: record.Node.SourceAnchor));
        }

        var edges = new List<VisualEdge>();
        var paths = new List<VisualPath>();
        var diagnostics = new List<VisualDiagnostic>();
        var coordinateSpaces = nodes.Where(node => node.Geometry is not null).Select(node => node.Geometry!.CoordinateSpace)
            .Distinct(StringComparer.Ordinal).ToArray();
        var hasIncompatibleCoordinateSpaces = coordinateSpaces.Length > 1;
        if (hasIncompatibleCoordinateSpaces)
            diagnostics.Add(new VisualDiagnostic("VisualCoordinateSpaceIncompatible",
                "DOCX anchored shapes use incompatible relative coordinate frames and were not merged into a semantic relation.",
                Format: "docx", PartUri: anchor.PartUri, PartitionId: sourceNodeId));
        var labelRecords = shapeRecords
            .Where(item => item.IsTextBox && item.Geometry is not null && !string.IsNullOrWhiteSpace(item.Node.Label) && shapeSourceItems.ContainsKey(item.shapeId))
            .ToArray();
        var labelChoices = new List<EdgeLabelCandidate>();
        foreach (var (labelRecord, labelIndex) in labelRecords.Select((item, index) => (item, index)))
        {
            // Edge-label assignment uses the connector segment, not matching text. This keeps
            // repeated labels independent and gives the shared one-to-one assigner the same
            // deterministic features for every connector.
            foreach (var (connector, connectorIndex) in connectorElements.Select((item, index) => (item, index)))
            {
                var connectorGeometry = connector.Vml && connector.Element.Name == V + "line"
                    ? ReadVmlLineGeometry(connector.Element)
                    : ReadVisualGeometry(connector.Element);
                if (connectorGeometry is null) continue;
                var first = new VisualPoint(connectorGeometry.X, connectorGeometry.Y);
                var last = new VisualPoint(connectorGeometry.X + connectorGeometry.Width, connectorGeometry.Y + connectorGeometry.Height);
                var labelCenter = new VisualPoint(labelRecord.Geometry!.X + labelRecord.Geometry.Width / 2,
                    labelRecord.Geometry.Y + labelRecord.Geometry.Height / 2);
                var distance = GeometryMath.DistanceToSegment(labelCenter, first, last, out _);
                var segmentScale = Math.Max(Math.Sqrt(connectorGeometry.Width * connectorGeometry.Width + connectorGeometry.Height * connectorGeometry.Height), 1);
                var score = 1d - distance / Math.Max(segmentScale * 0.35, 1);
                if (score > 0)
                    labelChoices.Add(new EdgeLabelCandidate("label:" + labelRecord.shapeId, "edge:" + connectorIndex, score));
            }
        }
        var labelAssignments = EdgeLabelAssigner.Assign(labelChoices);
        var assignedLabelByEdge = labelAssignments.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var assignedLabelShapeIds = labelAssignments.Keys
            .Select(item => item["label:".Length..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var labelRecord in labelRecords.Where(item => !assignedLabelShapeIds.Contains(item.shapeId)))
        {
            if (!shapeSourceItems.TryGetValue(labelRecord.shapeId, out var labelSourceItemId)) continue;
            nodes.RemoveAll(node => node.Id == lookup[labelRecord.shapeId].Node.Id);
            sourceItems.RemoveAll(item => item.Id == labelSourceItemId);
            sourceItems.Add(new VisualSourceItem(labelSourceItemId, VisualSourceItemKind.TextLabel,
                VisualDisposition.IgnoredDecorative,
                Reason: labelChoices.Any(item => item.LabelId == "label:" + labelRecord.shapeId)
                    ? "unresolved edge label; retained as document text" : "independent textbox retained as document text",
                SourceAnchor: labelRecord.Node.SourceAnchor));
        }
        foreach (var labelId in labelChoices.Select(item => item.LabelId).Distinct(StringComparer.Ordinal))
        {
            if (labelAssignments.ContainsKey(labelId)) continue;
            var shapeId = labelId["label:".Length..];
            diagnostics.Add(new VisualDiagnostic("VisualEdgeLabelUnresolved",
                "DOCX textbox was near a connector but could not be assigned uniquely as its edge label.", sourceNodeId,
                Fallback: "textbox retained as document text and visual fallback", Remedy: "place the textbox on one connector segment",
                Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: shapeId,
                SourceObjectType: "textbox", Confidence: 0));
        }

        static VisualPathPoint? PointAt(XElement element, Geometry? geometry, bool end)
        {
            if (geometry is null || (geometry.Width == 0 && geometry.Height == 0)) return null;
            var point = new VisualPoint(end ? geometry.X + geometry.Width : geometry.X,
                end ? geometry.Y + geometry.Height : geometry.Y);
            var xfrm = element.Descendants(A + "xfrm").FirstOrDefault();
            var flipH = string.Equals((string?)xfrm?.Attribute("flipH"), "1", StringComparison.Ordinal) || string.Equals((string?)xfrm?.Attribute("flipH"), "true", StringComparison.OrdinalIgnoreCase);
            var flipV = string.Equals((string?)xfrm?.Attribute("flipV"), "1", StringComparison.Ordinal) || string.Equals((string?)xfrm?.Attribute("flipV"), "true", StringComparison.OrdinalIgnoreCase);
            if (flipH) point = new VisualPoint(geometry.X + geometry.Width - (point.X - geometry.X), point.Y);
            if (flipV) point = new VisualPoint(point.X, geometry.Y + geometry.Height - (point.Y - geometry.Y));
            if (double.TryParse((string?)xfrm?.Attribute("rot"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rotation) && Math.Abs(rotation) > 1e-9)
            {
                var degrees = rotation / 60000d;
                var radians = degrees * Math.PI / 180d;
                var center = new VisualPoint(geometry.X + geometry.Width / 2, geometry.Y + geometry.Height / 2);
                var dx = point.X - center.X;
                var dy = point.Y - center.Y;
                point = new VisualPoint(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
                    center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
            }
            return new VisualPathPoint(point.X, point.Y);
        }
        // Build one shared inference document for the whole visual region. Projection below only
        // selects its connector result; it must not re-cluster/re-infer per connector.
        var sharedCanvasId = "docx:" + sourceNodeId;
        // Preserve every source shape as a distinct inference primitive even when malformed
        // input repeats cNvPr IDs. The shared shape_id alias lets the engine reject collisions;
        // semantic projection still deduplicates repeated source IDs through `lookup` above.
        var sharedNodeRecords = shapeRecords.Where(item => item.Geometry is not null && !item.IsTextBox).ToArray();
        var sharedNodeShapeIds = sharedNodeRecords.ToDictionary(item => item.PrimitiveId, item => item.shapeId, StringComparer.Ordinal);
        var sharedNodes = sharedNodeRecords
            .Select(item => (VisualPrimitive)new VisualNodePrimitive(item.PrimitiveId, sharedCanvasId, item.Node.SourceAnchor ?? anchor,
                new VisualRect(item.Geometry!.X, item.Geometry.Y, item.Geometry.Width, item.Geometry.Height), Text: item.Node.Label,
                GroupId: item.Node.Group, Aliases: [new VisualIdentityAlias("shape_id", item.shapeId)]))
            .ToArray();
        var sharedConnectors = connectorElements.Select((item, index) =>
        {
            var literal = item.Vml && item.Element.Name == V + "line";
            var geometry = literal ? ReadVmlLineGeometry(item.Element) : ReadVisualGeometry(item.Element);
            var points = geometry is null
                ? Array.Empty<VisualPoint>()
                : new[]
                {
                    PointAt(item.Element, geometry, false) is { } startPoint
                        ? new VisualPoint(startPoint.X, startPoint.Y)
                        : new VisualPoint(geometry.X, geometry.Y),
                    PointAt(item.Element, geometry, true) is { } endPoint
                        ? new VisualPoint(endPoint.X, endPoint.Y)
                        : new VisualPoint(geometry.X + geometry.Width, geometry.Y + geometry.Height),
                };
            static string? SharedAlias(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('#');
            var from = literal ? null : SharedAlias((string?)item.Element.Attribute("from") ?? (string?)item.Element.Descendants().FirstOrDefault(x => x.Name.LocalName == "stCxn")?.Attribute("id"));
            var to = literal ? null : SharedAlias((string?)item.Element.Attribute("to") ?? (string?)item.Element.Descendants().FirstOrDefault(x => x.Name.LocalName == "endCxn")?.Attribute("id"));
            var id = item.Vml ? (string?)item.Element.Attribute("id") : (string?)item.Element.Descendants().FirstOrDefault(x => x.Name.LocalName == "cNvPr")?.Attribute("id");
            id ??= index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var drawingLine = item.Element.Descendants(A + "ln").FirstOrDefault();
            var startArrowhead = item.Vml && item.Element.Descendants(V + "stroke").Any(stroke => !string.IsNullOrWhiteSpace((string?)stroke.Attribute("startarrow"))) ||
                drawingLine?.Element(A + "headEnd") is not null
                ? new ArrowheadEvidence(true, Kind: item.Vml ? "vml-start" : "drawingml-head", Confidence: 1) : null;
            var endArrowhead = item.Vml && item.Element.Descendants(V + "stroke").Any(stroke => !string.IsNullOrWhiteSpace((string?)stroke.Attribute("endarrow"))) ||
                drawingLine?.Element(A + "tailEnd") is not null
                ? new ArrowheadEvidence(true, Kind: item.Vml ? "vml-end" : "drawingml-tail", Confidence: 1) : null;
            return (VisualPrimitive)new VisualConnectorPrimitive(id, sharedCanvasId, anchor,
                new VisualConnectorPath(points, StartArrowhead: startArrowhead, EndArrowhead: endArrowhead), from, to);
        }).ToArray();
        var sharedTextLabels = labelRecords.Select((item, index) =>
            (VisualPrimitive)new VisualTextPrimitive(item.PrimitiveId, sharedCanvasId, item.Node.SourceAnchor ?? anchor,
                item.Geometry is null ? null : new VisualRect(item.Geometry.X, item.Geometry.Y, item.Geometry.Width, item.Geometry.Height),
                item.Node.Label, GroupId: item.Node.Group)).ToArray();
        var sharedPrimitives = sharedNodes.Concat(sharedConnectors).Concat(sharedTextLabels).ToArray();
        var sharedBounds = sharedNodes.Select(node => node.Bounds).Where(item => item is not null).Cast<VisualRect>().ToArray();
        var sharedWidth = Math.Max(1, sharedBounds.Select(item => item.Right).DefaultIfEmpty(1).Max());
        var sharedHeight = Math.Max(1, sharedBounds.Select(item => item.Bottom).DefaultIfEmpty(1).Max());
        var sharedDocument = new VisualPrimitiveDocument("docx:" + sourceNodeId, DocumentFormatKind.Docx,
            [new VisualCanvas(sharedCanvasId, anchor.PartUri, "part-0001", sharedWidth, sharedHeight, "ooxml", anchor)], sharedPrimitives);
        var sharedClusterer = new DiagramClusterer();
        var sharedClusters = sharedClusterer.Cluster(sharedDocument);
        SoftConnectionResult sharedInference;
        try
        {
            using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (inferenceTimeout is { } timeout)
            {
                if (timeout <= TimeSpan.Zero) inferenceCts.Cancel();
                else inferenceCts.CancelAfter(timeout);
            }
            sharedInference = new SoftConnectionEngine().Infer(sharedDocument, sharedClusters,
                new SoftConnectionOptions(VisualInferenceContext.Current), inferenceCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Do not project a partially solved graph. Keep every connector as fallback
            // geometry and expose one stable warning for callers/QA.
            diagnostics.Add(new VisualDiagnostic("VisualInferenceTimeout",
                "DOCX visual inference exceeded its configured time budget and returned no partial topology.", sourceNodeId,
                Fallback: "all connector geometry retained as visual fallback", Remedy: "increase VisualInferenceTimeout or simplify the diagram",
                Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectType: "inference"));
            sharedInference = new SoftConnectionResult([], [], [], Diagnostics: [new VisualExtractionDiagnostic("VisualInferenceTimeout", "visual inference timed out")]);
        }

        static bool SegmentIntersectsInterior(VisualPoint first, VisualPoint last, Geometry rectangle)
        {
            var left = rectangle.X + 1e-6;
            var right = rectangle.X + rectangle.Width - 1e-6;
            var top = rectangle.Y + 1e-6;
            var bottom = rectangle.Y + rectangle.Height - 1e-6;
            var dx = last.X - first.X;
            var dy = last.Y - first.Y;
            var enter = 0d;
            var exit = 1d;
            static bool Clip(double p, double q, ref double enter, ref double exit)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                var ratio = q / p;
                if (p < 0)
                {
                    if (ratio > exit) return false;
                    if (ratio > enter) enter = ratio;
                }
                else
                {
                    if (ratio < enter) return false;
                    if (ratio < exit) exit = ratio;
                }
                return true;
            }
            return rectangle.Width > 0 && rectangle.Height > 0 &&
                Clip(-dx, first.X - left, ref enter, ref exit) &&
                Clip(dx, right - first.X, ref enter, ref exit) &&
                Clip(-dy, first.Y - top, ref enter, ref exit) &&
                Clip(dy, bottom - first.Y, ref enter, ref exit) && exit > enter;
        }

        (string? SourceKey, string? TargetKey, bool AmbiguousStart, bool AmbiguousEnd, VisualConnectionEvidence? Evidence, IReadOnlyList<string>? RejectedCandidateIds)
            InferConnector(string connectorId, string? startAlias, string? endAlias, IReadOnlyList<VisualPathPoint?> endpoints,
                ArrowheadEvidence? startArrowhead, ArrowheadEvidence? endArrowhead)
        {
            var nativeAliasAmbiguous = (startAlias is not null && sharedNodeRecords.Count(item => StringComparer.Ordinal.Equals(item.shapeId, startAlias)) != 1) ||
                (endAlias is not null && sharedNodeRecords.Count(item => StringComparer.Ordinal.Equals(item.shapeId, endAlias)) != 1);
            if (nativeAliasAmbiguous)
                return (null, null, false, false, null, ["VisualNativeAliasAmbiguous"]);
            if (hasIncompatibleCoordinateSpaces) return (null, null, false, false, null, null);
            var path = endpoints.Where(point => point is not null).Select(point => new VisualPoint(point!.X, point.Y)).ToArray();
            if (path.Length < 2 && (startAlias is null || endAlias is null))
                return (null, null, false, false, null, null);
            var options = new SoftConnectionOptions(VisualInferenceContext.Current);
            var inference = sharedInference;
            var pair = inference.Resolved.SingleOrDefault(item => item.ConnectorId == connectorId);
            var unresolvedPair = inference.Unresolved.SingleOrDefault(item => item.ConnectorId == connectorId);
            var rejectedCandidateIds = unresolvedPair?.RejectedCandidateIds;
            var startCandidates = inference.Candidates.Where(candidate => candidate.ConnectorId == connectorId && candidate.IsStart && !candidate.IsHardRejected).ToArray();
            var endCandidates = inference.Candidates.Where(candidate => candidate.ConnectorId == connectorId && !candidate.IsStart && !candidate.IsHardRejected).ToArray();
            var ambiguousStart = startAlias is null && startCandidates.FirstOrDefault()?.Features.CandidateMargin < options.HighMargin;
            var ambiguousEnd = endAlias is null && endCandidates.FirstOrDefault()?.Features.CandidateMargin < options.HighMargin;
            if (pair?.SourceId is null || pair.TargetId is null)
                return (null, null, ambiguousStart, ambiguousEnd, null, rejectedCandidateIds);
            if (startAlias is null && endAlias is null && path.Length >= 2 &&
                sharedNodeRecords.Any(item => item.PrimitiveId != pair.SourceId && item.PrimitiveId != pair.TargetId &&
                    SegmentIntersectsInterior(path[0], path[^1], item.Geometry!)))
                return (null, null, ambiguousStart, ambiguousEnd, null,
                    (rejectedCandidateIds ?? []).Append("VisualIntermediateNodeCrossing").ToArray());
            var physicalStartId = pair.Direction == ConnectionDirection.Reverse ? pair.TargetId : pair.SourceId;
            var physicalEndId = pair.Direction == ConnectionDirection.Reverse ? pair.SourceId : pair.TargetId;
            var selected = inference.Candidates.Where(candidate => candidate.ConnectorId == connectorId &&
                    (candidate.IsStart && candidate.NodeId == physicalStartId || !candidate.IsStart && candidate.NodeId == physicalEndId))
                .ToArray();
            var margin = selected.Select(candidate => candidate.Features.CandidateMargin).DefaultIfEmpty(1).Min();
            var evidence = new VisualConnectionEvidence(
                pair.IsNative ? "native-connection" : "soft-geometry",
                pair.Confidence.ToString(), pair.Score,
                SecondBestScore: Math.Max(0, pair.Score - margin), CandidateMargin: margin,
                BoundaryDistanceNormalized: selected.Select(candidate => candidate.Features.BoundaryDistanceNormalized).DefaultIfEmpty(0).Max(),
                RayIntersects: selected.All(candidate => candidate.Features.RayIntersects),
                RayFirstHit: selected.All(candidate => candidate.Features.RayFirstHit),
                AngularDeviationDegrees: selected.Select(candidate => candidate.Features.AngularDeviationDegrees).DefaultIfEmpty(0).Max(),
                PerpendicularOffsetNormalized: selected.Select(candidate => candidate.Features.PerpendicularOffsetNormalized).DefaultIfEmpty(0).Max(),
                IntermediateNodeCount: selected.Select(candidate => candidate.Features.IntermediateNodeCount).DefaultIfEmpty(0).Max(),
                ArrowheadEvidence: "none", ClusterId: pair.ClusterId,
                RejectedCandidateIds: pair.RejectedCandidateIds);
            var sourceKey = sharedNodeShapeIds.TryGetValue(pair.SourceId, out var mappedSource) ? mappedSource : null;
            var targetKey = sharedNodeShapeIds.TryGetValue(pair.TargetId, out var mappedTarget) ? mappedTarget : null;
            return (sourceKey, targetKey, ambiguousStart, ambiguousEnd, evidence, rejectedCandidateIds);
        }

        foreach (var (connector, index) in connectorElements.Select((item, index) => (item, index)))
        {
            static string? NormalizeRef(string? value) =>
                string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('#');
            var isLiteralVmlLine = connector.Vml && connector.Element.Name == V + "line";
            var drawingLine = connector.Element.Descendants(A + "ln").FirstOrDefault();
            var startArrowhead = connector.Vml && connector.Element.Descendants(V + "stroke").Any(stroke => !string.IsNullOrWhiteSpace((string?)stroke.Attribute("startarrow"))) ||
                drawingLine?.Element(A + "headEnd") is not null
                ? new ArrowheadEvidence(true, Kind: connector.Vml ? "vml-start" : "drawingml-head", Confidence: 1) : null;
            var endArrowhead = connector.Vml && connector.Element.Descendants(V + "stroke").Any(stroke => !string.IsNullOrWhiteSpace((string?)stroke.Attribute("endarrow"))) ||
                drawingLine?.Element(A + "tailEnd") is not null
                ? new ArrowheadEvidence(true, Kind: connector.Vml ? "vml-end" : "drawingml-tail", Confidence: 1) : null;
            var vmlReversed = isLiteralVmlLine && VmlCoordinate((string?)connector.Element.Attribute("from")) > VmlCoordinate((string?)connector.Element.Attribute("to"));
            var start = isLiteralVmlLine ? null
                : connector.Vml
                ? NormalizeRef((string?)connector.Element.Attribute("from") ?? (string?)connector.Element.Attribute("start"))
                : NormalizeRef((string?)connector.Element.Descendants().FirstOrDefault(item => item.Name.LocalName == "stCxn")?.Attribute("id"));
            var end = isLiteralVmlLine ? null
                : connector.Vml
                ? NormalizeRef((string?)connector.Element.Attribute("to") ?? (string?)connector.Element.Attribute("end"))
                : NormalizeRef((string?)connector.Element.Descendants().FirstOrDefault(item => item.Name.LocalName == "endCxn")?.Attribute("id"));
            var geometry = connector.Vml && connector.Element.Name == V + "line"
                ? ReadVmlLineGeometry(connector.Element)
                : ReadVisualGeometry(connector.Element);
            var connectorObjectId = connector.Vml
                ? (string?)connector.Element.Attribute("id")
                : (string?)connector.Element.Descendants().FirstOrDefault(item => item.Name.LocalName == "cNvPr")?.Attribute("id");
            connectorObjectId ??= index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var points = new[] { PointAt(connector.Element, geometry, false), PointAt(connector.Element, geometry, true) };
            var inferred = InferConnector(connectorObjectId, start, end, points, startArrowhead, endArrowhead);
            var sourceKey = inferred.SourceKey;
            var targetKey = inferred.TargetKey;
            // PointAt already applies DrawingML flip/rotation to the inference segment;
            // only literal VML retains a normalized geometry box, so its source order still
            // needs the legacy from/to correction.
            if (vmlReversed && ((startArrowhead is not null) != (endArrowhead is not null)))
                (sourceKey, targetKey) = (targetKey, sourceKey);
            var nativeSourceResolved = start is not null && sourceKey == start;
            var nativeTargetResolved = end is not null && targetKey == end;
            var ambiguousStart = inferred.AmbiguousStart;
            var ambiguousEnd = inferred.AmbiguousEnd;
            var source = sourceKey is not null ? lookup[sourceKey].Node.Id : null;
            var target = targetKey is not null ? lookup[targetKey].Node.Id : null;
            var selfEdge = source is not null && source == target;
            if (selfEdge) { source = null; target = null; }
            var resolved = source is not null && target is not null;
            var resolution = resolved
                ? nativeSourceResolved && nativeTargetResolved
                    ? VisualEdgeResolution.NativeConnection
                    : VisualEdgeResolution.GeometryInferred
                : VisualEdgeResolution.Unresolved;
            var edgeAnchor = anchor with { Locators = anchor.Locators.Concat([new AnchorLocator("connector", index.ToString(System.Globalization.CultureInfo.InvariantCulture))]).ToArray() };
            var labelShapeId = assignedLabelByEdge.TryGetValue("edge:" + index, out var assignedLabelId)
                ? assignedLabelId["label:".Length..] : null;
            string? label = null;
            if (labelShapeId is not null)
            {
                var assignedLabel = labelRecords.First(item => StringComparer.Ordinal.Equals(item.shapeId, labelShapeId));
                label = assignedLabel.Node.Label.Trim();
                var labelAnchor = anchor with
                {
                    Locators = [new AnchorLocator("connector", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new AnchorLocator("shape_id", assignedLabel.shapeId)]
                };
                if (shapeSourceItems.TryGetValue(assignedLabel.shapeId, out var labelShapeSourceId))
                {
                    // The source textbox remains an ordinary DocumentNode, but once adopted as
                    // an edge label it must no longer also be a semantic VisualNode.
                    nodes.RemoveAll(node => node.Id == lookup[assignedLabel.shapeId].Node.Id);
                    sourceItems.RemoveAll(item => item.Id == labelShapeSourceId);
                    sourceItems.Add(new VisualSourceItem(labelShapeSourceId, VisualSourceItemKind.TextLabel,
                        VisualDisposition.IgnoredDecorative, Reason: "attached as connector label", SourceAnchor: labelAnchor));
                }
            }
            var diagnosticConfidence = resolved
                ? resolution == VisualEdgeResolution.NativeConnection ? 1d : 0.8d
                : 0d;
            if (ambiguousStart || ambiguousEnd)
                diagnostics.Add(new VisualDiagnostic("VisualConnectorAmbiguous", "DOCX connector endpoint inference was ambiguous.", sourceNodeId,
                    Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: connectorObjectId,
                    SourceObjectType: "connector", Confidence: diagnosticConfidence));
            if (inferred.RejectedCandidateIds?.Contains("VisualNativeAliasAmbiguous", StringComparer.Ordinal) == true)
                diagnostics.Add(new VisualDiagnostic("VisualNativeAliasAmbiguous", "DOCX native connector alias was ambiguous and remained unresolved.", sourceNodeId,
                    Fallback: "connector retained as visual fallback", Remedy: "assign unique shape IDs before reconnecting the connector",
                    Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: connectorObjectId,
                    SourceObjectType: "connector", Confidence: diagnosticConfidence));
            if (!resolved)
                diagnostics.Add(new VisualDiagnostic("VisualConnectorUnresolved",
                    selfEdge ? "DOCX connector self-edge was not projected." : "DOCX connector endpoint could not be resolved.", sourceNodeId,
                    Fallback: "connector retained as visual fallback", Remedy: "snap both connector endpoints to distinct shapes",
                    Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: connectorObjectId,
                    SourceObjectType: "connector", Confidence: diagnosticConfidence));
            var pathPoints = points.Where(point => point is not null).Select(point => point!).ToArray();
            string? pathId = null;
            if (geometry is not null)
            {
                pathId = "path_" + index;
                paths.Add(new VisualPath(pathId, pathPoints, geometry, edgeAnchor, resolved ? 0.9 : 0.4, !resolved, sourceNodeId));
            }
            // Native endpoint references and DrawingML connector shapes carry directed
            // relation semantics even when an explicit arrowhead is omitted. Free geometry
            // (including literal VML and unsnapped WPS lines) remains undirected without one.
            var hasArrowhead = startArrowhead is not null || endArrowhead is not null ||
                resolved && (connector.Element.Name == A + "cxnSp" || start is not null && end is not null);
            var connectorSourceId = "connector:" + index;
            if (resolved)
                sourceItems.Add(new VisualSourceItem(connectorSourceId, VisualSourceItemKind.Connector,
                    VisualDisposition.ProjectedEdge, ProjectedEdgeId: "edge_" + index, SourceAnchor: edgeAnchor));
            else if (pathId is not null)
                sourceItems.Add(new VisualSourceItem(connectorSourceId, VisualSourceItemKind.Connector,
                    VisualDisposition.VisualFallback, FallbackPathId: pathId, Reason: "unresolved connector geometry", SourceAnchor: edgeAnchor));
            else
                sourceItems.Add(new VisualSourceItem(connectorSourceId, VisualSourceItemKind.Connector,
                    VisualDisposition.DiagnosticOnly, DiagnosticCode: "VisualConnectorUnresolved", SourceAnchor: edgeAnchor));
            edges.Add(new VisualEdge("edge_" + index, source, target, Label: label,
                Resolution: resolution, SourceNodeId: sourceNodeId, Direction: hasArrowhead ? "directed" : "undirected",
                Geometry: geometry, Confidence: resolved ? (resolution == VisualEdgeResolution.NativeConnection ? 1 : 0.8) : 0,
                Path: pathPoints, SourceAnchor: edgeAnchor, EdgeDirection: hasArrowhead ? VisualEdgeDirection.Directed : VisualEdgeDirection.Undirected,
                Evidence: resolved ? inferred.Evidence : null));
        }

        // A wps:wsp shape is always sent to the engine as a full node candidate (only DrawingML
        // a:sp text boxes get the pre-engine label treatment above), so a small "YES"-style label
        // built as a wps:wsp shape has no path back out of node-hood when the engine simply never
        // considers it a plausible connector endpoint. Recover that case here: a shape absent from
        // every connector's Candidates (never within the engine's own endpoint search radius) and
        // not attached to any edge, but sitting close to a connector that DID resolve, is almost
        // certainly a mislabeled edge label rather than a deliberately unconnected node. This only
        // reclassifies shapes the engine already left untouched; it can never override a resolved
        // or ambiguous engine decision -- R0_FIX_04's equidistant TARGET_A/TARGET_B stay real nodes
        // because the engine already lists them as Candidates.
        var engineCandidateNodeIds = sharedInference.Candidates.Select(candidate => candidate.NodeId).ToHashSet(StringComparer.Ordinal);
        var edgeAttachedNodeIds = edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
            .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal);
        var nodeMinorAxes = sharedNodeRecords.Select(item => Math.Min(item.Geometry!.Width, item.Geometry.Height))
            .OrderBy(value => value).ToArray();
        if (nodeMinorAxes.Length > 0)
        {
            var midIndex = nodeMinorAxes.Length / 2;
            var minorAxisMedian = nodeMinorAxes.Length % 2 == 0
                ? (nodeMinorAxes[midIndex - 1] + nodeMinorAxes[midIndex]) / 2 : nodeMinorAxes[midIndex];
            var labelRadius = Math.Max(minorAxisMedian * OrphanShapeLabelRadiusFactor, 1);
            var orphanLabelChoices = new List<EdgeLabelCandidate>();
            foreach (var record in sharedNodeRecords)
            {
                if (engineCandidateNodeIds.Contains(record.PrimitiveId)) continue;
                if (edgeAttachedNodeIds.Contains(record.Node.Id)) continue;
                foreach (var (connector, connectorIndex) in connectorElements.Select((item, index) => (item, index)))
                {
                    if (edges[connectorIndex].SourceId is null || edges[connectorIndex].TargetId is null) continue;
                    if (assignedLabelByEdge.ContainsKey("edge:" + connectorIndex)) continue;
                    var connectorGeometry = connector.Vml && connector.Element.Name == V + "line"
                        ? ReadVmlLineGeometry(connector.Element)
                        : ReadVisualGeometry(connector.Element);
                    if (connectorGeometry is null) continue;
                    var first = new VisualPoint(connectorGeometry.X, connectorGeometry.Y);
                    var last = new VisualPoint(connectorGeometry.X + connectorGeometry.Width, connectorGeometry.Y + connectorGeometry.Height);
                    var center = new VisualPoint(record.Geometry!.X + record.Geometry.Width / 2, record.Geometry.Y + record.Geometry.Height / 2);
                    var distance = GeometryMath.DistanceToSegment(center, first, last, out _);
                    var score = 1d - distance / labelRadius;
                    if (score > 0)
                        orphanLabelChoices.Add(new EdgeLabelCandidate("label:" + record.shapeId, "edge:" + connectorIndex, score));
                }
            }
            var orphanAssignments = EdgeLabelAssigner.Assign(orphanLabelChoices);
            foreach (var labelId in orphanLabelChoices.Select(item => item.LabelId).Distinct(StringComparer.Ordinal))
            {
                var shapeId = labelId["label:".Length..];
                if (!shapeSourceItems.TryGetValue(shapeId, out var shapeSourceItemId)) continue;
                var demoted = lookup[shapeId];
                nodes.RemoveAll(node => node.Id == demoted.Node.Id);
                sourceItems.RemoveAll(item => item.Id == shapeSourceItemId);
                if (orphanAssignments.TryGetValue(labelId, out var assignedEdgeId))
                {
                    var connectorIndex = int.Parse(assignedEdgeId["edge:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    var labelAnchor = anchor with
                    {
                        Locators = [new AnchorLocator("connector", connectorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            new AnchorLocator("shape_id", shapeId)]
                    };
                    edges[connectorIndex] = edges[connectorIndex] with { Label = demoted.Node.Label.Trim() };
                    sourceItems.Add(new VisualSourceItem(shapeSourceItemId, VisualSourceItemKind.TextLabel,
                        VisualDisposition.IgnoredDecorative, Reason: "attached as connector label", SourceAnchor: labelAnchor));
                }
                else
                {
                    diagnostics.Add(new VisualDiagnostic("VisualEdgeLabelUnresolved",
                        "DOCX shape was near a resolved connector but could not be assigned uniquely as its edge label.", sourceNodeId,
                        Fallback: "shape retained as document text and visual fallback", Remedy: "move the shape onto one connector segment or increase its distance from unrelated connectors",
                        Format: "docx", PartUri: anchor.PartUri, PartitionId: "part-0001", SourceObjectId: shapeId,
                        SourceObjectType: "shape", Confidence: 0));
                    sourceItems.Add(new VisualSourceItem(shapeSourceItemId, VisualSourceItemKind.TextLabel,
                        VisualDisposition.IgnoredDecorative, Reason: "unresolved edge label; retained as document text", SourceAnchor: demoted.Node.SourceAnchor));
                }
            }
        }

        var graph = new VisualGraph("docx_visual_" + sourceNodeId, nodes, edges, diagnostics, "LR",
            Paths: paths, SourceItems: sourceItems);
        return graph with { Quality = VisualGraphValidator.ComputeQuality(graph) };
    }

    private static Geometry? ReadVisualGeometry(XElement element)
    {
        var extent = element.Descendants(WP + "extent").FirstOrDefault();
        var xfrm = element.Descendants(A + "xfrm").FirstOrDefault();
        var off = xfrm?.Element(A + "off");
        var ext = xfrm?.Element(A + "ext");
        var style = (string?)element.Attribute("style");
        double Parse(string? value) => double.TryParse(value?.TrimEnd('p','t','x','m'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        var x = Parse((string?)off?.Attribute("x"));
        var y = Parse((string?)off?.Attribute("y"));
        var width = Parse((string?)ext?.Attribute("cx"));
        var height = Parse((string?)ext?.Attribute("cy"));
        var anchor = element.AncestorsAndSelf(WP + "anchor").FirstOrDefault();
        var horizontal = anchor?.Element(WP + "positionH");
        var vertical = anchor?.Element(WP + "positionV");
        var horizontalFrame = (string?)horizontal?.Attribute("relativeFrom");
        var verticalFrame = (string?)vertical?.Attribute("relativeFrom");
        var offsetX = horizontal?.Element(WP + "posOffset")?.Value;
        var offsetY = vertical?.Element(WP + "posOffset")?.Value;
        if (anchor is not null)
        {
            x = Parse(offsetX) + x;
            y = Parse(offsetY) + y;
        }
        if (extent is not null) { width = Parse((string?)extent.Attribute("cx")); height = Parse((string?)extent.Attribute("cy")); }
        if (style is not null)
        {
            foreach (var token in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = token.Split(':', 2); if (pair.Length != 2) continue;
                var value = Parse(pair[1]);
                if (pair[0].Trim().Equals("margin-left", StringComparison.OrdinalIgnoreCase)) x = value;
                else if (pair[0].Trim().Equals("margin-top", StringComparison.OrdinalIgnoreCase)) y = value;
                else if (pair[0].Trim().Equals("width", StringComparison.OrdinalIgnoreCase)) width = value;
                else if (pair[0].Trim().Equals("height", StringComparison.OrdinalIgnoreCase)) height = value;
            }
        }
        if (width <= 0 && height <= 0) return null;
        var compatiblePageFrames = new[] { "page", "margin" };
        var coordinateSpace = anchor is null ||
            (compatiblePageFrames.Contains(horizontalFrame, StringComparer.OrdinalIgnoreCase) &&
             compatiblePageFrames.Contains(verticalFrame, StringComparer.OrdinalIgnoreCase) &&
             horizontal?.Element(WP + "align") is null && vertical?.Element(WP + "align") is null)
            ? "docx-page" : "docx-relative-" + (horizontalFrame ?? verticalFrame ?? "unknown");
        return new Geometry(coordinateSpace, x, y, width, height);
    }

    private static double VmlCoordinate(string? endpoint)
    {
        var value = (endpoint ?? string.Empty).Split(",", StringSplitOptions.TrimEntries).FirstOrDefault();
        value = value?.TrimEnd('p','t','x','m');
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static Geometry? ReadVmlLineGeometry(XElement line)
    {
        static double Value(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var number = text.Trim().TrimEnd('p','t','x','m');
            return double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        static (double X, double Y) Point(string? text)
        {
            var parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
            return (parts.Length > 0 ? Value(parts[0]) : 0, parts.Length > 1 ? Value(parts[1]) : 0);
        }
        var from = Point((string?)line.Attribute("from"));
        var to = Point((string?)line.Attribute("to"));
        var x = Math.Min(from.X, to.X);
        var y = Math.Min(from.Y, to.Y);
        return new Geometry("ooxml", x, y, Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
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
    // A nested w:tbl inside a cell (D08) is *not* folded into that cell's own text (CellText only
    // reads the cell's own paragraphs) — it is instead extracted as its own sibling Table node,
    // ordered immediately after this table by consuming further slots from the shared ordinal.
    // That sibling now records where it came from (nested_table_parent/_row/_column) so a reader
    // can put it back inside its host cell instead of guessing from document order alone.
    private static void AddTable(XElement table, string partUri, RawSliceRef? slice, int order,
        ICollection<DocumentNode> nodes, IDictionary<string, RawSliceRef> sliceMap, ref int ordinal,
        DocxHiddenStyles hiddenStyles, DocxContentControl? contentControl = null, DocxNestedTable? nested = null)
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
                var text = CellText(tc, hiddenStyles);
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
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        AddContentControlExtensions(extensions, contentControl);
        if (nested is not null)
        {
            extensions["nested_table_parent"] = JsonSerializer.SerializeToElement(nested.ParentNodeId);
            extensions["nested_table_row"] = JsonSerializer.SerializeToElement(nested.Row);
            extensions["nested_table_column"] = JsonSerializer.SerializeToElement(nested.Column);
            extensions["nested_table_paragraph_offset"] = JsonSerializer.SerializeToElement(nested.ParagraphOffset);
            extensions["nested_table_line_offset"] = JsonSerializer.SerializeToElement(nested.LineOffset);
        }
        if (CountMathRoots(table) > 0) extensions["math_linear"] = JsonSerializer.SerializeToElement(true);
        // Same rule as AddParagraph: an equation directly under a cell paragraph is preserved as an
        // anchor by the cell rewrite, so the table stays editable; one nested deeper cannot be
        // addressed, and the table is read-only rather than losing it on the next edit.
        var restorable = RelevantDescendants(table).Where(IsMathRoot).All(element => element.Parent?.Name == W + "p");
        var effectiveSlice = restorable ? slice : null;
        nodes.Add(new(id, NodeKind.Table, null, order, ContentLayer.Body, new TableNodeContent(grid), anchor, effectiveSlice,
            Editability: restorable ? NodeEditability.EditableWithConstraints : NodeEditability.Protected,
            Provenance: [new(EvidenceKind.Native)], Extensions: extensions.Count == 0 ? null : extensions));
        if (effectiveSlice is not null) sliceMap[id] = effectiveSlice;

        // Direct tc children only (a block content control inside the cell is transparent): a
        // deeper nested table is discovered when we recurse into this one, so scanning only one
        // level down here avoids visiting (and re-adding) it twice. Row/column are indices into
        // this table's own TableNodeContent.Rows so a consumer can address the host cell directly.
        var rowIndex = 0;
        foreach (var row in table.Elements(W + "tr"))
        {
            var cellIndex = 0;
            foreach (var tc in row.Elements(W + "tc"))
            {
                var cellBlocks = CellBlocks(tc).ToArray();
                var keptParagraph = KeptCellParagraphs(cellBlocks, hiddenStyles);
                for (var blockIndex = 0; blockIndex < cellBlocks.Length; blockIndex++)
                {
                    if (cellBlocks[blockIndex].Name != W + "tbl") continue;
                    // How many of the host cell's own (kept) paragraphs precede this table, so a
                    // reader can put the nested rows back between "before" and "after" instead of
                    // after everything the cell says.
                    // The cell's text joins paragraphs with "\n", and a w:br inside a paragraph also
                    // becomes "\n", so a reader splitting that text by line cannot tell the two
                    // apart. Record the position in both units: kept paragraphs, and the "\n"-lines
                    // those paragraphs occupy in CellText's output.
                    var paragraphOffset = 0;
                    var lineOffset = 0;
                    for (var preceding = 0; preceding < blockIndex; preceding++)
                    {
                        if (!keptParagraph[preceding]) continue;
                        paragraphOffset++;
                        lineOffset += 1 + ParagraphText(cellBlocks[preceding], hiddenStyles).Count(character => character == '\n');
                    }
                    var nestedOrder = ordinal++;
                    AddTable(cellBlocks[blockIndex], partUri, null, nestedOrder, nodes, sliceMap, ref ordinal, hiddenStyles,
                        contentControl, new DocxNestedTable(id, rowIndex, cellIndex, paragraphOffset, lineOffset));
                }
                cellIndex++;
            }
            rowIndex++;
        }
    }

    /// <summary>Where a nested table sat inside its host table's cell grid, and after how many of
    /// the host cell's own kept paragraphs (see <see cref="CellText"/>) it appeared.</summary>
    private sealed record DocxNestedTable(string ParentNodeId, int Row, int Column, int ParagraphOffset, int LineOffset);

    /// <summary>Marks, per cell block, whether a paragraph survives <see cref="CellText"/>'s trimming
    /// of leading and trailing empty paragraphs (tables are never "kept" paragraphs).</summary>
    private static bool[] KeptCellParagraphs(IReadOnlyList<XElement> blocks, DocxHiddenStyles hiddenStyles)
    {
        var kept = new bool[blocks.Count];
        var paragraphs = new List<int>();
        for (var index = 0; index < blocks.Count; index++) if (blocks[index].Name == W + "p") paragraphs.Add(index);
        var first = 0;
        var last = paragraphs.Count - 1;
        while (first <= last && ParagraphText(blocks[paragraphs[first]], hiddenStyles).Length == 0) first++;
        while (last >= first && ParagraphText(blocks[paragraphs[last]], hiddenStyles).Length == 0) last--;
        for (var position = first; position <= last; position++) kept[paragraphs[position]] = true;
        return kept;
    }

    /// <summary>The nearest block-level content control (w:sdt) a node came out of.</summary>
    private sealed record DocxContentControl(string? Alias, string? Tag);

    // A w:sdt is a wrapper around body content, so it is unwrapped rather than projected: the
    // w:p/w:tbl (and nested w:sdt) under its w:sdtContent become ordinary body elements. The
    // control's identity travels with them as content_control/sdt_alias/sdt_tag extensions, and
    // an inner control's alias/tag wins over an outer one's.
    private static IEnumerable<(XElement Element, DocxContentControl? Control)> ExpandContentControls(
        XElement element, DocxContentControl? inherited)
    {
        if (element.Name != W + "sdt")
        {
            yield return (element, inherited);
            yield break;
        }
        var properties = element.Element(W + "sdtPr");
        var control = new DocxContentControl(
            (string?)properties?.Element(W + "alias")?.Attribute(W + "val") ?? inherited?.Alias,
            (string?)properties?.Element(W + "tag")?.Attribute(W + "val") ?? inherited?.Tag);
        foreach (var child in element.Element(W + "sdtContent")?.Elements() ?? Enumerable.Empty<XElement>())
            foreach (var expanded in ExpandContentControls(child, control)) yield return expanded;
    }

    private static void AddContentControlExtensions(IDictionary<string, JsonElement> extensions, DocxContentControl? control)
    {
        if (control is null) return;
        extensions["content_control"] = JsonSerializer.SerializeToElement(true);
        if (!string.IsNullOrEmpty(control.Alias)) extensions["sdt_alias"] = JsonSerializer.SerializeToElement(control.Alias);
        if (!string.IsNullOrEmpty(control.Tag)) extensions["sdt_tag"] = JsonSerializer.SerializeToElement(control.Tag);
    }

    // A cell's text is its own paragraphs joined by "\n" so a multi-paragraph cell keeps its line
    // structure (the readable serializer renders "\n" inside a cell as <br>). Concatenating every
    // w:t in the subtree instead — the previous behaviour — silently glued "A", "B", "C" into
    // "ABC". Leading and trailing empty paragraphs are dropped because Word always leaves one
    // after a nested table, and they would otherwise show up as stray blank lines.
    private static string CellText(XElement cell, DocxHiddenStyles hiddenStyles)
    {
        var texts = CellBlocks(cell).Where(block => block.Name == W + "p")
            .Select(block => ParagraphText(block, hiddenStyles)).ToList();
        while (texts.Count > 0 && texts[0].Length == 0) texts.RemoveAt(0);
        while (texts.Count > 0 && texts[^1].Length == 0) texts.RemoveAt(texts.Count - 1);
        return string.Join("\n", texts);
    }

    /// <summary>The block-level children of a cell, seeing through content-control wrappers.</summary>
    private static IEnumerable<XElement> CellBlocks(XContainer cell)
    {
        foreach (var child in cell.Elements())
        {
            if (child.Name == W + "sdt")
            {
                if (child.Element(W + "sdtContent") is { } content)
                    foreach (var nested in CellBlocks(content)) yield return nested;
                continue;
            }
            if (child.Name == W + "customXml")
            {
                foreach (var nested in CellBlocks(child)) yield return nested;
                continue;
            }
            yield return child;
        }
    }

    private static int? ParsePositiveInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    private static async Task<int> AddRelatedTextPartsAsync(ZipArchive archive, IReadOnlyDictionary<string, string> relationships, string relationshipFragment,
        NodeKind kind, ContentLayer layer, ICollection<DocumentNode> nodes, int ordinal, DocxHiddenStyles hiddenStyles, CancellationToken cancellationToken)
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
                nodes.Add(new(id, kind, null, ordinal++, layer, new TextNodeContent(ParagraphText(paragraph, hiddenStyles)), anchor,
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

    private static void AddFootnotes(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal, DocxHiddenStyles hiddenStyles)
    {
        var xml = SafeXml.LoadDocument(bytes);
        foreach (var footnote in xml.Descendants(W + "footnote"))
        {
            var noteId = (string?)footnote.Attribute(W + "id") ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = new SourceAnchor("docx", "/word/footnotes.xml", [new("footnote_id", noteId)], ordinal);
            var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
            nodes.Add(new(id, NodeKind.Footnote, null, ordinal++, ContentLayer.Body, new TextNodeContent(ParagraphText(footnote, hiddenStyles)), anchor,
                Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)]));
        }
    }

    // D16: word/endnotes.xml mirrors word/footnotes.xml but was never read at all, so an
    // endnote's text disappeared entirely rather than merely losing its number.
    private static void AddEndnotes(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal, DocxHiddenStyles hiddenStyles)
    {
        var xml = SafeXml.LoadDocument(bytes);
        foreach (var endnote in xml.Descendants(W + "endnote"))
        {
            var noteId = (string?)endnote.Attribute(W + "id") ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var anchor = new SourceAnchor("docx", "/word/endnotes.xml", [new("endnote_id", noteId)], ordinal);
            var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
            nodes.Add(new(id, NodeKind.Endnote, null, ordinal++, ContentLayer.Body, new TextNodeContent(ParagraphText(endnote, hiddenStyles)), anchor,
                Editability: NodeEditability.Protected, Provenance: [new(EvidenceKind.Native)]));
        }
    }

    // D17: word/comments.xml was never read, so a reviewer comment's text disappeared entirely.
    // w:author is captured as a comment_author extension so readable can label who wrote it.
    private static void AddComments(byte[] bytes, ICollection<DocumentNode> nodes, ref int ordinal, DocxHiddenStyles hiddenStyles)
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
            nodes.Add(new(id, NodeKind.Comment, null, ordinal++, ContentLayer.Metadata, new TextNodeContent(ParagraphText(comment, hiddenStyles)), anchor,
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
            // An equation is a third kind of opaque content, but unlike a table or a text box it
            // has no node of its own: it is projected as one linearized string inside the owning
            // paragraph. The element itself is therefore still yielded (so callers can linearize
            // it in place) while its m:* subtree is not walked — otherwise its inner w:t/m:t would
            // be emitted a second time, next to the linearization.
            if (IsMathRoot(child)) continue;
            foreach (var descendant in RelevantDescendants(child)) yield return descendant;
        }
    }

    private static bool IsMathRoot(XElement element) => element.Name == M + "oMathPara" || element.Name == M + "oMath";

    /// <summary>Counts the outermost equations under <paramref name="element"/>, matching what
    /// <see cref="LinearizeMath"/> converts (an m:oMathPara and its inner m:oMath count once).</summary>
    private static int CountMathRoots(XElement element) =>
        IsMathRoot(element) ? 1 : element.Elements().Sum(CountMathRoots);

    // A block-level equation (m:oMathPara / m:oMath as a sibling of w:p) becomes its own Paragraph
    // node, and its slice is that OMML element itself. There is no w:p to rewrite inside it, so an
    // edit to its linear text can only replace the whole equation with an ordinary paragraph -
    // which restore does, reporting the lost markup as DocxMathReplaced.
    private static void AddMathParagraph(XElement math, string partUri, RawSliceRef? slice, int order,
        ICollection<DocumentNode> nodes, IDictionary<string, RawSliceRef> sliceMap, DocxContentControl? contentControl)
    {
        var anchor = new SourceAnchor("docx", partUri,
            [new("body_child_ordinal", order.ToString(System.Globalization.CultureInfo.InvariantCulture))], order);
        var id = NodeIdGenerator.CreateForSource("docx", DocumentFormatKind.Docx, anchor);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["math_linear"] = JsonSerializer.SerializeToElement(true),
        };
        AddContentControlExtensions(extensions, contentControl);
        nodes.Add(new(id, NodeKind.Paragraph, null, order, ContentLayer.Body, new TextNodeContent(LinearizeMath(math)), anchor, slice,
            Editability: slice is null ? NodeEditability.Protected : NodeEditability.EditableInPlace,
            Provenance: [new(EvidenceKind.Native)], Extensions: extensions));
        if (slice is not null) sliceMap[id] = slice;
    }

    /// <summary>The block kinds <see cref="XmlSliceScanner"/> records at a body position, and so
    /// the only elements an F1 splice can address.</summary>
    private static bool IsSliceableBlock(XElement element) =>
        element.Name == W + "p" || element.Name == W + "tbl" || IsMathRoot(element);

    /// <summary>Inline markup an edit carries over instead of rewriting, paired with the text the
    /// projection rendered it as. Two shapes qualify: an equation, whose OMML cannot be rebuilt from
    /// the single linear string it projects as, and an inline content control / smart tag /
    /// custom-XML element, whose wrapper carries identity (an alias, a tag, a schema binding) that
    /// its text does not. <paramref name="Offset"/> is where its text starts in the paragraph's own
    /// projection and <paramref name="TemplateSpan"/> how many run templates its content
    /// contributed, so the runs after it keep taking their formatting from the right place.</summary>
    private sealed record DocxInlineAnchor(XElement Element, string Text, int Offset, bool IsContainer, int TemplateSpan);

    /// <summary>Inline containers: markup that holds runs without being a run. Their content is
    /// projected as ordinary text (see <see cref="RelevantDescendants"/>), so an edit has to put it
    /// back inside the wrapper rather than beside it.</summary>
    private static bool IsInlineContainer(XElement element) =>
        element.Name == W + "sdt" || element.Name == W + "smartTag" || element.Name == W + "customXml";

    /// <summary>What one paragraph offers a rewrite: the text it projects, the run templates its
    /// character positions map onto, and the inline anchors an edit must route around. Built from
    /// the paragraph's direct children in order, which is the order
    /// <see cref="ParagraphText"/> walks, so anchor offsets index the projected text directly.</summary>
    private sealed record DocxParagraphLayout(string Text, IReadOnlyList<OriginalRunTemplate> Templates,
        IReadOnlyList<DocxInlineAnchor> Anchors);

    private static DocxParagraphLayout ReadParagraphLayout(XElement paragraph)
    {
        var templates = new List<OriginalRunTemplate>();
        var anchors = new List<DocxInlineAnchor>();
        var offset = 0;
        foreach (var child in paragraph.Elements())
        {
            if (OpaqueParagraphContainers.Contains(child.Name)) continue;
            if (IsMathRoot(child))
            {
                // An equation contributes no template: its runs are m:r, not w:r, and nothing the
                // editor wrote is ever formatted from them.
                var linear = LinearizeMath(child);
                anchors.Add(new(child, linear, offset, false, 0));
                offset += linear.Length;
                continue;
            }
            if (IsInlineContainer(child))
            {
                var start = templates.Count;
                foreach (var run in RelevantDescendants(child).Where(element => element.Name == W + "r"))
                    AddRunTemplates(templates, run);
                var contained = InlineText(child);
                anchors.Add(new(child, contained, offset, true, templates.Count - start));
                offset += contained.Length;
                continue;
            }
            if (child.Name == W + "r") AddRunTemplates(templates, child);
            offset += InlineText(child).Length;
        }
        return new(InlineText(paragraph), templates, anchors);
    }

    private static IReadOnlyList<DocxInlineAnchor> ParagraphInlineAnchors(XElement paragraph) =>
        ReadParagraphLayout(paragraph).Anchors;

    /// <summary>Restore-side <see cref="ParagraphText"/>. A slice is reparsed on its own, with no
    /// styles.xml at hand to resolve style-driven hiding, and tracked-change markup is refused
    /// before an edit reaches here, so the two walks agree on every paragraph an edit can touch.</summary>
    private static string InlineText(XContainer container)
    {
        var output = new StringBuilder();
        foreach (var element in RelevantDescendants(container))
        {
            if (IsMathRoot(element)) output.Append(LinearizeMath(element));
            else if (element.Name == W + "t") output.Append(element.Value);
            else if (element.Name is var name && (name == W + "br" || name == W + "cr"))
            {
                if (IsPageBreakElement(element)) continue;
                output.Append('\n');
            }
            else if (element.Name == W + "tab") output.Append('\t');
        }
        return output.ToString();
    }

    /// <summary>Splits a plain-text edit around the anchor texts it still contains, so a text-only
    /// rewrite (a table cell, a paragraph that never projected rich runs) keeps the OMML and the
    /// inline controls the editor left alone.</summary>
    private static IReadOnlyList<TextRun> SplitTextAroundAnchors(string text, IReadOnlyList<DocxInlineAnchor> anchors)
    {
        var runs = new List<TextRun>();
        var cursor = 0;
        foreach (var anchor in anchors)
        {
            if (anchor.Text.Length == 0) continue;
            var found = text.IndexOf(anchor.Text, cursor, StringComparison.Ordinal);
            if (found < 0) continue;
            if (found > cursor) runs.Add(new TextRun(text[cursor..found]));
            runs.Add(new TextRun(anchor.Text));
            cursor = found + anchor.Text.Length;
        }
        if (cursor < text.Length || runs.Count == 0) runs.Add(new TextRun(text[cursor..]));
        return runs;
    }

    // Word stores a native equation as structured OMML whose text leaves are m:t, not w:t, so a
    // w:t-only projection dropped every equation without a trace. This renders the common OMML
    // constructs as linear text (the same convention Word itself uses in its linear input mode):
    // E=mc² becomes "E=mc^2". Anything unrecognised still contributes its text leaves, so an
    // exotic construct degrades to its characters rather than to nothing.
    private static string LinearizeMath(XElement element)
    {
        var name = element.Name;
        if (name == M + "t" || name == W + "t") return element.Value;
        if (name == M + "oMathPara") return string.Join(" ", element.Elements(M + "oMath").Select(LinearizeMath));
        if (name == M + "f") return MathOperand(MathPart(element, "num")) + "/" + MathOperand(MathPart(element, "den"));
        if (name == M + "sSup") return MathPart(element, "e") + "^" + MathGroup(MathPart(element, "sup"));
        if (name == M + "sSub") return MathPart(element, "e") + "_" + MathGroup(MathPart(element, "sub"));
        if (name == M + "sSubSup")
            return MathPart(element, "e") + "_" + MathGroup(MathPart(element, "sub")) + "^" + MathGroup(MathPart(element, "sup"));
        if (name == M + "rad") return MathPart(element, "deg") + "√(" + MathPart(element, "e") + ")";
        if (name == M + "d")
        {
            var properties = element.Element(M + "dPr");
            var open = (string?)properties?.Element(M + "begChr")?.Attribute(M + "val") ?? "(";
            var close = (string?)properties?.Element(M + "endChr")?.Attribute(M + "val") ?? ")";
            var separator = (string?)properties?.Element(M + "sepChr")?.Attribute(M + "val") ?? ",";
            return open + string.Join(separator, element.Elements(M + "e").Select(LinearizeMath)) + close;
        }
        if (name == M + "nary")
        {
            var symbol = (string?)element.Element(M + "naryPr")?.Element(M + "chr")?.Attribute(M + "val") ?? "∫";
            var sub = MathPart(element, "sub");
            var sup = MathPart(element, "sup");
            return symbol + (sub.Length == 0 ? string.Empty : "_" + MathGroup(sub)) +
                (sup.Length == 0 ? string.Empty : "^" + MathGroup(sup)) + "(" + MathPart(element, "e") + ")";
        }
        if (name == M + "func") return MathPart(element, "fName") + "(" + MathPart(element, "e") + ")";
        if (name == M + "eqArr") return string.Join("\n", element.Elements(M + "e").Select(LinearizeMath));
        if (name == M + "m")
            return "[" + string.Join(";", element.Elements(M + "mr")
                .Select(row => string.Join(",", row.Elements(M + "e").Select(LinearizeMath)))) + "]";
        // m:bar, m:acc, m:box and m:groupChr pass their content through unchanged; m:oMath, m:e,
        // m:r and every unknown construct simply concatenate their children. Property elements
        // (anything ending in "Pr") carry formatting, never text, and are skipped.
        return string.Concat(element.Elements()
            .Where(child => !child.Name.LocalName.EndsWith("Pr", StringComparison.Ordinal))
            .Select(LinearizeMath));
    }

    private static string MathPart(XElement element, string localName) =>
        string.Concat(element.Elements(M + localName).Select(LinearizeMath));

    /// <summary>Parenthesizes a sub/superscript that is longer than a single character, so
    /// <c>c^2</c> stays readable while <c>x^(n+1)</c> keeps its grouping unambiguous.</summary>
    private static string MathGroup(string value) => value.Length > 1 ? "(" + value + ")" : value;

    /// <summary>Parenthesizes a fraction operand only when it contains an operator or a space, so
    /// <c>(a+b)/2</c> and <c>x/y</c> read naturally while <c>(x^2)/(n+1)</c> keeps its grouping.</summary>
    private static string MathOperand(string value) =>
        value.Length > 1 && value.Any(character => char.IsWhiteSpace(character) || "+-*/=^_±×÷·".Contains(character))
            ? "(" + value + ")" : value;

    // D18 (coordinator-adjudicated): only w:br type="page" is excluded here — an explicit page
    // break already gets its own PageBreak marker node (see AddParagraph), so it must not also
    // contribute a "\n"/LineBreak to the owning paragraph's own text. w:cr and a plain w:br
    // (no type, or any type other than "page" — e.g. ordinary Shift+Enter line wrapping) are
    // untouched and keep contributing a line break exactly as before.
    private static bool IsPageBreakElement(XElement element) =>
        element.Name == W + "br" && StringComparer.OrdinalIgnoreCase.Equals((string?)element.Attribute(W + "type"), "page");

    private static string? TextBoxShapeId(XElement textBox)
    {
        var owner = textBox.Ancestors().FirstOrDefault(item =>
            item.Name == A + "sp" || item.Name == WPS + "wsp" || item.Name == V + "shape" ||
            item.Name == V + "rect" || item.Name == V + "roundrect");
        return (string?)owner?.Attribute("id") ??
            (string?)owner?.Descendants().FirstOrDefault(item => item.Name.LocalName == "cNvPr")?.Attribute("id");
    }

    private static string TextBoxText(XElement textBox, DocxHiddenStyles hiddenStyles)
    {
        var boxes = textBox.Name == W + "txbxContent"
            ? [textBox]
            : textBox.Descendants(W + "txbxContent").ToArray();
        var paragraphs = boxes.SelectMany(box => box.Elements(W + "p")).ToArray();
        return paragraphs.Length == 0
            ? ParagraphText(textBox, hiddenStyles)
            : string.Join("\n", paragraphs.Select(paragraph => ParagraphText(paragraph, hiddenStyles)));
    }

    private static string ParagraphText(XContainer container, DocxHiddenStyles hiddenStyles)
    {
        var output = new StringBuilder();
        foreach (var element in RelevantDescendants(container))
        {
            if (IsHiddenContentElement(element, hiddenStyles)) continue;
            if (IsMathRoot(element)) output.Append(LinearizeMath(element));
            else if (element.Name == W + "t") output.Append(element.Value);
            else if (element.Name is var name && (name == W + "br" || name == W + "cr"))
            {
                if (IsPageBreakElement(element)) continue;
                output.Append('\n');
            }
            else if (element.Name == W + "tab") output.Append('\t');
        }
        return output.ToString();
    }

    private static string HiddenParagraphText(XContainer container, DocxHiddenStyles hiddenStyles)
    {
        var output = new StringBuilder();
        foreach (var element in RelevantDescendants(container))
        {
            if (element.Name == W + "delText" || element.Name == W + "t" && IsHiddenContentElement(element, hiddenStyles))
                output.Append(element.Value);
        }
        return output.ToString();
    }

    private static bool IsHiddenContentElement(XElement element, DocxHiddenStyles hiddenStyles) =>
        element.AncestorsAndSelf().Any(ancestor => ancestor.Name == W + "del") ||
        element.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor.Name == W + "r") is { } run && IsHiddenRun(run, hiddenStyles);

    private static bool IsHiddenRun(XElement run, DocxHiddenStyles hiddenStyles) =>
        run.Ancestors(W + "del").Any() || hiddenStyles.IsHiddenRun(run);

    private static IReadOnlyList<TextRun> ExtractRichTextRuns(XElement paragraph, IReadOnlyDictionary<string, string> relationships,
        DocxHiddenStyles hiddenStyles)
    {
        var runs = new List<TextRun>();
        foreach (var element in RelevantDescendants(paragraph))
        {
            if (IsMathRoot(element))
            {
                // The linearized equation is one inline unit, marked Code so the readable
                // projection renders ^, _ and / verbatim instead of as Markdown syntax.
                runs.Add(new TextRun(LinearizeMath(element), Code: true));
                continue;
            }
            if (element.Name != W + "r") continue;
            var run = element;
            if (IsHiddenRun(run, hiddenStyles)) continue;
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

    /// <summary>Reads one level's <c>w:vanish</c> as a tri-state: <c>null</c> means "says nothing,
    /// keep inheriting", so a nearer level's explicit <c>w:val="0|false|off"</c> can switch an
    /// inherited hide back off.</summary>
    private static bool? VanishState(XElement? runProperties)
    {
        var vanish = runProperties?.Element(W + "vanish");
        if (vanish is null) return null;
        return !StringComparer.OrdinalIgnoreCase.Equals((string?)vanish.Attribute(W + "val"), "off") && IsEnabled(vanish);
    }

    /// <summary>
    /// Resolves whether a run is hidden, following the OOXML property-inheritance chain rather
    /// than only the run's own <c>w:rPr/w:vanish</c>: <c>w:docDefaults</c>, then the paragraph
    /// style chain (<c>w:basedOn</c>, falling back to the <c>w:default="1"</c> paragraph style),
    /// then the character style chain, then the run's direct properties. Text hidden through a
    /// style is therefore excluded from the visible/sanitized projections exactly like directly
    /// vanished text. A <c>w:vanish</c> inside <c>w:pPr/w:rPr</c> is deliberately ignored: it
    /// formats the paragraph <em>mark</em>, not the paragraph's runs.
    /// </summary>
    private sealed class DocxHiddenStyles
    {
        public static readonly DocxHiddenStyles Empty = new(false,
            new Dictionary<string, bool?>(StringComparer.Ordinal), new Dictionary<string, bool?>(StringComparer.Ordinal), null);

        private readonly bool documentDefault;
        private readonly IReadOnlyDictionary<string, bool?> paragraphStyles;
        private readonly IReadOnlyDictionary<string, bool?> characterStyles;
        private readonly string? defaultParagraphStyleId;
        private readonly bool hasStyleVanish;

        public DocxHiddenStyles(bool documentDefault, IReadOnlyDictionary<string, bool?> paragraphStyles,
            IReadOnlyDictionary<string, bool?> characterStyles, string? defaultParagraphStyleId)
        {
            this.documentDefault = documentDefault;
            this.paragraphStyles = paragraphStyles;
            this.characterStyles = characterStyles;
            this.defaultParagraphStyleId = defaultParagraphStyleId;
            hasStyleVanish = documentDefault || paragraphStyles.Values.Any(value => value is not null) ||
                characterStyles.Values.Any(value => value is not null);
        }

        public bool IsHiddenRun(XElement run)
        {
            var properties = run.Element(W + "rPr");
            // Overwhelmingly the common case: no style anywhere in the document mentions w:vanish,
            // so only the run's own properties can hide it and no ancestor walk is needed.
            if (!hasStyleVanish) return VanishState(properties) ?? false;
            var effective = documentDefault;
            var paragraphStyleId = (string?)run.Ancestors(W + "p").FirstOrDefault()?.Element(W + "pPr")?
                .Element(W + "pStyle")?.Attribute(W + "val") ?? defaultParagraphStyleId;
            if (paragraphStyleId is not null && paragraphStyles.TryGetValue(paragraphStyleId, out var fromParagraph) &&
                fromParagraph is { } paragraphVanish) effective = paragraphVanish;
            var characterStyleId = (string?)properties?.Element(W + "rStyle")?.Attribute(W + "val");
            if (characterStyleId is not null && characterStyles.TryGetValue(characterStyleId, out var fromCharacter) &&
                fromCharacter is { } characterVanish) effective = characterVanish;
            return VanishState(properties) ?? effective;
        }
    }

    /// <summary>Guards against a hand-written or corrupt styles.xml whose w:basedOn chain is
    /// cyclic or absurdly deep; a chain longer than this simply stops inheriting.</summary>
    private const int MaxStyleChainDepth = 32;

    private static DocxHiddenStyles ReadHiddenStyles(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.GetEntry("word/styles.xml") is not { } entry) return DocxHiddenStyles.Empty;
        var root = SafeXml.LoadDocument(ReadEntryAsync(entry, cancellationToken).GetAwaiter().GetResult()).Root;
        if (root is null) return DocxHiddenStyles.Empty;
        var documentDefault = VanishState(root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr")) ?? false;
        var declared = new Dictionary<string, (string? Type, string? BasedOn, bool? Vanish)>(StringComparer.Ordinal);
        string? defaultParagraphStyleId = null;
        foreach (var style in root.Elements(W + "style"))
        {
            if ((string?)style.Attribute(W + "styleId") is not { } styleId) continue;
            var type = (string?)style.Attribute(W + "type");
            declared[styleId] = (type, (string?)style.Element(W + "basedOn")?.Attribute(W + "val"), VanishState(style.Element(W + "rPr")));
            if (defaultParagraphStyleId is null && StringComparer.OrdinalIgnoreCase.Equals(type, "paragraph") &&
                (string?)style.Attribute(W + "default") is { } flag &&
                !StringComparer.OrdinalIgnoreCase.Equals(flag, "0") && !StringComparer.OrdinalIgnoreCase.Equals(flag, "false") &&
                !StringComparer.OrdinalIgnoreCase.Equals(flag, "off")) defaultParagraphStyleId = styleId;
        }

        var resolved = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var paragraphStyles = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var characterStyles = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var (styleId, style) in declared)
        {
            var value = Resolve(styleId, new HashSet<string>(StringComparer.Ordinal), 0);
            if (StringComparer.OrdinalIgnoreCase.Equals(style.Type, "character")) characterStyles[styleId] = value;
            else if (style.Type is null || StringComparer.OrdinalIgnoreCase.Equals(style.Type, "paragraph")) paragraphStyles[styleId] = value;
        }
        return new DocxHiddenStyles(documentDefault, paragraphStyles, characterStyles, defaultParagraphStyleId);

        // The nearest declaration in a w:basedOn chain wins, so a style that says nothing inherits
        // its base's answer and one that says w:val="0" cancels it.
        bool? Resolve(string styleId, HashSet<string> visiting, int depth)
        {
            if (resolved.TryGetValue(styleId, out var cached)) return cached;
            if (depth >= MaxStyleChainDepth || !visiting.Add(styleId) || !declared.TryGetValue(styleId, out var style)) return null;
            var value = style.Vanish ?? (style.BasedOn is null ? null : Resolve(style.BasedOn, visiting, depth + 1));
            visiting.Remove(styleId);
            resolved[styleId] = value;
            return value;
        }
    }

    private static bool IsMonospaceFont(string? fontName) => fontName is not null &&
        (fontName.Contains("consolas", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("courier", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("menlo", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("monaco", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("source code", StringComparison.OrdinalIgnoreCase));
    private static DocxRunCharacterMap BuildRunMap(string nodeId, XElement paragraph, DocxHiddenStyles hiddenStyles)
    {
        var spans = new List<RunCharacterSpan>();
        var start = 0;
        var ordinal = 0;
        // An equation contributes its linearization to the paragraph's character stream (see
        // ParagraphText), so it needs a span of its own: omitting it left every later run mapped to
        // an offset short by the equation's length.
        foreach (var run in RelevantDescendants(paragraph).Where(element => element.Name == W + "r" || IsMathRoot(element)))
        {
            if (IsHiddenContentElement(run, hiddenStyles)) continue;
            var text = IsMathRoot(run) ? LinearizeMath(run)
                : string.Concat(run.Elements().Select(element => element.Name == W + "t" ? element.Value :
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

    /// <summary>What an edited block could not carry back: equations whose OMML the edit typed over,
    /// and inline containers whose wrapper the edit dissolved.</summary>
    private readonly record struct DocxParagraphEditResult(int ReplacedEquations, int UnwrappedContainers)
    {
        public static DocxParagraphEditResult operator +(DocxParagraphEditResult left, DocxParagraphEditResult right) =>
            new(left.ReplacedEquations + right.ReplacedEquations, left.UnwrappedContainers + right.UnwrappedContainers);
    }

    /// <summary>Rewrites one block slice from its edited content, reporting through
    /// <paramref name="edits"/> the inline markup the edit did not carry back.</summary>
    private static byte[] ReplaceParagraphContent(byte[] originalSlice, NodeContent content, out DocxParagraphEditResult edits)
    {
        var block = LoadSliceElement(originalSlice);
        // A block-level equation's slice *is* the OMML: there is no w:p around it to rewrite, so an
        // edited linear form can only become an ordinary paragraph carrying that text.
        if (IsMathRoot(block))
        {
            edits = new(1, 0);
            return Serialize(new XElement(W + "p", CreateRichRun(new TextRun(PlainTextOf(content)))));
        }
        if (content is RichTextNodeContent rich)
        {
            RejectUnsupportedRichTextParagraph(block);
            edits = ReplaceParagraphRuns(block, rich.Runs);
            return Serialize(block);
        }
        if (content is not TextNodeContent plain)
            throw new InvalidDataException("An edited DOCX paragraph must retain text or supported rich text content.");
        RejectUnsupportedPlainTextParagraph(block);
        var anchors = ParagraphInlineAnchors(block);
        // A plain-text edit carries an equation or a content control only as the text it projected,
        // so the text is split around the projections it still contains and the run rewrite matches
        // them back onto the markup they came from.
        if (anchors.Count > 0)
        {
            edits = ReplaceParagraphRuns(block, SplitTextAroundAnchors(plain.Text, anchors));
            return Serialize(block);
        }
        edits = default;
        ReplaceParagraphText(block, plain.Text);
        return Serialize(block);
    }

    private static string PlainTextOf(NodeContent content) => content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => throw new InvalidDataException("An edited DOCX paragraph must retain text or supported rich text content.")
    };

    private static byte[] Serialize(XElement element) => SafeXml.Utf8(element.ToString(SaveOptions.DisableFormatting));

    private static void ReplaceParagraphText(XElement paragraph, string text)
    {
        // RelevantDescendants, not Descendants: a text box in this paragraph projects as its own
        // TextBox node and its characters were never part of this paragraph's text, so writing the
        // edit across them would blank a box on every edit to the paragraph around it.
        var textElements = RelevantDescendants(paragraph).Where(element => element.Name == W + "t").ToArray();
        if (textElements.Length == 0) paragraph.Add(new XElement(W + "r", new XElement(W + "t", text)));
        else WriteTextAcross(textElements, text);
    }

    /// <summary>Preserve existing run/text boundaries where possible. This is the minimal
    /// character-offset-map policy: original character spans remain assigned to their original
    /// formatting runs; growth is assigned to the final run.</summary>
    private static void WriteTextAcross(IReadOnlyList<XElement> textElements, string text)
    {
        var originalLengths = textElements.Select(element => element.Value.Length).ToArray();
        var offset = 0;
        for (var index = 0; index < textElements.Count; index++)
        {
            var length = index == textElements.Count - 1
                ? text.Length - offset
                : Math.Min(originalLengths[index], Math.Max(0, text.Length - offset));
            var replacement = text.Substring(offset, length);
            textElements[index].Value = replacement;
            if (!StringComparer.Ordinal.Equals(replacement, replacement.Trim())) textElements[index].SetAttributeValue(XNamespace.Xml + "space", "preserve");
            offset += length;
        }
    }

    /// <summary>Where one inline anchor ends up in the edited text. <c>Start &lt; 0</c> means the
    /// edit typed over it and the markup is dropped; a non-null <see cref="InnerText"/> means the
    /// wrapper is kept and only its own content was rewritten.</summary>
    private sealed record DocxAnchorPlacement(DocxInlineAnchor Anchor, int Start, int Length, string? InnerText)
    {
        public int End => Start + Length;
    }

    /// <summary>Locates every inline anchor inside the edited text, in order. Neither OMML nor an
    /// inline control can be rebuilt from the text it projects as, so each is looked for verbatim in
    /// what the editor wrote: found, the original markup is simply left where it stands. A control
    /// the search missed had its own text edited - if everything around it came back untouched the
    /// difference is its new content and goes back inside the wrapper; otherwise the edit ran
    /// across the wrapper's boundary and the wrapper cannot survive it.</summary>
    private static IReadOnlyList<DocxAnchorPlacement> PlaceInlineAnchors(DocxParagraphLayout layout, string combined)
    {
        var placements = new List<DocxAnchorPlacement>();
        var cursor = 0;
        foreach (var anchor in layout.Anchors)
        {
            if (anchor.Text.Length == 0) { placements.Add(new(anchor, cursor, 0, null)); continue; }
            var found = cursor <= combined.Length ? combined.IndexOf(anchor.Text, cursor, StringComparison.Ordinal) : -1;
            if (found >= 0)
            {
                placements.Add(new(anchor, found, anchor.Text.Length, null));
                cursor = found + anchor.Text.Length;
                continue;
            }
            if (anchor.IsContainer && CanRewriteContainerText(anchor.Element))
            {
                var prefix = layout.Text[..Math.Min(anchor.Offset, layout.Text.Length)];
                var suffix = layout.Text[Math.Min(anchor.Offset + anchor.Text.Length, layout.Text.Length)..];
                if (prefix.Length >= cursor && combined.Length >= prefix.Length + suffix.Length &&
                    combined.StartsWith(prefix, StringComparison.Ordinal) && combined.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var inner = combined[prefix.Length..(combined.Length - suffix.Length)];
                    placements.Add(new(anchor, prefix.Length, inner.Length, inner));
                    cursor = prefix.Length + inner.Length;
                    continue;
                }
            }
            placements.Add(new(anchor, -1, 0, null));
        }
        return placements;
    }

    /// <summary>A control whose text comes from w:t alone can take a new string back; one holding a
    /// break, a tab, or an equation cannot, because the string carries no record of where they sat.</summary>
    private static bool CanRewriteContainerText(XElement container) =>
        RelevantDescendants(container).All(element =>
            element.Name != W + "br" && element.Name != W + "cr" && element.Name != W + "tab" && !IsMathRoot(element));

    private static void WriteContainerText(XElement container, string text)
    {
        var textElements = RelevantDescendants(container).Where(element => element.Name == W + "t").ToArray();
        if (textElements.Length == 0)
        {
            (container.Element(W + "sdtContent") ?? container).Add(new XElement(W + "r", new XElement(W + "t", text)));
            return;
        }
        WriteTextAcross(textElements, text);
    }

    /// <summary>Rewrites a paragraph's direct w:r children from <paramref name="runs"/>, keeping in
    /// place every equation and inline control those runs still spell out. Returns the markup the
    /// edit could not carry back.</summary>
    private static DocxParagraphEditResult ReplaceParagraphRuns(XElement paragraph, IReadOnlyList<TextRun> runs)
    {
        var layout = ReadParagraphLayout(paragraph);
        var templates = layout.Templates;
        var combined = string.Concat(runs.Select(run => run.Text));
        var placements = PlaceInlineAnchors(layout, combined);
        var lost = new DocxParagraphEditResult(
            placements.Count(item => item.Start < 0 && !item.Anchor.IsContainer),
            placements.Count(item => item.Start < 0 && item.Anchor.IsContainer));

        // Paragraph properties stay untouched.  We replace only the direct w:r children,
        // preserving surrounding bookmark/comment anchors and the paragraph's layout,
        // numbering, alignment, and style properties.  Each replacement run starts from
        // the original run properties at the same character position, so unprojected
        // typography (font family, size, color, language, kerning, etc.) survives an edit.
        // An anchor is not a direct w:r, so it stays exactly where it was and the rewritten runs
        // are threaded around it - which is what keeps "before <anchor> after" in its own order.
        paragraph.Elements(W + "r").Remove();
        foreach (var placement in placements)
        {
            if (placement.Start < 0) placement.Anchor.Element.Remove();
            else if (placement.InnerText is not null) WriteContainerText(placement.Anchor.Element, placement.InnerText);
        }
        var kept = placements.Where(item => item.Start >= 0).ToArray();

        var insertionPoint = paragraph.Element(W + "pPr");
        var templateIndex = 0;
        var keptIndex = 0;
        var position = 0;
        foreach (var run in runs)
        {
            var length = run.Text.Length;
            var consumed = 0;
            while (true)
            {
                var absolute = position + consumed;
                if (keptIndex < kept.Length && kept[keptIndex].Start == absolute && kept[keptIndex].Length == 0)
                { PlaceAnchor(kept[keptIndex++]); continue; }
                if (consumed >= length) break;
                if (keptIndex < kept.Length && absolute >= kept[keptIndex].Start && absolute < kept[keptIndex].End)
                {
                    // These characters are the anchor's own projection: the markup already spells
                    // them out, so no run is written for them.
                    consumed += Math.Min(length - consumed, kept[keptIndex].End - absolute);
                    if (position + consumed >= kept[keptIndex].End) PlaceAnchor(kept[keptIndex++]);
                    continue;
                }
                var limit = keptIndex < kept.Length ? Math.Min(length, kept[keptIndex].Start - position) : length;
                if (limit <= consumed) break;
                EmitRun(run with { Text = run.Text[consumed..limit] });
                consumed = limit;
            }
            position += length;
        }
        while (keptIndex < kept.Length) PlaceAnchor(kept[keptIndex++]);
        return lost;

        void PlaceAnchor(DocxAnchorPlacement placement)
        {
            insertionPoint = placement.Anchor.Element;
            templateIndex += placement.Anchor.TemplateSpan;
        }

        void EmitRun(TextRun run)
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
                    var span = index == groupEnd - 1
                        ? run.Text.Length - textOffset
                        : Math.Min(template.Length, run.Text.Length - textOffset);
                    Insert(CreateRichRun(run with { Text = run.Text.Substring(textOffset, span) }, template.Properties));
                    textOffset += span;
                }
                if (textOffset < run.Text.Length)
                    Insert(CreateRichRun(run with { Text = run.Text[textOffset..] }, templates.Count == 0 ? null : templates[Math.Min(templateIndex, templates.Count - 1)].Properties));
                templateIndex = groupEnd;
                return;
            }
            var specialTemplate = templateIndex < templates.Count ? templates[templateIndex] : templates.LastOrDefault();
            Insert(CreateRichRun(run, specialTemplate?.Properties));
            if (templateIndex < templates.Count) templateIndex++;
        }

        void Insert(XElement replacement)
        {
            if (insertionPoint is null) paragraph.AddFirst(replacement);
            else insertionPoint.AddAfterSelf(replacement);
            insertionPoint = replacement;
        }
    }

    private sealed record OriginalRunTemplate(int Length, XElement? Properties, string? StyleId, bool Bold, bool Italic, bool Underline, bool Strike, bool Code, TextRunKind Kind);

    /// <summary>Appends the templates one w:r contributes: one per projected character span, so a
    /// character offset in the edited text maps back onto the formatting it originally carried.</summary>
    private static void AddRunTemplates(ICollection<OriginalRunTemplate> result, XElement run)
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

    private static int FindMatchingTemplate(IReadOnlyList<OriginalRunTemplate> templates, int start, TextRun run)
    {
        for (var index = start; index < templates.Count; index++)
            if (Matches(templates[index], run)) return index;
        return -1;
    }

    private static bool Matches(OriginalRunTemplate template, TextRun run) =>
        template.Kind == run.Kind && template.StyleId == run.StyleId && template.Bold == run.Bold && template.Italic == run.Italic &&
        template.Underline == run.Underline && template.Strike == run.Strike && template.Code == run.Code;

    /// <summary>Reparses one recorded slice. The wrapper carries every prefix a body block or a
    /// text box may use without redeclaring - m:, for an equation, above all, and the drawing and
    /// VML prefixes a shape sits in - since Word declares each of them once on w:document and never
    /// again on the block that uses it. A prefix the fragment does not use costs nothing: LINQ to
    /// XML only writes back the declarations the serialized subtree actually needs.</summary>
    private static XElement LoadSliceElement(byte[] originalSlice)
    {
        var fragment = Encoding.UTF8.GetString(originalSlice);
        var wrapper = $"<drmd:root xmlns:drmd=\"urn:drmd\" xmlns:w=\"{W}\" xmlns:w14=\"{W14}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\" xmlns:wp=\"{WP}\" xmlns:m=\"{M}\" " +
            $"xmlns:v=\"{V}\" xmlns:wps=\"{WPS}\" xmlns:wpg=\"{WPG}\" xmlns:mc=\"{MC}\" " +
            "xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w10=\"urn:schemas-microsoft-com:office:word\">" +
            $"{fragment}</drmd:root>";
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

    private static bool IsTitleStyle(string? style) =>
        style is not null && style.Equals("Title", StringComparison.OrdinalIgnoreCase);

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

    private static int ListLevel(XElement paragraph, string? style)
    {
        var ilvl = paragraph.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val")?.Value;
        if (int.TryParse(ilvl, out var level)) return Math.Max(0, level);
        // Word style-only lists such as "List Bullet 2" do not carry w:ilvl. The
        // trailing style number is the conventional nesting level (1 is top-level).
        var match = System.Text.RegularExpressions.Regex.Match(style ?? string.Empty,
            @"list(?:bullet|number|paragraph)\s*(?<level>[1-9][0-9]*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["level"].Value, out var styleLevel) ? Math.Max(0, styleLevel - 1) : 0;
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

    private static byte[] ReplaceTableCells(byte[] originalSlice, NodeContent content, out DocxParagraphEditResult edits)
    {
        if (content is not TableNodeContent edited) throw new InvalidDataException("An edited DOCX table must retain table cell content.");
        var table = LoadSliceElement(originalSlice);
        if (table.Descendants(W + "fldChar").Any()) throw new InvalidDataException("A field boundary cannot be edited in a table.");
        var rows = table.Elements(W + "tr").ToArray();
        if (rows.Length != edited.Rows.Count) throw new InvalidDataException("DOCX table row count changed; F1 table structure edits are not supported.");
        edits = default;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var cells = rows[rowIndex].Elements(W + "tc").ToArray();
            if (cells.Length != edited.Rows[rowIndex].Count) throw new InvalidDataException("DOCX table cell count changed; F1 table structure edits are not supported.");
            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
                edits += ReplaceCellText(cells[cellIndex], edited.Rows[rowIndex][cellIndex].Text);
        }
        return Serialize(table);
    }

    // Writes a cell's text back paragraph by paragraph. Two things matter here: the cell's own
    // paragraphs are the only targets — a nested table's w:t sits in the same subtree but belongs
    // to its own Table node, and blanking it (the previous cell.Descendants(w:t) sweep did) would
    // erase the nested table on any edit to the host table — and "\n" is the cell's paragraph
    // separator (see CellText), so each segment goes back into the paragraph it came from.
    private static DocxParagraphEditResult ReplaceCellText(XElement cell, string text)
    {
        var paragraphs = CellBlocks(cell).Where(block => block.Name == W + "p").ToArray();
        if (paragraphs.Length == 0)
        {
            var paragraph = new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", text)));
            cell.Add(paragraph);
            return default;
        }
        var segments = text.Split('\n');
        if (segments.Length == paragraphs.Length)
        {
            var replaced = default(DocxParagraphEditResult);
            for (var index = 0; index < paragraphs.Length; index++) replaced += ReplaceCellParagraphText(paragraphs[index], segments[index]);
            return replaced;
        }
        // A cell whose paragraph count changed cannot be mapped back unambiguously. Splitting or
        // merging paragraphs is refused rather than guessed at, matching the row/cell count checks.
        if (segments.Length > 1)
            throw new InvalidDataException("DOCX table cell paragraph count changed; F1 cell paragraph structure edits are not supported.");
        return paragraphs.Skip(1).Aggregate(ReplaceCellParagraphText(paragraphs[0], text),
            (replaced, paragraph) => replaced + ReplaceCellParagraphText(paragraph, string.Empty));
    }

    private static DocxParagraphEditResult ReplaceCellParagraphText(XElement paragraph, string text)
    {
        // A cell paragraph holding an equation or an inline content control goes through the
        // anchor-preserving run rewrite, so untouched markup survives an edit to a neighbouring cell
        // instead of being duplicated as the text it already projected. ReplaceTableCells rewrites
        // every cell on every table edit, so this runs for unchanged cells too.
        var anchors = ParagraphInlineAnchors(paragraph);
        if (anchors.Count > 0) return ReplaceParagraphRuns(paragraph, SplitTextAroundAnchors(text, anchors));
        var textElements = paragraph.Descendants(W + "t").ToArray();
        if (textElements.Length == 0)
        {
            if (text.Length == 0) return default;
            paragraph.Add(new XElement(W + "r", new XElement(W + "t", text)));
            return default;
        }
        textElements[0].Value = text;
        if (!StringComparer.Ordinal.Equals(text, text.Trim())) textElements[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var element in textElements.Skip(1)) element.Value = string.Empty;
        return default;
    }

    /// <summary>Rewrites one w:txbxContent slice from the edited text of its TextBox node. The
    /// projection joined the box's own w:p children with "\n" (see <see cref="TextBoxText"/>), so the
    /// edit is split on "\n" and each segment goes back into the paragraph it came from; a segment
    /// with no paragraph left duplicates the last one - its w:pPr and the first w:rPr of its runs, so
    /// a new line keeps the box's formatting - and a paragraph with no segment left is dropped. An
    /// empty edit leaves one empty paragraph: a text box with no w:p at all is not valid
    /// WordprocessingML.</summary>
    private static byte[] ReplaceTextBoxContent(byte[] originalSlice, NodeContent content, out DocxParagraphEditResult edits)
    {
        var box = LoadSliceElement(originalSlice);
        if (box.Name != W + "txbxContent")
            throw new InvalidDataException("An edited DOCX text box must be anchored on its w:txbxContent element.");
        if (box.Descendants(W + "fldChar").Any() || box.Descendants(W + "instrText").Any())
            throw new InvalidDataException("A field boundary cannot be edited in a text box.");
        var paragraphs = box.Elements(W + "p").ToList();
        // A break inside a box paragraph projects as the same "\n" that separates the box's
        // paragraphs, so an edited string no longer says which of the two a "\n" now means.
        if (paragraphs.Any(paragraph => InlineText(paragraph).Contains('\n', StringComparison.Ordinal)))
            throw new InvalidDataException("A DOCX text box paragraph holding a line break cannot be rewritten from its linear text.");
        var segments = PlainTextOf(content).Split('\n');
        edits = default;
        if (paragraphs.Count == 0)
        {
            foreach (var segment in segments) box.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", segment))));
            return Serialize(box);
        }
        while (paragraphs.Count > segments.Length)
        {
            paragraphs[^1].Remove();
            paragraphs.RemoveAt(paragraphs.Count - 1);
        }
        while (paragraphs.Count < segments.Length)
        {
            var template = paragraphs[^1];
            var added = new XElement(W + "p");
            if (template.Element(W + "pPr") is { } paragraphProperties) added.Add(new XElement(paragraphProperties));
            var runProperties = template.Elements(W + "r").Select(run => run.Element(W + "rPr")).FirstOrDefault(item => item is not null);
            added.Add(new XElement(W + "r", runProperties is null ? null : new XElement(runProperties), new XElement(W + "t")));
            template.AddAfterSelf(added);
            paragraphs.Add(added);
        }
        for (var index = 0; index < segments.Length; index++) edits += ReplaceTextBoxParagraphText(paragraphs[index], segments[index]);
        return Serialize(box);
    }

    /// <summary>Writes one segment back into one text-box paragraph, keeping every anchor the
    /// projection only spelled out - an equation, an inline content control - where it stood. The
    /// w:t sweep is <see cref="RelevantDescendants"/>, the walk the projection itself used, so a
    /// table or a further text box inside this paragraph keeps its own text.</summary>
    private static DocxParagraphEditResult ReplaceTextBoxParagraphText(XElement paragraph, string text)
    {
        var anchors = ParagraphInlineAnchors(paragraph);
        if (anchors.Count > 0) return ReplaceParagraphRuns(paragraph, SplitTextAroundAnchors(text, anchors));
        var textElements = RelevantDescendants(paragraph).Where(element => element.Name == W + "t").ToArray();
        if (textElements.Length == 0)
        {
            if (text.Length == 0) return default;
            paragraph.Add(new XElement(W + "r", new XElement(W + "t", text)));
            return default;
        }
        WriteTextAcross(textElements, text);
        return default;
    }

    /// <summary>Whether two slices claim any of the same bytes - a text box and the block it sits
    /// in, above all, which no single ordered splice can rewrite.</summary>
    private static bool Overlaps(RawSliceRef left, RawSliceRef right) =>
        StringComparer.Ordinal.Equals(left.PartUri, right.PartUri) &&
        left.StartOffset < right.EndOffset && right.StartOffset < left.EndOffset;

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

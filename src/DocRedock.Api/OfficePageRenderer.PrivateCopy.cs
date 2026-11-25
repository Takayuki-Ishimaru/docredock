using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DocRedock.Api;

public partial class LibreOfficePageRenderer
{
    private const long MaxEntryBytes = 32L * 1024 * 1024;
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const string DecompressionBudgetMessage = "Office rendering package exceeds the decompression budget.";

    // Parts that only carry macros or the instructions for fetching external data. The page shows the saved
    // values, so the private copy drops these parts with their relationships and content type overrides.
    private static readonly HashSet<string> RemovedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.ms-office.vbaProject", "application/vnd.ms-office.vbaProjectSignature",
        "application/vnd.ms-office.vbaProjectSignatureAgile", "application/vnd.ms-office.vbaProjectSignatureV3",
        "application/vnd.ms-word.vbaData+xml", "application/vnd.ms-word.keyMapCustomizations+xml",
        "application/vnd.ms-word.attachedToolbars",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.connections+xml",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.queryTable+xml",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml",
    };
    // Relationship types end the same way in the Transitional, Strict and Microsoft namespaces.
    private static readonly string[] RemovedRelationshipTypes =
        ["/vbaProject", "/wordVbaData", "/keyMapCustomizations", "/attachedToolbars", "/connections", "/queryTable", "/externalLink"];
    private static readonly Dictionary<string, string> MacroFreeMainTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.ms-word.document.macroEnabled.main+xml"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        ["application/vnd.ms-excel.sheet.macroEnabled.main+xml"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
        ["application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
    };
    // Word fields whose result comes from another file, application or database.
    private static readonly HashSet<string> ExternalFieldTypes = new(StringComparer.OrdinalIgnoreCase)
        { "INCLUDEPICTURE", "INCLUDETEXT", "INCLUDE", "IMPORT", "LINK", "DDE", "DDEAUTO", "RD", "DATABASE" };
    private static readonly string[] RelationshipNamespaces =
        ["http://schemas.openxmlformats.org/officeDocument/2006/relationships", "http://purl.oclc.org/ooxml/officeDocument/relationships"];
    // [1]Sheet1!A1 or '[1]Sheet 1'!A1, but not the structured reference Table1[1].
    private static readonly Regex ExternalWorkbookReference = new(@"(?<![\w\]])'?\[\d+\]", RegexOptions.CultureInvariant);

    private sealed class CopyBudget { public long Total; }

    /// <summary>Counts the bytes an entry really decompresses to; the sizes a package declares are not trusted.</summary>
    private sealed class BoundedEntryStream(Stream inner, CopyBudget budget) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytes = inner.Read(buffer, offset, count);
            read += bytes; budget.Total += bytes;
            if (read > MaxEntryBytes || budget.Total > MaxPackageBytes) throw new InvalidDataException(DecompressionBudgetMessage);
            return bytes;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Reads a part, or returns null when it really holds more than the limit.</summary>
    private static byte[]? ReadAtMost(ZipArchiveEntry entry, int limit)
    {
        using var stream = entry.Open(); using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        for (int count; (count = stream.Read(buffer, 0, buffer.Length)) > 0;)
        {
            if (bytes.Length + count > limit) return null;
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private sealed record PrivateCopyPlan(HashSet<string> RemovedParts, Dictionary<string, HashSet<string>> RemovedRelationshipIds, IReadOnlyList<string> KeptNames)
    {
        public bool Removes(string name) => RemovedParts.Contains(name) || RelationshipOwner(name) is { Length: > 0 } owner && RemovedParts.Contains(owner);
    }

    private static XmlDocument LoadPackageXml(ZipArchiveEntry entry, CopyBudget budget, bool preserveWhitespace)
    {
        var xml = new XmlDocument { XmlResolver = null, PreserveWhitespace = preserveWhitespace };
        using var stream = new BoundedEntryStream(entry.Open(), budget);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        xml.Load(reader);
        return xml;
    }

    /// <summary>word/_rels/document.xml.rels belongs to word/document.xml; _rels/.rels to the package ("").</summary>
    private static string? RelationshipOwner(string name)
    {
        if (!name.EndsWith(".rels", StringComparison.Ordinal)) return null;
        var slash = name.LastIndexOf('/');
        var folder = slash < 0 ? "" : name[..slash];
        if (folder != "_rels" && !folder.EndsWith("/_rels", StringComparison.Ordinal)) return null;
        return folder[..^5] + name[(slash + 1)..^5];
    }

    private static string? ResolveTarget(string owner, string target) =>
        Uri.TryCreate("http://package.invalid/" + owner, UriKind.Absolute, out var origin) && Uri.TryCreate(origin, target, out var uri) &&
        uri.Scheme == "http" && uri.Host == "package.invalid" ? Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/') : null;

    private static bool IsRemovedRelationshipType(string type) =>
        type.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || RemovedRelationshipTypes.Any(suffix => type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static bool RemovesRelationship(XmlElement relationship, string owner, HashSet<string> removedParts)
    {
        var type = relationship.GetAttribute("Type");
        if (relationship.GetAttribute("TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase)) return !type.EndsWith("/hyperlink", StringComparison.Ordinal);
        // An internal relationship must name a part of this package that the copy keeps.
        return IsRemovedRelationshipType(type) || ResolveTarget(owner, relationship.GetAttribute("Target")) is not { } target || removedParts.Contains(target);
    }

    /// <summary>Checks the entry names and decides which parts and relationships the private copy leaves out.</summary>
    private static PrivateCopyPlan PlanPrivateCopy(ZipArchive input, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in input.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name[0] is '/' or '\\' || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/', '\\').Contains("..") || !names.Add(name))
                throw new InvalidDataException("Office rendering package has an unsafe or duplicate entry name.");
            if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || name.Equals("xl/connections.xml", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("xl/queryTables/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
        }
        var budget = new CopyBudget();
        if (input.GetEntry("[Content_Types].xml") is { } types)
            foreach (var item in LoadPackageXml(types, budget, false).DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e =>
                         e.LocalName == "Override" && RemovedContentTypes.Contains(e.GetAttribute("ContentType"))))
                removed.Add(Uri.UnescapeDataString(item.GetAttribute("PartName")).TrimStart('/'));
        var relationshipParts = input.Entries.Where(e => RelationshipOwner(e.FullName) is not null).ToArray();
        foreach (var entry in relationshipParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = RelationshipOwner(entry.FullName)!;
            foreach (var relationship in LoadPackageXml(entry, budget, false).DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e =>
                         !e.GetAttribute("TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase) && IsRemovedRelationshipType(e.GetAttribute("Type"))))
                if (ResolveTarget(owner, relationship.GetAttribute("Target")) is { Length: > 0 } target) removed.Add(target);
        }
        // The relationship ids that disappear are needed before their owning part is written, wherever it is in the archive.
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        budget = new CopyBudget();
        foreach (var entry in relationshipParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = RelationshipOwner(entry.FullName)!;
            if (removed.Contains(owner)) continue;
            var gone = LoadPackageXml(entry, budget, false).DocumentElement!.ChildNodes.OfType<XmlElement>()
                .Where(e => RemovesRelationship(e, owner, removed)).Select(e => e.GetAttribute("Id")).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (gone.Count > 0) ids[owner] = gone;
        }
        var plan = new PrivateCopyPlan(removed, ids, []);
        return plan with { KeptNames = input.Entries.Select(e => e.FullName).Where(name => !plan.Removes(name)).ToArray() };
    }

    /// <summary>Drops the overrides of removed parts and declares a macro-enabled main part as its macro-free type.</summary>
    private static void MakeContentTypesConsistent(XmlDocument types, PrivateCopyPlan plan)
    {
        foreach (var item in types.DocumentElement!.ChildNodes.OfType<XmlElement>().ToArray())
        {
            var type = item.GetAttribute("ContentType");
            if (item.LocalName == "Override")
            {
                if (plan.RemovedParts.Contains(Uri.UnescapeDataString(item.GetAttribute("PartName")).TrimStart('/'))) types.DocumentElement.RemoveChild(item);
                else if (MacroFreeMainTypes.TryGetValue(type, out var plain)) item.SetAttribute("ContentType", plain);
            }
            else if (item.LocalName == "Default" && RemovedContentTypes.Contains(type) &&
                     !plan.KeptNames.Any(name => name.EndsWith("." + item.GetAttribute("Extension"), StringComparison.OrdinalIgnoreCase)))
                types.DocumentElement.RemoveChild(item);
        }
    }

    /// <summary>Removes what pointed at a removed relationship, so that no reference in the part dangles.</summary>
    private static void RemoveDanglingReferences(XmlDocument xml, HashSet<string> removedIds)
    {
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>().ToArray())
        {
            var dangling = element.Attributes.OfType<XmlAttribute>().Where(a => removedIds.Contains(a.Value) &&
                (RelationshipNamespaces.Contains(a.NamespaceURI) || a.LocalName == "relid" && a.NamespaceURI == "urn:schemas-microsoft-com:office:office")).ToArray();
            if (dangling.Length == 0) continue;
            foreach (var attribute in dangling) element.Attributes.Remove(attribute);
            // An element that only named the relationship (externalReference, subDoc, attachedTemplate, a linked blip) means nothing without it.
            if (element.ParentNode is { } parent && element != xml.DocumentElement && (element.LocalName == "externalData" ||
                !element.ChildNodes.OfType<XmlElement>().Any() && !element.Attributes.OfType<XmlAttribute>().Any(a => a.Prefix != "xmlns" && a.Name != "xmlns")))
                parent.RemoveChild(element);
        }
    }

    /// <summary>The workbook lists its external links and may name ranges in them; both go with the link parts.</summary>
    private static void RemoveExternalWorkbookReferences(XmlDocument workbook)
    {
        foreach (var list in workbook.DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName is "externalReferences" or "definedNames").ToArray())
        {
            if (list.LocalName == "definedNames")
                foreach (var name in list.ChildNodes.OfType<XmlElement>().Where(e => ExternalWorkbookReference.IsMatch(e.InnerText)).ToArray())
                    list.RemoveChild(name);
            if (list.LocalName == "externalReferences" || !list.ChildNodes.OfType<XmlElement>().Any()) workbook.DocumentElement.RemoveChild(list);
        }
    }

    /// <summary>A table or pivot cache filled by a query keeps its saved rows but no longer names the connection or asks for a refresh.</summary>
    private static void DetachSpreadsheetQueries(XmlDocument xml)
    {
        var root = xml.DocumentElement!;
        if (root.LocalName == "table")
        {
            if (root.GetAttribute("tableType") == "queryTable") root.RemoveAttribute("tableType");
            root.RemoveAttribute("connectionId");
            foreach (var column in xml.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "tableColumn")) column.RemoveAttribute("queryTableFieldId");
        }
        else if (root.LocalName == "pivotCacheDefinition")
        {
            root.RemoveAttribute("refreshOnLoad");
            foreach (var source in root.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "cacheSource")) source.RemoveAttribute("connectionId");
        }
    }

    private static bool IsExternalField(string instruction)
    {
        var text = instruction.AsSpan().TrimStart();
        var length = 0;
        while (length < text.Length && char.IsAsciiLetter(text[length])) length++;
        return length > 0 && ExternalFieldTypes.Contains(text[..length].ToString());
    }

    private sealed class WordField
    {
        public StringBuilder Instruction { get; } = new();
        public List<XmlElement> Code { get; } = [];
        public bool Separated { get; set; }
    }

    /// <summary>Replaces each field that fetches external content by its cached result: the field characters and the
    /// instruction go, the runs between "separate" and "end" stay, so the page shows what Word last displayed.</summary>
    private static void ReplaceExternalFieldsByCachedResult(XmlDocument xml, string word)
    {
        foreach (var simple in xml.GetElementsByTagName("fldSimple", word).OfType<XmlElement>().Where(e => IsExternalField(e.GetAttribute("instr", word))).ToArray())
        {
            if (simple.ParentNode is not { } parent) continue;
            foreach (var data in simple.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "fldData").ToArray()) simple.RemoveChild(data);
            while (simple.FirstChild is { } child) parent.InsertBefore(child, simple);
            parent.RemoveChild(simple);
        }
        var open = new Stack<WordField>();
        var external = new List<WordField>();
        foreach (var element in xml.GetElementsByTagName("*", word).OfType<XmlElement>().ToArray())
        {
            if (element.LocalName == "rPr" || element.ParentNode is not XmlElement { LocalName: "r" } run || run.NamespaceURI != word) continue;
            var kind = element.LocalName == "fldChar" ? element.GetAttribute("fldCharType", word) : "";
            if (kind == "begin") open.Push(new WordField());
            if (open.Count == 0) continue;
            // Everything before "separate" is field code, also for the fields this one is nested in.
            foreach (var field in open) if (!field.Separated) field.Code.Add(element);
            var current = open.Peek();
            if (element.LocalName is "instrText" or "delInstrText" && !current.Separated) current.Instruction.Append(element.InnerText);
            if (kind == "separate") current.Separated = true;
            else if (kind == "end")
            {
                open.Pop();
                if (current.Separated) current.Code.Add(element);
                if (IsExternalField(current.Instruction.ToString())) external.Add(current);
            }
        }
        // A field that never ends still must not reach the engine as a field.
        external.AddRange(open.Where(field => IsExternalField(field.Instruction.ToString())));
        foreach (var node in external.SelectMany(field => field.Code))
        {
            if (node.ParentNode is not XmlElement run) continue;
            run.RemoveChild(node);
            if (!run.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName != "rPr")) run.ParentNode?.RemoveChild(run);
        }
    }
}

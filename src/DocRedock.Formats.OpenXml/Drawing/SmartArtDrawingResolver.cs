using System.Xml;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>The cached SmartArt drawing part resolved for a diagram, with its package part URI.</summary>
public sealed record SmartArtDrawingPart(string PartUri, XmlElement ShapeTree);

/// <summary>
/// Resolves the cached diagram drawing part owned by a diagram data part, reading parts only
/// through the caller-supplied reader and never touching the filesystem or network.
/// </summary>
public static class SmartArtDrawingResolver
{
    private const string DiagramNamespace = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DiagramDrawingNamespace = "http://schemas.microsoft.com/office/drawing/2008/diagram";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DiagramDataType = RelationshipNamespace + "/diagramData";
    private const string DiagramDrawingType = "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing";

    private const int MaxXmlBytes = 4 * 1024 * 1024;
    private const int MaxXmlDepth = 128;

    public static SmartArtDrawingPart? TryResolve(
        XmlElement relIds,
        string ownerPart,
        Func<string, byte[]?> readPart,
        ICollection<string>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relIds is null)
            throw new ArgumentNullException(nameof(relIds));
        if (string.IsNullOrEmpty(ownerPart))
            throw new ArgumentException("Owner part URI is required.", nameof(ownerPart));
        ArgumentNullException.ThrowIfNull(readPart);

        if (ownerPart[0] != '/' || !IsCanonicalPartUri(ownerPart))
        {
            Warn(warnings, $"Invalid owner part URI '{ownerPart}'.");
            return null;
        }

        var dmId = relIds.GetAttribute("dm", RelationshipNamespace);
        if (dmId.Length == 0)
        {
            Warn(warnings, "No r:dm relationship id on the diagram relIds element.");
            return null;
        }

        var ownerRels = ReadRelationships(OwnerRelsUri(ownerPart), readPart, warnings, cancellationToken);
        if (ownerRels is null)
            return null;

        string? dataTarget = null;
        foreach (var rel in ownerRels)
        {
            if (rel.Id == dmId && rel.Type == DiagramDataType)
            {
                if (rel.IsExternal)
                {
                    Warn(warnings, $"DiagramData relationship '{dmId}' on '{ownerPart}' is external.");
                    return null;
                }

                dataTarget = rel.Target;
                break;
            }
        }

        if (dataTarget is null)
        {
            Warn(warnings, $"No diagramData relationship '{dmId}' on '{ownerPart}'.");
            return null;
        }

        if (!TryResolveTargetUri(ownerPart, dataTarget, out var dataPart))
        {
            Warn(warnings, $"Diagram data relationship target '{dataTarget}' is not a safe internal part URI.");
            return null;
        }

        var dataXml = ReadXml(dataPart, readPart, warnings, cancellationToken);
        if (dataXml is null)
            return null;

        if (!RootIs(dataXml, DiagramNamespace, "dataModel"))
        {
            Warn(warnings, $"'{dataPart}' is not a diagram data part.");
            return null;
        }

        var dataRels = ReadRelationships(OwnerRelsUri(dataPart), readPart, warnings, cancellationToken);
        if (dataRels is null)
            return null;

        var drawingRels = new List<RelInfo>();
        foreach (var rel in dataRels)
        {
            if (rel.Type == DiagramDrawingType && !rel.IsExternal)
                drawingRels.Add(rel);
        }

        var extRelId = FindDataModelExtRelId(dataXml.DocumentElement!);
        string? drawingTarget = null;
        if (extRelId is not null)
        {
            foreach (var rel in dataRels)
            {
                if (rel.Id == extRelId)
                {
                    if (rel.Type != DiagramDrawingType || rel.IsExternal)
                    {
                        Warn(warnings, $"Relationship '{extRelId}' on '{dataPart}' is not an internal diagramDrawing relationship.");
                        return null;
                    }

                    drawingTarget = rel.Target;
                    break;
                }
            }

            if (drawingTarget is null)
            {
                Warn(warnings, $"No diagramDrawing relationship '{extRelId}' on '{dataPart}'.");
                return null;
            }
        }
        else if (drawingRels.Count == 1)
        {
            drawingTarget = drawingRels[0].Target;
        }
        else
        {
            Warn(warnings, drawingRels.Count == 0
                ? $"No diagramDrawing relationship on '{dataPart}'."
                : $"Ambiguous diagramDrawing relationships on '{dataPart}' and no dataModelExt relId.");
            return null;
        }

        if (!TryResolveTargetUri(dataPart, drawingTarget, out var drawingPart))
        {
            Warn(warnings, $"Diagram drawing relationship target '{drawingTarget}' is not a safe internal part URI.");
            return null;
        }

        var drawingXml = ReadXml(drawingPart, readPart, warnings, cancellationToken);
        if (drawingXml is null)
            return null;

        if (!RootIs(drawingXml, DiagramDrawingNamespace, "drawing"))
        {
            Warn(warnings, $"'{drawingPart}' is not a diagram drawing part.");
            return null;
        }

        var drawingElement = drawingXml.DocumentElement!;
        XmlElement? shapeTree = null;
        foreach (XmlNode child in drawingElement.ChildNodes)
        {
            if (child is XmlElement element && element.LocalName == "spTree" && element.NamespaceURI == DiagramDrawingNamespace)
            {
                shapeTree = element;
                break;
            }
        }

        if (shapeTree is null)
        {
            Warn(warnings, $"Part '{drawingPart}' has no diagram shape tree.");
            return null;
        }

        return new SmartArtDrawingPart(drawingPart, shapeTree);
    }

    private static bool RootIs(XmlDocument document, string ns, string localName)
    {
        var root = document.DocumentElement;
        return root is not null && root.LocalName == localName && root.NamespaceURI == ns;
    }

    private static void Warn(ICollection<string>? warnings, string message)
    {
        if (warnings is not null)
            warnings.Add(message);
    }

    private static string OwnerRelsUri(string partUri)
    {
        var slash = partUri.LastIndexOf('/');
        var directory = partUri.Substring(0, slash + 1);
        var name = partUri.Substring(slash + 1);
        return directory + "_rels/" + name + ".rels";
    }

    // Owner URIs arrive already normalized: no traversal, fragment, query, backslash or controls.
    private static bool IsCanonicalPartUri(string uri)
    {
        if (uri.IndexOfAny(['\\', '#', '?']) >= 0)
            return false;
        foreach (var ch in uri)
        {
            if (char.IsControl(ch))
                return false;
        }

        foreach (var segment in uri.Split('/'))
        {
            if (segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a relationship target against its source part into a canonical root-relative part URI.
    /// Rejects external targets, absolute URIs, percent-encoded or plain segments that escape the package root.
    /// </summary>
    private static bool TryResolveTargetUri(string sourcePart, string target, out string partUri)
    {
        partUri = string.Empty;
        if (target.Length == 0 || target.IndexOf('\\') >= 0 || target.IndexOfAny(['#', '?']) >= 0)
            return false;
        foreach (var ch in target)
        {
            if (char.IsControl(ch))
                return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            return false;
        }

        // Decoded output must be a plain path: no fragment/query/backslash/controls, no
        // residual percent-encoding (e.g. %252e double-encoding), no network-path root.
        if (decoded.IndexOfAny(['\\', '#', '?', '%']) >= 0 || decoded.StartsWith("//", StringComparison.Ordinal))
            return false;
        foreach (var ch in decoded)
        {
            if (char.IsControl(ch))
                return false;
        }

        // A scheme (e.g. https:, file:) may not appear before the first path separator.
        var firstSlash = decoded.IndexOf('/');
        var schemeProbe = firstSlash < 0 ? decoded : decoded.Substring(0, firstSlash);
        if (schemeProbe.IndexOf(':') >= 0)
            return false;

        var segments = new List<string>();
        var up = 0;
        foreach (var segment in decoded.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                // Collapse against the source directory here; a surplus that would
                // escape the package root is rejected after the base is applied.
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                else
                    up++;
                continue;
            }

            if (segment.IndexOf(':') >= 0)
                return false;
            segments.Add(segment);
        }

        var baseDirectory = decoded[0] == '/'
            ? "/"
            : sourcePart.Substring(0, sourcePart.LastIndexOf('/'));
        var baseSegments = baseDirectory == "/" ? Array.Empty<string>() : baseDirectory.Substring(1).Split('/');
        if (up > baseSegments.Length)
            return false;
        var combined = new List<string>(baseSegments.Take(baseSegments.Length - up));
        combined.AddRange(segments);
        if (combined.Count == 0)
            return false;
        partUri = "/" + string.Join('/', combined);
        return true;
    }

    private static List<RelInfo>? ReadRelationships(
        string relsUri,
        Func<string, byte[]?> readPart,
        ICollection<string>? warnings,
        CancellationToken cancellationToken)
    {
        var xml = ReadXml(relsUri, readPart, warnings, cancellationToken);
        if (xml is null)
            return null;

        var root = xml.DocumentElement;
        if (root is null || root.LocalName != "Relationships" || root.NamespaceURI != PackageRelationshipsNamespace)
        {
            Warn(warnings, $"'{relsUri}' is not a package relationships part.");
            return null;
        }

        var result = new List<RelInfo>();
        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is not XmlElement element || element.LocalName != "Relationship" || element.NamespaceURI != PackageRelationshipsNamespace)
                continue;

            // Attribute values keep surrounding spaces in the test fixtures' rels markup.
            var id = element.GetAttribute("Id").Trim();
            var type = element.GetAttribute("Type").Trim();
            var target = element.GetAttribute("Target").Trim();
            var mode = element.GetAttribute("TargetMode").Trim();
            result.Add(new RelInfo(id, type, target, mode));
        }

        // Duplicate Ids make relationship identity ambiguous; refuse rather than pick a match.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in result)
        {
            if (!seen.Add(rel.Id))
            {
                Warn(warnings, $"'{relsUri}' contains duplicate relationship id '{rel.Id}'.");
                return null;
            }
        }

        return result;
    }

    private static XmlDocument? ReadXml(
        string partUri,
        Func<string, byte[]?> readPart,
        ICollection<string>? warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = readPart(partUri);
        if (bytes is null)
        {
            Warn(warnings, $"Part '{partUri}' not found.");
            return null;
        }

        if (bytes.Length > MaxXmlBytes)
        {
            Warn(warnings, $"Part '{partUri}' exceeds the {MaxXmlBytes} byte limit.");
            return null;
        }

        var document = new XmlDocument { XmlResolver = null };
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes,
            MaxCharactersFromEntities = 0,
        };

        try
        {
            // Depth is enforced while streaming, before any DOM is constructed.
            using (var probe = XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
            {
                var steps = 0;
                while (probe.Read())
                {
                    if (probe.NodeType == XmlNodeType.Element && probe.Depth + 1 > MaxXmlDepth)
                    {
                        Warn(warnings, $"Part '{partUri}' exceeds the nesting limit of {MaxXmlDepth}.");
                        return null;
                    }

                    if ((++steps & 0x3FF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            document.Load(reader);
        }
        catch (XmlException)
        {
            Warn(warnings, $"Part '{partUri}' is not valid XML or contains prohibited markup.");
            return null;
        }

        var root = document.DocumentElement;
        if (root is null)
        {
            Warn(warnings, $"Part '{partUri}' has no root element.");
            return null;
        }

        return document;
    }

    private static string? FindDataModelExtRelId(XmlElement root)
    {
        var ext = FindDescendant(root, DiagramDrawingNamespace, "dataModelExt");
        if (ext is null)
            return null;

        var relId = ext.GetAttribute("relId");
        return relId.Length == 0 ? null : relId;
    }

    private static XmlElement? FindDescendant(XmlElement root, string ns, string localName)
    {
        if (root.LocalName == localName && root.NamespaceURI == ns)
            return root;

        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is XmlElement element)
            {
                var found = FindDescendant(element, ns, localName);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private sealed record RelInfo(string Id, string Type, string Target, string TargetMode)
    {
        public bool IsExternal => TargetMode.Length > 0 && !TargetMode.Equals("Internal", StringComparison.Ordinal);
    }
}

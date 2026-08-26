using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DocRedock.Render;

/// <summary>
/// Replaces an Office template's primary content part while preserving the
/// template package and importing every generated relationship dependency that
/// the replacement XML actually references. Imported relationship IDs and part
/// names are allocated against the template, so existing drawings and media are
/// never overwritten.
/// </summary>
internal static class OfficeTemplatePackageMerger
{
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void ApplyGeneratedContent(string generatedPath, string templatePath, RenderFormat format)
    {
        using var generated = ZipFile.OpenRead(generatedPath);
        using var template = ZipFile.Open(templatePath, ZipArchiveMode.Update);

        var generatedPrimaryName = format switch
        {
            RenderFormat.Docx => "word/document.xml",
            RenderFormat.Pptx => "ppt/slides/slide1.xml",
            RenderFormat.Xlsx => "xl/worksheets/sheet1.xml",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var targetPrimaryName = FindTargetPrimaryPart(template, format);
        var generatedPrimary = LoadXml(generated, generatedPrimaryName, "Generated package has no primary content part.");
        var generatedContentTypes = LoadXml(generated, "[Content_Types].xml", "Generated package has no content-types part.");
        var targetContentTypes = LoadXml(template, "[Content_Types].xml", "Template has no content-types part.");

        var generatedRelationshipsName = RelationshipPartName(generatedPrimaryName);
        var generatedRelationships = TryLoadXml(generated, generatedRelationshipsName);
        var targetRelationshipsName = RelationshipPartName(targetPrimaryName);
        var targetRelationships = TryLoadXml(template, targetRelationshipsName) ?? NewRelationshipsDocument();
        var targetRelationshipRoot = targetRelationships.Root ?? throw new InvalidDataException("Template relationship part is empty.");

        var copiedParts = new Dictionary<string, string>(StringComparer.Ordinal);
        var reservedParts = new HashSet<string>(StringComparer.Ordinal);
        var relationshipIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var relationshipAttributes = generatedPrimary.Root?.DescendantsAndSelf()
            .Attributes()
            .Where(attribute => attribute.Name.Namespace == OfficeRelationships)
            .ToArray() ?? Array.Empty<XAttribute>();

        foreach (var sourceId in relationshipAttributes.Select(attribute => attribute.Value).Distinct(StringComparer.Ordinal))
        {
            var sourceRelationship = generatedRelationships?.Root?
                .Elements(PackageRelationships + "Relationship")
                .SingleOrDefault(relationship => string.Equals((string?)relationship.Attribute("Id"), sourceId, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"Generated primary content references missing relationship '{sourceId}'.");
            RejectExternalRelationship(sourceRelationship);

            var sourceTarget = RequiredAttribute(sourceRelationship, "Target");
            var sourceTargetPart = ResolveTargetPart(generatedPrimaryName, sourceTarget);
            var targetTargetPart = CopyPartGraph(
                generated,
                template,
                generatedContentTypes,
                targetContentTypes,
                sourceTargetPart,
                copiedParts,
                reservedParts);
            var targetId = AllocateRelationshipId(targetRelationshipRoot, sourceId);
            relationshipIdMap.Add(sourceId, targetId);
            targetRelationshipRoot.Add(new XElement(PackageRelationships + "Relationship",
                new XAttribute("Id", targetId),
                new XAttribute("Type", RequiredAttribute(sourceRelationship, "Type")),
                new XAttribute("Target", RelativeTarget(targetPrimaryName, targetTargetPart))));
        }

        foreach (var attribute in relationshipAttributes)
            attribute.Value = relationshipIdMap[attribute.Value];

        ReplaceXml(template, targetPrimaryName, generatedPrimary);
        if (relationshipIdMap.Count > 0) ReplaceXml(template, targetRelationshipsName, targetRelationships);
        if (copiedParts.Count > 0) ReplaceXml(template, "[Content_Types].xml", targetContentTypes);
    }

    private static string FindTargetPrimaryPart(ZipArchive template, RenderFormat format) => format switch
    {
        RenderFormat.Docx => template.GetEntry("word/document.xml")?.FullName,
        RenderFormat.Pptx => template.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry => entry.FullName)
            .FirstOrDefault(),
        RenderFormat.Xlsx => template.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry => entry.FullName)
            .FirstOrDefault(),
        _ => null,
    } ?? throw new InvalidDataException("Template has no primary content part.");

    private static string CopyPartGraph(
        ZipArchive generated,
        ZipArchive template,
        XDocument generatedContentTypes,
        XDocument targetContentTypes,
        string sourcePartName,
        IDictionary<string, string> copiedParts,
        ISet<string> reservedParts)
    {
        if (copiedParts.TryGetValue(sourcePartName, out var existing)) return existing;
        var sourceEntry = generated.GetEntry(sourcePartName)
            ?? throw new InvalidDataException($"Generated relationship target '{sourcePartName}' is missing.");
        var targetPartName = AllocatePartName(template, targetContentTypes, sourcePartName, reservedParts);
        copiedParts.Add(sourcePartName, targetPartName);
        reservedParts.Add(targetPartName);

        var sourceRelationshipsName = RelationshipPartName(sourcePartName);
        var sourceRelationships = TryLoadXml(generated, sourceRelationshipsName);
        if (sourceRelationships is not null)
        {
            var copiedRelationships = NewRelationshipsDocument();
            var copiedRelationshipRoot = copiedRelationships.Root!;
            foreach (var sourceRelationship in sourceRelationships.Root?.Elements(PackageRelationships + "Relationship")
                         ?? Enumerable.Empty<XElement>())
            {
                RejectExternalRelationship(sourceRelationship);
                var childSourcePart = ResolveTargetPart(sourcePartName, RequiredAttribute(sourceRelationship, "Target"));
                var childTargetPart = CopyPartGraph(
                    generated,
                    template,
                    generatedContentTypes,
                    targetContentTypes,
                    childSourcePart,
                    copiedParts,
                    reservedParts);
                copiedRelationshipRoot.Add(new XElement(PackageRelationships + "Relationship",
                    new XAttribute("Id", RequiredAttribute(sourceRelationship, "Id")),
                    new XAttribute("Type", RequiredAttribute(sourceRelationship, "Type")),
                    new XAttribute("Target", RelativeTarget(targetPartName, childTargetPart))));
            }
            ReplaceXml(template, RelationshipPartName(targetPartName), copiedRelationships);
        }

        CopyEntry(sourceEntry, template, targetPartName);
        EnsureContentType(generatedContentTypes, targetContentTypes, sourcePartName, targetPartName);
        return targetPartName;
    }

    private static string AllocatePartName(ZipArchive template, XDocument targetContentTypes, string sourcePartName, ISet<string> reservedParts)
    {
        var slash = sourcePartName.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : sourcePartName[..(slash + 1)];
        var fileName = slash < 0 ? sourcePartName : sourcePartName[(slash + 1)..];
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        if (!stem.StartsWith("docredock-", StringComparison.OrdinalIgnoreCase)) stem = "docredock-" + stem;

        for (var suffix = 1; ; suffix++)
        {
            var candidate = directory + stem + (suffix == 1 ? string.Empty : "-" + suffix) + extension;
            var contentTypeReserved = targetContentTypes.Root?.Elements(ContentTypes + "Override")
                .Any(element => string.Equals((string?)element.Attribute("PartName"), "/" + candidate, StringComparison.Ordinal)) == true;
            if (template.GetEntry(candidate) is null && !reservedParts.Contains(candidate) && !contentTypeReserved) return candidate;
        }
    }

    private static string AllocateRelationshipId(XElement relationships, string preferred)
    {
        var used = relationships.Elements(PackageRelationships + "Relationship")
            .Select(element => (string?)element.Attribute("Id"))
            .Where(id => id is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(preferred)) return preferred;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = preferred + "_" + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static void EnsureContentType(XDocument sourceTypes, XDocument targetTypes, string sourcePartName, string targetPartName)
    {
        var sourceRoot = sourceTypes.Root ?? throw new InvalidDataException("Generated content-types part is empty.");
        var targetRoot = targetTypes.Root ?? throw new InvalidDataException("Template content-types part is empty.");
        var sourceOverride = sourceRoot.Elements(ContentTypes + "Override")
            .SingleOrDefault(element => string.Equals((string?)element.Attribute("PartName"), "/" + sourcePartName, StringComparison.Ordinal));
        var extension = Path.GetExtension(sourcePartName).TrimStart('.');
        var contentType = (string?)sourceOverride?.Attribute("ContentType")
            ?? (string?)sourceRoot.Elements(ContentTypes + "Default")
                .FirstOrDefault(element => string.Equals((string?)element.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase))?
                .Attribute("ContentType")
            ?? throw new InvalidDataException($"Generated part '{sourcePartName}' has no content type.");

        var targetDefault = targetRoot.Elements(ContentTypes + "Default")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase));
        if (targetDefault is null)
        {
            targetRoot.AddFirst(new XElement(ContentTypes + "Default", new XAttribute("Extension", extension), new XAttribute("ContentType", contentType)));
            return;
        }
        if (string.Equals((string?)targetDefault.Attribute("ContentType"), contentType, StringComparison.Ordinal)) return;

        targetRoot.Add(new XElement(ContentTypes + "Override",
            new XAttribute("PartName", "/" + targetPartName),
            new XAttribute("ContentType", contentType)));
    }

    private static string RelationshipPartName(string partName)
    {
        var slash = partName.LastIndexOf('/');
        return slash < 0
            ? "_rels/" + partName + ".rels"
            : partName[..(slash + 1)] + "_rels/" + partName[(slash + 1)..] + ".rels";
    }

    private static string ResolveTargetPart(string sourcePartName, string target)
    {
        if (target.Contains('\\') || Uri.TryCreate(target, UriKind.Absolute, out _))
            throw new InvalidDataException("Generated package contains an unsupported relationship target.");
        var segments = new List<string>();
        if (!target.StartsWith('/'))
        {
            var slash = sourcePartName.LastIndexOf('/');
            if (slash >= 0) segments.AddRange(sourcePartName[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var segment in target.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("Generated relationship escapes the package root.");
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string RelativeTarget(string sourcePartName, string targetPartName)
    {
        var sourceDirectory = sourcePartName.Split('/')[..^1];
        var target = targetPartName.Split('/');
        var common = 0;
        while (common < sourceDirectory.Length && common < target.Length && string.Equals(sourceDirectory[common], target[common], StringComparison.Ordinal)) common++;
        return string.Concat(Enumerable.Repeat("../", sourceDirectory.Length - common)) + string.Join('/', target[common..]);
    }

    private static void RejectExternalRelationship(XElement relationship)
    {
        if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated package contains an external relationship dependency.");
    }

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) ?? throw new InvalidDataException($"Relationship is missing '{name}'.");

    private static XDocument NewRelationshipsDocument() => new(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(PackageRelationships + "Relationships"));

    private static XDocument LoadXml(ZipArchive archive, string name, string missingMessage) =>
        TryLoadXml(archive, name) ?? throw new InvalidDataException(missingMessage);

    private static XDocument? TryLoadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void ReplaceXml(ZipArchive archive, string name, XDocument document)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static void CopyEntry(ZipArchiveEntry source, ZipArchive targetArchive, string targetName)
    {
        var target = targetArchive.CreateEntry(targetName, CompressionLevel.Optimal);
        using var input = source.Open();
        using var output = target.Open();
        input.CopyTo(output);
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DocRedock.Api;

/// <summary>A source location of an AI package, the same for every manifest schema.</summary>
public sealed record AiPackageManifestLocation(
    string Id,
    string PartitionId,
    string Label,
    int? PageNumber = null,
    int? SlideNumber = null,
    string? SheetName = null,
    IReadOnlyList<string>? HeadingPath = null);

/// <summary>One part as its manifest describes it. <see cref="NodeIds"/> is filled only by schema
/// 1.x, which listed node identities inline; from 2.0 they live in the source index
/// (<see cref="AiPackageManifestReader.ReadSourceIndexAsync"/>). <see cref="EstimatedTokens"/> and
/// <see cref="TableBlock"/> appear from 2.1.</summary>
public sealed record AiPackageManifestPart(
    string Id,
    string Path,
    int NodeCount,
    IReadOnlyList<AiPackageManifestLocation> Locations,
    bool ExceedsTarget,
    int? EstimatedTokens = null,
    AiPackageTableBlock? TableBlock = null,
    IReadOnlyList<string>? NodeIds = null)
{
    // Schema 1.x only: the location id and cell address of each entry of NodeIds, in the same order.
    internal IReadOnlyList<string> LegacyNodeLocations { get; init; } = [];
    internal IReadOnlyList<string?> LegacyCellAddresses { get; init; } = [];
}

public sealed record AiPackageManifestFile(string Path, long Bytes, string Sha256);

/// <summary>A manifest normalized across schema versions 1.x and 2.x.</summary>
public sealed record AiPackageManifest(
    string SchemaVersion,
    string? GeneratorVersion,
    string SourceFileName,
    string SourceSha256,
    string Format,
    string ContentPolicy,
    int TargetCharacters,
    bool TableRowBlocks,
    string Document,
    string Review,
    string Report,
    string? SourceIndex,
    IReadOnlyList<AiPackageManifestLocation> Locations,
    IReadOnlyList<AiPackageManifestPart> Parts,
    IReadOnlyList<AiPackageManifestFile> Files);

/// <summary>Node identities (and worksheet cell addresses) of one part's source location.</summary>
public sealed record AiPackageSourceGroup(string SourceId, IReadOnlyList<string> NodeIds, IReadOnlyList<string?>? CellAddresses);

/// <summary>The manifest's schema version is missing or not one this reader understands. A manifest
/// that is not valid JSON, or lacks a required field, raises <see cref="InvalidDataException"/>.</summary>
public sealed class AiPackageSchemaException(string? schemaVersion, string message) : NotSupportedException(message)
{
    public string? SchemaVersion { get; } = schemaVersion;
}

/// <summary>
/// Reads AI package manifests written by any DocRedock version: schema 1.x (v0.3.0, node identities
/// inline in each part), 2.0 (v0.3.1, shared locations and a separate source index) and later 2.x
/// minor versions, which only add fields. Unknown fields are ignored. Another major version, or a
/// manifest without a schema version, is refused with <see cref="AiPackageSchemaException"/> instead
/// of being read as if it had the same layout.
/// </summary>
public static class AiPackageManifestReader
{
    public const string SupportedVersions = "1.x, 2.x";

    public static AiPackageManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new InvalidDataException("manifest.json is not valid JSON.", exception); }
        using (document) return Parse(document.RootElement);
    }

    /// <summary>Reads manifest.json of a package directory or ZIP.</summary>
    public static async Task<AiPackageManifest> ReadAsync(string packagePath, CancellationToken cancellationToken = default) =>
        Parse(System.Text.Encoding.UTF8.GetString(await ReadPackageFileAsync(packagePath, "manifest.json", cancellationToken).ConfigureAwait(false)));

    /// <summary>Node identities and cell addresses of every part, keyed by part id. Schema 2.x reads
    /// the source index; schema 1.x groups the inline node identities by location.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<AiPackageSourceGroup>>> ReadSourceIndexAsync(
        string packagePath, AiPackageManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SourceIndex is null)
        {
            if (!manifest.SchemaVersion.StartsWith("1.", StringComparison.Ordinal))
                throw new InvalidDataException("AI package manifest has no source_index.");
            return manifest.Parts.ToDictionary(part => part.Id, part => (IReadOnlyList<AiPackageSourceGroup>)part.Locations
                .Select(location => LegacyGroup(part, location.Id)).ToArray(), StringComparer.Ordinal);
        }
        using var index = JsonDocument.Parse(await ReadPackageFileAsync(packagePath, manifest.SourceIndex, cancellationToken).ConfigureAwait(false));
        var result = new Dictionary<string, IReadOnlyList<AiPackageSourceGroup>>(StringComparer.Ordinal);
        foreach (var part in Array(index.RootElement, "parts"))
            result[Text(part, "id")] = Array(part, "sources").Select(group => new AiPackageSourceGroup(Text(group, "source_id"),
                Array(group, "node_ids").Select(id => id.GetString() ?? string.Empty).ToArray(),
                group.TryGetProperty("cell_addresses", out var cells) && cells.ValueKind == JsonValueKind.Array
                    ? cells.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() : null).ToArray()
                    : null)).ToArray();
        return result;
    }

    /// <summary>Checks every file the manifest lists against its size and SHA-256. Returns one
    /// message per missing or changed file; an empty list means the package is intact.</summary>
    public static async Task<IReadOnlyList<string>> VerifyFilesAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            byte[] bytes;
            try { bytes = await ReadPackageFileAsync(packagePath, file.Path, cancellationToken).ConfigureAwait(false); }
            catch (FileNotFoundException) { problems.Add($"{file.Path}: missing"); continue; }
            if (bytes.LongLength != file.Bytes) problems.Add($"{file.Path}: {bytes.LongLength} bytes, manifest lists {file.Bytes}");
            else if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256))
                problems.Add($"{file.Path}: SHA-256 differs from the manifest");
        }
        return problems;
    }

    private static AiPackageManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("manifest.json is not a JSON object.");
        if (!root.TryGetProperty("schema_version", out var versionElement))
            throw new AiPackageSchemaException(null, $"AI package manifest has no schema_version (supported: {SupportedVersions}).");
        var version = versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString()! : null;
        var parts = version?.Split('.');
        if (version is null || parts is not { Length: 2 } || !parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)) ||
            parts[0] is not ("1" or "2"))
            throw new AiPackageSchemaException(version ?? versionElement.GetRawText(),
                $"Unsupported AI package manifest schema_version {(version is null ? versionElement.GetRawText() : "'" + version + "'")} (supported: {SupportedVersions}).");
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AI package manifest is missing 'source'.");
        var legacy = parts[0] == "1";
        var locations = new List<AiPackageManifestLocation>();
        List<AiPackageManifestPart> manifestParts;
        if (legacy)
        {
            // 1.x carried every node's location inline; equal locations become one shared entry.
            var keys = new Dictionary<string, AiPackageManifestLocation>(StringComparer.Ordinal);
            manifestParts = Array(root, "parts").Select(part =>
            {
                var partLocations = new List<AiPackageManifestLocation>();
                var nodeLocations = new List<string>();
                var cellAddresses = new List<string?>();
                foreach (var item in Array(part, "sources"))
                {
                    cellAddresses.Add(OptionalText(item, "cell_address"));
                    var candidate = Location(item, string.Empty);
                    var key = JsonSerializer.Serialize(candidate);
                    if (!keys.TryGetValue(key, out var shared))
                    {
                        shared = candidate with { Id = "source-" + (keys.Count + 1).ToString("D4", CultureInfo.InvariantCulture) };
                        keys.Add(key, shared);
                        locations.Add(shared);
                    }
                    if (!partLocations.Contains(shared)) partLocations.Add(shared);
                    nodeLocations.Add(shared.Id);
                }
                var nodeIds = Array(part, "node_ids").Select(id => id.GetString() ?? string.Empty).ToArray();
                return new AiPackageManifestPart(Text(part, "id"), Text(part, "path"), nodeIds.Length, partLocations,
                    Bool(part, "exceeds_target"), NodeIds: nodeIds) { LegacyNodeLocations = nodeLocations, LegacyCellAddresses = cellAddresses };
            }).ToList();
        }
        else
        {
            locations.AddRange(Array(root, "locations").Select(item => Location(item, Text(item, "id"))));
            var byId = locations.ToDictionary(location => location.Id, StringComparer.Ordinal);
            manifestParts = Array(root, "parts").Select(part => new AiPackageManifestPart(Text(part, "id"), Text(part, "path"),
                Int(part, "node_count") ?? 0,
                Array(part, "source_ids").Select(id => byId.TryGetValue(id.GetString() ?? string.Empty, out var location) ? location
                    : throw new InvalidDataException($"Part {Text(part, "id")} names an unknown location {id.GetString()}.")).ToArray(),
                Bool(part, "exceeds_target"), Int(part, "estimated_tokens"), TableBlock(part))).ToList();
        }
        return new AiPackageManifest(version, OptionalText(root, "generator_version"), Text(source, "file_name"), Text(source, "sha256"),
            Text(source, "format"), Text(root, "content_policy"), Int(root, "target_characters") ?? 0, Bool(root, "table_row_blocks"),
            OptionalText(root, "document") ?? "document.md", OptionalText(root, "review") ?? "review.md", OptionalText(root, "report") ?? "report.json",
            legacy ? null : OptionalText(root, "source_index"), locations, manifestParts,
            Array(root, "files").Select(file => new AiPackageManifestFile(Text(file, "path"),
                file.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var size) ? size
                    : throw new InvalidDataException($"AI package manifest file '{Text(file, "path")}' has no byte size."),
                Text(file, "sha256"))).ToArray());
    }

    // A 1.x part listed its nodes inline; group them by location like a source index does.
    private static AiPackageSourceGroup LegacyGroup(AiPackageManifestPart part, string locationId)
    {
        var indexes = Enumerable.Range(0, part.NodeIds?.Count ?? 0)
            .Where(index => index < part.LegacyNodeLocations.Count && part.LegacyNodeLocations[index] == locationId).ToArray();
        var cells = indexes.Select(index => index < part.LegacyCellAddresses.Count ? part.LegacyCellAddresses[index] : null).ToArray();
        return new AiPackageSourceGroup(locationId, indexes.Select(index => part.NodeIds![index]).ToArray(),
            cells.Any(cell => cell is not null) ? cells : null);
    }

    private static AiPackageManifestLocation Location(JsonElement item, string id) => new(id, Text(item, "partition_id"), Text(item, "label"),
        Int(item, "page_number"), Int(item, "slide_number"), OptionalText(item, "sheet_name"),
        item.TryGetProperty("heading_path", out var path) && path.ValueKind == JsonValueKind.Array
            ? path.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray() : null);

    private static AiPackageTableBlock? TableBlock(JsonElement part) =>
        part.TryGetProperty("table_block", out var block) && block.ValueKind == JsonValueKind.Object
            ? new AiPackageTableBlock(Text(block, "table_id"), Int(block, "index") ?? 0, Int(block, "count") ?? 0,
                OptionalText(block, "header_range"), Text(block, "row_range"), OptionalText(block, "previous_part"), OptionalText(block, "next_part"))
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string Text(JsonElement element, string name) => OptionalText(element, name) ??
        throw new InvalidDataException($"AI package manifest is missing '{name}'.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // A package is a directory or a ZIP with its files at the root. Names come from the manifest,
    // so a name that would leave the package is refused rather than followed.
    private static async Task<byte[]> ReadPackageFileAsync(string packagePath, string relativePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (relativePath.Length == 0 || relativePath.Contains('\\') || relativePath.StartsWith('/') || Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"AI package path '{relativePath}' is not a relative path inside the package.");
        if (Directory.Exists(packagePath))
        {
            // A link inside a package folder could point anywhere; DocRedock never writes one.
            var path = Path.GetFullPath(packagePath);
            foreach (var segment in relativePath.Split('/'))
            {
                path = Path.Combine(path, segment);
                var item = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (!item.Exists) throw new FileNotFoundException("AI package file not found.", relativePath);
                if (item.LinkTarget is not null)
                    throw new InvalidDataException($"AI package path '{relativePath}' is a link; packages contain only regular files.");
            }
            return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }
        if (!File.Exists(packagePath)) throw new FileNotFoundException("AI package not found.", packagePath);
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.GetEntry(relativePath) ?? throw new FileNotFoundException("AI package file not found.", relativePath);
        await using var input = entry.Open();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, token).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

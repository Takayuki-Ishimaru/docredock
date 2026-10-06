using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>The schema version of a package JSON file (the manifest, the source index or the
/// report) is missing or not one this reader understands. A file that is not valid JSON, or lacks
/// a required field, raises <see cref="InvalidDataException"/>.</summary>
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
/// <remarks>
/// A manifest is checked before anything is read through it: the fields this reader uses must be
/// present with their documented types, ids and paths must be unique, and every file the manifest
/// refers to (document, review, report, source index, parts) must be listed in <c>files</c> with its
/// size and SHA-256, so <see cref="VerifyFilesAsync"/> can never pass without checking it. Anything
/// else raises <see cref="InvalidDataException"/>, as do JSON objects that name a key twice and ZIP
/// archives that hold two entries with one name, which different readers would resolve differently.
/// </remarks>
public static class AiPackageManifestReader
{
    public const string SupportedVersions = "1.x, 2.x";

    // Readers that keep the first or the last value of a key twice in one object would disagree
    // about the package. System.Text.Json already refuses NaN, Infinity and comments.
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowDuplicateProperties = false };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static AiPackageManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = ParseJson(json, "manifest.json");
        return Parse(document.RootElement);
    }

    /// <summary>Reads manifest.json of a package directory or ZIP.</summary>
    public static async Task<AiPackageManifest> ReadAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        using var files = new PackageFiles(packagePath);
        byte[] bytes;
        try { bytes = await files.ReadAsync("manifest.json", cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException exception) when (files.NestedManifest() is { } nested)
        {
            throw new FileNotFoundException(
                $"AI package has no manifest.json at its top level; found '{nested}', but package files must be at the top level.",
                "manifest.json", exception);
        }
        return Parse(Utf8(bytes, "manifest.json"));
    }

    /// <summary>Node identities and cell addresses of every part, keyed by part id. Schema 2.x reads
    /// the source index; schema 1.x groups the inline node identities by location. The index must
    /// agree with the manifest: its schema is 1.x, it lists each manifest part exactly once, a part's
    /// groups name exactly that part's locations, their node identities add up to its node count,
    /// and cell addresses, when present, have one entry per node identity.</summary>
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
        var name = manifest.SourceIndex;
        byte[] bytes;
        using (var files = new PackageFiles(packagePath))
            bytes = await files.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        using var index = ParseJson(Utf8(bytes, name), name);
        var fields = new Fields(name);
        var root = fields.Object(index.RootElement, "the index");
        var version = fields.Text(root, "", "schema_version");
        if (!IsMinorVersionOf(version, "1"))
            throw new AiPackageSchemaException(version, $"AI package {name} schema_version '{version}' is unsupported (supported: 1.x).");
        var parts = new Dictionary<string, AiPackageManifestPart>(StringComparer.Ordinal);
        foreach (var part in manifest.Parts) parts.TryAdd(part.Id, part);
        var result = new Dictionary<string, IReadOnlyList<AiPackageSourceGroup>>(StringComparer.Ordinal);
        var position = 0;
        foreach (var item in fields.Array(root, "", "parts"))
        {
            var where = $"parts[{position++}]";
            fields.Object(item, where);
            var id = fields.Text(item, where, "id");
            if (!parts.TryGetValue(id, out var part)) throw fields.Invalid($"{where} names a part the manifest does not list: '{id}'");
            if (result.ContainsKey(id)) throw fields.Invalid($"parts list '{id}' twice");
            var groups = new List<AiPackageSourceGroup>();
            var groupPosition = 0;
            foreach (var group in fields.Array(item, where, "sources"))
            {
                var groupWhere = $"{where}.sources[{groupPosition++}]";
                fields.Object(group, groupWhere);
                var nodeIds = fields.Strings(group, groupWhere, "node_ids");
                IReadOnlyList<string?>? cells = null;
                if (Fields.Optional(group, "cell_addresses") is { } raw)
                {
                    if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() != nodeIds.Count ||
                        raw.EnumerateArray().Any(cell => cell.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        throw fields.Invalid($"{groupWhere}.cell_addresses must hold one address or null for each of its {nodeIds.Count} node_ids");
                    cells = raw.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() : null).ToArray();
                }
                groups.Add(new AiPackageSourceGroup(fields.Text(group, groupWhere, "source_id"), nodeIds, cells));
            }
            var named = groups.Select(group => group.SourceId).ToArray();
            var expected = part.Locations.Select(location => location.Id).ToArray();
            if (named.Distinct(StringComparer.Ordinal).Count() != named.Length || !named.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw fields.Invalid($"{where} groups name the locations [{string.Join(", ", named)}], but the manifest part lists [{string.Join(", ", expected)}]");
            var total = groups.Sum(group => group.NodeIds.Count);
            if (total != part.NodeCount) throw fields.Invalid($"{where} lists {total} node ids, but the manifest part counts {part.NodeCount}");
            result[id] = groups;
        }
        var absent = manifest.Parts.Select(part => part.Id).Where(id => !result.ContainsKey(id)).ToArray();
        if (absent.Length > 0) throw fields.Invalid($"no entry for {string.Join(", ", absent)}");
        return result;
    }

    /// <summary>Checks every file the manifest lists against its size and SHA-256. Returns one
    /// message per missing or changed file; an empty list means the package is intact. The manifest
    /// read by <see cref="Parse(string)"/> already lists every file it refers to. A name that would
    /// leave the package, a link inside a package folder or an unreadable ZIP entry raises
    /// <see cref="InvalidDataException"/>.</summary>
    public static async Task<IReadOnlyList<string>> VerifyFilesAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var files = new PackageFiles(packagePath);
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                // A different size already says the content changed; it is found without reading the file.
                var size = files.Size(file.Path);
                if (size != file.Bytes)
                {
                    problems.Add($"{file.Path}: {size} bytes, manifest lists {file.Bytes}");
                    continue;
                }
                bytes = await files.ReadAsync(file.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { problems.Add($"{file.Path}: missing"); continue; }
            if (bytes.LongLength != file.Bytes) problems.Add($"{file.Path}: {bytes.LongLength} bytes, manifest lists {file.Bytes}");
            else if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256))
                problems.Add($"{file.Path}: SHA-256 differs from the manifest");
        }
        return problems;
    }

    /// <summary>Whether the package's report.json says something needs comparison with the source
    /// (review pages, OCR items, worksheet table boundaries). Read review.md before giving such a
    /// package to a model. A report whose schema is not 1.x raises <see cref="AiPackageSchemaException"/>;
    /// one without a boolean <c>review.required</c> raises <see cref="InvalidDataException"/>.</summary>
    public static async Task<bool> ReadReviewRequiredAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] bytes;
        using (var files = new PackageFiles(packagePath))
            bytes = await files.ReadAsync(manifest.Report, cancellationToken).ConfigureAwait(false);
        using var report = ParseJson(Utf8(bytes, manifest.Report), manifest.Report);
        var fields = new Fields(manifest.Report);
        var root = fields.Object(report.RootElement, "the report");
        var version = fields.Text(root, "", "schema_version");
        if (!IsMinorVersionOf(version, "1"))
            throw new AiPackageSchemaException(version, $"AI package {manifest.Report} schema_version '{version}' is unsupported (supported: 1.x).");
        var review = fields.Object(fields.Required(root, "", "review"), "review");
        var required = fields.Required(review, "review", "required");
        return required.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw fields.Invalid($"review.required must be true or false, got {Fields.Kind(required)}"),
        };
    }

    private static AiPackageManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("manifest.json is not a JSON object.");
        if (!root.TryGetProperty("schema_version", out var versionElement))
            throw new AiPackageSchemaException(null, $"AI package manifest has no schema_version (supported: {SupportedVersions}).");
        var version = versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString()! : null;
        if (version is null || !(IsMinorVersionOf(version, "1") || IsMinorVersionOf(version, "2")))
            throw new AiPackageSchemaException(version ?? versionElement.GetRawText(),
                $"Unsupported AI package manifest schema_version {(version is null ? versionElement.GetRawText() : "'" + version + "'")} (supported: {SupportedVersions}).");
        var fields = new Fields("manifest.json");
        var source = fields.Object(fields.Required(root, "", "source"), "source");
        var files = Files(fields, root);
        var legacy = version.StartsWith("1.", StringComparison.Ordinal);
        var (locations, parts) = legacy ? LegacyParts(fields, root) : CurrentParts(fields, root);
        CheckParts(fields, parts);
        var sourceIndex = legacy ? null : fields.OptionalPath(root, "", "source_index");
        var manifest = new AiPackageManifest(version, fields.OptionalText(root, "", "generator_version"),
            fields.Text(source, "source", "file_name"), fields.Sha256(source, "source", "sha256"), fields.Text(source, "source", "format"),
            fields.Text(root, "", "content_policy"), fields.OptionalCount(root, "", "target_characters") ?? 0,
            fields.Flag(root, "", "table_row_blocks"),
            fields.OptionalPath(root, "", "document") ?? "document.md", fields.OptionalPath(root, "", "review") ?? "review.md",
            fields.OptionalPath(root, "", "report") ?? "report.json", sourceIndex, locations, parts, files);
        // A file the manifest names but does not list would pass verification unchecked.
        var listed = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var unlisted = new[] { manifest.Document, manifest.Review, manifest.Report }
            .Concat(sourceIndex is null ? [] : [sourceIndex]).Concat(parts.Select(part => part.Path))
            .Where(path => !listed.Contains(path)).ToArray();
        if (unlisted.Length > 0)
            throw fields.Invalid($"files does not list {string.Join(", ", unlisted.Take(5).Select(path => "'" + path + "'"))}" +
                (unlisted.Length > 5 ? $" and {unlisted.Length - 5} more" : string.Empty) + ", so its size and SHA-256 cannot be checked");
        return manifest;
    }

    private static AiPackageManifestFile[] Files(Fields fields, JsonElement root)
    {
        var files = new List<AiPackageManifestFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var item in fields.Array(root, "", "files"))
        {
            var where = $"files[{position++}]";
            fields.Object(item, where);
            var path = fields.Path(item, where, "path");
            if (!seen.Add(path)) throw fields.Invalid($"files list '{path}' twice");
            files.Add(new AiPackageManifestFile(path, fields.Bytes(item, where, "bytes"), fields.Sha256(item, where, "sha256")));
        }
        return files.ToArray();
    }

    // 1.x carried every node's location inline; equal locations become one shared entry.
    private static (List<AiPackageManifestLocation>, List<AiPackageManifestPart>) LegacyParts(Fields fields, JsonElement root)
    {
        var keys = new Dictionary<string, AiPackageManifestLocation>(StringComparer.Ordinal);
        var locations = new List<AiPackageManifestLocation>();
        var parts = new List<AiPackageManifestPart>();
        var position = 0;
        foreach (var part in fields.Array(root, "", "parts"))
        {
            var where = $"parts[{position++}]";
            fields.Object(part, where);
            var nodeIds = fields.Strings(part, where, "node_ids");
            var sources = fields.Array(part, where, "sources").ToArray();
            if (sources.Length != nodeIds.Count)
                throw fields.Invalid($"{where} lists {nodeIds.Count} node_ids but {sources.Length} sources; schema 1.x pairs them one to one");
            var partLocations = new List<AiPackageManifestLocation>();
            var nodeLocations = new List<string>();
            var cellAddresses = new List<string?>();
            for (var index = 0; index < sources.Length; index++)
            {
                var sourceWhere = $"{where}.sources[{index}]";
                var item = fields.Object(sources[index], sourceWhere);
                if (fields.OptionalText(item, sourceWhere, "node_id") is { } nodeId && nodeId != nodeIds[index])
                    throw fields.Invalid($"{sourceWhere}.node_id '{nodeId}' is not node_ids[{index}] '{nodeIds[index]}'");
                cellAddresses.Add(fields.OptionalText(item, sourceWhere, "cell_address"));
                var candidate = Location(fields, item, sourceWhere, string.Empty);
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
            parts.Add(new AiPackageManifestPart(fields.Text(part, where, "id"), fields.Path(part, where, "path"), nodeIds.Count, partLocations,
                fields.Flag(part, where, "exceeds_target"), NodeIds: nodeIds)
            { LegacyNodeLocations = nodeLocations, LegacyCellAddresses = cellAddresses });
        }
        return (locations, parts);
    }

    private static (List<AiPackageManifestLocation>, List<AiPackageManifestPart>) CurrentParts(Fields fields, JsonElement root)
    {
        var locations = new List<AiPackageManifestLocation>();
        var byId = new Dictionary<string, AiPackageManifestLocation>(StringComparer.Ordinal);
        var position = 0;
        foreach (var item in fields.Array(root, "", "locations"))
        {
            var where = $"locations[{position++}]";
            fields.Object(item, where);
            var location = Location(fields, item, where, fields.Text(item, where, "id"));
            if (!byId.TryAdd(location.Id, location)) throw fields.Invalid($"locations list '{location.Id}' twice");
            locations.Add(location);
        }
        var parts = new List<AiPackageManifestPart>();
        position = 0;
        foreach (var part in fields.Array(root, "", "parts"))
        {
            var where = $"parts[{position++}]";
            fields.Object(part, where);
            var id = fields.Text(part, where, "id");
            var sourceIds = fields.Strings(part, where, "source_ids");
            var partLocations = sourceIds.Select(sourceId => byId.TryGetValue(sourceId, out var location) ? location
                : throw fields.Invalid($"part {id} names an unknown location '{sourceId}'")).ToArray();
            if (sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Count)
                throw fields.Invalid($"part {id} names a location twice in source_ids");
            parts.Add(new AiPackageManifestPart(id, fields.Path(part, where, "path"), fields.Count(part, where, "node_count"), partLocations,
                fields.Flag(part, where, "exceeds_target"), fields.OptionalCount(part, where, "estimated_tokens"), TableBlock(fields, part, where)));
        }
        return (locations, parts);
    }

    private static void CheckParts(Fields fields, List<AiPackageManifestPart> parts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!ids.Add(part.Id)) throw fields.Invalid($"parts list the id '{part.Id}' twice");
            if (!paths.Add(part.Path)) throw fields.Invalid($"parts list the path '{part.Path}' twice");
        }
        foreach (var part in parts)
            foreach (var (key, neighbour) in new[] { ("previous_part", part.TableBlock?.PreviousPart), ("next_part", part.TableBlock?.NextPart) })
                if (neighbour is not null && !ids.Contains(neighbour))
                    throw fields.Invalid($"part {part.Id} {key} names an unknown part '{neighbour}'");
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

    private static AiPackageManifestLocation Location(Fields fields, JsonElement item, string where, string id) => new(id,
        fields.Text(item, where, "partition_id"), fields.Text(item, where, "label"),
        fields.OptionalCount(item, where, "page_number"), fields.OptionalCount(item, where, "slide_number"),
        fields.OptionalText(item, where, "sheet_name"), fields.OptionalStrings(item, where, "heading_path"));

    private static AiPackageTableBlock? TableBlock(Fields fields, JsonElement part, string where)
    {
        if (Fields.Optional(part, "table_block") is not { } block) return null;
        where += ".table_block";
        fields.Object(block, where);
        var index = fields.Count(block, where, "index");
        var count = fields.Count(block, where, "count");
        if (index < 1 || index > count) throw fields.Invalid($"{where}.index must be from 1 to count ({count}), got {index}");
        return new AiPackageTableBlock(fields.Text(block, where, "table_id"), index, count, fields.OptionalText(block, where, "header_range"),
            fields.Text(block, where, "row_range"), fields.OptionalText(block, where, "previous_part"), fields.OptionalText(block, where, "next_part"))
        { ContextRange = fields.OptionalText(block, where, "context_range") };
    }

    // MAJOR.MINOR with ASCII digits only and the given major: "2.1" and "2.17", never "2.1\n" or "02.1".
    private static bool IsMinorVersionOf(string version, string major) =>
        version.Split('.') is [var first, { Length: > 0 } minor] && first == major && minor.All(char.IsAsciiDigit);

    private static JsonDocument ParseJson(string json, string name)
    {
        // "\ud800" without its pair is valid JSON but no text: reading it fails wherever it is read, as
        // early as the duplicate-key check for a property name.
        JsonDocument document;
        try { document = JsonDocument.Parse(json, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException($"AI package {name} is not valid JSON: {exception.Message}", exception); }
        catch (InvalidOperationException exception) { throw Unpaired(exception); }
        try { ReadEveryString(document.RootElement); }
        catch (InvalidOperationException exception)
        {
            document.Dispose();
            throw Unpaired(exception);
        }
        return document;

        InvalidDataException Unpaired(Exception exception) =>
            new($"AI package {name} contains an unpaired surrogate escape (\\ud800-\\udfff).", exception);

        static void ReadEveryString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        ReadEveryString(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) ReadEveryString(item);
                    break;
                case JsonValueKind.String:
                    _ = element.GetString();
                    break;
            }
        }
    }

    private static string Utf8(byte[] bytes, string name)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException($"AI package {name} is not UTF-8 text.", exception); }
    }

    // Names come from the manifest, so a name that would leave the package is refused rather than
    // followed: absolute, drive and ".." paths, backslashes, colons (C:x, alternate data streams) and NUL.
    private static bool IsPackagePath(string path) =>
        path.Length > 0 && path.IndexOfAny(['\\', ':', '\0']) < 0 && !path.StartsWith('/') && !System.IO.Path.IsPathRooted(path) &&
        path.Split('/').All(segment => segment is not ("" or "." or ".."));

    // Field checks for one JSON file. Messages name the file and the field, e.g.
    // "AI package manifest.json: parts[2].table_block.index must be from 1 to count (3), got 4."
    private sealed class Fields(string file)
    {
        public InvalidDataException Invalid(string message) => new($"AI package {file}: {message}.");

        public static string Kind(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Number => "a number",
            JsonValueKind.String => "a string",
            JsonValueKind.Array => "an array",
            _ => "an object",
        };

        private static string Name(string where, string key) => where.Length == 0 ? key : where + "." + key;

        public static JsonElement? Optional(JsonElement element, string key) =>
            element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

        public JsonElement Object(JsonElement value, string where) => value.ValueKind == JsonValueKind.Object ? value
            : throw Invalid($"{where} must be an object, got {Kind(value)}");

        public JsonElement Required(JsonElement element, string where, string key) => Optional(element, key) ??
            throw Invalid($"{Name(where, key)} is {(element.TryGetProperty(key, out _) ? "null" : "missing")}");

        public string Text(JsonElement element, string where, string key) => AsText(Required(element, where, key), where, key);

        public string? OptionalText(JsonElement element, string where, string key) =>
            Optional(element, key) is { } value ? AsText(value, where, key) : null;

        private string AsText(JsonElement value, string where, string key) => value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw Invalid($"{Name(where, key)} must be a string, got {Kind(value)}");

        public int Count(JsonElement element, string where, string key) => AsCount(Required(element, where, key), where, key);

        public int? OptionalCount(JsonElement element, string where, string key) =>
            Optional(element, key) is { } value ? AsCount(value, where, key) : null;

        private int AsCount(JsonElement value, string where, string key) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number
                : throw Invalid($"{Name(where, key)} must be a whole number from 0 to {int.MaxValue}, got {value.GetRawText()}");

        public long Bytes(JsonElement element, string where, string key)
        {
            var value = Required(element, where, key);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number
                : throw Invalid($"{Name(where, key)} must be a whole number from 0 to {long.MaxValue}, got {value.GetRawText()}");
        }

        public bool Flag(JsonElement element, string where, string key) => Optional(element, key) switch
        {
            null => false,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { } value => throw Invalid($"{Name(where, key)} must be true or false, got {Kind(value)}"),
        };

        public IEnumerable<JsonElement> Array(JsonElement element, string where, string key)
        {
            var value = Required(element, where, key);
            return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
                : throw Invalid($"{Name(where, key)} must be an array, got {Kind(value)}");
        }

        public IReadOnlyList<string> Strings(JsonElement element, string where, string key) => AsStrings(Array(element, where, key), where, key);

        public IReadOnlyList<string>? OptionalStrings(JsonElement element, string where, string key) => Optional(element, key) switch
        {
            null => null,
            { ValueKind: JsonValueKind.Array } value => AsStrings(value.EnumerateArray(), where, key),
            { } value => throw Invalid($"{Name(where, key)} must be an array, got {Kind(value)}"),
        };

        private string[] AsStrings(IEnumerable<JsonElement> values, string where, string key) =>
            values.Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()!
                : throw Invalid($"{Name(where, key)} must hold only strings")).ToArray();

        public string Path(JsonElement element, string where, string key) => AsPath(Text(element, where, key), where, key);

        public string? OptionalPath(JsonElement element, string where, string key) =>
            OptionalText(element, where, key) is { } value ? AsPath(value, where, key) : null;

        private string AsPath(string value, string where, string key) => IsPackagePath(value) ? value
            : throw Invalid($"{Name(where, key)} '{value}' is not a relative path inside the package");

        public string Sha256(JsonElement element, string where, string key)
        {
            var value = Text(element, where, key);
            return value.Length == 64 && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant()
                : throw Invalid($"{Name(where, key)} must be 64 hexadecimal digits, got '{value}'");
        }
    }

    // A package is a directory or a ZIP with its files at the root, opened once per call.
    private sealed class PackageFiles : IDisposable
    {
        private readonly string? directory;
        private readonly ZipArchive? zip;
        private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);

        public PackageFiles(string packagePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
            if (Directory.Exists(packagePath))
            {
                directory = System.IO.Path.GetFullPath(packagePath);
                return;
            }
            if (!File.Exists(packagePath)) throw new FileNotFoundException("AI package not found.", packagePath);
            zip = ZipFile.OpenRead(packagePath);
            try
            {
                // .NET returns the first of two equal names and Python the last; Python also cuts a name
                // at NUL and, on Windows, reads a backslash as "/". Such an archive is refused rather than
                // read differently by different consumers.
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.IndexOfAny(['\0', '\\']) >= 0)
                        throw new InvalidDataException($"The AI package ZIP holds an entry name with a {(entry.FullName.Contains('\0') ? "NUL" : "backslash")}: '{entry.FullName}'.");
                    if (!entries.TryAdd(entry.FullName, entry))
                        throw new InvalidDataException($"The AI package ZIP holds more than one entry named '{entry.FullName}'.");
                }
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        public long Size(string relativePath) => zip is null ? new FileInfo(Resolve(relativePath)).Length : Entry(relativePath).Length;

        public async Task<byte[]> ReadAsync(string relativePath, CancellationToken token)
        {
            if (zip is null)
            {
                var path = Resolve(relativePath);
                // A pipe or device reports no length and could block or never end; an empty file needs no read.
                return new FileInfo(path).Length == 0 ? [] : await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            }
            var entry = Entry(relativePath);
            await using var input = entry.Open();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, token).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            // Python's zipfile refuses an entry whose bytes do not match the archive's own CRC-32, so this
            // reader does too, whatever the manifest says.
            if (Crc32(bytes) != entry.Crc32)
                throw new InvalidDataException($"AI package entry '{relativePath}' does not match its CRC-32 in the ZIP.");
            return bytes;
        }

        private static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }

        /// <summary>A manifest.json one folder down: the package's parent was given, or the package
        /// folder itself was zipped.</summary>
        public string? NestedManifest() => zip is not null
            ? entries.Keys.Where(name => name.Count(character => character == '/') == 1 && name.EndsWith("/manifest.json", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).FirstOrDefault()
            : Directory.EnumerateDirectories(directory!).Order(StringComparer.Ordinal)
                .Where(folder => File.Exists(System.IO.Path.Combine(folder, "manifest.json")))
                .Select(folder => System.IO.Path.GetFileName(folder) + "/manifest.json").FirstOrDefault();

        public void Dispose() => zip?.Dispose();

        private static void CheckPath(string relativePath)
        {
            if (!IsPackagePath(relativePath))
                throw new InvalidDataException($"AI package path '{relativePath}' is not a relative path inside the package.");
        }

        private ZipArchiveEntry Entry(string relativePath)
        {
            CheckPath(relativePath);
            if (!entries.TryGetValue(relativePath, out var entry)) throw new FileNotFoundException("AI package file not found.", relativePath);
            return entry.IsEncrypted ? throw new InvalidDataException($"AI package entry '{relativePath}' is encrypted in the ZIP.") : entry;
        }

        private string Resolve(string relativePath)
        {
            CheckPath(relativePath);
            // A link inside a package folder could point anywhere; DocRedock never writes one.
            var path = directory!;
            foreach (var segment in relativePath.Split('/'))
            {
                path = System.IO.Path.Combine(path, segment);
                var item = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (!item.Exists) throw new FileNotFoundException("AI package file not found.", relativePath);
                if (item.LinkTarget is not null)
                    throw new InvalidDataException($"AI package path '{relativePath}' is a link; packages contain only regular files.");
            }
            return File.Exists(path) ? path : throw new FileNotFoundException("AI package file not found.", relativePath);
        }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    /// message per listed file that is missing, changed or refused (a link, pipe or device in a
    /// package folder, an encrypted or unreadable ZIP entry), in the same words as
    /// docs/examples/ai_package_reader.py; an empty list means the package is intact. The manifest read
    /// by <see cref="Parse(string)"/> already lists every file it refers to. A package that cannot be
    /// opened at all (not a folder or a plain ZIP) raises <see cref="InvalidDataException"/>.</summary>
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
                    problems.Add($"{file.Path}: size mismatch (expected {file.Bytes} bytes, got {size})");
                    continue;
                }
                bytes = await files.ReadAsync(file.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { problems.Add($"{file.Path}: missing"); continue; }
            catch (InvalidDataException refused) when (IsRefused(refused)) { problems.Add($"{file.Path}: refused ({refused.Message})"); continue; }
            var expected = file.Sha256.ToLowerInvariant();
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (bytes.LongLength != file.Bytes) problems.Add($"{file.Path}: size mismatch (expected {file.Bytes} bytes, got {bytes.LongLength})");
            else if (actual != expected) problems.Add($"{file.Path}: sha256 mismatch (expected {expected}, got {actual})");
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

    // Why a package file is refused: a link, pipe or device in a package folder, a path that would
    // leave the package, or a ZIP entry that cannot be read as every reader reads it. The message is
    // the text docs/examples/ai_package_reader.py prints after "refused (".
    private const string RefusedKey = "DocRedock.AiPackage.Refused";

    private static InvalidDataException Refused(string reason, Exception? inner = null) => new(reason, inner) { Data = { [RefusedKey] = true } };

    private static bool IsRefused(InvalidDataException exception) => exception.Data.Contains(RefusedKey);

    // A package is a directory or a ZIP with its files at the root, opened once per call. Files are
    // read as docs/examples/ai_package_reader.py reads them, so both readers accept and refuse the same
    // packages, for the same reasons, worded the same way.
    private sealed class PackageFiles : IDisposable
    {
        private readonly string? directory;
        private readonly string? archive;
        private readonly ZipArchive? zip;
        private readonly Dictionary<string, ZipItem> entries = new(StringComparer.Ordinal);

        // Files larger than this are refused rather than read into memory (both readers use the limit).
        private const long MaxFileBytes = 2_147_483_520;

        // One central directory record and the .NET entry at the same position.
        private sealed record ZipItem(ZipArchiveEntry Entry, string OriginalName, int Flags, int Method, long HeaderOffset,
            long CompressedSize, long Size, uint Crc)
        {
            public long End { get; set; }
        }

        public PackageFiles(string packagePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
            if (Directory.Exists(packagePath))
            {
                directory = System.IO.Path.GetFullPath(packagePath);
                return;
            }
            if (!File.Exists(packagePath)) throw new FileNotFoundException("AI package not found.", packagePath);
            archive = packagePath;
            var records = ZipLayout.Read(packagePath);
            try { zip = ZipFile.OpenRead(packagePath); }
            catch (InvalidDataException exception) { throw new InvalidDataException($"not a package folder or ZIP archive: {packagePath} ({exception.Message})", exception); }
            try
            {
                if (zip.Entries.Count != records.Count)
                    throw new InvalidDataException($"not a package folder or ZIP archive: {packagePath} (its central directory could not be read)");
                // A package ZIP is read only when it is plain, so that every reader reads it the same way
                // (the Python example makes the same checks in the same order): .NET returns the first of
                // two equal names and Python the last, Python cuts a name at NUL and on Windows reads a
                // backslash as "/", and Python 3.12 renames an entry by a Unicode Path extra field.
                var twice = new SortedSet<string>(StringComparer.Ordinal);
                var items = new List<ZipItem>();
                for (var index = 0; index < records.Count; index++)
                {
                    var record = records[index];
                    var name = zip.Entries[index].FullName;
                    if (name.IndexOfAny(['\0', '\\']) >= 0)
                        throw new InvalidDataException($"the ZIP holds an entry name with a {(name.Contains('\0') ? "NUL" : "backslash")}: {Python.Repr(name)}");
                    if (record.UnicodePath) throw new InvalidDataException($"the ZIP gives {Python.Repr(name)} a Unicode Path extra field");
                    var item = new ZipItem(zip.Entries[index], record.OriginalName, record.Flags, record.Method, record.HeaderOffset,
                        record.CompressedSize, record.Size, record.Crc);
                    items.Add(item);
                    if (!entries.TryAdd(name, item)) twice.Add(name);
                }
                if (twice.Count > 0)
                    throw new InvalidDataException("the ZIP holds more than one entry named " + string.Join(", ", twice.Select(Python.Repr)));
                // Where each entry must end: at the next local header or the central directory (Python 3.12).
                var end = records.DirectoryStart;
                foreach (var item in items.OrderBy(item => item.HeaderOffset).Reverse())
                {
                    item.End = end;
                    end = item.HeaderOffset;
                }
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        public long Size(string relativePath) => zip is null ? new FileInfo(Resolve(relativePath)).Length : Entry(relativePath).Size;

        public async Task<byte[]> ReadAsync(string relativePath, CancellationToken token)
        {
            if (zip is null)
            {
                var path = Resolve(relativePath);
                var length = new FileInfo(path).Length;
                if (length > MaxFileBytes) throw TooLarge(relativePath);
                // Where stat is unavailable a pipe or device was not ruled out; it reports no length,
                // and an empty file needs no read.
                return length == 0 ? [] : await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            }
            var item = Entry(relativePath);
            OpenChecks(relativePath, item);
            if (item.Size > MaxFileBytes) throw TooLarge(relativePath);
            var bytes = new byte[item.Size];
            var total = 0;
            try
            {
                await using var input = item.Entry.Open();
                while (total < bytes.Length)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(total, Math.Min(bytes.Length - total, 81920)), token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                }
            }
            catch (InvalidDataException exception)
            {
                throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: {exception.Message}", exception);
            }
            // As Python's zipfile does: the bytes up to the entry's size, checked against its CRC-32.
            if (Crc32(bytes.AsSpan(0, total)) != item.Crc)
                throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: Bad CRC-32 for file {Python.Repr(item.OriginalName)}");
            if (total != bytes.Length)
                throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: it ends before its size in the ZIP");
            return bytes;
        }

        private static InvalidDataException TooLarge(string relativePath) =>
            Refused($"cannot read {Python.Repr(relativePath)}: it is larger than {MaxFileBytes} bytes");

        // What Python's zipfile checks when it opens an entry, in its order and with its messages,
        // including the overlap check Python 3.12 added.
        private void OpenChecks(string relativePath, ZipItem item)
        {
            InvalidDataException Unreadable(string reason) => Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: {reason}");
            using var stream = new FileStream(archive!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[30];
            stream.Position = item.HeaderOffset;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) throw Unreadable("Truncated file header");
            if (!header.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8)) throw Unreadable("Bad magic number for file header");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var name = new byte[nameLength];
            var nameRead = stream.ReadAtLeast(name, name.Length, throwOnEndOfStream: false);
            name = name[..nameRead];
            if ((item.Flags & 0x20) != 0) throw Unreadable("compressed patched data (flag bit 5)");
            if ((item.Flags & 0x40) != 0) throw Unreadable("strong encryption (flag bit 6)");
            if (ZipLayout.Decode(name, flags) is not { } local) throw Unreadable("the file name in its header is not UTF-8");
            if (local != item.OriginalName)
                throw Unreadable($"File name in directory {Python.Repr(item.OriginalName)} and header {Python.BytesRepr(name)} differ.");
            if (item.HeaderOffset + 30 + nameLength + extraLength + item.CompressedSize > item.End && item.End != item.HeaderOffset)
                throw Unreadable($"Overlapped entries: {Python.Repr(item.OriginalName)} (possible zip bomb)");
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes)
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
            if (!IsPackagePath(relativePath)) throw Refused($"unsafe package path: {Python.Repr(relativePath)}");
        }

        private ZipItem Entry(string relativePath)
        {
            CheckPath(relativePath);
            if (!entries.TryGetValue(relativePath, out var item)) throw new FileNotFoundException("AI package file not found.", relativePath);
            if ((item.Flags & 0x1) != 0) throw Refused($"{Python.Repr(relativePath)} is encrypted in the ZIP");
            if (item.Method is not (0 or 8))
                throw Refused($"{Python.Repr(relativePath)} uses ZIP compression method {item.Method}; packages use deflate");
            return item;
        }

        private string Resolve(string relativePath)
        {
            CheckPath(relativePath);
            // A link inside a package folder could point anywhere, and a pipe or device could block or
            // never end; DocRedock writes neither. A folder where a file should be is a missing file.
            var path = directory!;
            foreach (var segment in relativePath.Split('/'))
            {
                path = System.IO.Path.Combine(path, segment);
                var item = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (item.LinkTarget is not null) throw Refused($"package path is a link: {Python.Repr(relativePath)}");
                if (!item.Exists) throw new FileNotFoundException("AI package file not found.", relativePath);
            }
            if (!File.Exists(path)) throw new FileNotFoundException("AI package file not found.", relativePath);
            return FileKind.IsRegular(path) ? path : throw Refused($"package path is not a regular file: {Python.Repr(relativePath)}");
        }
    }

    // The central directory of a package ZIP, read as Python's zipfile reads it, with the plain-ZIP
    // checks both example readers make before either library reads an entry.
    private sealed class ZipLayout : List<ZipLayout.Record>
    {
        public sealed record Record(string OriginalName, int Flags, int Method, long HeaderOffset, long CompressedSize, long Size,
            uint Crc, bool UnicodePath);

        public long DirectoryStart { get; private init; }

        private static readonly UTF8Encoding StrictName = new(false, true);

        // Code page 437 above 0x7F, as Python decodes a ZIP name without the UTF-8 flag.
        private const string Cp437High =
            "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

        public static string? Decode(ReadOnlySpan<byte> name, int flags)
        {
            if ((flags & 0x800) != 0)
            {
                try { return StrictName.GetString(name); }
                catch (DecoderFallbackException) { return null; }
            }
            var text = new StringBuilder(name.Length);
            foreach (var value in name) text.Append(value < 0x80 ? (char)value : Cp437High[value - 0x80]);
            return text.ToString();
        }

        public static ZipLayout Read(string path)
        {
            InvalidDataException NotZip(string detail) => new($"not a package folder or ZIP archive: {path} ({detail})");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var start = Math.Max(length - 65535 - 22, 0);
            var tail = new byte[length - start];
            stream.Position = start;
            stream.ReadExactly(tail);
            // The end record: the last 22 bytes when there is no comment, else the last signature.
            var at = tail.Length >= 22 && tail.AsSpan(tail.Length - 22, 4).SequenceEqual("PK\u0005\u0006"u8) &&
                tail[^1] == 0 && tail[^2] == 0 ? tail.Length - 22 : tail.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
            if (at < 0 || at + 22 > tail.Length) throw NotZip("File is not a zip file");
            long disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 4));
            long directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 6));
            long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 10));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 12));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 16));
            var location = start + at;
            if (location >= 20)
            {
                var locator = new byte[20];
                stream.Position = location - 20;
                stream.ReadExactly(locator);
                if (locator.AsSpan(0, 4).SequenceEqual("PK\u0006\u0007"u8))
                {
                    var record = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                    var data = new byte[56];
                    if (record >= 0 && record + 56 <= length)
                    {
                        stream.Position = record;
                        stream.ReadExactly(data);
                        if (data.AsSpan(0, 4).SequenceEqual("PK\u0006\u0006"u8))
                        {
                            disk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
                            directoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
                            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32));
                            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40));
                            offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48));
                            location = record;
                        }
                    }
                }
            }
            if (size < 0 || offset < 0 || offset + size > length) throw NotZip("Bad offset for central directory");
            var directory = new byte[size];
            stream.Position = offset;
            stream.ReadExactly(directory);
            var layout = new ZipLayout { DirectoryStart = offset };
            for (var position = 0; position < directory.Length;)
            {
                if (position + 46 > directory.Length) throw NotZip("Truncated central directory");
                ReadOnlySpan<byte> header = directory.AsSpan(position);
                if (!header[..4].SequenceEqual("PK\u0001\u0002"u8)) throw NotZip("Bad magic number for central directory");
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
                if (position + 46 + nameLength + extraLength + commentLength > directory.Length) throw NotZip("Truncated central directory");
                var name = Decode(header.Slice(46, nameLength), flags) ?? throw NotZip("an entry name is not UTF-8");
                var extract = header[6];
                if (extract > 63)
                    throw new InvalidDataException($"{path} uses a ZIP feature this reader does not read (zip file version {(extract / 10.0).ToString("0.0", CultureInfo.InvariantCulture)})");
                long compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
                long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
                long headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
                var unicodePath = false;
                var extra = header.Slice(46 + nameLength, extraLength);
                while (extra.Length >= 4)
                {
                    var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                    var blockLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                    if (blockLength + 4 > extra.Length) throw NotZip($"Corrupt extra field {id:x4} (size={blockLength})");
                    var block = extra.Slice(4, blockLength);
                    if (id == 0x0001)
                    {
                        // Zip64 sizes and offset, in this order, for each field the record left at 0xFFFFFFFF.
                        if (uncompressed == 0xFFFFFFFF) uncompressed = Zip64Value(ref block) ?? throw NotZip("Corrupt zip64 extra field");
                        if (compressed == 0xFFFFFFFF) compressed = Zip64Value(ref block) ?? throw NotZip("Corrupt zip64 extra field");
                        if (headerOffset == 0xFFFFFFFF) headerOffset = Zip64Value(ref block) ?? throw NotZip("Corrupt zip64 extra field");
                    }
                    else if (id == 0x7075) unicodePath = true;
                    extra = extra[(4 + blockLength)..];
                }
                layout.Add(new Record(name, flags, BinaryPrimitives.ReadUInt16LittleEndian(header[10..]), headerOffset, compressed, uncompressed,
                    BinaryPrimitives.ReadUInt32LittleEndian(header[16..]), unicodePath));
                position += 46 + nameLength + extraLength + commentLength;
            }
            if (disk != 0 || directoryDisk != 0) throw new InvalidDataException("the ZIP spans several disks");
            if (offset + size != location) throw new InvalidDataException("the ZIP's central directory does not end where its end record begins");
            if (count != layout.Count)
                throw new InvalidDataException($"the ZIP's end record counts {count} entries, but its central directory holds {layout.Count}");
            return layout;
        }

        private static long? Zip64Value(ref ReadOnlySpan<byte> block)
        {
            if (block.Length < 8) return null;
            var value = (long)BinaryPrimitives.ReadUInt64LittleEndian(block);
            block = block[8..];
            return value;
        }
    }

    // How docs/examples/ai_package_reader.py words things, so both readers report a refused file alike.
    private static class Python
    {
        public static string Repr(string value)
        {
            var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
            var text = new StringBuilder().Append(quote);
            foreach (var character in value)
            {
                if (character == '\\' || character == quote) text.Append('\\').Append(character);
                else if (character == '\t') text.Append("\\t");
                else if (character == '\n') text.Append("\\n");
                else if (character == '\r') text.Append("\\r");
                else if (character < 0x20 || character is >= '\u007f' and <= ' ')
                    text.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                else if (char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
                         UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or
                         UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator)
                    text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(character);
            }
            return text.Append(quote).ToString();
        }

        public static string BytesRepr(ReadOnlySpan<byte> value)
        {
            var quote = value.Contains((byte)'\'') && !value.Contains((byte)'"') ? '"' : '\'';
            var text = new StringBuilder("b").Append(quote);
            foreach (var item in value)
            {
                if (item == '\\' || item == quote) text.Append('\\').Append((char)item);
                else if (item == '\t') text.Append("\\t");
                else if (item == '\n') text.Append("\\n");
                else if (item == '\r') text.Append("\\r");
                else if (item is < 0x20 or >= 0x7f) text.Append("\\x").Append(item.ToString("x2", CultureInfo.InvariantCulture));
                else text.Append((char)item);
            }
            return text.Append(quote).ToString();
        }
    }

    // Whether an existing path names a regular file, from stat(2) as DocRedock.Cli's FileIdentity
    // reads it: false for a pipe, socket or device, which could block or never end when read. True
    // where folders hold none of those (Windows) or stat is unavailable.
    private static class FileKind
    {
        private const int StatBufferSize = 256;

        public static bool IsRegular(string path)
        {
            try
            {
                var mode = OperatingSystem.IsMacOS() ? MacMode(path) : OperatingSystem.IsLinux() ? LinuxMode(path) : null;
                return mode is not { } value || (value & 0xF000) == 0x8000;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return true;
            }
        }

        private static bool Supported => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

        // macOS, 64-bit-inode layout on both architectures: st_mode is a uint16 at offset 4.
        [SupportedOSPlatform("macos")]
        private static int? MacMode(string path)
        {
            if (!Supported) return null;
            var buffer = new byte[StatBufferSize];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatX64(path, buffer) : MacStatArm64(path, buffer);
            return result == 0 ? BitConverter.ToUInt16(buffer, 4) : null;
        }

        // Linux (glibc and musl): st_mode is a uint32 at offset 24 on x86_64 and 16 on aarch64. glibc
        // before 2.33 exports only the versioned __xstat.
        [SupportedOSPlatform("linux")]
        private static int? LinuxMode(string path)
        {
            if (!Supported) return null;
            var buffer = new byte[StatBufferSize];
            int result;
            try { result = LinuxStat(path, buffer); }
            catch (EntryPointNotFoundException) { result = LinuxXStat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, path, buffer); }
            return result == 0 ? (int)BitConverter.ToUInt32(buffer, RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16) : null;
        }

        [SupportedOSPlatform("macos")]
        [DllImport("libc", EntryPoint = "stat$INODE64")]
        private static extern int MacStatX64(string path, byte[] statBuffer);

        [SupportedOSPlatform("macos")]
        [DllImport("libc", EntryPoint = "stat")]
        private static extern int MacStatArm64(string path, byte[] statBuffer);

        [SupportedOSPlatform("linux")]
        [DllImport("libc", EntryPoint = "stat")]
        private static extern int LinuxStat(string path, byte[] statBuffer);

        [SupportedOSPlatform("linux")]
        [DllImport("libc", EntryPoint = "__xstat")]
        private static extern int LinuxXStat(int version, string path, byte[] statBuffer);
    }
}

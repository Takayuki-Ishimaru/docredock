using System.Buffers.Binary;
using System.Buffers;
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
    IReadOnlyList<AiPackageManifestFile> Files)
{
    /// <summary>The multiplier recorded at the manifest root, or null when absent. Part estimates
    /// are returned as stored; the reader never applies this multiplier again. A missing multiplier
    /// does not imply that a part has an estimate or that its estimate used a multiplier of one.</summary>
    public double? TokenEstimateMultiplier { get; init; }
}

/// <summary>Node identities (and worksheet cell addresses) of one part's source location.</summary>
public sealed record AiPackageSourceGroup(string SourceId, IReadOnlyList<string> NodeIds, IReadOnlyList<string?>? CellAddresses);

/// <summary>What a package's report.json says about review: whether something needs comparison with
/// the source (review pages, OCR items, worksheet table boundaries), and the summary's
/// <c>review_items_detected</c> and <c>hidden_content_included</c> flags (null in a report that predates
/// them). None of them certifies the conversion.</summary>
public sealed record AiPackageReview(bool Required, bool? ReviewItemsDetected, bool? HiddenContentIncluded)
{
    public bool ConversionReviewRequired { get; init; }
    public bool RevisionSharingRequired { get; init; }
    public RevisionSharingReview? SharingReview { get; init; }

    /// <summary>Reader policy only; does not alter the package's report. Older complete packages
    /// without an inclusion flag are conservatively treated as needing hidden-content review.</summary>
    public bool HiddenReviewRequired(bool requireHiddenReview, string contentPolicy) => requireHiddenReview &&
        (HiddenContentIncluded == true || HiddenContentIncluded is null && contentPolicy == "complete");

    public IReadOnlyList<string> StopReasons(bool requireHiddenReview, string contentPolicy)
    {
        var reasons = new List<string>();
        if (ConversionReviewRequired || Required && !RevisionSharingRequired) reasons.Add("conversion_review");
        if (RevisionSharingRequired) reasons.Add("revision_sharing");
        if (HiddenReviewRequired(requireHiddenReview, contentPolicy)) reasons.Add("hidden_content");
        return reasons;
    }
}

/// <summary>One part read by <see cref="AiPackageManifestReader.ReadVerifiedPartAsync"/>: its Markdown,
/// exactly the bytes that were verified, its source-index groups and the package's review state,
/// with the manifest the package was checked against.</summary>
public sealed record AiPackageVerifiedPart(
    AiPackageManifest Manifest,
    AiPackageManifestPart Part,
    string Markdown,
    IReadOnlyList<AiPackageSourceGroup> Sources,
    AiPackageReview Review);

/// <summary>A verified in-memory snapshot. Later reads and searches use the checked bytes.</summary>
public sealed record AiPackageVerifiedPackage(AiPackageManifest Manifest,
    IReadOnlyList<AiPackageVerifiedPart> Parts, AiPackageReview Review, string ReviewMarkdown);

/// <summary>Whole-package verification with only the checked review text retained.</summary>
public sealed record AiPackageVerifiedReview(AiPackageManifest Manifest, AiPackageReview Review, string ReviewMarkdown);

/// <summary>A package file is missing, changed, refused or not listed in the manifest.
/// <see cref="Problems"/> holds the lines <see cref="AiPackageManifestReader.VerifyFilesAsync"/> returns.</summary>
public sealed class AiPackageVerificationException(IReadOnlyList<string> problems)
    : Exception("AI package verification failed: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

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
        return await ReadManifestAsync(files, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AiPackageManifest> ReadManifestAsync(PackageFiles files, CancellationToken cancellationToken)
    {
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

    /// <summary>Checks the package and returns one part with what it was checked against, in one call:
    /// every listed file must match its size and SHA-256 with no other file in the package
    /// (<see cref="VerifyFilesAsync"/>), and the source index and report must be well formed. The part's
    /// Markdown is exactly the bytes that were verified. Raises <see cref="AiPackageVerificationException"/>
    /// when a file is missing, changed, refused or not listed, <see cref="KeyNotFoundException"/> for an
    /// unknown part id, and what <see cref="ReadAsync"/>, <see cref="ReadSourceIndexAsync"/> and
    /// <see cref="ReadReviewAsync"/> raise for a malformed package.</summary>
    public static async Task<AiPackageVerifiedPart> ReadVerifiedPartAsync(string packagePath, string partId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partId);
        using var files = new PackageFiles(packagePath);
        var manifest = await ReadManifestAsync(files, cancellationToken).ConfigureAwait(false);
        var part = manifest.Parts.FirstOrDefault(item => item.Id == partId) ??
            throw new KeyNotFoundException($"AI package has no part '{partId}'.");
        // Each file used after the checks is read once, so the bytes checked are the bytes used. A file
        // of another size, or one that is missing or refused, is left for verification to report.
        var listed = manifest.Files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[] { part.Path, manifest.Report }.Concat(manifest.SourceIndex is { } index ? [index] : []))
        {
            if (contents.ContainsKey(path)) continue;
            try
            {
                if (files.Size(path) == listed[path]) contents[path] = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { }
            catch (InvalidDataException refused) when (IsRefused(refused)) { }
        }
        var problems = await VerifyAsync(files, manifest, contents, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0) throw new AiPackageVerificationException(problems);
        var sources = SourceIndex(manifest, manifest.SourceIndex is { } name ? contents[name] : null);
        var review = Review(manifest, contents[manifest.Report]);
        return new AiPackageVerifiedPart(manifest, part, Utf8(contents[part.Path], part.Path), sources[part.Id], review);
    }

    /// <summary>Verifies every file and the source index, but retains only report/review bytes.
    /// Unselected document content is hashed with a bounded buffer and is never decoded or retained.</summary>
    public static async Task<AiPackageVerifiedReview> ReadVerifiedReviewAsync(string packagePath,
        CancellationToken cancellationToken = default)
    {
        using var files = new PackageFiles(packagePath);
        var manifest = await ReadManifestAsync(files, cancellationToken).ConfigureAwait(false);
        var listed = manifest.Files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[] { manifest.Report, manifest.Review }
                     .Concat(manifest.SourceIndex is { } index ? [index] : []).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (files.Size(path) == listed[path]) contents[path] = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { }
            catch (InvalidDataException refused) when (IsRefused(refused)) { }
        }
        var problems = await VerifyAsync(files, manifest, contents, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0) throw new AiPackageVerificationException(problems);
        _ = SourceIndex(manifest, manifest.SourceIndex is { } name ? contents[name] : null);
        return new(manifest, Review(manifest, contents[manifest.Report]), Utf8(contents[manifest.Review], manifest.Review));
    }

    public static async Task<AiPackageVerifiedPackage> ReadVerifiedPackageAsync(string packagePath,
        CancellationToken cancellationToken = default)
    {
        using var files = new PackageFiles(packagePath);
        var manifest = await ReadManifestAsync(files, cancellationToken).ConfigureAwait(false);
        var listed = manifest.Files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in manifest.Parts.Select(part => part.Path).Append(manifest.Report).Append(manifest.Review)
                     .Concat(manifest.SourceIndex is { } index ? [index] : []).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (files.Size(path) == listed[path]) contents[path] = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { }
            catch (InvalidDataException refused) when (IsRefused(refused)) { }
        }
        var problems = await VerifyAsync(files, manifest, contents, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0) throw new AiPackageVerificationException(problems);
        var sources = SourceIndex(manifest, manifest.SourceIndex is { } name ? contents[name] : null);
        var review = Review(manifest, contents[manifest.Report]);
        return new(manifest, manifest.Parts.Select(part => new AiPackageVerifiedPart(manifest, part,
            Utf8(contents[part.Path], part.Path), sources[part.Id], review)).ToArray(), review,
            Utf8(contents[manifest.Review], manifest.Review));
    }

    /// <summary>Node identities and cell addresses of every part, keyed by part id. Schema 2.x reads
    /// the source index; schema 1.x groups the inline node identities by location. The index must
    /// agree with the manifest: its schema is 1.x, it lists each manifest part exactly once, a part's
    /// groups name exactly that part's locations, their node identities add up to its node count,
    /// and cell addresses, when present, have one entry per node identity. In every schema a part
    /// lists a node once, and every part that lists a node puts it at the same location and cell.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<AiPackageSourceGroup>>> ReadSourceIndexAsync(
        string packagePath, AiPackageManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SourceIndex is null) return SourceIndex(manifest, null);
        byte[] bytes;
        using (var files = new PackageFiles(packagePath))
            bytes = await files.ReadAsync(manifest.SourceIndex, cancellationToken).ConfigureAwait(false);
        return SourceIndex(manifest, bytes);
    }

    private static Dictionary<string, IReadOnlyList<AiPackageSourceGroup>> SourceIndex(AiPackageManifest manifest, byte[]? bytes)
    {
        // Node id -> the first part that listed it and where that part put it.
        var places = new Dictionary<string, (string Part, string SourceId, string? Cell)>(StringComparer.Ordinal);
        if (manifest.SourceIndex is null)
        {
            if (!manifest.SchemaVersion.StartsWith("1.", StringComparison.Ordinal))
                throw new InvalidDataException("AI package manifest has no source_index.");
            var legacy = new Dictionary<string, IReadOnlyList<AiPackageSourceGroup>>(StringComparer.Ordinal);
            var manifestFields = new Fields("manifest.json");
            var partPosition = 0;
            foreach (var part in manifest.Parts)
            {
                var groups = part.Locations.Select(location => LegacyGroup(part, location.Id)).ToArray();
                CheckNodes(manifestFields, $"parts[{partPosition++}]", part.Id, groups, places);
                legacy[part.Id] = groups;
            }
            return legacy;
        }
        var name = manifest.SourceIndex;
        using var index = ParseJson(Utf8(bytes!, name), name);
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
                    if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() != nodeIds.Length ||
                        raw.EnumerateArray().Any(cell => cell.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        throw fields.Invalid($"{groupWhere}.cell_addresses must hold one address or null for each of its {nodeIds.Length} node_ids");
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
            CheckNodes(fields, where, id, groups, places);
            result[id] = groups;
        }
        var absent = manifest.Parts.Select(part => part.Id).Where(id => !result.ContainsKey(id)).ToArray();
        if (absent.Length > 0) throw fields.Invalid($"no entry for {string.Join(", ", absent)}");
        return result;
    }

    // A part lists a node once, and every part that lists a node puts it at the same location and
    // cell: joining text to its source by node id must have one answer.
    private static void CheckNodes(Fields fields, string where, string partId, IReadOnlyList<AiPackageSourceGroup> groups,
        Dictionary<string, (string Part, string SourceId, string? Cell)> places)
    {
        static string Place(string sourceId, string? cell) => sourceId + " " + (cell is null ? "without a cell" : "cell " + Python.Repr(cell));
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
            for (var index = 0; index < group.NodeIds.Count; index++)
            {
                var node = group.NodeIds[index];
                var cell = group.CellAddresses?[index];
                if (!listed.Add(node)) throw fields.Invalid($"{where} lists node {Python.Repr(node)} twice");
                if (!places.TryAdd(node, (partId, group.SourceId, cell)) && places[node] is var first &&
                    (first.SourceId != group.SourceId || first.Cell != cell))
                    throw fields.Invalid($"{where} puts node {Python.Repr(node)} at {Place(group.SourceId, cell)}, but part {first.Part} puts it at {Place(first.SourceId, first.Cell)}");
            }
    }

    /// <summary>Checks the package against its manifest's file list. Returns one message per listed file
    /// that is missing, changed or refused (a link, pipe or device in a package folder, an encrypted or
    /// unreadable ZIP entry) and per other file in the package, in the same words as
    /// docs/examples/ai_package_reader.py; an empty list means the package holds exactly the listed files
    /// (and manifest.json), each matching its size and SHA-256. Files an operating system leaves behind
    /// (.DS_Store, Thumbs.db, desktop.ini, AppleDouble "._" files, __MACOSX folders) are not reported.
    /// The manifest read by <see cref="Parse(string)"/> already lists every file it refers to. A package
    /// that cannot be opened at all (not a folder or a plain ZIP) raises <see cref="InvalidDataException"/>.</summary>
    public static async Task<IReadOnlyList<string>> VerifyFilesAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var files = new PackageFiles(packagePath);
        return await VerifyAsync(files, manifest, new Dictionary<string, byte[]>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    // contents holds bytes the caller already read; they are checked instead of reading the file again,
    // so the caller uses exactly the bytes that were verified.
    private static async Task<List<string>> VerifyAsync(PackageFiles files, AiPackageManifest manifest,
        Dictionary<string, byte[]> contents, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long actualBytes;
            string actual;
            if (contents.TryGetValue(file.Path, out var bytes))
            {
                actualBytes = bytes.LongLength;
                actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            else
                try
                {
                    // A different size already says the content changed; it is found without reading the file.
                    var size = files.Size(file.Path);
                    if (size != file.Bytes)
                    {
                        problems.Add($"{file.Path}: size mismatch (expected {file.Bytes} bytes, got {size})");
                        continue;
                    }
                    (actualBytes, actual) = await files.HashAsync(file.Path, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException) { problems.Add($"{file.Path}: missing"); continue; }
                catch (InvalidDataException refused) when (IsRefused(refused)) { problems.Add($"{file.Path}: refused ({refused.Message})"); continue; }
            var expected = file.Sha256.ToLowerInvariant();
            if (actualBytes != file.Bytes) problems.Add($"{file.Path}: size mismatch (expected {file.Bytes} bytes, got {actualBytes})");
            else if (actual != expected) problems.Add($"{file.Path}: sha256 mismatch (expected {expected}, got {actual})");
        }
        // Anything else in the package is not covered by the manifest, whatever refers to it. A link
        // where a folder of listed files belongs has already made those files refused.
        var listed = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in listed)
            for (var end = path.IndexOf('/'); end >= 0; end = path.IndexOf('/', end + 1))
                folders.Add(path[..end]);
        foreach (var name in files.Files().Order(Utf8Order))
            if (name != "manifest.json" && !listed.Contains(name) && !folders.Contains(name) && !LeftBySystem(name))
                problems.Add($"{Shown(name)}: not listed in the manifest");
        return problems;
    }

    // Files operating systems leave in folders and archives (Finder, Explorer); never part of a package.
    private static bool LeftBySystem(string name)
    {
        var start = name.LastIndexOf('/') + 1;
        var baseName = name[start..];
        return baseName is ".DS_Store" or "Thumbs.db" or "desktop.ini" || baseName.StartsWith("._", StringComparison.Ordinal) ||
            name.Split('/')[0] == "__MACOSX";
    }

    // Names sorted as Python sorts them: by code point, which is the order of their UTF-8 bytes.
    private static readonly Comparer<string> Utf8Order = Comparer<string>.Create((left, right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right)));

    /// <summary>Whether the package's report.json says something needs comparison with the source;
    /// <see cref="ReadReviewAsync"/>'s <see cref="AiPackageReview.Required"/>.</summary>
    public static async Task<bool> ReadReviewRequiredAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default) =>
        (await ReadReviewAsync(packagePath, manifest, cancellationToken).ConfigureAwait(false)).Required;

    /// <summary>What the package's report.json says about review: whether something needs comparison
    /// with the source (review pages, OCR items, worksheet table boundaries) and the summary's flags.
    /// Read review.md before giving a package that requires review to a model. A report whose schema is
    /// not 1.x raises <see cref="AiPackageSchemaException"/>; one without a boolean <c>review.required</c>,
    /// or with a summary flag that is not a boolean, raises <see cref="InvalidDataException"/>.</summary>
    public static async Task<AiPackageReview> ReadReviewAsync(string packagePath, AiPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] bytes;
        using (var files = new PackageFiles(packagePath))
            bytes = await files.ReadAsync(manifest.Report, cancellationToken).ConfigureAwait(false);
        return Review(manifest, bytes);
    }

    private static AiPackageReview Review(AiPackageManifest manifest, byte[] bytes)
    {
        using var report = ParseJson(Utf8(bytes, manifest.Report), manifest.Report);
        var fields = new Fields(manifest.Report);
        var root = fields.Object(report.RootElement, "the report");
        var version = fields.Text(root, "", "schema_version");
        if (!IsMinorVersionOf(version, "1"))
            throw new AiPackageSchemaException(version, $"AI package {manifest.Report} schema_version '{version}' is unsupported (supported: 1.x).");
        var review = fields.Object(fields.Required(root, "", "review"), "review");
        var required = fields.Required(review, "review", "required");
        if (required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw fields.Invalid($"review.required must be true or false, got {Fields.Kind(required)}");
        var sharingRequired = false;
        RevisionSharingReview? sharingReview = null;
        if (Fields.Optional(root, "sharing_review") is { } rawSharing)
        {
            var sharing = fields.Object(rawSharing, "sharing_review");
            foreach (var key in new[] { "required", "revisions_present", "revisions_included", "deleted_text_included" })
            {
                var flag = fields.Required(sharing, "sharing_review", key);
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw fields.Invalid($"sharing_review.{key} must be true or false, got {Fields.Kind(flag)}");
            }
            var policy = fields.Text(sharing, "sharing_review", "revisions_policy");
            if (policy is not ("final" or "markup" or "reject"))
                throw fields.Invalid($"sharing_review.revisions_policy must be final, markup or reject, got {JsonSerializer.Serialize(policy)}");
            sharingRequired = sharing.GetProperty("required").GetBoolean();
            sharingReview = new(sharingRequired, sharing.GetProperty("revisions_present").GetBoolean(),
                sharing.GetProperty("revisions_included").GetBoolean(), sharing.GetProperty("deleted_text_included").GetBoolean(), policy);
        }
        else if (Fields.Optional(root, "diagnostics") is { ValueKind: JsonValueKind.Array } diagnostics)
            sharingRequired = diagnostics.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() == "TrackedRevisionsPresent");
        var summary = Fields.Optional(root, "summary") is { } value ? fields.Object(value, "summary") : (JsonElement?)null;
        var itemsDetected = summary is { } flags ? fields.OptionalFlag(flags, "summary", "review_items_detected") : null;
        return new AiPackageReview(required.ValueKind == JsonValueKind.True || sharingRequired,
            itemsDetected, summary is { } other ? fields.OptionalFlag(other, "summary", "hidden_content_included") : null)
        {
            ConversionReviewRequired = itemsDetected == true || required.GetBoolean() && (itemsDetected != false || !sharingRequired),
            RevisionSharingRequired = sharingRequired,
            SharingReview = sharingReview,
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
        // 2.x keeps node identities only in the source index, so a 2.x package always has one.
        var sourceIndex = legacy ? null : fields.Path(root, "", "source_index");
        var multiplier = fields.OptionalMultiplier(root, "", "token_estimate_multiplier");
        var manifest = new AiPackageManifest(version, fields.OptionalText(root, "", "generator_version"),
            fields.Text(source, "source", "file_name"), fields.Sha256(source, "source", "sha256"), fields.Text(source, "source", "format"),
            fields.Text(root, "", "content_policy"), fields.OptionalCount(root, "", "target_characters") ?? 0,
            fields.Flag(root, "", "table_row_blocks"),
            fields.OptionalPath(root, "", "document") ?? "document.md", fields.OptionalPath(root, "", "review") ?? "review.md",
            fields.OptionalPath(root, "", "report") ?? "report.json", sourceIndex, locations, parts, files)
        { TokenEstimateMultiplier = multiplier };
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
            if (path == "manifest.json") throw fields.Invalid($"{where}.path is manifest.json, which cannot hold its own size and SHA-256");
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
            if (sources.Length != nodeIds.Length)
                throw fields.Invalid($"{where} lists {nodeIds.Length} node_ids but {sources.Length} sources; schema 1.x pairs them one to one");
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
            parts.Add(new AiPackageManifestPart(fields.Text(part, where, "id"), fields.Path(part, where, "path"), nodeIds.Length, partLocations,
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
            if (sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length)
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
        var tables = new Dictionary<string, List<AiPackageManifestPart>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var part in parts)
        {
            foreach (var (key, neighbour) in new[] { ("previous_part", part.TableBlock?.PreviousPart), ("next_part", part.TableBlock?.NextPart) })
                if (neighbour is not null && !ids.Contains(neighbour))
                    throw fields.Invalid($"part {part.Id} {key} names an unknown part '{neighbour}'");
            if (part.TableBlock is not { } block) continue;
            if (!tables.TryGetValue(block.TableId, out var blocks))
            {
                tables.Add(block.TableId, blocks = []);
                order.Add(block.TableId);
            }
            blocks.Add(part);
        }
        // The blocks of a cut table form one chain, 1 to count, each naming its neighbours, so a reader
        // following next_part reaches every block once and never runs into another table or in a circle.
        foreach (var tableId in order)
        {
            var blocks = tables[tableId];
            var byIndex = new Dictionary<int, AiPackageManifestPart>();
            foreach (var part in blocks)
            {
                var block = part.TableBlock!;
                if (block.Count != blocks.Count)
                    throw fields.Invalid($"part {part.Id} says table {Python.Repr(tableId)} has {block.Count} blocks, but the manifest lists {blocks.Count}");
                if (!byIndex.TryAdd(block.Index, part))
                    throw fields.Invalid($"table {Python.Repr(tableId)} gives index {block.Index} to parts {byIndex[block.Index].Id} and {part.Id}");
            }
            for (var index = 1; index <= blocks.Count; index++)
            {
                var part = byIndex[index];
                foreach (var (key, neighbour, actual) in new[]
                         { ("previous_part", index - 1, part.TableBlock!.PreviousPart), ("next_part", index + 1, part.TableBlock.NextPart) })
                {
                    var expected = byIndex.TryGetValue(neighbour, out var other) ? other.Id : null;
                    if (actual == expected) continue;
                    throw fields.Invalid($"part {part.Id} table_block.{key} is {(actual is null ? "null" : Python.Repr(actual))}, but " +
                        (expected is null ? $"it is the {(neighbour < index ? "first" : "last")} block of table {Python.Repr(tableId)}"
                            : $"block {neighbour}/{blocks.Count} of table {Python.Repr(tableId)} is {expected}"));
                }
            }
        }
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
    // followed: absolute, drive and ".." paths, backslashes, colons (C:x, alternate data streams), NUL and
    // the other characters IsUnshown names, which package names never contain.
    private static bool IsPackagePath(string path) =>
        path.Length > 0 && path.IndexOfAny(['\\', ':']) < 0 && !path.Any(IsUnshown) && !path.StartsWith('/') &&
        !System.IO.Path.IsPathRooted(path) && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    // Characters that move the cursor, start a terminal escape sequence, break a line or reorder the text
    // around them when printed: C0 and C1 controls, DEL, line and paragraph separators and bidirectional
    // formatting characters (the Python reader's UNSHOWN_RE, which also names lone surrogates).
    private static bool IsUnshown(char character) => character is <= '\u001f' or (>= '\u007f' and <= '\u009f') or '\u061c' or
        '\u200e' or '\u200f' or '\u2028' or '\u2029' or (>= '\u202a' and <= '\u202e') or (>= '\u2066' and <= '\u2069');

    // A name as both readers print it: the characters IsUnshown names, and lone surrogates, as \xNN or \uNNNN.
    private static string Shown(string text)
    {
        var shown = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                shown.Append(character).Append(text[++index]);
            else if (IsUnshown(character) || char.IsSurrogate(character))
                shown.Append(character < 0x100 ? "\\x" : "\\u").Append(((int)character).ToString(character < 0x100 ? "x2" : "x4", CultureInfo.InvariantCulture));
            else shown.Append(character);
        }
        return shown.ToString();
    }

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

        public bool Flag(JsonElement element, string where, string key) => OptionalFlag(element, where, key) ?? false;

        // A stored estimate is already widened by the exporter's multiplier, so a reader only reports
        // it: the value must be a finite number in the exporter's own range, never recomputed.
        public double? OptionalMultiplier(JsonElement element, string where, string key)
        {
            if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            var text = value.GetRawText();
            if (value.ValueKind != JsonValueKind.Number || !double.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var multiplier) || !double.IsFinite(multiplier) ||
                multiplier < AiTokenEstimate.MinimumMultiplier || multiplier > AiTokenEstimate.MaximumMultiplier)
                throw Invalid($"{Name(where, key)} must be a finite number from {AiTokenEstimate.MinimumMultiplier:0.#} " +
                    $"to {AiTokenEstimate.MaximumMultiplier:0.#}, got {text}");
            return multiplier;
        }

        public bool? OptionalFlag(JsonElement element, string where, string key) => Optional(element, key) switch
        {
            null => null,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { } value => throw Invalid($"{Name(where, key)} must be true or false, got {Kind(value)}"),
        };

        public JsonElement.ArrayEnumerator Array(JsonElement element, string where, string key)
        {
            var value = Required(element, where, key);
            return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
                : throw Invalid($"{Name(where, key)} must be an array, got {Kind(value)}");
        }

        public string[] Strings(JsonElement element, string where, string key) => AsStrings(Array(element, where, key), where, key);

        public string[]? OptionalStrings(JsonElement element, string where, string key) => Optional(element, key) switch
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
        private readonly Dictionary<string, HashSet<string>> listings = new(StringComparer.Ordinal);

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
                await using var input = await item.Entry.OpenAsync(token).ConfigureAwait(false);
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

        // The same open, size and ZIP CRC rules as ReadAsync, with no payload-sized allocation.
        public async Task<(long Bytes, string Sha256)> HashAsync(string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var path = zip is null ? Resolve(relativePath) : null;
            var item = zip is null ? null : Entry(relativePath);
            if (item is not null) OpenChecks(relativePath, item);
            var length = item?.Size ?? new FileInfo(path!).Length;
            if (length > MaxFileBytes) throw TooLarge(relativePath);
            if (item is null && length == 0) return (0, Convert.ToHexStringLower(SHA256.HashData([])));
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var crc = 0xFFFFFFFFu;
            try
            {
                try
                {
                    await using var input = item is null ? File.OpenRead(path!) : await item.Entry.OpenAsync(token).ConfigureAwait(false);
                    while (item is null || total < length)
                    {
                        var count = item is null ? buffer.Length : (int)Math.Min(buffer.Length, length - total);
                        var read = await input.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > MaxFileBytes) throw TooLarge(relativePath);
                        hash.AppendData(buffer.AsSpan(0, read));
                        if (item is not null) crc = UpdateCrc32(crc, buffer.AsSpan(0, read));
                    }
                }
                catch (InvalidDataException exception) when (item is not null && !IsRefused(exception))
                {
                    throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: {exception.Message}", exception);
                }
                if (item is not null)
                {
                    if (~crc != item.Crc)
                        throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: Bad CRC-32 for file {Python.Repr(item.OriginalName)}");
                    if (total != length)
                        throw Refused($"cannot read {Python.Repr(relativePath)} from the ZIP: it ends before its size in the ZIP");
                }
                return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
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

        private static uint Crc32(ReadOnlySpan<byte> bytes) => ~UpdateCrc32(0xFFFFFFFFu, bytes);

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
            return crc;
        }

        /// <summary>A manifest.json one folder down: the package's parent was given, or the package
        /// folder itself was zipped.</summary>
        public string? NestedManifest() => zip is not null
            ? entries.Keys.Where(name => name.Count(character => character == '/') == 1 && name.EndsWith("/manifest.json", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).FirstOrDefault()
            : Directory.EnumerateDirectories(directory!).Order(StringComparer.Ordinal)
                .Where(folder => File.Exists(System.IO.Path.Combine(folder, "manifest.json")))
                .Select(folder => System.IO.Path.GetFileName(folder) + "/manifest.json").FirstOrDefault();

        /// <summary>Every file in the package as a package path: the ZIP entries that are not folders, or
        /// everything in the folder that is not a folder (links, pipes and devices included), without
        /// following links.</summary>
        public IEnumerable<string> Files()
        {
            if (zip is not null) return entries.Keys.Where(name => !name.EndsWith('/'));
            var names = new List<string>();
            Walk(directory!, string.Empty);
            return names;

            void Walk(string folder, string prefix)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    var name = prefix + System.IO.Path.GetFileName(path);
                    var item = new DirectoryInfo(path);
                    if (item.Exists && item.LinkTarget is null) Walk(path, name + "/");
                    else names.Add(name);
                }
            }
        }

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
                if (!Names(path).Contains(segment)) throw new FileNotFoundException("AI package file not found.", relativePath);
                path = System.IO.Path.Combine(path, segment);
                var item = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (item.LinkTarget is not null) throw Refused($"package path is a link: {Python.Repr(relativePath)}");
                if (!item.Exists) throw new FileNotFoundException("AI package file not found.", relativePath);
            }
            if (!File.Exists(path)) throw new FileNotFoundException("AI package file not found.", relativePath);
            return FileKind.IsRegular(path) ? path : throw Refused($"package path is not a regular file: {Python.Repr(relativePath)}");
        }

        // The exact names in a package folder. A name differing in letter case or Unicode form is a
        // missing file everywhere, as in a ZIP, not only where the file system tells the names apart.
        private HashSet<string> Names(string folder)
        {
            if (listings.TryGetValue(folder, out var names)) return names;
            try
            {
                names = Directory.EnumerateFileSystemEntries(folder).Select(System.IO.Path.GetFileName).OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (IOException) { names = []; }   // not a folder (a file, or nothing)
            listings.Add(folder, names);
            return names;
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
                    // The Zip64 end record ends where its locator begins, as Python 3.12's zipfile requires.
                    var locatorStart = location - 20;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) > 1)
                        throw new InvalidDataException("the ZIP spans several disks");
                    var record = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                    if (locatorStart - 56 < 0 || record > (ulong)(locatorStart - 56))
                        throw new InvalidDataException("the ZIP's Zip64 end record locator points past the record");
                    var data = new byte[56];
                    stream.Position = (long)record;
                    stream.ReadExactly(data);
                    if (!data.AsSpan(0, 4).SequenceEqual("PK\u0006\u0006"u8))
                        throw new InvalidDataException("the ZIP's Zip64 end record is not where its locator points");
                    if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4)) != (ulong)(locatorStart - (long)record - 12))
                        throw new InvalidDataException("the ZIP's Zip64 end record does not end where its locator begins");
                    disk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
                    directoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
                    count = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32));
                    size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40));
                    offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48));
                    location = (long)record;
                }
            }
            // Checked before the directory is read, in the Python reader's order (which checks before zipfile reads).
            if (disk != 0 || directoryDisk != 0) throw new InvalidDataException("the ZIP spans several disks");
            if (size < 0 || offset < 0 || offset != location - size)
                throw new InvalidDataException("the ZIP's central directory does not end where its end record begins");
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
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101",
            Justification = "libc stat requires UTF-8; LPUTF8Str avoids ANSI best-fit conversion.")]
        [DllImport("libc", EntryPoint = "stat$INODE64")]
        private static extern int MacStatX64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);

        [SupportedOSPlatform("macos")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101",
            Justification = "libc stat requires UTF-8; LPUTF8Str avoids ANSI best-fit conversion.")]
        [DllImport("libc", EntryPoint = "stat")]
        private static extern int MacStatArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);

        [SupportedOSPlatform("linux")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101",
            Justification = "libc stat requires UTF-8; LPUTF8Str avoids ANSI best-fit conversion.")]
        [DllImport("libc", EntryPoint = "stat")]
        private static extern int LinuxStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);

        [SupportedOSPlatform("linux")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101",
            Justification = "libc stat requires UTF-8; LPUTF8Str avoids ANSI best-fit conversion.")]
        [DllImport("libc", EntryPoint = "__xstat")]
        private static extern int LinuxXStat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);
    }
}

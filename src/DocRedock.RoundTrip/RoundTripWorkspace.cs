using System.Text;
using System.Text.Json;
using DocRedock.Core.Diff;
using DocRedock.Core.Documents;

namespace DocRedock.RoundTrip;

/// <summary>
/// The sidecar and immutable source area belonging to one DRMD projection.
/// The workspace does not mutate the original input file.
/// </summary>
public sealed class RoundTripWorkspace
{
    public const string CurrentSchemaVersion = "1.1";
    public string RootPath { get; }
    public string ManifestPath => Path.Combine(RootPath, "manifest.json");
    public string OriginalSourcePath => Path.Combine(RootPath, "source", OriginalStoredFileName(Manifest.Source.FileName));
    public RoundTripManifest Manifest { get; private set; }

    private RoundTripWorkspace(string rootPath, RoundTripManifest manifest)
    {
        RootPath = Path.GetFullPath(rootPath);
        Manifest = manifest;
    }

    public static async Task<RoundTripWorkspace> CreateAsync(
        string workspacePath,
        string sourcePath,
        RoundTripWorkspaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RoundTripWorkspaceOptions();
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The source file was not found.", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        var sourceHash = Hashing.File(sourcePath);
        var markdownPath = options.MarkdownPath is null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workspacePath))!, Path.GetFileNameWithoutExtension(sourcePath) + ".md")
            : Path.GetFullPath(options.MarkdownPath);
        if (options.MarkdownContent is null && !File.Exists(markdownPath))
            throw new FileNotFoundException("The Markdown projection was not found.", markdownPath);
        var markdownBytes = options.MarkdownContent is not null
            ? new UTF8Encoding(false).GetBytes(options.MarkdownContent.Replace("\r\n", "\n").Replace("\r", "\n"))
            : File.Exists(markdownPath) ? await File.ReadAllBytesAsync(markdownPath, cancellationToken).ConfigureAwait(false) : Array.Empty<byte>();

        var documentId = options.DocumentId ?? "doc_" + sourceHash[..16];
        var projectionId = options.ProjectionId ?? "proj_" + Hashing.Bytes(markdownBytes)[..16];
        var manifest = new RoundTripManifest
        {
            DocumentId = documentId,
            Generator = new GeneratorInfo { Version = options.GeneratorVersion ?? "0.2.0" },
            Source = new SourceInfo
            {
                FileName = sourceInfo.Name,
                Format = options.SourceFormat ?? FormatFromExtension(sourceInfo.Extension),
                Sha256 = sourceHash,
                SourceRevisionId = options.SourceRevisionId ?? "rev_" + sourceHash[..16],
                Size = sourceInfo.Length,
                MacroEnabled = options.SourceMacroEnabled,
            },
            Projection = new ProjectionInfo
            {
                FileName = Path.GetFileName(markdownPath),
                Profile = options.Profile,
                ProjectionId = projectionId,
                ContentPolicy = options.ContentPolicy,
            },
            Providers = options.Providers ?? new ProviderSet(),
            Ocr = options.Ocr ?? new OcrManifestInfo { Enabled = options.OcrEnabled },
            Preservation = options.Preservation ?? new PreservationInfo(),
            Capabilities = options.Capabilities ?? new CapabilityInfo
            {
                ByteRestore = true,
                EditableRestore = options.EditableRestore,
                Render = options.Render,
                GraphChunks = options.GraphChunks,
            },
            Integrity = new IntegrityInfo { MarkdownBaselineSha256 = Hashing.Bytes(markdownBytes) },
        };

        var finalRoot = Path.GetFullPath(workspacePath);
        if (Directory.Exists(finalRoot))
            throw new IOException("The workspace directory already exists.");
        if (options.MarkdownContent is not null && File.Exists(markdownPath))
            throw new IOException("The Markdown output already exists.");
        var parent = Path.GetDirectoryName(finalRoot)
            ?? throw new IOException("The workspace path has no parent directory.");
        Directory.CreateDirectory(parent);
        var stagingRoot = Path.Combine(parent, $".{Path.GetFileName(finalRoot)}.{Guid.NewGuid():N}.tmp");
        var wroteMarkdown = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            foreach (var relative in new[] { "source", "source/indexes", "graph", "maps", "assets", "derived/ocr", "derived/chunks", "derived/previews", "reports" })
                Directory.CreateDirectory(Path.Combine(stagingRoot, relative));

            var originalPath = Path.Combine(stagingRoot, "source", OriginalStoredFileName(sourceInfo.Name));
            await AtomicFile.CopyAsync(sourcePath, originalPath, cancellationToken).ConfigureAwait(false);
            if (options.MarkdownContent is not null)
            {
                await AtomicFile.WriteAsync(markdownPath, markdownBytes, cancellationToken).ConfigureAwait(false);
                wroteMarkdown = true;
            }
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "graph", "index.json"), "{\n  \"schema_version\": \"1.1\",\n  \"parts\": []\n}\n", cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "maps", "projection-map.jsonl"), "", cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "maps", "anchor-map.jsonl"), "", cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "assets", "index.json"), "{\n  \"schema_version\": \"1.0\",\n  \"assets\": []\n}\n", cancellationToken).ConfigureAwait(false);
            var exportReport = new BasicReport(
                "export",
                true,
                "F0",
                "byte-identical",
                "Round-trip workspace baseline created",
                []);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "reports", "export-report.json"), JsonCanonicalizer.Serialize(exportReport), cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "reports", "export-report.md"), exportReport.ToMarkdown(), cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteUtf8Async(Path.Combine(stagingRoot, "reports", "provider-report.json"), JsonCanonicalizer.Serialize(new BasicReport(
                "providers", true, "F0", "byte-identical", "Provider execution details are recorded by the orchestrator.", [])), cancellationToken).ConfigureAwait(false);

            var stagingWorkspace = new RoundTripWorkspace(stagingRoot, manifest);
            await stagingWorkspace.RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
            Directory.Move(stagingRoot, finalRoot);
            return new RoundTripWorkspace(finalRoot, manifest);
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            if (wroteMarkdown && File.Exists(markdownPath)) File.Delete(markdownPath);
            throw;
        }
    }

    public static async Task<RoundTripWorkspace> OpenAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspacePath);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("manifest.json was not found.", manifestPath);
        var manifest = JsonSerializer.Deserialize<RoundTripManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonCanonicalizer.Options)
            ?? throw new WorkspaceIntegrityException("manifest.json is empty or invalid.");
        return new RoundTripWorkspace(root, manifest);
    }

    public async Task<WorkspaceIntegrityReport> VerifyAsync(string? markdownPath = null, bool requireUnchangedProjection = false, CancellationToken cancellationToken = default)
    {
        var issues = new List<IntegrityIssue>();
        var warnings = new List<IntegrityIssue>();
        if (!Directory.Exists(RootPath)) issues.Add(new("workspace.missing", "Workspace directory is missing.", RootPath));
        if (Manifest.SchemaVersion != CurrentSchemaVersion) issues.Add(new("schema.unsupported", $"Unsupported schema version '{Manifest.SchemaVersion}'.", "manifest.json"));
        if (string.IsNullOrWhiteSpace(Manifest.DocumentId)) issues.Add(new("manifest.document_id", "document_id is required.", "manifest.json"));

        var storedSourceName = OriginalStoredFileName(Manifest.Source.FileName);
        var originalPath = Path.Combine(RootPath, "source", storedSourceName);
        if (!File.Exists(originalPath)) issues.Add(new("source.missing", "The immutable original source is missing.", "source/" + storedSourceName));
        else
        {
            var sourceInfo = new FileInfo(originalPath);
            var hash = Hashing.File(originalPath);
            if (!hash.Equals(Manifest.Source.Sha256, StringComparison.OrdinalIgnoreCase)) issues.Add(new("source.hash", "The original source hash does not match manifest.", "manifest.json"));
            if (sourceInfo.Length != Manifest.Source.Size) issues.Add(new("source.size", "The original source size does not match manifest.", "manifest.json"));
        }

        var resolvedMarkdown = markdownPath is null
            ? Path.Combine(Directory.GetParent(RootPath)?.FullName ?? RootPath, Path.GetFileName(Manifest.Projection.FileName))
            : Path.GetFullPath(markdownPath);
        if (!File.Exists(resolvedMarkdown)) issues.Add(new("markdown.missing", "The bound Markdown projection is missing.", Manifest.Projection.FileName));
        var projectionChanged = false;
        if (File.Exists(resolvedMarkdown) && !Hashing.File(resolvedMarkdown).Equals(Manifest.Integrity.MarkdownBaselineSha256, StringComparison.OrdinalIgnoreCase))
        {
            projectionChanged = true;
            var diagnostic = new IntegrityIssue("markdown.hash", "The Markdown projection differs from its baseline hash.", Manifest.Projection.FileName);
            if (requireUnchangedProjection) issues.Add(diagnostic); else warnings.Add(diagnostic);
        }

        foreach (var required in new[] { "graph/index.json", "maps/projection-map.jsonl", "maps/anchor-map.jsonl", "assets/index.json", "reports/export-report.json", "reports/export-report.md", "reports/provider-report.json", "checksums.json" })
            if (!File.Exists(Path.Combine(RootPath, required))) issues.Add(new("sidecar.missing", $"Required sidecar '{required}' is missing.", required));
        if ((Manifest.Source.Format is "docx" or "xlsx" or "pptx") &&
            !StringComparer.Ordinal.Equals(Manifest.Providers.FormatAdapter.Id, "docredock.adapter.none"))
            foreach (var required in new[] { "source/indexes/package-index.json", "source/indexes/relationship-graph.json" })
                if (!File.Exists(Path.Combine(RootPath, required))) issues.Add(new("sidecar.missing", $"Required Office source index '{required}' is missing.", required));

        if (Manifest.Preservation.OriginalSliceIndexed &&
            !File.Exists(Path.Combine(RootPath, "source", "indexes", "xml-slices.jsonl")))
            issues.Add(new("slice_index.missing", "Manifest declares original slices but the raw slice index is missing.", "source/indexes/xml-slices.jsonl"));

        await VerifyProjectionMapAsync(issues, cancellationToken).ConfigureAwait(false);
        await VerifyAssetsAsync(issues, cancellationToken).ConfigureAwait(false);
        await VerifyOcrAsync(issues, cancellationToken).ConfigureAwait(false);
        await VerifyManifestIntegrityAsync(issues, cancellationToken).ConfigureAwait(false);
        await VerifyChecksumsAsync(issues, cancellationToken).ConfigureAwait(false);
        return new WorkspaceIntegrityReport { Issues = issues, Warnings = warnings, ProjectionChanged = projectionChanged };
    }

    public async Task VerifyStrictAsync(string? markdownPath = null, CancellationToken cancellationToken = default)
    {
        var report = await VerifyAsync(markdownPath, requireUnchangedProjection: true, cancellationToken).ConfigureAwait(false);
        if (!report.IsValid) throw new WorkspaceIntegrityException(string.Join("; ", report.Issues.Select(i => $"{i.Code}: {i.Message}")));
    }

    public async Task<RestoreResult> RestoreOriginalAsync(string destinationPath, string? markdownPath = null, CancellationToken cancellationToken = default)
    {
        // F0 is only valid for an untouched projection and intact sidecar.  Keep this
        // check in the library API so callers cannot accidentally turn an edited
        // Markdown file into a byte-identical restore by bypassing the CLI.
        await VerifyStrictAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        await VerifySourceAsync(cancellationToken).ConfigureAwait(false);
        var source = Path.Combine(RootPath, "source", OriginalStoredFileName(Manifest.Source.FileName));
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Restore destination must not be the immutable source.");
        if (File.Exists(destination)) throw new IOException("Restore destination already exists.");
        await AtomicFile.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        var report = new BasicReport("restore", true, "F0", "byte-identical", "Byte-identical original restored", Array.Empty<string>());
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "reports", "restore-report.json"), JsonCanonicalizer.Serialize(report), cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "reports", "restore-report.md"), report.ToMarkdown(), cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
        return new RestoreResult(destination, "F0", Manifest.Source.Sha256, true, Array.Empty<string>());
    }

    /// <summary>
    /// Restores the immutable original after a graph-aware comparison proved that
    /// the projection contains derived annotations only (for example an OCR correction).
    /// </summary>
    public async Task<RestoreResult> RestoreOriginalForDiffAsync(
        string destinationPath,
        DiffResult diff,
        string? markdownPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.DirtySet.HasOriginalMutations)
            throw new WorkspaceIntegrityException("F0 restore is not valid when the DirtySet mutates original content.");
        var verification = await VerifyAsync(markdownPath, requireUnchangedProjection: false, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new WorkspaceIntegrityException(string.Join("; ", verification.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        await VerifySourceAsync(cancellationToken).ConfigureAwait(false);
        var destination = Path.GetFullPath(destinationPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(OriginalSourcePath, destination))
            throw new IOException("Restore destination must not be the immutable source.");
        if (File.Exists(destination)) throw new IOException("Restore destination already exists.");
        await AtomicFile.CopyAsync(OriginalSourcePath, destination, cancellationToken).ConfigureAwait(false);
        var warnings = diff.PatchSet.Operations.Count == 0
            ? Array.Empty<string>()
            : new[] { "Derived annotations changed; the preserved source bytes were intentionally left unchanged." };
        var report = new BasicReport("restore", true, "F0", "byte-identical", "Preserved original restored", warnings);
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "reports", "restore-report.json"), JsonCanonicalizer.Serialize(report), cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "reports", "restore-report.md"), report.ToMarkdown(), cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
        return new RestoreResult(destination, "F0", Manifest.Source.Sha256, true, warnings);
    }

    public async Task WriteGraphAsync<T>(T graph, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RootPath, "graph", "index.json");
        var json = graph is DocumentGraph documentGraph
            ? DeterministicJson.Serialize(documentGraph) + "\n"
            : JsonCanonicalizer.Serialize(graph);
        await AtomicFile.WriteUtf8Async(path, json, cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteProjectionMapAsync(IEnumerable<string> jsonLines, CancellationToken cancellationToken = default)
    {
        var canonical = string.Join("", jsonLines.Select(JsonCanonicalizer.Canonicalize));
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "maps", "projection-map.jsonl"), canonical, cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteRawSliceIndexAsync(IEnumerable<string> jsonLines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var canonical = string.Join("", jsonLines.Select(JsonCanonicalizer.Canonicalize));
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "source", "indexes", "xml-slices.jsonl"), canonical, cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteSourceIndexAsync(string fileName, string json, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source index must be a JSON file name.", nameof(fileName));
        ArgumentNullException.ThrowIfNull(json);
        await AtomicFile.WriteUtf8Async(
            Path.Combine(RootPath, "source", "indexes", fileName),
            JsonCanonicalizer.Canonicalize(json),
            cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteDerivedOcrAsync(string assetId, object result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        if (Path.GetFileName(assetId) != assetId)
            throw new ArgumentException("OCR asset id must not contain a path.", nameof(assetId));
        ArgumentNullException.ThrowIfNull(result);
        await AtomicFile.WriteUtf8Async(
            Path.Combine(RootPath, "derived", "ocr", assetId + ".json"),
            JsonCanonicalizer.Serialize(result),
            cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteChunksAsync(IEnumerable<string> jsonLines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var canonical = string.Join("", jsonLines.Select(JsonCanonicalizer.Canonicalize));
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "derived", "chunks", "default.jsonl"), canonical, cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAssetsAsync(IEnumerable<WorkspaceAsset> assets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var entries = new List<AssetIndexEntry>();
        foreach (var asset in assets.OrderBy(asset => asset.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(asset.FileName);
            if (string.IsNullOrWhiteSpace(fileName) || !StringComparer.Ordinal.Equals(fileName, asset.FileName))
                throw new InvalidDataException("Asset file name must not contain a path.");
            var actualHash = Hashing.Bytes(asset.Content.Span);
            if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, asset.Sha256))
                throw new InvalidDataException($"Asset hash does not match content for '{asset.Id}'.");
            await AtomicFile.WriteAsync(Path.Combine(RootPath, "assets", fileName), asset.Content, cancellationToken).ConfigureAwait(false);
            entries.Add(new AssetIndexEntry(asset.Id, fileName, asset.MediaType, actualHash, asset.Content.Length, asset.SourcePartUri, asset.AliasPartUris));
        }
        await AtomicFile.WriteUtf8Async(
            Path.Combine(RootPath, "assets", "index.json"),
            JsonCanonicalizer.Serialize(new AssetIndex("1.0", entries)),
            cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteReportAsync(string name, object report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Report name must be a JSON file name.", nameof(name));
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "reports", name), JsonCanonicalizer.Serialize(report), cancellationToken).ConfigureAwait(false);
        await RefreshIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifySourceAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(RootPath, "source", OriginalStoredFileName(Manifest.Source.FileName));
        if (!File.Exists(path)) throw new WorkspaceIntegrityException("Immutable source is missing.");
        await Task.Run(() =>
        {
            if (!Hashing.File(path).Equals(Manifest.Source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new WorkspaceIntegrityException("Immutable source hash does not match manifest.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshIntegrityAsync(CancellationToken cancellationToken)
    {
        var markdownBaselineHash = Manifest.Integrity.MarkdownBaselineSha256;
        var graphPath = Path.Combine(RootPath, "graph", "index.json");
        var projectionMapPath = Path.Combine(RootPath, "maps", "projection-map.jsonl");
        var rawSlicePath = Path.Combine(RootPath, "source", "indexes", "xml-slices.jsonl");
        var assetIndexPath = Path.Combine(RootPath, "assets", "index.json");
        Manifest.Integrity = new IntegrityInfo
        {
            BaselineGraphSha256 = File.Exists(graphPath) ? Hashing.File(graphPath) : "",
            ProjectionMapSha256 = File.Exists(projectionMapPath) ? Hashing.File(projectionMapPath) : "",
            RawSliceIndexSha256 = File.Exists(rawSlicePath) ? Hashing.File(rawSlicePath) : "",
            AssetIndexSha256 = File.Exists(assetIndexPath) ? Hashing.File(assetIndexPath) : "",
            // This is intentionally the baseline hash, not the current hash.  A
            // changed projection must make Verify/F0 fail, never silently rebase.
            MarkdownBaselineSha256 = markdownBaselineHash,
        };
        var manifestJson = JsonCanonicalizer.Serialize(Manifest);
        await AtomicFile.WriteUtf8Async(ManifestPath, manifestJson, cancellationToken).ConfigureAwait(false);
        var checksums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals("checksums.json", StringComparison.Ordinal)) continue;
            checksums[relative] = Hashing.File(path);
        }
        await AtomicFile.WriteUtf8Async(Path.Combine(RootPath, "checksums.json"), JsonCanonicalizer.Serialize(checksums), cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyChecksumsAsync(List<IntegrityIssue> issues, CancellationToken cancellationToken)
    {
        var path = Path.Combine(RootPath, "checksums.json");
        if (!File.Exists(path)) return;
        try
        {
            var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonCanonicalizer.Options);
            if (checksums is null) { issues.Add(new("checksums.invalid", "checksums.json is invalid.", "checksums.json")); return; }
            foreach (var item in checksums.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var relative = item.Key.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
                {
                    issues.Add(new("checksums.path", "Checksum path escapes workspace.", item.Key));
                    continue;
                }
                var file = Path.GetFullPath(Path.Combine(RootPath, relative));
                var resolvedRelative = Path.GetRelativePath(RootPath, file);
                if (resolvedRelative == ".." || resolvedRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    issues.Add(new("checksums.path", "Checksum path escapes workspace.", item.Key));
                    continue;
                }
                if (!File.Exists(file)) issues.Add(new("checksums.missing", "A checksummed file is missing.", item.Key));
                else if (!Hashing.File(file).Equals(item.Value, StringComparison.OrdinalIgnoreCase)) issues.Add(new("checksums.mismatch", "A checksummed file has changed.", item.Key));
            }
        }
        catch (JsonException) { issues.Add(new("checksums.invalid", "checksums.json is invalid.", "checksums.json")); }
    }

    private async Task VerifyProjectionMapAsync(List<IntegrityIssue> issues, CancellationToken cancellationToken)
    {
        var path = Path.Combine(RootPath, "maps", "projection-map.jsonl");
        if (!File.Exists(path)) return;
        var lineNumber = 0;
        var mappedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("projection_id", out var projectionId) ||
                    !StringComparer.Ordinal.Equals(projectionId.GetString(), Manifest.Projection.ProjectionId))
                {
                    issues.Add(new(
                        "projection_map.projection_id",
                        $"Projection map line {lineNumber} does not match manifest projection_id.",
                        "maps/projection-map.jsonl"));
                }
                if (document.RootElement.TryGetProperty("node_id", out var nodeId) && !string.IsNullOrWhiteSpace(nodeId.GetString()))
                    mappedNodeIds.Add(nodeId.GetString()!);
            }

            var graphPath = Path.Combine(RootPath, "graph", "index.json");
            if (File.Exists(graphPath))
            {
                var graph = DeterministicJson.Deserialize<DocumentGraph>(
                    await File.ReadAllTextAsync(graphPath, cancellationToken).ConfigureAwait(false));
                if (graph?.Partitions is not null)
                {
                    var nodes = graph.Partitions.SelectMany(partition => partition.Nodes ?? Array.Empty<DocumentNode>()).ToArray();
                    var projectedNodes = StringComparer.OrdinalIgnoreCase.Equals(Manifest.Projection.ContentPolicy, "complete")
                        ? nodes
                        : nodes.Where(node => node.Layer is not (ContentLayer.Hidden or ContentLayer.Metadata) &&
                            node.Kind is not (NodeKind.Comment or NodeKind.Revision)).ToArray();
                    foreach (var nodeId in projectedNodes.Select(node => node.Id).Where(nodeId => !mappedNodeIds.Contains(nodeId)))
                        issues.Add(new("projection_map.node_missing", $"Projection map has no contribution for baseline node '{nodeId}'.", "maps/projection-map.jsonl"));
                    var baselineIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var nodeId in mappedNodeIds.Where(nodeId => !baselineIds.Contains(nodeId)))
                        issues.Add(new("projection_map.node_unknown", $"Projection map references unknown node '{nodeId}'.", "maps/projection-map.jsonl"));
                }
            }
        }
        catch (JsonException)
        {
            issues.Add(new("projection_map.invalid", $"Projection map line {lineNumber} is invalid JSON.", "maps/projection-map.jsonl"));
        }
    }

    private async Task VerifyAssetsAsync(List<IntegrityIssue> issues, CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(RootPath, "assets", "index.json");
        if (!File.Exists(indexPath)) return;
        try
        {
            var index = JsonSerializer.Deserialize<AssetIndex>(
                await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false),
                JsonCanonicalizer.Options);
            if (index is null)
            {
                issues.Add(new("assets.invalid", "Asset index is empty.", "assets/index.json"));
                return;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in index.Assets)
            {
                if (Path.GetFileName(asset.FileName) != asset.FileName || !names.Add(asset.FileName))
                {
                    issues.Add(new("assets.path", "Asset index contains an unsafe or duplicate file name.", asset.FileName));
                    continue;
                }
                var path = Path.Combine(RootPath, "assets", asset.FileName);
                if (!File.Exists(path)) issues.Add(new("assets.missing", "Indexed asset is missing.", "assets/" + asset.FileName));
                else
                {
                    var info = new FileInfo(path);
                    if (info.Length != asset.Size) issues.Add(new("assets.size", "Indexed asset size does not match.", "assets/" + asset.FileName));
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Hashing.File(path), asset.Sha256))
                        issues.Add(new("assets.hash", "Indexed asset hash does not match.", "assets/" + asset.FileName));
                }
            }
        }
        catch (JsonException)
        {
            issues.Add(new("assets.invalid", "Asset index is invalid JSON.", "assets/index.json"));
        }
    }

    private Task VerifyManifestIntegrityAsync(List<IntegrityIssue> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check("graph/index.json", Manifest.Integrity.BaselineGraphSha256, "integrity.graph");
        Check("maps/projection-map.jsonl", Manifest.Integrity.ProjectionMapSha256, "integrity.projection_map");
        Check("assets/index.json", Manifest.Integrity.AssetIndexSha256, "integrity.assets");
        if (Manifest.Preservation.OriginalSliceIndexed)
            Check("source/indexes/xml-slices.jsonl", Manifest.Integrity.RawSliceIndexSha256, "integrity.raw_slices");
        return Task.CompletedTask;

        void Check(string relative, string expected, string code)
        {
            var path = Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return;
            if (string.IsNullOrWhiteSpace(expected) || !StringComparer.OrdinalIgnoreCase.Equals(Hashing.File(path), expected))
                issues.Add(new(code, $"Manifest integrity hash does not match '{relative}'.", relative));
        }
    }

    private async Task VerifyOcrAsync(List<IntegrityIssue> issues, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(RootPath, "derived", "ocr");
        if (!Directory.Exists(directory)) return;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("status", out var status) || string.IsNullOrWhiteSpace(status.GetString()))
                    issues.Add(new("ocr.status_missing", "OCR result has no processing status.", Path.GetRelativePath(RootPath, path)));
                else
                    counts[status.GetString()!] = counts.GetValueOrDefault(status.GetString()!) + 1;
            }
            catch (JsonException)
            {
                issues.Add(new("ocr.invalid", "OCR result is invalid JSON.", Path.GetRelativePath(RootPath, path)));
            }
        }
        Check("completed", Manifest.Ocr.StatusSummary.Completed);
        Check("not_required", Manifest.Ocr.StatusSummary.NotRequired);
        Check("skipped_by_policy", Manifest.Ocr.StatusSummary.SkippedByPolicy);
        Check("skipped_by_budget", Manifest.Ocr.StatusSummary.SkippedByBudget);
        Check("unavailable", Manifest.Ocr.StatusSummary.Unavailable);
        Check("failed", Manifest.Ocr.StatusSummary.Failed);

        void Check(string status, int expected)
        {
            if (counts.GetValueOrDefault(status) != expected)
                issues.Add(new("ocr.summary", $"OCR status summary for '{status}' does not match per-item results.", "manifest.json"));
        }
    }

    private static string FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" or ".docm" => "docx",
        ".xlsx" or ".xlsm" => "xlsx",
        ".pptx" or ".pptm" => "pptx",
        ".pdf" => "pdf",
        _ => "unknown",
    };

    private static string OriginalStoredFileName(string sourceFileName) =>
        "original" + Path.GetExtension(Path.GetFileName(sourceFileName)).ToLowerInvariant();

    private sealed class BasicReport
    {
        public BasicReport(
            string operation,
            bool success,
            string fidelityLevel,
            string packagePreservationLevel,
            string summary,
            IReadOnlyList<string> warnings)
            => (Operation, Success, FidelityLevel, PackagePreservationLevel, Summary, Warnings) =
                (operation, success, fidelityLevel, packagePreservationLevel, summary, warnings);
        [System.Text.Json.Serialization.JsonPropertyName("schema_version")] public string SchemaVersion => "1.0";
        [System.Text.Json.Serialization.JsonPropertyName("operation")] public string Operation { get; }
        [System.Text.Json.Serialization.JsonPropertyName("success")] public bool Success { get; }
        [System.Text.Json.Serialization.JsonPropertyName("fidelity_level")] public string FidelityLevel { get; }
        [System.Text.Json.Serialization.JsonPropertyName("package_preservation_level")] public string PackagePreservationLevel { get; }
        [System.Text.Json.Serialization.JsonPropertyName("summary")] public string Summary { get; }
        [System.Text.Json.Serialization.JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; }
        [System.Text.Json.Serialization.JsonPropertyName("diagnostics")] public IReadOnlyList<object> Diagnostics => [];

        public string ToMarkdown()
        {
            var lines = new List<string>
            {
                $"# {char.ToUpperInvariant(Operation[0]) + Operation[1..]} result",
                "",
                $"- Success: `{Success.ToString().ToLowerInvariant()}`",
                $"- Fidelity level: `{FidelityLevel}`",
                $"- Package preservation: `{PackagePreservationLevel}`",
                $"- Summary: {Summary}",
            };
            if (Warnings.Count > 0)
            {
                lines.Add("");
                lines.Add("## Warnings");
                lines.Add("");
                lines.AddRange(Warnings.Select(warning => $"- {warning}"));
            }
            return string.Join("\n", lines) + "\n";
        }
    }

    private sealed record AssetIndex(
        [property: System.Text.Json.Serialization.JsonPropertyName("schema_version")] string SchemaVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] IReadOnlyList<AssetIndexEntry> Assets);

    private sealed record AssetIndexEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("file_name")] string FileName,
        [property: System.Text.Json.Serialization.JsonPropertyName("media_type")] string MediaType,
        [property: System.Text.Json.Serialization.JsonPropertyName("sha256")] string Sha256,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("source_part_uri")] string? SourcePartUri,
        [property: System.Text.Json.Serialization.JsonPropertyName("alias_part_uris")] IReadOnlyList<string>? AliasPartUris);
}

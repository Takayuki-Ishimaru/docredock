using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocRedock.RoundTrip;

public sealed class RoundTripManifest
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "1.1";
    [JsonPropertyName("document_id")] public string DocumentId { get; init; } = "";
    [JsonPropertyName("generator")] public GeneratorInfo Generator { get; init; } = new();
    [JsonPropertyName("source")] public SourceInfo Source { get; init; } = new();
    [JsonPropertyName("providers")] public ProviderSet Providers { get; init; } = new();
    [JsonPropertyName("projection")] public ProjectionInfo Projection { get; init; } = new();
    [JsonPropertyName("ocr")] public OcrManifestInfo Ocr { get; init; } = new();
    [JsonPropertyName("preservation")] public PreservationInfo Preservation { get; init; } = new();
    [JsonPropertyName("capabilities")] public CapabilityInfo Capabilities { get; init; } = new();
    [JsonPropertyName("integrity")] public IntegrityInfo Integrity { get; set; } = new();
    [JsonPropertyName("license_profile")] public string LicenseProfile { get; init; } = "enterprise-permissive";
}

public sealed class GeneratorInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "docredock";
    [JsonPropertyName("version")] public string Version { get; init; } = "0.2.0";
}

public sealed class SourceInfo
{
    [JsonPropertyName("file_name")] public string FileName { get; init; } = "";
    [JsonPropertyName("format")] public string Format { get; init; } = "unknown";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("source_revision_id")] public string SourceRevisionId { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("macro_enabled")] public bool MacroEnabled { get; init; }
    [JsonPropertyName("encrypted")] public bool Encrypted { get; init; }
    [JsonPropertyName("signed")] public bool Signed { get; init; }
}

public sealed class ProviderSet
{
    [JsonPropertyName("format_adapter")] public ProviderInfo FormatAdapter { get; init; } = new("docredock.adapter.none", "0.2.0", 1);
    [JsonPropertyName("markdown")] public ProviderInfo Markdown { get; init; } = new("docredock.markdown.default", "0.2.0", 1);
    [JsonPropertyName("ocr")] public ProviderInfo Ocr { get; init; } = new("docredock.ocr.none", "0.2.0", 1);
}

public sealed class ProviderInfo
{
    public ProviderInfo() { }
    public ProviderInfo(string id, string version, int interfaceVersion) => (Id, Version, InterfaceVersion) = (id, version, interfaceVersion);
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("interface_version")] public int InterfaceVersion { get; init; } = 1;
    [JsonPropertyName("engine_version")] public string? EngineVersion { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "built-in";
}

public sealed class ProjectionInfo
{
    [JsonPropertyName("file_name")] public string FileName { get; init; } = "document.md";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "roundtrip";
    [JsonPropertyName("projection_id")] public string ProjectionId { get; init; } = "";
    [JsonPropertyName("content_policy")] public string ContentPolicy { get; init; } = "visible";
    [JsonPropertyName("encoding")] public string Encoding { get; init; } = "utf-8";
    [JsonPropertyName("line_endings")] public string LineEndings { get; init; } = "lf";
    [JsonPropertyName("partitioned")] public bool Partitioned { get; init; }
    [JsonPropertyName("contributor_map_schema")] public string ContributorMapSchema { get; init; } = "1.0";
}

public sealed class OcrManifestInfo
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("languages")] public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();
    [JsonPropertyName("derived_schema")] public string DerivedSchema { get; init; } = "1.1";
    [JsonPropertyName("status_summary")] public OcrStatusSummary StatusSummary { get; init; } = new();
}

public sealed class OcrStatusSummary
{
    [JsonPropertyName("completed")] public int Completed { get; init; }
    [JsonPropertyName("not_required")] public int NotRequired { get; init; }
    [JsonPropertyName("skipped_by_policy")] public int SkippedByPolicy { get; init; }
    [JsonPropertyName("skipped_by_budget")] public int SkippedByBudget { get; init; }
    [JsonPropertyName("unavailable")] public int Unavailable { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
}

public sealed class PreservationInfo
{
    [JsonPropertyName("f0_byte_restore")] public bool F0ByteRestore { get; init; } = true;
    [JsonPropertyName("f1_target")] public string F1Target { get; init; } = "part-payload-identical";
    [JsonPropertyName("raw_zip_entry_copy_supported")] public bool RawZipEntryCopySupported { get; init; }
    [JsonPropertyName("original_slice_indexed")] public bool OriginalSliceIndexed { get; init; }
}

public sealed class CapabilityInfo
{
    [JsonPropertyName("byte_restore")] public bool ByteRestore { get; init; } = true;
    [JsonPropertyName("editable_restore")] public bool EditableRestore { get; init; }
    [JsonPropertyName("render")] public bool Render { get; init; }
    [JsonPropertyName("graph_chunks")] public bool GraphChunks { get; init; }
}

public sealed class IntegrityInfo
{
    [JsonPropertyName("baseline_graph_sha256")] public string BaselineGraphSha256 { get; init; } = "";
    [JsonPropertyName("projection_map_sha256")] public string ProjectionMapSha256 { get; init; } = "";
    [JsonPropertyName("raw_slice_index_sha256")] public string RawSliceIndexSha256 { get; init; } = "";
    [JsonPropertyName("asset_index_sha256")] public string AssetIndexSha256 { get; init; } = "";
    [JsonPropertyName("markdown_baseline_sha256")] public string MarkdownBaselineSha256 { get; init; } = "";
}

internal static class JsonCanonicalizer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    internal static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var compact = new JsonSerializerOptions(Options) { WriteIndented = false };
        return JsonSerializer.Serialize(document.RootElement, compact) + "\n";
    }
}

internal static class Hashing
{
    internal static string Bytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string File(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

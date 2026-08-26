namespace DocRedock.RoundTrip;

public sealed record IntegrityIssue(string Code, string Message, string? Path = null);

public sealed class WorkspaceIntegrityReport
{
    public bool IsValid => Issues.Count == 0;
    public IReadOnlyList<IntegrityIssue> Issues { get; init; } = Array.Empty<IntegrityIssue>();
    public IReadOnlyList<IntegrityIssue> Warnings { get; init; } = Array.Empty<IntegrityIssue>();
    public bool ProjectionChanged { get; init; }
    public int WarningCount => Warnings.Count;
    public int ErrorCount => Issues.Count;
}

public sealed record RestoreResult(
    string DestinationPath,
    string FidelityLevel,
    string SourceSha256,
    bool ByteIdentical,
    IReadOnlyList<string> Warnings);

public sealed class WorkspaceIntegrityException : Exception
{
    public WorkspaceIntegrityException(string message) : base(message) { }
}

public sealed class RoundTripWorkspaceOptions
{
    public string? MarkdownPath { get; init; }
    public string? MarkdownContent { get; init; }
    // Projection/source metadata accepted by the CLI and preserved for the
    // manifest even when extraction is provided by another adapter.
    public string? ProjectionId { get; init; }
    public string? SourceFormat { get; init; }
    public bool SourceMacroEnabled { get; init; }
    public string? DocumentId { get; init; }
    public string Profile { get; init; } = "roundtrip";
    public string ContentPolicy { get; init; } = "visible";
    public bool OcrEnabled { get; init; }
    public bool EditableRestore { get; init; }
    public bool Render { get; init; }
    public bool GraphChunks { get; init; }
    public string? GeneratorVersion { get; init; }
    public string? SourceRevisionId { get; init; }
    public ProviderSet? Providers { get; init; }
    public OcrManifestInfo? Ocr { get; init; }
    public PreservationInfo? Preservation { get; init; }
    public CapabilityInfo? Capabilities { get; init; }
}

public sealed record WorkspaceAsset
{
    public WorkspaceAsset(
        string Id,
        string FileName,
        string MediaType,
        string Sha256,
        ReadOnlyMemory<byte> Content,
        string? SourcePartUri = null,
        IReadOnlyList<string>? AliasPartUris = null)
    {
        this.Id = Id;
        this.FileName = FileName;
        this.MediaType = MediaType;
        this.Sha256 = Sha256;
        this.Content = Content;
        this.SourcePartUri = SourcePartUri;
        this.AliasPartUris = AliasPartUris ?? Array.Empty<string>();
    }

    public string Id { get; init; }
    public string FileName { get; init; }
    public string MediaType { get; init; }
    public string Sha256 { get; init; }
    public ReadOnlyMemory<byte> Content { get; init; }
    public string? SourcePartUri { get; init; }
    public IReadOnlyList<string> AliasPartUris { get; init; }
}

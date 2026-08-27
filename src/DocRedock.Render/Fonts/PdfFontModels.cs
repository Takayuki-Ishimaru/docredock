namespace DocRedock.Render.Fonts;

public enum PdfFontSource
{
    ExplicitPath,
    Environment,
    System
}

public enum FontEmbeddingPermission
{
    Installable,
    PreviewAndPrint,
    Editable,
    Restricted,
    BitmapOnly,
    Unknown
}

public sealed record PdfFontRequest(
    IReadOnlySet<uint> RequiredCodePoints,
    string? ExplicitPath = null,
    int? ExplicitFaceIndex = null);

public sealed record ResolvedPdfFont(
    string SourcePath,
    int FaceIndex,
    string FamilyName,
    string PostScriptName,
    PdfFontSource Source,
    FontEmbeddingPermission EmbeddingPermission,
    byte[] StandaloneSfntBytes);

public sealed record OpenTypeFontInfo(
    string FamilyName,
    string SubfamilyName,
    string PostScriptName,
    FontEmbeddingPermission EmbeddingPermission,
    IReadOnlyList<uint> MissingCodePoints,
    bool HasTrueTypeOutlines);

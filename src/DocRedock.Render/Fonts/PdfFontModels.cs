namespace DocRedock.Render.Fonts;

public enum PdfFontSource
{
    ExplicitPath,
    Environment,
    System
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Permission is the font embedding rights domain term and part of the existing public API.")]
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

#pragma warning disable CA1819 // Existing public API exposes owned binary buffers without cloning.
public sealed record ResolvedPdfFont(
    string SourcePath,
    int FaceIndex,
    string FamilyName,
    string PostScriptName,
    PdfFontSource Source,
    FontEmbeddingPermission EmbeddingPermission,
    byte[] StandaloneSfntBytes);
#pragma warning restore CA1819

public sealed record OpenTypeFontInfo(
    string FamilyName,
    string SubfamilyName,
    string PostScriptName,
    FontEmbeddingPermission EmbeddingPermission,
    IReadOnlyList<uint> MissingCodePoints,
    bool HasTrueTypeOutlines);

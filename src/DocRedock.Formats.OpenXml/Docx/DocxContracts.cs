using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Formats.OpenXml.Docx;

public sealed record DocxExportOptions(bool IncludeFurniture = true, bool IncludeFootnotes = true, bool StrictSecurity = true);
public sealed record DocxRestoreOptions(bool Strict = true, bool AllowInsertParagraph = true);
public sealed record RunCharacterSpan(int Start, int End, int RunOrdinal, string Text);
public sealed record DocxRunCharacterMap(string NodeId, IReadOnlyList<RunCharacterSpan> Spans);
/// <summary>The blocks that stand for one projected block in the branches of a body-level
/// mc:AlternateContent the extractor did *not* select. They carry the same text at the same
/// position, so an F1 edit to the projected block is applied to them too and Word keeps showing the
/// edit whichever branch it resolves. An empty <paramref name="Slices"/> means no branch had a
/// counterpart, which restore reports as DocxAlternateContentFallbackStale.</summary>
public sealed record DocxAlternateMirror(int AlternateContentOrdinal, IReadOnlyList<RawSliceRef> Slices);
public sealed record DocxSourceIndex(
    string SourcePath,
    string SourceSha256,
    IReadOnlyDictionary<string, RawSliceRef> BlockSlices,
    IReadOnlyDictionary<string, DocxRunCharacterMap> RunCharacterMaps,
    int BodyEndTagStart,
    bool HasMacro,
    bool HasSignature,
    bool HasDocumentProtection,
    bool HasTrackedRevisions = false,
    IReadOnlyDictionary<long, DocxAlternateMirror>? AlternateMirrors = null);
public sealed record DocxExtractionResult(DocumentGraph Graph, DocxSourceIndex SourceIndex, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DocxRestoreResult(bool Succeeded, DiffResult Diff, FidelityReport Fidelity, IReadOnlyList<Diagnostic> Diagnostics);

using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Formats.OpenXml.Docx;

public sealed record DocxExportOptions(bool IncludeFurniture = true, bool IncludeFootnotes = true, bool StrictSecurity = true);
public sealed record DocxRestoreOptions(bool Strict = true, bool AllowInsertParagraph = true);
public sealed record RunCharacterSpan(int Start, int End, int RunOrdinal, string Text);
public sealed record DocxRunCharacterMap(string NodeId, IReadOnlyList<RunCharacterSpan> Spans);
public sealed record DocxSourceIndex(
    string SourcePath,
    string SourceSha256,
    IReadOnlyDictionary<string, RawSliceRef> BlockSlices,
    IReadOnlyDictionary<string, DocxRunCharacterMap> RunCharacterMaps,
    int BodyEndTagStart,
    bool HasMacro,
    bool HasSignature,
    bool HasDocumentProtection,
    bool HasTrackedRevisions = false);
public sealed record DocxExtractionResult(DocumentGraph Graph, DocxSourceIndex SourceIndex, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record DocxRestoreResult(bool Succeeded, DiffResult Diff, FidelityReport Fidelity, IReadOnlyList<Diagnostic> Diagnostics);

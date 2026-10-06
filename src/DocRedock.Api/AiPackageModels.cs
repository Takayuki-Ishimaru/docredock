using DocRedock.Core.Documents;

namespace DocRedock.Api;

public enum AiPackageForm { Directory, Zip }

/// <param name="TableRowBlocks">Opt-in. When a worksheet's readable projection exceeds
/// <paramref name="TargetCharacters"/>, a worksheet table too large for one part is cut between rows
/// into blocks that each repeat the table header and name their original cell range. Off by default:
/// sheets, pages and slides then stay whole as before.</param>
public sealed record AiPackageExportOptions(
    ReadableDocumentExportOptions Document,
    string OutputPath,
    AiPackageForm Form = AiPackageForm.Directory,
    int TargetCharacters = 12_000,
    bool TableRowBlocks = false);

/// <summary>How <see cref="AiPackageContentBuilder"/> divides a document into parts.</summary>
/// <param name="TargetCharacters">Soft size target in .NET characters, not a token budget.</param>
/// <param name="TableRowBlocks">See <see cref="AiPackageExportOptions.TableRowBlocks"/>.</param>
public sealed record AiPackagePartOptions(int TargetCharacters = 12_000, bool TableRowBlocks = false);

/// <summary>One block of a worksheet table that was cut between rows across consecutive parts.
/// Ranges are original A1 addresses on the part's sheet. Every block starts with the table's
/// header rows (<see cref="HeaderRange"/>, null when the table has no header row); block 2 onward
/// repeats them.</summary>
public sealed record AiPackageTableBlock(
    string TableId,
    int Index,
    int Count,
    string? HeaderRange,
    string RowRange,
    string? PreviousPart,
    string? NextPart);

public sealed record AiPackageSourceLocation(
    string NodeId,
    string PartitionId,
    string Label,
    int? PageNumber = null,
    int? SlideNumber = null,
    string? SheetName = null,
    IReadOnlyList<string>? HeadingPath = null,
    string? CellAddress = null);

public sealed record AiPackagePart(
    string Id,
    string Path,
    string Markdown,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<AiPackageSourceLocation> Sources,
    bool ExceedsTarget,
    AiPackageTableBlock? TableBlock = null)
{
    /// <summary>Rough, model-independent size of <see cref="Markdown"/> in tokens; see
    /// <see cref="AiTokenEstimate"/>.</summary>
    public int EstimatedTokens => AiTokenEstimate.Estimate(Markdown);
}

/// <summary>A rough token count that needs no tokenizer: about four ASCII characters per token and
/// one token per other character (Japanese text included). Real tokenizers differ by model; use it
/// to compare parts and plan batches, not as a model limit.</summary>
public static class AiTokenEstimate
{
    public static int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ascii = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsAscii) ascii++;
            else other++;
        }
        return (ascii + 3) / 4 + other;
    }
}

public sealed record AiPackageExportResult(
    string OutputPath,
    AiPackageForm Form,
    DocumentGraph Graph,
    IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> Diagnostics,
    IReadOnlyList<AiPackagePart> Parts,
    ExportSummary Summary,
    ExportReview Review);

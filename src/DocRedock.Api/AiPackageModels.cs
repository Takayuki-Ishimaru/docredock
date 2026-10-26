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
    bool TableRowBlocks = false)
{
    /// <summary>Optional margin for heuristic token estimates; finite, from 1 to 100.</summary>
    public double TokenEstimateMultiplier { get; init; } = 1;
}

/// <summary>How <see cref="AiPackageContentBuilder"/> divides a document into parts.</summary>
/// <param name="TargetCharacters">Soft size target in .NET characters, not a token budget.</param>
/// <param name="TableRowBlocks">See <see cref="AiPackageExportOptions.TableRowBlocks"/>.</param>
public sealed record AiPackagePartOptions(int TargetCharacters = 12_000, bool TableRowBlocks = false)
{
    /// <summary>See <see cref="AiPackageExportOptions.TokenEstimateMultiplier"/>.</summary>
    public double TokenEstimateMultiplier { get; init; } = 1;
}

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
    string? NextPart)
{
    /// <summary>Cells of the short text just above the table (its title, unit or conditions) that
    /// block 1 starts with and block 2 onward repeats before the header, so a block read alone still
    /// says what its numbers mean. Null when the table has no such text. Manifest schema 2.2.</summary>
    public string? ContextRange { get; init; }
}

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
    public int EstimatedTokens => AiTokenEstimate.Estimate(Markdown, TokenEstimateMultiplier);

    /// <summary>Safety margin applied to <see cref="EstimatedTokens"/>; see
    /// <see cref="AiPackageExportOptions.TokenEstimateMultiplier"/>. 1 keeps the plain estimate.</summary>
    public double TokenEstimateMultiplier { get; init; } = 1;
}

/// <summary>A rough token count that needs no tokenizer: about four ASCII characters per token and
/// one token per other character (Japanese text included). Real tokenizers differ by model; use it
/// to compare parts and plan batches, not as a model limit.</summary>
public static class AiTokenEstimate
{
    /// <summary>Smallest and largest accepted <see cref="AiPackageExportOptions.TokenEstimateMultiplier"/>.</summary>
    public const double MinimumMultiplier = 1;
    public const double MaximumMultiplier = 100;

    /// <summary>True for a finite multiplier from <see cref="MinimumMultiplier"/> through <see cref="MaximumMultiplier"/>.</summary>
    public static bool IsSupportedMultiplier(double multiplier) =>
        multiplier is >= MinimumMultiplier and <= MaximumMultiplier;

    public static int Estimate(string text) => Estimate(text, 1);

    /// <summary>The same estimate widened by a user-chosen safety margin, rounded up and capped at
    /// <see cref="int.MaxValue"/>. A margin is still a heuristic, never a model tokenizer count.</summary>
    public static int Estimate(string text, double multiplier)
    {
        if (!IsSupportedMultiplier(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier,
                $"Token estimate multiplier must be between {MinimumMultiplier:0.#} and {MaximumMultiplier:0.#}.");
        ArgumentNullException.ThrowIfNull(text);
        var ascii = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsAscii) ascii++;
            else other++;
        }
        var estimate = ((long)ascii + 3) / 4 + other;
        if (multiplier == 1) return (int)Math.Min(estimate, int.MaxValue);
        var widened = Math.Ceiling(estimate * multiplier);
        return widened >= int.MaxValue ? int.MaxValue : (int)widened;
    }
}

public sealed record AiPackageExportResult(
    string OutputPath,
    AiPackageForm Form,
    DocumentGraph Graph,
    IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> Diagnostics,
    IReadOnlyList<AiPackagePart> Parts,
    ExportSummary Summary,
    ExportReview Review)
{
    public AiPackageSizeSummary? Size { get; init; }
}

public sealed record AiPackageSizeSummary(int PartCount, int DocumentCharacters, int TotalPartCharacters,
    int MaximumPartCharacters, int ExceedingParts, int TargetCharacters)
{
    public static AiPackageSizeSummary Build(string document, IReadOnlyList<AiPackagePart> parts, int target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parts);
        return new(parts.Count, document.Length, parts.Sum(part => part.Markdown.Length),
            parts.Count == 0 ? 0 : parts.Max(part => part.Markdown.Length), parts.Count(part => part.ExceedsTarget), target);
    }
    public string DescribeJapanese(bool estimated = false) =>
        $"{(estimated ? "予想" : "実績")}: {PartCount}分割／最大 {MaximumPartCharacters:N0}文字／目安 {TargetCharacters:N0}文字／目安超過 {ExceedingParts}分割。" +
        $"全文 {DocumentCharacters:N0}文字、分割本文の合計 {TotalPartCharacters:N0}文字。" +
        (PartCount <= 1 ? "1分割では必要な本文だけに絞る効果が限られます。Excelの行分割を検討してください。" : "必要な分割だけをAIへ渡してください。全分割の合計は注記の反復で全文より増える場合があります。");
}

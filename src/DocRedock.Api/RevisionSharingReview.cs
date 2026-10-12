using DocRedock.Core.Reporting;

namespace DocRedock.Api;

/// <summary>Sharing decision independent of conversion quality. Inclusion flags are conservative:
/// markup may retain revisions even when some revision content is hidden by the content policy.</summary>
public sealed record RevisionSharingReview(bool Required, bool RevisionsPresent, bool RevisionsIncluded,
    bool DeletedTextIncluded, string RevisionsPolicy)
{
    public static string Policy(string? revisions, string contentPolicy)
    {
        ArgumentNullException.ThrowIfNull(contentPolicy);
        var value = revisions?.Trim().ToLowerInvariant() ??
            (contentPolicy.Trim().Equals("sanitized", StringComparison.OrdinalIgnoreCase) ? "final" : "markup");
        return value is "final" or "markup" or "reject" ? value :
            throw new ArgumentException("Revisions must be final, markup, or reject.", nameof(revisions));
    }

    public static RevisionSharingReview Build(IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var present = diagnostics.Any(item => item.Code == "TrackedRevisionsPresent");
        var policy = diagnostics.FirstOrDefault(item => item.Code == "RevisionPolicy")?.Message ?? "markup";
        var included = present && policy == "markup";
        return new(included, present, included,
            included && diagnostics.Any(item => item.Code == "DeletedRevisionContentIncluded"), policy);
    }
}

namespace DocRedock.Core.Documents;

/// <summary>Node extension keys that format adapters write and the export review reads. They live here so that
/// an adapter does not have to reference the project that owns the reader.</summary>
public static class ReviewExtensionKeys
{
    /// <summary>Marks a node whose text a drawn line strikes through in part (a worksheet cell, a slide's text or
    /// table, a Word paragraph or table): which characters the line strikes could not be determined, so the text
    /// is left unmarked and the node needs source review (<c>ReviewElementKind.StrikethroughUnplaced</c>). The
    /// node carries <see cref="SourceReviewRequired"/> as well.</summary>
    public const string StrikeUnplaced = "strike_unplaced";

    /// <summary>Marks a node that stands for source content the export could not fully analyze: it needs the same
    /// comparison with the source as an unresolved figure does (<c>ReadableMarkdownSerializer.RequiresSourceReview</c>).</summary>
    public const string SourceReviewRequired = "source_review_required";

    /// <summary>A final DOCX table has no reliable rectangular grid; its surviving cell text is
    /// rendered in row order without assigning it to inferred columns.</summary>
    public const string FinalTableGridUnresolved = "final_table_grid_unresolved";
}

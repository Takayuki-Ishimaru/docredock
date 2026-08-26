using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>
/// Describes the edits that the built-in restore path can safely apply to a
/// projected block.  These values are intentionally terse because they are
/// emitted in hidden DRMD marker attributes and are also consumed by AI tools.
/// </summary>
public sealed record DocRedockMarkdownBlockPolicy(
    string Editability,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Constraints)
{
    public bool AllowsEdits => Operations.Count > 0;
}

public static class DocRedockMarkdownEditPolicy
{
    private static readonly DocRedockMarkdownBlockPolicy Protected =
        new("protected", [], ["preserve-marker", "preserve-content"]);

    /// <summary>
    /// Returns a conservative policy for the built-in adapters.  A node is
    /// advertised as editable only when the current Markdown-to-graph editor
    /// and the corresponding format restore path can both represent the edit.
    /// </summary>
    public static DocRedockMarkdownBlockPolicy For(DocumentFormatKind format, DocumentNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Editability is NodeEditability.Protected or NodeEditability.Passthrough)
            return Protected;

        if (node.Editability == NodeEditability.AnnotationOnly || node.Layer == ContentLayer.Derived)
            return new("annotation-only", ["replace-annotation"], ["original-unchanged"]);

        if (node.Editability == NodeEditability.RenderOnly || format == DocumentFormatKind.Pdf)
            return new("render-only", ["replace-text"], ["render-fallback-required", "no-package-restore"]);

        return format switch
        {
            DocumentFormatKind.Docx when node.Kind is NodeKind.Paragraph or NodeKind.Heading or NodeKind.ListItem =>
                new("text", ["replace-text", "explicit-delete"], ["preserve-kind", "preserve-order"]),
            DocumentFormatKind.Docx when node.Kind == NodeKind.Table =>
                new("table-cells", ["replace-table-cells", "explicit-delete"], ["same-shape", "preserve-order"]),
            DocumentFormatKind.Xlsx when node.Kind == NodeKind.Cell =>
                new("cell-value-or-formula", ["replace-cell"], ["preserve-address", "no-delete", "safe-formula"]),
            DocumentFormatKind.Pptx when node.Kind == NodeKind.Shape =>
                new("shape-text", ["replace-text"], ["existing-shape", "no-delete", "preserve-order"]),
            _ => new("unsupported", [], ["preserve-marker", "preserve-content"]),
        };
    }
}

using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

public sealed class DocRedockMarkdownEditPolicyTests
{
    [Theory]
    [InlineData(DocumentFormatKind.Docx, NodeKind.Paragraph, "text", "replace-text")]
    [InlineData(DocumentFormatKind.Docx, NodeKind.Table, "table-cells", "replace-table-cells")]
    [InlineData(DocumentFormatKind.Xlsx, NodeKind.Cell, "cell-value-or-formula", "replace-cell")]
    [InlineData(DocumentFormatKind.Pptx, NodeKind.Shape, "shape-text", "replace-text")]
    public void Advertises_only_edits_supported_by_the_built_in_restore_path(
        DocumentFormatKind format,
        NodeKind kind,
        string editability,
        string operation)
    {
        var policy = DocRedockMarkdownEditPolicy.For(format, Node(kind));

        Assert.Equal(editability, policy.Editability);
        Assert.Contains(operation, policy.Operations);
        Assert.True(policy.AllowsEdits);
    }

    [Theory]
    [InlineData(NodeEditability.Protected)]
    [InlineData(NodeEditability.Passthrough)]
    public void Protected_and_passthrough_nodes_never_advertise_edits(NodeEditability editability)
    {
        var policy = DocRedockMarkdownEditPolicy.For(DocumentFormatKind.Docx, Node(NodeKind.Paragraph, editability));

        Assert.Equal("protected", policy.Editability);
        Assert.False(policy.AllowsEdits);
    }

    [Fact]
    public void Pdf_text_is_explicitly_render_only()
    {
        var policy = DocRedockMarkdownEditPolicy.For(
            DocumentFormatKind.Pdf,
            Node(NodeKind.Paragraph, NodeEditability.RenderOnly));

        Assert.Equal("render-only", policy.Editability);
        Assert.Contains("render-fallback-required", policy.Constraints);
        Assert.Contains("no-package-restore", policy.Constraints);
    }

    [Fact]
    public void Editable_graph_nodes_are_not_advertised_when_the_adapter_cannot_restore_them()
    {
        var policy = DocRedockMarkdownEditPolicy.For(
            DocumentFormatKind.Docx,
            Node(NodeKind.TextBox, NodeEditability.EditableWithConstraints));

        Assert.Equal("unsupported", policy.Editability);
        Assert.False(policy.AllowsEdits);
    }

    [Fact]
    public void Serializer_emits_policy_as_hidden_marker_attributes()
    {
        var graph = new DocumentGraph(
            DocumentGraph.CurrentSchemaVersion,
            "doc_policy",
            DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Summary", 0, [Node(NodeKind.Cell)])]);

        var markdown = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("drmd_rules: 1.0", markdown);
        Assert.Contains("editability=cell-value-or-formula", markdown);
        Assert.Contains("operations=replace-cell", markdown);
        Assert.Contains("constraints=preserve-address,no-delete,safe-formula", markdown);
    }

    private static DocumentNode Node(
        NodeKind kind,
        NodeEditability editability = NodeEditability.EditableInPlace) => new(
            "n_1",
            kind,
            null,
            0,
            ContentLayer.Body,
            kind == NodeKind.Table
                ? new TableNodeContent([new TableCell[] { "A" }])
                : new TextNodeContent("text"),
            Editability: editability);
}

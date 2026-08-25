using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Core.Diff;

public enum PatchOperationKind { ReplaceContent, InsertNode, ExplicitDelete, UpdateDerivedAnnotation }
public sealed record PatchOperation(PatchOperationKind Kind, string NodeId, DocumentNode? Before, DocumentNode? After, bool MutatesOriginal);
public sealed record PatchSet(IReadOnlyList<PatchOperation> Operations)
{
    public bool HasOriginalMutations => Operations.Any(operation => operation.MutatesOriginal);
}
public sealed record DirtyNode(string NodeId, IReadOnlySet<string> PartUris, bool MutatesOriginal, PatchOperationKind Reason);
public sealed record DirtySet(IReadOnlyList<DirtyNode> Nodes)
{
    public IReadOnlySet<string> DirtyPartUris => Nodes.Where(node => node.MutatesOriginal).SelectMany(node => node.PartUris).ToHashSet(StringComparer.Ordinal);
    public bool HasOriginalMutations => Nodes.Any(node => node.MutatesOriginal);
}
public sealed record DiffDiagnostic(string Code, string Message, DiagnosticSeverity Severity, string? NodeId = null);
public sealed record DiffResult(PatchSet PatchSet, DirtySet DirtySet, IReadOnlyList<DiffDiagnostic> Diagnostics);
public sealed record DiffOptions(IReadOnlySet<string>? ExplicitDeleteNodeIds = null);

public interface IDiffEngine { DiffResult Compare(DocumentGraph baseline, DocumentGraph edited, DiffOptions? options = null); }

/// <summary>Derives dirty nodes from graph comparison. Missing Markdown nodes never imply delete.</summary>
public sealed class DocumentGraphDiffEngine : IDiffEngine
{
    public DiffResult Compare(DocumentGraph baseline, DocumentGraph edited, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        options ??= new DiffOptions();
        var original = baseline.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var current = edited.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var patches = new List<PatchOperation>();
        var dirty = new List<DirtyNode>();
        var diagnostics = new List<DiffDiagnostic>();

        foreach (var node in original.Values.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(node.Id, out var changed))
            {
                if (options.ExplicitDeleteNodeIds?.Contains(node.Id) == true)
                    Add(PatchOperationKind.ExplicitDelete, node, null);
                else
                    diagnostics.Add(new("MissingNode", "A node is absent from the projection and will be preserved.", DiagnosticSeverity.Warning, node.Id));
                continue;
            }
            // Records containing IReadOnlyList/Dictionary compare collection references, not their
            // contents. Canonical JSON gives graph comparisons a value-based, deterministic boundary.
            if (!StringComparer.Ordinal.Equals(DeterministicJson.Serialize(node), DeterministicJson.Serialize(changed)))
                Add(IsDerivedOnly(node, changed) ? PatchOperationKind.UpdateDerivedAnnotation : PatchOperationKind.ReplaceContent, node, changed);
        }
        foreach (var node in current.Values.Where(node => !original.ContainsKey(node.Id)).OrderBy(node => node.Id, StringComparer.Ordinal))
            Add(IsDerivedOnly(null, node) ? PatchOperationKind.UpdateDerivedAnnotation : PatchOperationKind.InsertNode, null, node);

        return new(new(patches), new(dirty), diagnostics);

        void Add(PatchOperationKind kind, DocumentNode? before, DocumentNode? after)
        {
            var target = after ?? before!;
            var mutates = kind != PatchOperationKind.UpdateDerivedAnnotation &&
                target.Layer != ContentLayer.Derived &&
                target.Editability != NodeEditability.AnnotationOnly;
            patches.Add(new(kind, target.Id, before, after, mutates));
            var parts = mutates && target.Source is not null ? new HashSet<string>(StringComparer.Ordinal) { target.Source.PartUri } : new HashSet<string>(StringComparer.Ordinal);
            dirty.Add(new(target.Id, parts, mutates, kind));
        }
    }

    private static bool IsDerivedOnly(DocumentNode? before, DocumentNode after) =>
        (before?.Layer ?? after.Layer) == ContentLayer.Derived ||
        (before?.Editability ?? after.Editability) == NodeEditability.AnnotationOnly;
}

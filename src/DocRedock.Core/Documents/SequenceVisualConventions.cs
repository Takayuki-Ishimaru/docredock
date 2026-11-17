namespace DocRedock.Core.Documents;

/// <summary>
/// How a <see cref="VisualGraph"/> carries a sequence diagram without extending the shared model. An adapter that has
/// recognized a sequence diagram (participant boxes, lifelines, messages, alt/opt/loop frames) writes:
/// <list type="bullet">
/// <item>each participant as a <see cref="VisualNode"/> with its box as <see cref="VisualNode.Geometry"/>;</item>
/// <item>each message as a <see cref="VisualEdge"/> from the sending to the receiving participant (the same node twice for a
/// message to itself), carrying <see cref="MessageEvidence"/> in its evidence codes, its label, its line style (a dashed
/// line is a reply) and its bounds as <see cref="VisualEdge.Geometry"/>;</item>
/// <item>each frame as a node whose <see cref="VisualNode.Group"/> is <see cref="FrameGroup"/>, whose
/// <see cref="VisualNode.Lane"/> is the frame keyword (<c>alt</c>, <c>opt</c>, <c>loop</c>, ...) and whose label is the
/// condition (the keyword itself when there is none);</item>
/// <item>each divider that starts a frame's next branch as a node whose group is <see cref="BranchGroup"/>, whose lane is
/// its keyword (<c>else</c>, <c>and</c>) and whose label is its condition.</item>
/// </list>
/// Messages are ordered by the vertical position of their geometry, frames and branches by theirs.
/// </summary>
public static class SequenceVisualConventions
{
    public const string FrameGroup = "sequence-frame";
    public const string BranchGroup = "sequence-branch";
    public const string MessageEvidence = "SequenceMessage";
    public const string SelfEvidence = "SequenceSelf";
    public const string AsyncEvidence = "SequenceAsync";
    public const string ReturnEvidence = "SequenceReturn";

    /// <summary>True when the edge is a message of a recognized sequence diagram.</summary>
    public static bool IsMessage(VisualEdge? edge) =>
        edge?.Evidence?.EvidenceCodes?.Contains(MessageEvidence, StringComparer.Ordinal) == true;

    public static bool IsFrame(VisualNode? node) => string.Equals(node?.Group, FrameGroup, StringComparison.Ordinal);

    public static bool IsBranch(VisualNode? node) => string.Equals(node?.Group, BranchGroup, StringComparison.Ordinal);
}

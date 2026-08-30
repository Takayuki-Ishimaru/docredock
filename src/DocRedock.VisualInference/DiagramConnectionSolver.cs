namespace DocRedock.VisualInference;

public sealed record ConnectionAssignment(
    IReadOnlyList<ConnectionPairCandidate> Selected,
    double Score,
    double Margin,
    IReadOnlyList<ConnectionPairCandidate>? SecondSelected = null);

/// <summary>Bounded deterministic solver for one cluster. Each connector supplies alternatives including unresolved.</summary>
public static class DiagramConnectionSolver
{
    public static ConnectionAssignment Solve(IReadOnlyList<IReadOnlyList<ConnectionPairCandidate>> alternatives,
        int maxConnectors = 40, int beamWidth = 128)
    {
        var rows = alternatives.Take(maxConnectors)
            .Select(row => row.OrderByDescending(x => x.IsNative).ThenByDescending(x => x.Score)
                .ThenBy(x => x.SourceId, StringComparer.Ordinal).ThenBy(x => x.TargetId, StringComparer.Ordinal).ToArray())
            .ToArray();
        var overflow = alternatives.Skip(maxConnectors)
            .Select(row => row.FirstOrDefault(candidate => candidate.SourceId is null && candidate.TargetId is null) ??
                row.OrderBy(candidate => candidate.ConnectorId, StringComparer.Ordinal).First())
            .Select(candidate => candidate with
            {
                SourceId = null,
                TargetId = null,
                Confidence = ConnectionConfidence.Unresolved,
                RejectedCandidateIds = ["VisualClusterLimitExceeded"]
            })
            .ToArray();
        var states = new List<(double Score, List<ConnectionPairCandidate> Items, HashSet<string> Used, string Signature)>
        {
            (0, [], new(StringComparer.Ordinal), string.Empty)
        };
        foreach (var row in rows)
        {
            var next = new List<(double Score, List<ConnectionPairCandidate> Items, HashSet<string> Used, string Signature)>();
            foreach (var state in states)
            {
                foreach (var candidate in row)
                {
                    var key = RelationKey(candidate);
                    if (key is not null && state.Used.Contains(key)) continue;
                    var used = new HashSet<string>(state.Used, StringComparer.Ordinal);
                    if (key is not null) used.Add(key);
                    var candidateSignature = CandidateSignature(candidate);
                    var signature = state.Signature.Length == 0 ? candidateSignature : state.Signature + "|" + candidateSignature;
                    next.Add((state.Score + candidate.Score, [.. state.Items, candidate], used, signature));
                }
            }
            states = next.OrderByDescending(state => state.Score)
                .ThenBy(state => state.Signature, StringComparer.Ordinal)
                .Take(beamWidth).ToList();
        }
        var ranked = states.OrderByDescending(state => state.Score)
            .ThenBy(state => state.Signature, StringComparer.Ordinal).ToArray();
        var best = ranked.FirstOrDefault();
        var second = ranked.Skip(1).FirstOrDefault();
        var margin = ranked.Length > 1 ? best.Score - second.Score : best.Score;
        return new([.. best.Items ?? [], .. overflow], best.Score, margin,
            second.Items is null ? null : [.. second.Items, .. overflow]);
    }

    private static string? RelationKey(ConnectionPairCandidate candidate)
    {
        if (candidate.SourceId is null || candidate.TargetId is null) return null;
        if (candidate.Direction is ConnectionDirection.Unknown or ConnectionDirection.Bidirectional)
        {
            var first = candidate.SourceId;
            var second = candidate.TargetId;
            if (StringComparer.Ordinal.Compare(first, second) > 0) (first, second) = (second, first);
            return $"{candidate.ClusterId}|{first}|{second}|{candidate.Direction}";
        }
        return $"{candidate.ClusterId}|{candidate.SourceId}|{candidate.TargetId}|directed";
    }

    private static string CandidateSignature(ConnectionPairCandidate item) =>
        $"{item.ConnectorId}:{item.SourceId}>{item.TargetId}:{item.Direction}";
}

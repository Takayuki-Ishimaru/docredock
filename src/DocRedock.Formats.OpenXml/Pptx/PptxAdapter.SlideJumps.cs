using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Pptx;

public sealed partial class PptxAdapter
{
    /// <summary>Resolves the a:hlinkClick actions of one slide that jump to another slide of the presentation.
    /// <paramref name="Slides"/> are the slide parts in presentation order with whether each is hidden,
    /// <paramref name="Current"/> is the index of the slide the links are on, and
    /// <paramref name="Relationships"/> are that slide's own.</summary>
    private sealed record SlideJumps(IReadOnlyList<(string Part, bool Hidden)> Slides, int Current,
        IReadOnlyDictionary<string, Relationship>? Relationships)
    {
        private const string Scheme = "ppaction://";

        /// <summary>True when the action decides the link by itself: every PowerPoint action except the two
        /// that only say how to open the target of the relationship (a file, another presentation).</summary>
        public static bool Decides(string? action)
        {
            if (action is null || !action.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            var verb = Verb(action);
            return !verb.Equals("hlinkfile", StringComparison.OrdinalIgnoreCase) && !verb.Equals("hlinkpres", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The <see cref="SlideLinks"/> target of a jump to a slide (hlinksldjump by relationship, or
        /// hlinkshowjump to the first, last, next or previous slide), or null: for a slide that is not
        /// there and for the actions that lead to no slide (macro, program, OLE verb, custom show, end of show).
        /// The relative jumps follow the show, which passes over hidden slides; a jump by relationship
        /// names its slide even when that slide is hidden, as PowerPoint then shows it.</summary>
        public string? Target(string action, string? relationshipId)
        {
            if (!Decides(action)) return null;
            var verb = Verb(action);
            if (verb.Equals("hlinksldjump", StringComparison.OrdinalIgnoreCase))
            {
                if (relationshipId is null || Relationships is null || !Relationships.TryGetValue(relationshipId, out var relationship) ||
                    !relationship.Type.EndsWith("/slide", StringComparison.Ordinal)) return null;
                for (var index = 0; index < Slides.Count; index++)
                    if (StringComparer.Ordinal.Equals(Slides[index].Part, relationship.Target)) return SlideLinks.Target(index + 1);
                return null;
            }
            if (!verb.Equals("hlinkshowjump", StringComparison.OrdinalIgnoreCase)) return null;
            var query = action.IndexOf('?', StringComparison.Ordinal);
            var jump = query < 0 ? "" : action[(query + 1)..].Split('&').Select(pair => pair.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0].Equals("jump", StringComparison.OrdinalIgnoreCase))?[1] ?? "";
            var found = jump.ToLowerInvariant() switch
            {
                "firstslide" => Shown(0, 1),
                "lastslide" => Shown(Slides.Count - 1, -1),
                "nextslide" => Shown(Current + 1, 1),
                "previousslide" => Shown(Current - 1, -1),
                _ => -1,
            };
            return found < 0 ? null : SlideLinks.Target(found + 1);
        }

        private int Shown(int from, int step)
        {
            for (var index = from; index >= 0 && index < Slides.Count; index += step)
                if (!Slides[index].Hidden) return index;
            return -1;
        }

        private static ReadOnlySpan<char> Verb(string action)
        {
            var verb = action.AsSpan(Scheme.Length);
            var end = verb.IndexOf('?');
            return end < 0 ? verb : verb[..end];
        }
    }

    /// <summary>The target of a run's a:hlinkClick: a slide of this presentation for a jump action, the
    /// relationship's own target for a hyperlink, and none for an action that leads nowhere in a document.</summary>
    private static string? HyperlinkTarget(string? relationshipId, string? action, IReadOnlyDictionary<string, Relationship>? links, SlideJumps? jumps)
    {
        if (SlideJumps.Decides(action)) return jumps?.Target(action!, relationshipId);
        return relationshipId is not null && links is not null && links.TryGetValue(relationshipId, out var link) &&
            link.Type.EndsWith("/hyperlink", StringComparison.Ordinal) ? link.Target : null;
    }
}

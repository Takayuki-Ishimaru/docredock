namespace DocRedock.Render.Fonts;

public sealed class PdfFontResolver
{
    public const string FontPathEnvironmentVariable = "DOCREDOCK_PDF_FONT_PATH";
    public const string FontFaceIndexEnvironmentVariable = "DOCREDOCK_PDF_FONT_FACE_INDEX";

    public ResolvedPdfFont Resolve(PdfFontRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environmentPath = Environment.GetEnvironmentVariable(FontPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(request.ExplicitPath))
            return ResolveSingleSource(request.ExplicitPath, request.ExplicitFaceIndex, request.RequiredCodePoints, PdfFontSource.ExplicitPath);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            var faceIndex = ParseEnvironmentFaceIndex();
            return ResolveSingleSource(environmentPath, faceIndex, request.RequiredCodePoints, PdfFontSource.Environment);
        }

        Candidate? best = null;
        var failures = new FailureSummary();
        foreach (var file in SystemFontCatalog.GetInstalledFontFiles())
        {
            foreach (var candidate in InspectFile(file.Path, file.PreferredFaceIndex, request.RequiredCodePoints, PdfFontSource.System, failures))
            {
                if (!candidate.IsUsable) continue;
                if (best is null || candidate.Score < best.Score ||
                    candidate.Score == best.Score && StringComparer.Ordinal.Compare(candidate.Info.PostScriptName, best.Info.PostScriptName) < 0)
                    best = candidate;
                if (candidate.Score == 0) return ToResolved(candidate);
            }
        }
        if (best is not null) return ToResolved(best);
        throw failures.ToException(systemSearch: true);
    }

    private static ResolvedPdfFont ResolveSingleSource(
        string path,
        int? requestedFaceIndex,
        IReadOnlySet<uint> requiredCodePoints,
        PdfFontSource source)
    {
        var fullPath = Path.GetFullPath(path);
        var failures = new FailureSummary();
        var candidates = InspectFile(fullPath, requestedFaceIndex, requiredCodePoints, source, failures).ToArray();
        var usable = candidates.Where(candidate => candidate.IsUsable)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.FaceIndex)
            .FirstOrDefault();
        if (usable is not null) return ToResolved(usable);
        throw failures.ToException(systemSearch: false);
    }

    private static IReadOnlyList<Candidate> InspectFile(
        string path,
        int? requestedFaceIndex,
        IReadOnlySet<uint> requiredCodePoints,
        PdfFontSource source,
        FailureSummary failures)
    {
        byte[] fileData;
        try
        {
            fileData = SfntFaceExtractor.ReadFontFile(path);
        }
        catch (Exception exception) when (source == PdfFontSource.System && exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failures.InvalidFonts++;
            return [];
        }

        int faceCount;
        try { faceCount = SfntFaceExtractor.GetFaceCount(fileData); }
        catch (InvalidDataException) when (source == PdfFontSource.System)
        {
            failures.InvalidFonts++;
            return [];
        }

        var candidates = new List<Candidate>();
        IEnumerable<int> faceIndexes = requestedFaceIndex is { } selected ? [selected] : Enumerable.Range(0, faceCount);
        foreach (var faceIndex in faceIndexes)
        {
            byte[] standalone;
            OpenTypeFontInfo info;
            try
            {
                standalone = SfntFaceExtractor.ExtractFace(fileData, faceIndex);
                info = OpenTypeFontInspector.Inspect(standalone, requiredCodePoints);
            }
            catch (Exception exception) when (source == PdfFontSource.System && exception is InvalidDataException or IndexOutOfRangeException or OverflowException)
            {
                failures.InvalidFonts++;
                continue;
            }

            if (!info.HasTrueTypeOutlines)
            {
                failures.UnsupportedOutline = true;
                continue;
            }
            if (info.EmbeddingPermission is FontEmbeddingPermission.Restricted or FontEmbeddingPermission.BitmapOnly or FontEmbeddingPermission.Unknown)
            {
                failures.EmbeddingRestricted = true;
                continue;
            }
            if (info.MissingCodePoints.Count > 0)
            {
                failures.AddMissing(info.MissingCodePoints);
                continue;
            }

            var score = FamilyScore(info.FamilyName) * 10 + StyleScore(info.SubfamilyName);
            candidates.Add(new Candidate(path, faceIndex, source, standalone, info, score));
        }
        return candidates;
    }

    private static ResolvedPdfFont ToResolved(Candidate candidate)
    {
        var postScript = SanitizePdfName(candidate.Info.PostScriptName);
        if (postScript.Length == 0)
            postScript = SanitizePdfName(candidate.Info.FamilyName + "-" + candidate.Info.SubfamilyName);
        if (postScript.Length == 0) postScript = "DocRedockSystemFont";
        return new ResolvedPdfFont(
            candidate.Path,
            candidate.FaceIndex,
            candidate.Info.FamilyName,
            postScript,
            candidate.Source,
            candidate.Info.EmbeddingPermission,
            candidate.Standalone);
    }

    private static int? ParseEnvironmentFaceIndex()
    {
        var value = Environment.GetEnvironmentVariable(FontFaceIndexEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 0)
            throw new InvalidDataException($"ERROR PdfFontInvalidFaceIndex: {FontFaceIndexEnvironmentVariable} must be a non-negative integer.");
        return index;
    }

    private static int FamilyScore(string family)
    {
        var priorities = OperatingSystem.IsWindows()
            ? new[] { "Yu Gothic", "Yu Gothic UI", "Meiryo", "BIZ UDPGothic", "BIZ UDGothic", "MS Gothic", "Noto Sans CJK JP", "Noto Sans JP", "IPAexGothic", "IPAGothic" }
            : OperatingSystem.IsMacOS()
                ? new[] { "Hiragino Sans", "Hiragino Kaku Gothic ProN", "YuGothic", "Yu Gothic", "Noto Sans CJK JP", "Noto Sans JP", "IPAexGothic", "IPAGothic" }
                : new[] { "Noto Sans CJK JP", "Noto Sans JP", "IPAexGothic", "IPAGothic", "Source Han Sans JP", "TakaoGothic" };
        for (var index = 0; index < priorities.Length; index++)
            if (family.Equals(priorities[index], StringComparison.OrdinalIgnoreCase) ||
                family.Contains(priorities[index], StringComparison.OrdinalIgnoreCase))
                return index;
        return priorities.Length + 10;
    }

    private static int StyleScore(string subfamily)
    {
        if (subfamily.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
            subfamily.Contains("Oblique", StringComparison.OrdinalIgnoreCase)) return 5;
        if (subfamily.Equals("Regular", StringComparison.OrdinalIgnoreCase) ||
            subfamily.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
            subfamily.Contains("Regular", StringComparison.OrdinalIgnoreCase)) return 0;
        return 2;
    }

    private static string SanitizePdfName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
                builder.Append(character);
        return builder.ToString();
    }

    private sealed record Candidate(
        string Path,
        int FaceIndex,
        PdfFontSource Source,
        byte[] Standalone,
        OpenTypeFontInfo Info,
        int Score)
    {
        public bool IsUsable => Info.HasTrueTypeOutlines &&
            Info.MissingCodePoints.Count == 0 &&
            Info.EmbeddingPermission is FontEmbeddingPermission.Installable or FontEmbeddingPermission.PreviewAndPrint or FontEmbeddingPermission.Editable;
    }

    private sealed class FailureSummary
    {
        private readonly SortedSet<uint> missing = [];
        public bool UnsupportedOutline { get; set; }
        public bool EmbeddingRestricted { get; set; }
        public int InvalidFonts { get; set; }

        public void AddMissing(IEnumerable<uint> values)
        {
            foreach (var value in values)
            {
                if (missing.Count >= 16) break;
                missing.Add(value);
            }
        }

        public Exception ToException(bool systemSearch)
        {
            if (EmbeddingRestricted && !systemSearch)
                return new UnauthorizedAccessException(
                    "ERROR PdfFontEmbeddingRestricted: The selected font does not permit outline embedding according to OS/2.fsType.");
            if (UnsupportedOutline && !systemSearch)
                return new NotSupportedException(
                    "ERROR PdfFontUnsupportedOutline: The selected font uses CFF/CFF2 outlines, which are not supported by the built-in PDF writer.");
            if (missing.Count > 0)
            {
                var values = string.Join(", ", missing.Select(value => $"U+{value:X4}"));
                return new NotSupportedException(
                    $"ERROR PdfFontMissingGlyphs: No installed embeddable TrueType font covers all required characters. Missing: {values}. Specify --font-path or install a suitable Japanese font.");
            }
            if (EmbeddingRestricted && !systemSearch)
                return new UnauthorizedAccessException(
                    "ERROR PdfFontEmbeddingRestricted: The selected font does not permit outline embedding according to OS/2.fsType.");
            return new NotSupportedException(
                "ERROR PdfFontUnavailable: No embeddable installed TrueType font covers the document text. Install a Japanese font or specify --font-path.");
        }
    }
}

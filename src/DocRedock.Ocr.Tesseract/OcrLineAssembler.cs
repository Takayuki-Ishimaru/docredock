using System.Text;

namespace DocRedock.Ocr.Tesseract;

/// <summary>
/// Assembles OCR word boxes belonging to one (page, block, paragraph, line) group into a single
/// line of text. Tesseract's `jpn` model frequently reports one Japanese word per glyph; naively
/// joining every word with a space then produces "日 本 語" instead of "日本語". This assembler removes the
/// space between two adjacent words only when it is confident they are genuinely touching CJK
/// glyphs -- it never merges text arbitrarily. Latin words, digits, low-confidence words, and
/// wide gaps all keep the single space exactly as a plain `string.Join(" ", ...)` would produce.
/// </summary>
internal static class OcrLineAssembler
{
    /// <summary>Tesseract confidence (0-1, already divided by 100 by the caller) both adjacent
    /// words must meet before the space between them is even considered for removal.</summary>
    internal const double MinConfidenceForSpaceRemoval = 0.60;

    /// <summary>Maximum horizontal pixel gap between two word boxes, expressed as a fraction of
    /// the taller box's height, before the words are treated as too far apart to be adjacent
    /// glyphs that Tesseract merely split into separate "words". An actual full-width space in
    /// Japanese text is roughly 1 em wide -- well above this ratio -- so a small gap is good
    /// evidence the glyphs were touching.</summary>
    internal const double MaxGapToHeightRatio = 0.5;

    /// <summary>The text, geometry, and confidence of one recognized word, ordered left-to-right
    /// within its line.</summary>
    internal readonly record struct Word(string Text, double Left, double Width, double Height, double Confidence);

    /// <summary>Joins words already ordered left-to-right within a single (page, block,
    /// paragraph, line) group. Emits no space between two adjacent words only when both are
    /// high-confidence and the left word ends, and the right word starts, with a CJK character
    /// separated by a narrow horizontal gap; otherwise a single space is kept. Never called across
    /// lines, paragraphs, blocks, or pages -- each group is assembled independently.</summary>
    public static string AssembleLine(IReadOnlyList<Word> words)
    {
        if (words.Count == 0) return string.Empty;
        var builder = new StringBuilder(words[0].Text);
        for (var i = 1; i < words.Count; i++)
        {
            if (!ShouldOmitSpace(words[i - 1], words[i])) builder.Append(' ');
            builder.Append(words[i].Text);
        }
        return builder.ToString();
    }

    /// <summary>True only when every one of the following holds: (a) both words meet the
    /// confidence threshold; (b) the left word's last character and the right word's first
    /// character are both CJK (Han, Hiragana, Katakana, CJK punctuation/fullwidth forms -- the
    /// ideographic space is explicitly excluded so it never licenses merging on its own); and
    /// (c) the horizontal gap between the two boxes is small relative to the line height.
    /// Otherwise the space is kept, so Latin words, digits, low-confidence words, and wide gaps
    /// are never merged.</summary>
    internal static bool ShouldOmitSpace(Word left, Word right)
    {
        if (left.Confidence < MinConfidenceForSpaceRemoval || right.Confidence < MinConfidenceForSpaceRemoval)
            return false;
        if (left.Text.Length == 0 || right.Text.Length == 0) return false;
        if (!IsCjk(left.Text[^1]) || !IsCjk(right.Text[0])) return false;

        var gap = right.Left - (left.Left + left.Width);
        var allowedGap = MaxGapToHeightRatio * Math.Max(left.Height, right.Height);
        return gap <= allowedGap;
    }

    /// <summary>Han, Hiragana, Katakana, and CJK punctuation/fullwidth-form characters. The
    /// ideographic space (U+3000) is deliberately excluded even though it falls inside the CJK
    /// Symbols and Punctuation block: a wide gap that Tesseract recognized as an actual space
    /// character must never be treated as evidence that the surrounding glyphs were touching.</summary>
    internal static bool IsCjk(char c)
    {
        if (c == '　') return false;
        return
            (c >= '぀' && c <= 'ヿ') ||  // Hiragana, Katakana
            (c >= 'ㇰ' && c <= 'ㇿ') ||  // Katakana Phonetic Extensions
            (c >= '㐀' && c <= '䶿') ||  // CJK Unified Ideographs Extension A
            (c >= '一' && c <= '鿿') ||  // CJK Unified Ideographs (Han)
            (c >= '豈' && c <= '﫿') ||  // CJK Compatibility Ideographs
            (c >= '、' && c <= '〿') ||  // CJK Symbols and Punctuation (excl. U+3000 above)
            (c >= '＀' && c <= '￯');    // Halfwidth and Fullwidth Forms
    }
}

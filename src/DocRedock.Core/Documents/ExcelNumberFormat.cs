using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Core.Documents;

/// <summary>
/// Excel number-format engine shared by worksheet cells and native charts. It renders a number the way this
/// product displays it: dates as yyyy-MM-dd (with HH:mm:ss when the format has a time), clock and elapsed times,
/// and the numeric sections (grouping, decimals, percent, scale commas, fractions, exponents, literals,
/// conditions). Locale-dependent currency symbols are never invented.
/// </summary>
public static class ExcelNumberFormat
{
    /// <summary>Longest format code the engine applies; a longer one is never formatted.</summary>
    public const int MaxFormatCodeLength = 256;
    private const int MaxNumberFormatLength = MaxFormatCodeLength;
    private const int MaxPlaceholderDigits = 64;

    private sealed record NumberFormatAnalysis(bool HasDate, bool HasTime, bool IsElapsed, bool HasSeconds, bool HasAmPm, bool HasPercent, bool HasGrouping, int DecimalPlaces, string? Suffix);

    /// <summary>Format code of an ECMA-376 18.8.30 built-in number format id, or null when the id has none.</summary>
    public static string? ResolveBuiltIn(int id) => id switch
        {
            // ECMA-376 18.8.30 built-in formats. Ids 5-8 are locale-dependent currency formats and
            // ids 41-44 are accounting formats whose currency symbol also depends on the locale, so
            // those keep only their numeric shape (grouping, decimals, parenthesised negatives, "-"
            // for zero) and never invent a currency glyph.
            1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00",
            9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??",
            14 => "yyyy-MM-dd", 15 => "d-MMM-yy", 16 => "d-MMM", 17 => "MMM-yy", 18 => "h:mm tt", 19 => "h:mm:ss tt",
            20 => "h:mm", 21 => "h:mm:ss", 22 => "yyyy-MM-dd h:mm",
            37 => "#,##0;(#,##0)", 38 => "#,##0;[Red](#,##0)", 39 => "#,##0.00;(#,##0.00)", 40 => "#,##0.00;[Red](#,##0.00)",
            41 or 42 => "#,##0;(#,##0);\"-\"", 43 or 44 => "#,##0.00;(#,##0.00);\"-\"",
            45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 48 => "0.00E+00", 49 => "@",
            _ => null
        };

    /// <summary>True for a missing, blank or "General" code: the number keeps its plain form.</summary>
    public static bool IsGeneral(string? formatCode) =>
        string.IsNullOrWhiteSpace(formatCode) || formatCode.Trim().Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats <paramref name="number"/> with an Excel format code. Returns null when the code does not change the
    /// plain number or cannot be applied (empty, General, text, oversized, or a value outside the date range),
    /// so the caller keeps its own raw form.
    /// </summary>
    public static string? Format(double number, string? formatCode, bool date1904)
    {
        if (formatCode is not { Length: > 0 } format) return null;
        if (format.Length > MaxNumberFormatLength) return null;

        var analysis = AnalyzeNumberFormat(format, number);
        if (analysis.IsElapsed) return FormatElapsed(number, analysis);
        if (analysis.HasDate || analysis.HasTime)
        {
            try
            {
                if (analysis.HasDate)
                {
                    var date = date1904
                        ? new DateTime(1904, 1, 1).AddDays(number)
                        : DateTime.FromOADate(number);
                    return date.ToString(analysis.HasTime ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                return FormatClock(number, analysis);
            }
            catch (ArgumentException) { return null; }
        }
        try
        {
            return FormatNumericSections(format, number);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // Renders the numeric (non date/time/elapsed) sections of an Excel custom number format against
    // a value. When at least one section carries a leading "[&lt;op&gt;value]" condition (see
    // TryParseLeadingCondition), the format switches to condition-driven dispatch: sections are tested
    // against the raw signed value in order and the first true condition wins, falling back to the
    // first unconditioned section, and finally to section 0 if every section is conditional and none
    // matched. A matched conditional section always renders from the value's absolute magnitude with
    // no automatic sign prepended - exactly like a classic dedicated negative section, the format's own
    // literal text (e.g. "-0.0") is responsible for any sign glyph. Without any condition, the classic
    // positive;negative;zero;text grammar applies: negative values are always rendered from their
    // absolute value, with a dedicated negative section supplying its own sign glyph (e.g. "(...)" or a
    // literal "-") while the absence of one falls back to the positive section with a single leading
    // "-" prepended after rendering.
    private static string? FormatNumericSections(string format, double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return null;

        var wholeTrimmed = format.Trim();
        if (wholeTrimmed.Length == 0 || wholeTrimmed.Equals("General", StringComparison.OrdinalIgnoreCase) || wholeTrimmed == "@")
            return null;

        var sections = SplitFormatSections(format);
        var conditions = sections.Select(TryParseLeadingCondition).ToList();
        if (conditions.Any(condition => condition is not null))
        {
            var matchedIndex = -1;
            for (var i = 0; i < sections.Count; i++)
            {
                if (conditions[i] is { } condition && EvaluateCondition(condition.Op, condition.Threshold, number)) { matchedIndex = i; break; }
            }
            if (matchedIndex < 0) matchedIndex = conditions.FindIndex(condition => condition is null);
            if (matchedIndex < 0) matchedIndex = 0;
            return RenderSection(sections[matchedIndex], Math.Abs(number));
        }

        string section;
        double value;
        var prependMinus = false;
        if (number < 0 && sections.Count >= 2) { section = sections[1]; value = Math.Abs(number); }
        else if (number < 0) { section = sections[0]; value = Math.Abs(number); prependMinus = true; }
        else if (number == 0 && sections.Count >= 3) { section = sections[2]; value = 0; }
        else { section = sections[0]; value = number; }

        var rendered = RenderSection(section, value);
        if (rendered is null) return null;
        return prependMinus && rendered.Length > 0 ? "-" + rendered : rendered;
    }

    private static readonly Regex ConditionTagPattern = new(
        @"^(<=|>=|<>|<|>|=)(-?\d+(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses a leading "[&lt;op&gt;value]" condition tag from a section's opening run of
    /// bracketed tags - colour and condition tags may appear in either order, e.g. "[Red][&gt;=1000]#,##0"
    /// or "[&gt;=1000][Red]#,##0" - stopping at the first character that is not part of that leading
    /// bracket run. Returns null when no leading tag parses as a numeric condition, meaning the section
    /// is unconditioned.</summary>
    private static (string Op, double Threshold)? TryParseLeadingCondition(string section)
    {
        var i = 0;
        while (i < section.Length && section[i] == '[')
        {
            var end = section.IndexOf(']', i + 1);
            if (end < 0) break;
            var match = ConditionTagPattern.Match(section[(i + 1)..end]);
            if (match.Success && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                return (match.Groups[1].Value, threshold);
            i = end + 1;
        }
        return null;
    }

    private static bool EvaluateCondition(string op, double threshold, double value) => op switch
    {
        "<" => value < threshold,
        "<=" => value <= threshold,
        ">" => value > threshold,
        ">=" => value >= threshold,
        "=" => value == threshold,
        "<>" => value != threshold,
        _ => false
    };

    /// <summary>Splits a format string on top-level ';' separators, leaving quoted ("...") and
    /// bracketed ([...]) runs untouched so a literal semicolon inside either never splits a section.</summary>
    private static List<string> SplitFormatSections(string format)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '\\' && i + 1 < format.Length) { current.Append(c).Append(format[i + 1]); i++; continue; }
            if (c == '"') { inQuote = !inQuote; current.Append(c); continue; }
            if (!inQuote && c == '[')
            {
                var end = format.IndexOf(']', i + 1);
                if (end >= 0) { current.Append(format, i, end - i + 1); i = end; continue; }
            }
            if (!inQuote && c == ';') { sections.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        sections.Add(current.ToString());
        return sections;
    }

    private static string StripBracketsAndTrim(string section)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < section.Length; i++)
        {
            if (section[i] == '[')
            {
                var end = section.IndexOf(']', i + 1);
                if (end >= 0) { i = end; continue; }
            }
            sb.Append(section[i]);
        }
        return sb.ToString().Trim();
    }

    private static string? RenderSection(string section, double value)
    {
        var stripped = StripBracketsAndTrim(section);
        if (stripped.Length == 0) return string.Empty;
        if (stripped.Equals("General", StringComparison.OrdinalIgnoreCase) || stripped == "@") return null;

        if (TryRenderFraction(stripped, value, out var fractionRendered)) return fractionRendered;

        var tokens = Tokenize(section);
        if (tokens.HasText) return null;
        return tokens.IsExponent ? RenderExponent(tokens, value) : RenderFixedPoint(tokens, value);
    }

    // The leading placeholder run only counts as an integer part when it is followed by at least one
    // space/tab before the numerator - "int" and "sep" sit in one optional group that must match both
    // or neither. Without that constraint, a naive "[0#?]*[ \t]*(numerator)" would greedily backtrack
    // an integer-less run like "??/??" into a one-char "integer" placeholder plus a one-char numerator
    // (regex backtracking stops at the first shape that matches at all, not the intended one), instead
    // of leaving the whole two-char run as the numerator. Numerator/denominator placeholders stay
    // '?'-only (matching the pre-existing grammar); only the integer run may mix '0'/'#'/'?'.
    private static readonly Regex FractionSectionPattern = new(
        @"\G(?:(?<int>[0#?]+)(?<sep>[ \t]+))?(?<num>\?+)/(?:(?<denph>\?+)|(?<denfixed>[0-9]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Bare characters Excel treats as literals without quoting inside a number section. Anything
    // else outside quotes must be a placeholder or an operator, so it ends a literal run.
    private const string BareFractionLiterals = "-+()$¥€£:!^&'~{}=<> ";

    /// <summary>Consumes the literal tokens Excel allows around a fraction core - quoted text,
    /// backslash escapes, <c>_x</c> (a space), <c>*x</c> (nothing) and bare sign/currency/space
    /// characters - starting at <paramref name="index"/>, appending their display text.</summary>
    private static void ReadFractionLiterals(string section, ref int index, StringBuilder text)
    {
        while (index < section.Length)
        {
            var character = section[index];
            if (character == '"')
            {
                var close = section.IndexOf('"', index + 1);
                if (close < 0) { text.Append(section, index + 1, section.Length - index - 1); index = section.Length; return; }
                text.Append(section, index + 1, close - index - 1);
                index = close + 1;
            }
            else if (character == '\\' && index + 1 < section.Length) { text.Append(section[index + 1]); index += 2; }
            else if (character == '_' && index + 1 < section.Length) { text.Append(' '); index += 2; }
            else if (character == '*' && index + 1 < section.Length) { index += 2; }
            else if (BareFractionLiterals.Contains(character)) { text.Append(character); index++; }
            else return;
        }
    }

    // Bounds the magnitude TryRenderFraction will run its denominator search against. A remainder is
    // mathematically within [0, 1) whenever the section has an integer-part placeholder (it is always
    // value - floor(value)), but an integer-less section ("?/?") feeds the *whole* value in as an
    // improper fraction, so a pathologically large cell value must not reach the numerator/denominator
    // int casts below: remainder * maxDenominator (itself capped at 999,999) must stay well inside
    // int.MaxValue for every candidate denominator, which this bound comfortably guarantees.
    private const double MaxFractionRemainderMagnitude = 1000;

    /// <summary>Detects and renders an Excel fraction section ("# ?/?", "# ??/??", "# ?/4", "?/?", ...),
    /// reproducing Excel's TEXT()-style column alignment rather than a packed string: '?' pads its slot
    /// with a space, '0' pads with zero, '#' pads with nothing, and literal characters (the int/fraction
    /// separator, '/') are echoed verbatim. The integer part is right-aligned (padded on the left) via
    /// <see cref="RenderIntegerPart"/> - the same helper fixed-point rendering uses for a mixed
    /// '0'/'#'/'?' run; the numerator is likewise right-aligned (<c>PadLeft</c>); the denominator is
    /// left-aligned (padded on the right, <c>PadRight</c>) - both with spaces, since <see
    /// cref="FractionSectionPattern"/> only allows '?' there. A literal digit denominator ("# ?/4") is
    /// echoed exactly as written (never padded), with the numerator rounded to it and never reduced
    /// (matching Excel). A placeholder denominator ("?"/"??"/"???" =&gt; max 9/99/999, capped at six
    /// digits as a safety bound) is found by scanning every candidate denominator from 1 up to that max
    /// for the smallest rounding error, ties broken in favour of the smaller denominator.
    ///
    /// A remainder that rounds all the way up to the next whole unit (numerator == denominator) carries
    /// into the integer part - but only when the section has one to carry into; an integer-less section
    /// has no such slot, so it is left as numerator == denominator (e.g. "1/1") rather than silently
    /// dropping the unit. Once carrying is resolved, a zero numerator renders one of two ways: with an
    /// integer part, Excel blanks the whole separator+numerator+'/'+denominator run with spaces of the
    /// same total width, so a whole-number cell still lines up in a column with sibling cells that do
    /// show a fraction (e.g. "# ?/?" on an exact 3 =&gt; "3    "); without one there is no integer slot
    /// to fall back on, so the value keeps rendering as a literal fraction (e.g. "?/?" on 0 =&gt; "0/1",
    /// which falls out of the normal numerator/denominator path with no extra special-casing, since the
    /// search above already resolves a zero remainder to numerator 0 over the smallest denominator 1).
    ///
    /// <paramref name="rendered"/> comes back null (caller falls back to raw, matching
    /// FormatNumericSections' other "cannot render" cases) for a non-finite or unreasonably large
    /// remainder - see <see cref="MaxFractionRemainderMagnitude"/> - so no unchecked numeric cast ever
    /// overflows. The method itself returns false only when the section is not this exact shape at all,
    /// letting the caller fall back to the general placeholder tokenizer instead. Every alignment space
    /// stays in the returned text; a caller wanting a leading sign glyph (see FormatNumericSections'
    /// classic single-section negative fallback) prepends it in front of this method's own leading
    /// spaces, exactly like Excel's own "-" + formatted-absolute-value composition.</summary>
    private static bool TryRenderFraction(string strippedSection, double value, out string? rendered)
    {
        rendered = null;
        // "-# ?/?", "(# ?/?)" or "# ?/?\" kg\"": literals may surround the fraction core, and the
        // core itself must then account for every remaining character of the section.
        var prefix = new StringBuilder();
        var coreStart = 0;
        ReadFractionLiterals(strippedSection, ref coreStart, prefix);
        var match = FractionSectionPattern.Match(strippedSection, coreStart);
        if (!match.Success) return false;
        var suffix = new StringBuilder();
        var suffixStart = match.Index + match.Length;
        ReadFractionLiterals(strippedSection, ref suffixStart, suffix);
        if (suffixStart != strippedSection.Length) return false;

        var hasIntegerPart = match.Groups["int"].Success;
        double integerPart;
        double remainder;
        if (hasIntegerPart) { integerPart = Math.Floor(value); remainder = value - integerPart; }
        else { integerPart = 0; remainder = value; }

        if (!double.IsFinite(remainder) || Math.Abs(remainder) >= MaxFractionRemainderMagnitude) return true;

        var numWidth = match.Groups["num"].Value.Length;
        int numerator;
        int denominator;
        string? denominatorLiteral;
        int denWidth;
        if (match.Groups["denfixed"].Success)
        {
            var denfixedText = match.Groups["denfixed"].Value;
            if (!int.TryParse(denfixedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) || denominator <= 0)
                return false;
            numerator = (int)Math.Round(remainder * denominator, MidpointRounding.AwayFromZero);
            denominatorLiteral = denfixedText;
            denWidth = denfixedText.Length;
        }
        else
        {
            var placeholderDigits = Math.Min(match.Groups["denph"].Value.Length, 6);
            var maxDenominator = Math.Max((int)Math.Pow(10, placeholderDigits) - 1, 1);
            denominator = 1;
            numerator = (int)Math.Round(remainder, MidpointRounding.AwayFromZero);
            var bestError = Math.Abs(remainder - numerator);
            for (var d = 2; d <= maxDenominator; d++)
            {
                var n = (int)Math.Round(remainder * d, MidpointRounding.AwayFromZero);
                var error = Math.Abs(remainder - (double)n / d);
                if (error < bestError - 1e-12) { bestError = error; numerator = n; denominator = d; }
            }
            denominatorLiteral = null;
            denWidth = match.Groups["denph"].Value.Length;
        }

        // Only an integer-bearing section absorbs the carried unit; an integer-less one has nowhere to
        // put it, so numerator == denominator is left standing (e.g. "1/1") instead of vanishing.
        if (numerator == denominator && hasIntegerPart) { integerPart += 1; numerator = 0; }

        var sb = new StringBuilder();
        if (hasIntegerPart)
        {
            var integerDigits = integerPart.ToString("F0", CultureInfo.InvariantCulture);
            var integerText = RenderIntegerPart(integerDigits, match.Groups["int"].Value.ToList());
            // A value that is exactly zero must still show a digit: "#"-only integer placeholders
            // would otherwise leave the whole cell blank once the fraction part is blanked too.
            if (numerator == 0 && integerText.Trim().Length == 0)
                integerText = "0".PadLeft(Math.Max(1, integerText.Length), ' ');
            sb.Append(integerText);

            if (numerator == 0)
            {
                // Nothing left to show as a fraction: blank the separator + numerator + '/' +
                // denominator with spaces of the same total width so this cell's integer still lines
                // up in a column with sibling cells that do render a visible fraction.
                sb.Append(' ', match.Groups["sep"].Value.Length + numWidth + 1 + denWidth);
                rendered = prefix + sb.ToString() + suffix;
                return true;
            }

            sb.Append(match.Groups["sep"].Value);
        }

        sb.Append(numerator.ToString(CultureInfo.InvariantCulture).PadLeft(numWidth, ' '));
        sb.Append('/');
        sb.Append(denominatorLiteral ?? denominator.ToString(CultureInfo.InvariantCulture).PadRight(denWidth, ' '));

        rendered = prefix + sb.ToString() + suffix;
        return true;
    }

    /// <summary>Positions (in the raw section string) of digit placeholders ('0','#','?') that are not
    /// inside a quoted literal, a backslash escape, or a bracketed color/condition tag. Used to decide
    /// whether a comma sits between placeholders (grouping) or after all of them (scale).</summary>
    private static List<int> ScanPlaceholderPositions(string section)
    {
        var positions = new List<int>();
        var i = 0;
        while (i < section.Length)
        {
            var c = section[i];
            if (c == '[') { var end = section.IndexOf(']', i + 1); i = end >= 0 ? end + 1 : i + 1; continue; }
            if (c == '"') { var end = section.IndexOf('"', i + 1); i = end >= 0 ? end + 1 : section.Length; continue; }
            if (c == '\\' && i + 1 < section.Length) { i += 2; continue; }
            if (c is '0' or '#' or '?') { positions.Add(i); i++; continue; }
            i++;
        }
        return positions;
    }

    private sealed class SectionTokens
    {
        public readonly List<char> IntPlaceholders = new();
        public readonly List<char> DecimalPlaceholders = new();
        public bool HasGrouping;
        public int ScaleCommaCount;
        public bool IsPercent;
        public bool HasText;
        public bool IsExponent;
        public char ExponentSign = '+';
        public int ExponentDigitCount;
        public bool ExponentUpper = true;
        public readonly List<(string Text, bool IsQuotedStyle)> PrefixLiterals = new();
        public readonly List<(string Text, bool IsQuotedStyle)> SuffixLiterals = new();
    }

    /// <summary>Tokenizes one number-format section into placeholder runs, grouping/scale/percent flags,
    /// an optional exponent spec, and ordered prefix/suffix literal runs. Digit placeholders are capped at
    /// <see cref="MaxPlaceholderDigits"/> so a pathological format cannot blow up rendering; each comma is
    /// classified as grouping or scale from <see cref="ScanPlaceholderPositions"/>.</summary>
    private static SectionTokens Tokenize(string section)
    {
        var t = new SectionTokens();
        var positions = ScanPlaceholderPositions(section);
        var lastPlaceholderIndex = positions.Count > 0 ? positions[^1] : -1;
        var placeholderSeenCount = 0;
        var inDecimalPart = false;
        var i = 0;
        while (i < section.Length)
        {
            var c = section[i];
            if (c == '[')
            {
                var end = section.IndexOf(']', i + 1);
                i = end >= 0 ? end + 1 : i + 1;
                continue;
            }
            if (c == '"')
            {
                var end = section.IndexOf('"', i + 1);
                var text = end >= 0 ? section[(i + 1)..end] : section[(i + 1)..];
                AddLiteral(text, true);
                i = end >= 0 ? end + 1 : section.Length;
                continue;
            }
            if (c == '\\' && i + 1 < section.Length)
            {
                var next = section[i + 1];
                if (next == ',')
                {
                    ClassifyComma(i);
                    i += 2;
                    continue;
                }
                if (next == '"')
                {
                    var closeSlash = section.IndexOf('\\', i + 2);
                    if (closeSlash >= 0 && closeSlash + 1 < section.Length && section[closeSlash + 1] == '"')
                    {
                        AddLiteral(section[(i + 2)..closeSlash], true);
                        i = closeSlash + 2;
                        continue;
                    }
                    AddLiteral("\"", false);
                    i += 2;
                    continue;
                }
                AddLiteral(next.ToString(), false);
                i += 2;
                continue;
            }
            if (c == '_' && i + 1 < section.Length) { AddLiteral(" ", false); i += 2; continue; }
            if (c == '*' && i + 1 < section.Length) { i += 2; continue; }
            if (c is '0' or '#' or '?')
            {
                if (placeholderSeenCount < MaxPlaceholderDigits)
                    (inDecimalPart ? t.DecimalPlaceholders : t.IntPlaceholders).Add(c);
                placeholderSeenCount++;
                i++;
                continue;
            }
            if (c == '.' && !inDecimalPart)
            {
                inDecimalPart = true;
                i++;
                continue;
            }
            if (c == ',')
            {
                ClassifyComma(i);
                i++;
                continue;
            }
            if (c == '%')
            {
                t.IsPercent = true;
                AddLiteral("%", false);
                i++;
                continue;
            }
            if ((c == 'E' || c == 'e') && i + 1 < section.Length && (section[i + 1] == '+' || section[i + 1] == '-'))
            {
                t.IsExponent = true;
                t.ExponentUpper = c == 'E';
                t.ExponentSign = section[i + 1];
                i += 2;
                var digitCount = 0;
                while (i < section.Length && section[i] is '0' or '#' or '?' && digitCount < MaxPlaceholderDigits)
                {
                    digitCount++;
                    i++;
                }
                t.ExponentDigitCount = digitCount;
                placeholderSeenCount++;
                continue;
            }
            if (c == '@') { t.HasText = true; i++; continue; }
            AddLiteral(c.ToString(), false);
            i++;
        }
        return t;

        void AddLiteral(string text, bool isQuotedStyle) => (placeholderSeenCount == 0 ? t.PrefixLiterals : t.SuffixLiterals).Add((text, isQuotedStyle));
        void ClassifyComma(int index) { if (index < lastPlaceholderIndex) t.HasGrouping = true; else t.ScaleCommaCount++; }
    }

    private static string RenderFixedPoint(SectionTokens t, double value)
    {
        // A section with no digit placeholder at all (e.g. the literal-only "-" zero-section in
        // "#,##0;-#,##0;\"-\"", or a condition's "\"big\"") is a constant: it never displays the value,
        // regardless of magnitude, so numeric rendering is skipped entirely.
        if (t.IntPlaceholders.Count == 0 && t.DecimalPlaceholders.Count == 0)
        {
            var literalOnly = new StringBuilder();
            foreach (var (text, _) in t.PrefixLiterals) literalOnly.Append(text);
            foreach (var (text, _) in t.SuffixLiterals) literalOnly.Append(text);
            return literalOnly.ToString();
        }

        var v = value;
        if (t.IsPercent) v *= 100;
        if (t.ScaleCommaCount > 0) v /= Math.Pow(1000, t.ScaleCommaCount);

        var decimalCount = t.DecimalPlaceholders.Count;
        var mathDecimalCount = Math.Min(decimalCount, 15);
        var rounded = Math.Round(v, mathDecimalCount, MidpointRounding.AwayFromZero);
        var fixedString = rounded.ToString("F" + mathDecimalCount, CultureInfo.InvariantCulture);
        var dotIndex = fixedString.IndexOf('.');
        var integerDigits = (dotIndex >= 0 ? fixedString[..dotIndex] : fixedString).TrimStart('-');
        var decimalDigitsRaw = dotIndex >= 0 ? fixedString[(dotIndex + 1)..] : string.Empty;
        if (decimalCount > mathDecimalCount) decimalDigitsRaw += new string('0', decimalCount - mathDecimalCount);

        var integerOutput = RenderIntegerPart(integerDigits, t.IntPlaceholders);
        if (t.HasGrouping && integerOutput.Length > 0) integerOutput = ApplyGrouping(integerOutput);
        var decimalOutput = RenderDecimalPart(decimalDigitsRaw, t.DecimalPlaceholders);
        var numeric = integerOutput + decimalOutput;

        var sb = new StringBuilder();
        foreach (var (text, _) in t.PrefixLiterals) sb.Append(text);
        sb.Append(numeric);
        for (var index = 0; index < t.SuffixLiterals.Count; index++)
        {
            var (text, isQuoted) = t.SuffixLiterals[index];
            if (index == 0 && isQuoted && numeric.Length > 0 && NeedsCompatibilitySpace(text)) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // A quoted unit written directly after the digits ("円" in #,##0"円") historically rendered with
    // one separating space, and readers rely on that. A literal that already begins with its own
    // whitespace (#,##0" 円", the form Excel itself writes) must not receive a second one.
    private static bool NeedsCompatibilitySpace(string quotedLiteral) =>
        quotedLiteral.Length > 0 && !char.IsWhiteSpace(quotedLiteral[0]);

    // Scientific/engineering notation. The exponent is forced to a multiple of n, the number of integer
    // placeholders before the decimal point ("0.00E+00" has n=1, giving classic single-digit scientific
    // notation; "##0.0E+0" has n=3, Excel's "engineering" form, e.g. 12345 => "12.3E+3"). The mantissa's
    // integer part is then rendered through the same placeholder-aware helpers as fixed-point numbers
    // (0=zero-pad, #/?=no leading pad) so it always lands in [1, 10^n) - or exactly 0 for a zero value -
    // with a post-rounding carry (mirroring RenderFixedPoint's own rounding) if rounding the mantissa
    // pushes it up to 10^n. Assumes value is never negative: callers always render from the absolute
    // magnitude, prepending any sign to the whole rendered section outside of this method.
    private static string RenderExponent(SectionTokens t, double value)
    {
        var placeholderCount = Math.Max(t.IntPlaceholders.Count, 1);
        int exponent;
        double mantissa;
        if (value == 0) { exponent = 0; mantissa = 0; }
        else
        {
            var trueExponent = (int)Math.Floor(Math.Log10(value));
            exponent = placeholderCount * FloorDiv(trueExponent, placeholderCount);
            mantissa = value / Math.Pow(10, exponent);
        }
        var decimals = Math.Min(t.DecimalPlaceholders.Count, 15);
        mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero);
        var upperBound = Math.Pow(10, placeholderCount);
        if (mantissa >= upperBound) { mantissa /= Math.Pow(10, placeholderCount); exponent += placeholderCount; mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero); }
        else if (mantissa > 0 && mantissa < 1) { mantissa *= Math.Pow(10, placeholderCount); exponent -= placeholderCount; mantissa = Math.Round(mantissa, decimals, MidpointRounding.AwayFromZero); }

        var mantissaFixed = mantissa.ToString("F" + decimals, CultureInfo.InvariantCulture);
        var mantissaDotIndex = mantissaFixed.IndexOf('.');
        var mantissaIntegerDigits = mantissaDotIndex >= 0 ? mantissaFixed[..mantissaDotIndex] : mantissaFixed;
        var mantissaDecimalDigitsRaw = mantissaDotIndex >= 0 ? mantissaFixed[(mantissaDotIndex + 1)..] : string.Empty;
        var mantissaString = RenderIntegerPart(mantissaIntegerDigits, t.IntPlaceholders) + RenderDecimalPart(mantissaDecimalDigitsRaw, t.DecimalPlaceholders);

        var expDigits = Math.Max(t.ExponentDigitCount, 1);
        var sign = exponent < 0 ? "-" : t.ExponentSign == '+' ? "+" : string.Empty;
        var expString = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(expDigits, '0');

        var sb = new StringBuilder();
        foreach (var (text, _) in t.PrefixLiterals) sb.Append(text);
        sb.Append(mantissaString).Append(t.ExponentUpper ? 'E' : 'e').Append(sign).Append(expString);
        for (var index = 0; index < t.SuffixLiterals.Count; index++)
        {
            var (text, isQuoted) = t.SuffixLiterals[index];
            if (index == 0 && isQuoted && NeedsCompatibilitySpace(text)) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>Mathematical floor division (rounds the quotient towards negative infinity, unlike C#'s
    /// truncating '/'), used to snap an exponent down to the nearest multiple of the engineering
    /// placeholder count even when the true exponent is negative.</summary>
    private static int FloorDiv(int dividend, int divisor)
    {
        var quotient = dividend / divisor;
        var remainder = dividend % divisor;
        if (remainder != 0 && (remainder < 0) != (divisor < 0)) quotient--;
        return quotient;
    }

    /// <summary>Left-pads with '0' placeholders only (never '#'/'?'), passing digits through unchanged
    /// once they meet or exceed the placeholder count so real digits are never truncated. A value that
    /// rounds to exactly zero with no '0' placeholder anywhere renders as empty, matching Excel's "#"
    /// applied to 0.</summary>
    private static string RenderIntegerPart(string digits, List<char> placeholders)
    {
        if (digits == "0" && !placeholders.Contains('0')) return string.Empty;
        if (digits.Length >= placeholders.Count) return digits;

        var padCount = placeholders.Count - digits.Length;
        var sb = new StringBuilder();
        for (var i = 0; i < padCount; i++)
        {
            var placeholder = placeholders[i];
            if (placeholder == '0') sb.Append('0');
            else if (placeholder == '?') sb.Append(' ');
        }
        sb.Append(digits);
        return sb.ToString();
    }

    /// <summary>Renders decimal digits against their placeholders: '0' always shows its digit; a trailing
    /// run of '#'/'?' placeholders whose digit is '0' is suppressed (omitted for '#', blanked for '?')
    /// until a non-zero digit or a '0' placeholder is reached.</summary>
    private static string RenderDecimalPart(string digitsRaw, List<char> placeholders)
    {
        if (placeholders.Count == 0) return string.Empty;
        var digits = digitsRaw.Length >= placeholders.Count ? digitsRaw[..placeholders.Count] : digitsRaw.PadRight(placeholders.Count, '0');
        var output = new char?[placeholders.Count];
        var trailing = true;
        for (var i = placeholders.Count - 1; i >= 0; i--)
        {
            var placeholder = placeholders[i];
            var digit = digits[i];
            if (trailing && placeholder != '0' && digit == '0')
            {
                output[i] = placeholder == '?' ? (char?)' ' : null;
                continue;
            }
            trailing = false;
            output[i] = digit;
        }
        var sb = new StringBuilder();
        foreach (var ch in output) if (ch.HasValue) sb.Append(ch.Value);
        return sb.Length == 0 ? string.Empty : "." + sb;
    }

    private static string ApplyGrouping(string digits)
    {
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        var offset = digits.Length % 3;
        if (offset > 0) sb.Append(digits, 0, offset);
        for (var i = offset; i < digits.Length; i += 3)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(digits, i, 3);
        }
        return sb.ToString();
    }

    private static NumberFormatAnalysis AnalyzeNumberFormat(string format, double number)
    {
        var active = (number < 0 && format.Split(';').Length > 1 ? format.Split(';')[1] : format.Split(';')[0]);
        var hasDate = false;
        var hasTime = false;
        var hasMinuteToken = false;
        var isElapsed = false;
        var hasSeconds = false;
        var hasAmPm = false;
        var hasPercent = false;
        var hasGrouping = false;
        var decimalPlaces = 0;
        var inQuote = false;
        var hasDecimal = false;
        var suffix = new StringBuilder();
        for (var index = 0; index < active.Length; index++)
        {
            var character = active[index];
            if (character == '"') { inQuote = !inQuote; continue; }
            if (inQuote) { suffix.Append(character); continue; }
            if (character == '\\' && index + 1 < active.Length)
            {
                // A backslash before comma is common in OOXML custom formats;
                // retain comma grouping semantics while ignoring other escapes.
                if (active[index + 1] == ',') { hasGrouping = true; index++; continue; }
                if (active[index + 1] == '"')
                {
                    var closingSlash = active.IndexOf('\\', index + 2);
                    if (closingSlash >= 0 && closingSlash + 1 < active.Length && active[closingSlash + 1] == '"')
                    {
                        suffix.Append(active[(index + 2)..closingSlash]);
                        index = closingSlash + 1;
                        continue;
                    }
                }
                suffix.Append(active[++index]); continue;
            }
            if (character is '_' or '*') { if (index + 1 < active.Length) index++; continue; }
            if (character == '[')
            {
                var end = active.IndexOf(']', index + 1);
                if (end >= 0)
                {
                    var bracket = active[(index + 1)..end];
                    if (bracket.Equals("h", StringComparison.OrdinalIgnoreCase) || bracket.Equals("m", StringComparison.OrdinalIgnoreCase) || bracket.Equals("s", StringComparison.OrdinalIgnoreCase))
                    {
                        isElapsed = true; hasTime = true; hasSeconds |= bracket.Equals("s", StringComparison.OrdinalIgnoreCase);
                    }
                    index = end;
                    continue;
                }
            }
            if (character == '%') { hasPercent = true; continue; }
            if (character is 'y' or 'Y' or 'd' or 'D') hasDate = true;
            else if (character is 'h' or 'H') hasTime = true;
            else if (character is 's' or 'S') { hasTime = true; hasSeconds = true; }
            else if (character is 'm' or 'M')
            {
                // Excel uses m for either month or minute. Resolve it after scanning
                // unquoted tokens so quoted/escaped literals cannot affect the result.
                hasMinuteToken = true;
            }
            else if (character == ',') hasGrouping = true;
            else if (character == '.' && !hasDecimal)
            {
                hasDecimal = true;
                decimalPlaces = active[(index + 1)..].TakeWhile(c => c is '0' or '#' or '?').Count();
            }
            if ((character is 'A' or 'a') && active[index..].StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
            {
                hasAmPm = true; hasTime = true;
            }
        }
        if (hasMinuteToken) hasDate |= !hasTime;
        var literalSuffix = suffix.ToString().Trim();
        return new(hasDate, hasTime, isElapsed, hasSeconds, hasAmPm, hasPercent, hasGrouping, decimalPlaces, literalSuffix.Length == 0 ? null : literalSuffix);
    }

    private static string FormatClock(double number, NumberFormatAnalysis analysis)
    {
        var fraction = number - Math.Floor(number);
        if (fraction < 0) fraction += 1;
        var totalSeconds = (int)Math.Floor(fraction * 24 * 60 * 60 + 0.5);
        if (totalSeconds >= 24 * 60 * 60) totalSeconds = 0;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        if (analysis.HasAmPm)
        {
            var date = new DateTime(2000, 1, 1).AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
            return date.ToString(analysis.HasSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.InvariantCulture);
        }
        return hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture) + (analysis.HasSeconds ? ":" + seconds.ToString("D2", CultureInfo.InvariantCulture) : string.Empty);
    }

    private static string FormatElapsed(double number, NumberFormatAnalysis analysis)
    {
        var totalSeconds = (long)Math.Floor(Math.Abs(number) * 24 * 60 * 60 + 0.5);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        var sign = number < 0 ? "-" : string.Empty;
        return sign + hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("D2", CultureInfo.InvariantCulture) + (analysis.HasSeconds ? ":" + seconds.ToString("D2", CultureInfo.InvariantCulture) : string.Empty);
    }
}

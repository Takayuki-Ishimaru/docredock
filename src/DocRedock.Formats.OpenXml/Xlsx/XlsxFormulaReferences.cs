namespace DocRedock.Formats.OpenXml.Xlsx;

/// <summary>The cells a worksheet formula reads, as far as its text says: A1 references and ranges, whole columns and
/// whole rows, on the formula's own sheet or on a named one. A formula whose reach its text does not settle - a defined
/// name, a structured or external reference (<c>Table1[Price]</c>, <c>[1]Sheet1!A1</c>), a reference across sheets
/// (<c>Sheet1:Sheet3!A1</c>), <c>INDIRECT</c> or <c>OFFSET</c>, a spilled range (<c>A1#</c>) - is untraceable; a caller
/// takes it to read anything.</summary>
internal static class XlsxFormulaReferences
{
    internal const int MaxColumn = 16384, MaxRow = 1048576;

    /// <summary>A rectangle of cells, 1-based and inclusive; <c>Sheet</c> is null for the formula's own sheet.</summary>
    internal readonly record struct Area(string? Sheet, int FirstColumn, int FirstRow, int LastColumn, int LastRow)
    {
        internal bool Contains(int column, int row) => column >= FirstColumn && column <= LastColumn && row >= FirstRow && row <= LastRow;
    }

    /// <summary>Adds the areas the formula reads to <paramref name="areas"/> and says whether that is all it reads. A
    /// shared formula stands for the cells to the right of and below its own (<paramref name="extraColumns"/>,
    /// <paramref name="extraRows"/>): its relative references move with each of them, so each area covers where they go.</summary>
    internal static bool TryRead(string formula, int extraColumns, int extraRows, List<Area> areas)
    {
        var s = formula;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') { i = AfterQuoted(s, i); continue; }
            if (c == '[') return false;
            if (c == '#') { i = AfterError(s, i); continue; }
            if (c == '\'')
            {
                var end = AfterQuoted(s, i);
                if (end >= s.Length || s[end] != '!') return false;
                var sheet = s[(i + 1)..(end - 1)].Replace("''", "'", StringComparison.Ordinal);
                if (sheet.Contains(':', StringComparison.Ordinal)) return false;
                i = end + 1;
                if (!ReadQualified(s, ref i, sheet, extraColumns, extraRows, areas)) return false;
                continue;
            }
            if (IsWordChar(c))
            {
                var start = i;
                while (i < s.Length && IsWordChar(s[i])) i++;
                var word = s[start..i];
                if (i < s.Length && s[i] == '!')
                {
                    i++;
                    if (!ReadQualified(s, ref i, word, extraColumns, extraRows, areas)) return false;
                    continue;
                }
                if (i < s.Length && s[i] == '(')
                {
                    var name = word.ToUpperInvariant().Replace("_XLFN.", "", StringComparison.Ordinal).Replace("_XLWS.", "", StringComparison.Ordinal);
                    if (name is "INDIRECT" or "OFFSET") return false;
                    continue;
                }
                i = start;
                if (TryReadArea(s, ref i, null, extraColumns, extraRows, out var area))
                {
                    if (i < s.Length && s[i] == '#') return false;
                    areas.Add(area);
                    continue;
                }
                i = start + word.Length;
                if (IsNumber(word, s, ref i) || word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    continue;
                // A defined name, a LET or LAMBDA parameter, or a reference across sheets (Sheet1:Sheet3!A1).
                return false;
            }
            i++;
        }
        return true;
    }

    /// <summary>The column and row of an A1 cell reference, without dollar signs.</summary>
    internal static bool TryParseCell(string reference, out int column, out int row)
    {
        var i = 0;
        return TryReadCell(reference, ref i, out column, out _, out row, out _) && i == reference.Length;
    }

    // The reference after "Sheet!": a sheet-qualified #REF! reads nothing.
    private static bool ReadQualified(string s, ref int i, string sheet, int extraColumns, int extraRows, List<Area> areas)
    {
        if (i < s.Length && s[i] == '#') { i = AfterError(s, i); return true; }
        if (!TryReadArea(s, ref i, sheet, extraColumns, extraRows, out var area) || i < s.Length && s[i] == '#') return false;
        areas.Add(area);
        return true;
    }

    private static bool TryReadArea(string s, ref int i, string? sheet, int extraColumns, int extraRows, out Area area)
    {
        area = default;
        var start = i;
        if (TryReadCell(s, ref i, out var column, out var columnFixed, out var row, out var rowFixed))
        {
            var (lastColumn, lastColumnFixed, lastRow, lastRowFixed) = (column, columnFixed, row, rowFixed);
            var beforeColon = i;
            if (i < s.Length && s[i] == ':')
            {
                i++;
                if (!TryReadCell(s, ref i, out lastColumn, out lastColumnFixed, out lastRow, out lastRowFixed))
                {
                    i = beforeColon;
                    (lastColumn, lastColumnFixed, lastRow, lastRowFixed) = (column, columnFixed, row, rowFixed);
                }
            }
            if (Ends(s, i))
            {
                area = Spread(sheet, (column, columnFixed), (lastColumn, lastColumnFixed), (row, rowFixed), (lastRow, lastRowFixed), extraColumns, extraRows);
                return true;
            }
        }
        // Whole columns (A:C) or whole rows (2:5).
        i = start;
        if (TryReadLine(s, ref i, letters: true, out var first, out var firstFixed) && i < s.Length && s[i] == ':')
        {
            i++;
            if (TryReadLine(s, ref i, letters: true, out var last, out var lastFixed) && Ends(s, i))
            {
                area = Spread(sheet, (first, firstFixed), (last, lastFixed), (1, true), (MaxRow, true), extraColumns, 0);
                return true;
            }
        }
        i = start;
        if (TryReadLine(s, ref i, letters: false, out first, out firstFixed) && i < s.Length && s[i] == ':')
        {
            i++;
            if (TryReadLine(s, ref i, letters: false, out var last, out var lastFixed) && Ends(s, i))
            {
                area = Spread(sheet, (1, true), (MaxColumn, true), (first, firstFixed), (last, lastFixed), 0, extraRows);
                return true;
            }
        }
        i = start;
        return false;
    }

    // A reference ends where no letter, digit or call follows ("A1B" and "LOG10(" are names).
    private static bool Ends(string s, int i) => i >= s.Length || !IsWordChar(s[i]) && s[i] != '(';

    // Where the cells of a range go when the formula is shared to the right and down: relative ends move, fixed ones stay.
    private static Area Spread(string? sheet, (int Value, bool Fixed) firstColumn, (int Value, bool Fixed) lastColumn,
        (int Value, bool Fixed) firstRow, (int Value, bool Fixed) lastRow, int extraColumns, int extraRows)
    {
        static int Reach((int Value, bool Fixed) end, int extra) => end.Value + (end.Fixed ? 0 : extra);
        var columns = (Math.Min(firstColumn.Value, lastColumn.Value), Math.Max(Reach(firstColumn, extraColumns), Reach(lastColumn, extraColumns)));
        var rows = (Math.Min(firstRow.Value, lastRow.Value), Math.Max(Reach(firstRow, extraRows), Reach(lastRow, extraRows)));
        return new(sheet, columns.Item1, rows.Item1, Math.Min(columns.Item2, MaxColumn), Math.Min(rows.Item2, MaxRow));
    }

    private static bool TryReadCell(string s, ref int i, out int column, out bool columnFixed, out int row, out bool rowFixed)
    {
        row = 0; rowFixed = false;
        var start = i;
        if (!TryReadLine(s, ref i, letters: true, out column, out columnFixed) || !TryReadLine(s, ref i, letters: false, out row, out rowFixed))
        {
            i = start;
            return false;
        }
        return true;
    }

    // A column (letters, up to XFD) or a row (digits, up to 1048576), with an optional leading dollar sign.
    private static bool TryReadLine(string s, ref int i, bool letters, out int value, out bool isFixed)
    {
        value = 0;
        var start = i;
        isFixed = i < s.Length && s[i] == '$';
        if (isFixed) i++;
        var digits = i;
        while (i < s.Length && (letters ? char.IsAsciiLetter(s[i]) : char.IsAsciiDigit(s[i])) && i - digits < 8)
        {
            value = letters ? value * 26 + char.ToUpperInvariant(s[i]) - 'A' + 1 : value * 10 + s[i] - '0';
            i++;
        }
        if (i == digits || (letters ? value > MaxColumn || i - digits > 3 : value < 1 || value > MaxRow))
        {
            i = start;
            return false;
        }
        return true;
    }

    // A number, its exponent's sign included ("1E+10" reads as "1E", "+", "10").
    private static bool IsNumber(string word, string s, ref int i)
    {
        var mantissa = word.EndsWith('E') || word.EndsWith('e') ? word[..^1] : word;
        if (mantissa.Length == 0 || !mantissa.All(c => char.IsAsciiDigit(c) || c == '.') || mantissa.Count(c => c == '.') > 1) return false;
        if (mantissa.Length < word.Length && i < s.Length && s[i] is '+' or '-')
            for (i++; i < s.Length && char.IsAsciiDigit(s[i]); i++) { }
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '\\' or '?' || c > 127;

    // The index after a quoted run ("text" or 'Sheet name'), a doubled quote standing for one.
    private static int AfterQuoted(string s, int i)
    {
        var quote = s[i];
        for (i++; i < s.Length; i++)
        {
            if (s[i] != quote) continue;
            if (i + 1 < s.Length && s[i + 1] == quote) { i++; continue; }
            return i + 1;
        }
        return s.Length;
    }

    // The index after an error value such as #REF!, #N/A or #DIV/0!.
    private static int AfterError(string s, int i)
    {
        for (i++; i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '/' or '_'); i++) { }
        return i < s.Length && s[i] is '!' or '?' ? i + 1 : i;
    }
}

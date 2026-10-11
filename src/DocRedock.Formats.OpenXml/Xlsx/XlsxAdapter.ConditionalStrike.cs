using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    private readonly record struct ConditionalRange(int Top, int Bottom, int Left, int Right)
    {
        public bool Contains(XlsxCellRecord cell) => cell.RowIndex >= Top && cell.RowIndex <= Bottom &&
            cell.ColumnIndex >= Left && cell.ColumnIndex <= Right;
    }
    private sealed record ConditionalRule(XmlElement Xml, ConditionalRange[] Ranges, int Priority, bool Stop,
        bool? Strike, bool Valid, int Order);
    private enum OperandKind { Blank, Number, Text, Boolean }
    private readonly record struct ConditionalOperand(OperandKind Kind, string Text = "", double Number = 0);
    private static readonly Regex ConditionalReference = new(@"^(\$?)([A-Za-z]{1,3})(\$?)([1-9][0-9]{0,6})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static List<XlsxCellRecord> ApplyConditionalStrike(byte[] xml, string sheet, List<XlsxCellRecord> cells,
        IReadOnlyDictionary<int, bool> strikeStyles, List<string> warnings, CancellationToken token)
    {
        if (strikeStyles.Count == 0) return cells;
        var document = new XmlDocument();
        using (var reader = XmlReader.Create(new MemoryStream(xml), SafeXml)) document.Load(reader);
        var ns = document.DocumentElement?.NamespaceURI ?? "";
        var rules = new List<ConditionalRule>();
        var badRange = false;
        foreach (var formatting in document.GetElementsByTagName("conditionalFormatting", ns).OfType<XmlElement>())
        {
            var parts = formatting.GetAttribute("sqref").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var parsed = parts.Select(ConditionalCellRange).ToArray();
            var ranges = parsed.OfType<ConditionalRange>().ToArray();
            foreach (var rule in formatting.ChildNodes.OfType<XmlElement>().Where(rule => rule.LocalName == "cfRule" && rule.NamespaceURI == ns))
            {
                bool? strike = AttributeInt(rule, "dxfId") is { } id && strikeStyles.TryGetValue(id, out var value) ? value : null;
                var priority = AttributeInt(rule, "priority");
                var stop = rule.GetAttribute("stopIfTrue") is "1" or "true";
                if (strike.HasValue && (ranges.Length == 0 || ranges.Length != parts.Length)) badRange = true;
                rules.Add(new(rule, ranges, priority is > 0 ? priority.Value : int.MinValue, stop, strike,
                    priority is > 0 && ranges.Length == parts.Length && ranges.Length > 0, rules.Count));
            }
        }
        if (!rules.Any(rule => rule.Strike.HasValue)) return cells;
        var ordered = rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Order).ToArray();
        var lookup = cells.ToDictionary(cell => (cell.RowIndex, cell.ColumnIndex));
        var undecided = badRange;
        var work = 0;
        var result = new List<XlsxCellRecord>(cells.Count);
        foreach (var cell in cells)
        {
            token.ThrowIfCancellationRequested();
            var effective = cell;
            foreach (var rule in ordered)
            {
                // Intersect ranges with existing cells, including whole rows/columns. Never walk
                // the declared rectangle: a full-sheet rule need not allocate a full worksheet.
                if ((++work & 255) == 0) token.ThrowIfCancellationRequested();
                if (work > 2_000_000) { undecided = true; break; }
                if (!rule.Ranges.Any(range => range.Contains(cell))) continue;
                if (rule.Strike is null && !rule.Stop) continue;
                var matched = rule.Valid ? ConditionalMatches(rule, cell, lookup) : null;
                if (matched == false) continue;
                if (matched is null)
                {
                    // A higher-priority unknown strike or stop may mask any later rule. Keep the
                    // ordinary font/rich-text marks instead of assuming that the unknown is false.
                    undecided = true;
                    break;
                }
                if (rule.Strike is { } strike)
                {
                    effective = cell with
                    {
                        DisplayStyle = (cell.DisplayStyle ?? new XlsxCellStyle()) with { IsStrike = strike },
                        StruckRanges = strike && cell.Value is { Length: > 0 } text ? [new TextRange(0, text.Length)] : null,
                    };
                    break; // Highest-priority active definition of this font property wins.
                }
                if (rule.Stop) break;
            }
            result.Add(effective);
        }
        if (undecided)
            warnings.Add($"XlsxConditionalStrikeNotEvaluated: {sheet} has conditional strikethrough rule(s) whose conditions, priority or ranges could not be evaluated; ordinary formatting was retained for affected cells.");
        return result;
    }

    private static ConditionalRange? ConditionalCellRange(string text)
    {
        text = text.Replace("$", "", StringComparison.Ordinal);
        if (ParseRange(text) is { } range) return new(range.MinRow, range.MaxRow, range.MinColumn, range.MaxColumn);
        var ends = text.Split(':');
        if (ends.Length != 2) return null;
        if (ends.All(end => end.Length is > 0 and <= 3 && end.All(character => char.IsAsciiLetter(character))))
        {
            var left = ParseCellReference(ends[0] + "1").Column;
            var right = ParseCellReference(ends[1] + "1").Column;
            return left > 0 && right >= left ? new(1, MaxExcelRows, left, right) : null;
        }
        if (int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var top) &&
            int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bottom) &&
            top is > 0 and <= MaxExcelRows && bottom >= top && bottom <= MaxExcelRows)
            return new(top, bottom, 1, MaxExcelColumns);
        return null;
    }

    private static bool? ConditionalMatches(ConditionalRule rule, XlsxCellRecord cell,
        IReadOnlyDictionary<(int Row, int Column), XlsxCellRecord> cells)
    {
        var formulas = rule.Xml.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "formula" &&
            element.NamespaceURI == rule.Xml.NamespaceURI).Select(element => element.InnerText.Trim()).ToArray();
        ConditionalOperand? Operand(string expression) => ReadConditionalOperand(expression, cell, rule.Ranges[0], cells);
        var subject = CellOperand(cell);
        switch (rule.Xml.GetAttribute("type"))
        {
            case "cellIs":
                var op = rule.Xml.GetAttribute("operator");
                if (formulas.Length != (op is "between" or "notBetween" ? 2 : 1)) return null;
                var first = CompareOperands(subject, Operand(formulas[0]));
                if (first is null) return null;
                if (op is "between" or "notBetween")
                {
                    var second = CompareOperands(subject, Operand(formulas[1]));
                    return second is null ? null : op == "between" ? first >= 0 && second <= 0 : first < 0 || second > 0;
                }
                return CompareResult(first.Value, op);
            case "expression":
                if (formulas.Length != 1 || formulas[0].Length > 4096) return null;
                var expression = formulas[0].StartsWith('=') ? formulas[0][1..] : formulas[0];
                var quoted = false;
                for (var index = 0; index < expression.Length; index++)
                {
                    if (expression[index] == '"') { quoted = !quoted; continue; }
                    if (quoted || expression[index] is not ('=' or '<' or '>')) continue;
                    var length = index + 1 < expression.Length && expression[index + 1] is '=' or '>' ? 2 : 1;
                    var comparison = CompareOperands(Operand(expression[..index]), Operand(expression[(index + length)..]));
                    return comparison is null ? null : CompareResult(comparison.Value, expression.Substring(index, length));
                }
                return Operand(expression) is { Kind: OperandKind.Boolean or OperandKind.Number } boolean ? boolean.Number != 0 : null;
            case "containsText":
            case "notContainsText":
            case "beginsWith":
            case "endsWith":
                if (subject is not { Kind: OperandKind.Text or OperandKind.Blank } || !rule.Xml.HasAttribute("text")) return null;
                var text = rule.Xml.GetAttribute("text");
                var type = rule.Xml.GetAttribute("type");
                // SEARCH-based text rules can give wildcard characters special meaning.
                if (type is "containsText" or "notContainsText" && text.IndexOfAny(['*', '?', '~']) >= 0) return null;
                return type switch
                {
                    "containsText" => subject.Value.Text.Contains(text, StringComparison.OrdinalIgnoreCase),
                    "notContainsText" => !subject.Value.Text.Contains(text, StringComparison.OrdinalIgnoreCase),
                    "beginsWith" => subject.Value.Text.StartsWith(text, StringComparison.OrdinalIgnoreCase),
                    _ => subject.Value.Text.EndsWith(text, StringComparison.OrdinalIgnoreCase),
                };
            default: return null;
        }
    }

    private static ConditionalOperand? CellOperand(XlsxCellRecord? cell)
    {
        if (cell is null) return new(OperandKind.Blank);
        if (cell.CellType is "e" or "d" || cell.Formula is not null && string.IsNullOrEmpty(cell.Value)) return null;
        if (string.IsNullOrEmpty(cell.Value)) return new(OperandKind.Blank);
        if (cell.CellType is "s" or "inlineStr" or "str") return new(OperandKind.Text, cell.Value);
        if (cell.CellType == "b") return cell.Value is "0" or "1" ? new(OperandKind.Boolean, Number: cell.Value == "1" ? 1 : 0) : null;
        return double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? new(OperandKind.Number, Number: number) : null;
    }

    private static ConditionalOperand? ReadConditionalOperand(string text, XlsxCellRecord cell, ConditionalRange origin,
        IReadOnlyDictionary<(int Row, int Column), XlsxCellRecord> cells)
    {
        text = text.Trim();
        if (text.StartsWith('=')) text = text[1..].Trim();
        if (text.Length == 0 || text.Length > 4096) return null;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var inner = text[1..^1];
            if (inner.Replace("\"\"", "", StringComparison.Ordinal).Contains('"')) return null;
            return new(OperandKind.Text, inner.Replace("\"\"", "\"", StringComparison.Ordinal));
        }
        if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("TRUE()", StringComparison.OrdinalIgnoreCase)) return new(OperandKind.Boolean, Number: 1);
        if (text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE()", StringComparison.OrdinalIgnoreCase)) return new(OperandKind.Boolean);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return new(OperandKind.Number, Number: number);
        var match = ConditionalReference.Match(text);
        if (!match.Success) return null;
        var coordinate = ParseCellReference(match.Groups[2].Value + match.Groups[4].Value);
        if (coordinate.Row <= 0 || coordinate.Column <= 0) return null;
        var row = coordinate.Row + (match.Groups[3].Length == 0 ? cell.RowIndex - origin.Top : 0);
        var column = coordinate.Column + (match.Groups[1].Length == 0 ? cell.ColumnIndex - origin.Left : 0);
        if (row is <= 0 or > MaxExcelRows || column is <= 0 or > MaxExcelColumns) return null;
        return CellOperand(cells.GetValueOrDefault((row, column)));
    }

    private static int? CompareOperands(ConditionalOperand? left, ConditionalOperand? right)
    {
        if (left is not { } a || right is not { } b) return null;
        if (a.Kind == OperandKind.Blank) a = new(b.Kind, Number: 0);
        if (b.Kind == OperandKind.Blank) b = new(a.Kind, Number: 0);
        if (a.Kind != b.Kind) return null; // Cross-type coercion is deliberately outside the supported subset.
        return a.Kind == OperandKind.Text ? StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text) : a.Number.CompareTo(b.Number);
    }

    private static bool? CompareResult(int comparison, string op) => op switch
    {
        "equal" or "=" => comparison == 0,
        "notEqual" or "<>" => comparison != 0,
        "lessThan" or "<" => comparison < 0,
        "lessThanOrEqual" or "<=" => comparison <= 0,
        "greaterThan" or ">" => comparison > 0,
        "greaterThanOrEqual" or ">=" => comparison >= 0,
        _ => null,
    };
}

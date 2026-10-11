using System.Globalization;
using System.Text;

namespace DocRedock.Formats.OpenXml.Xlsx;

internal static partial class XlsxConditionalFormula
{
    private sealed partial class Evaluator
    {
        private static FormulaValue Number(double value) => double.IsFinite(value) ? new(FormulaValueKind.Number, Number: value) : NumErr;
        private static FormulaValue Boolean(bool value) => value ? True : False;
        private static bool Failed(FormulaValue value) => value.Kind is FormulaValueKind.Error or FormulaValueKind.Unknown;
        private static IReadOnlyList<FormulaValue> Items(Value value) => value.Range ?? [value.Scalar];
        internal static FormulaValue ToFormula(Value value) => !value.IsRange ? value.Scalar : value.Range is { Count: 1 } ? value.Range[0] : Unknown;
        private static FormulaValue Numeric(FormulaValue value) => value.Kind switch
        {
            FormulaValueKind.Number or FormulaValueKind.Error or FormulaValueKind.Unknown => value,
            FormulaValueKind.Blank => Number(0), FormulaValueKind.Boolean => Number(value.Number),
            _ => double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Number(number) : ValueErr,
        };
        private static FormulaValue Truth(FormulaValue value) => value.Kind switch
        {
            FormulaValueKind.Boolean or FormulaValueKind.Number => Boolean(value.Number != 0),
            FormulaValueKind.Blank => False, FormulaValueKind.Error or FormulaValueKind.Unknown => value,
            _ => bool.TryParse(value.Text, out var flag) ? Boolean(flag) : ValueErr,
        };
        private static string Text(FormulaValue value) => value.Kind switch
        {
            FormulaValueKind.Blank => "", FormulaValueKind.Boolean => value.Number != 0 ? "TRUE" : "FALSE",
            FormulaValueKind.Number => value.Number.ToString("G15", CultureInfo.InvariantCulture), _ => value.Text,
        };

        internal Value Evaluate(Node node, int depth)
        {
            Check();
            if (depth > MaxNestingDepth) throw new FormulaLimitException();
            switch (node)
            {
                case LiteralNode literal: return Value.Of(literal.Value);
                case RefNode reference:
                    var values = _resolve(reference.Raw);
                    if (values is null) return UnknownValue;
                    if (values.Count > MaxRangeItems) throw new FormulaLimitException();
                    // Retain reference provenance, even for one cell: SUM(A1) ignores
                    // text/booleans stored in a cell, while SUM("3",TRUE) coerces literals.
                    return Value.OfRange(values);
                case UnaryNode unary:
                    var value = Numeric(ToFormula(Evaluate(unary.Child, depth + 1)));
                    return Value.Of(Failed(value) ? value : Number(unary.Op == "-" ? -value.Number : unary.Op == "%" ? value.Number / 100 : value.Number));
                case BinaryNode binary:
                    var left = ToFormula(Evaluate(binary.Left, depth + 1));
                    var right = ToFormula(Evaluate(binary.Right, depth + 1));
                    if (left.Kind == FormulaValueKind.Unknown || right.Kind == FormulaValueKind.Unknown) return UnknownValue;
                    if (Failed(left) || Failed(right)) return Value.Of(Failed(left) ? left : right);
                    if (binary.Op == "&") return Value.Of(new(FormulaValueKind.Text, Text(left) + Text(right)));
                    if (binary.Op is "=" or "<>" or "<" or ">" or "<=" or ">=") return Value.Of(Compare(binary.Op, left, right));
                    left = Numeric(left); right = Numeric(right);
                    if (Failed(left) || Failed(right)) return Value.Of(Failed(left) ? left : right);
                    var x = left.Number; var y = right.Number;
                    return Value.Of(binary.Op == "/" && y == 0 ? Div0 : Number(binary.Op switch
                    { "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, "^" => Math.Pow(x, y), _ => double.NaN }));
                case CallNode call: return Call(call, depth);
                default: return UnknownValue;
            }
        }

        private static FormulaValue Compare(string op, FormulaValue left, FormulaValue right)
        {
            if (left.Kind == FormulaValueKind.Unknown || right.Kind == FormulaValueKind.Unknown) return Unknown;
            if (Failed(left) || Failed(right)) return Failed(left) ? left : right;
            if (left.Kind == FormulaValueKind.Blank) left = new(right.Kind, "", 0);
            if (right.Kind == FormulaValueKind.Blank) right = new(left.Kind, "", 0);
            static int Rank(FormulaValueKind kind) => kind == FormulaValueKind.Boolean ? 2 : kind == FormulaValueKind.Text ? 1 : 0;
            var order = left.Kind == right.Kind ? left.Kind == FormulaValueKind.Text
                ? StringComparer.OrdinalIgnoreCase.Compare(left.Text, right.Text) : left.Number.CompareTo(right.Number)
                : Rank(left.Kind).CompareTo(Rank(right.Kind));
            return Boolean(op switch { "=" => order == 0, "<>" => order != 0, "<" => order < 0, ">" => order > 0, "<=" => order <= 0, ">=" => order >= 0, _ => false });
        }

        private Value Call(CallNode call, int depth)
        {
            var name = call.Name.ToUpperInvariant();
            var count = call.Args.Count;
            if (name is "TODAY" or "NOW")
                return Value.Of(count == 0 && _resolve("#" + name) is { Count: 1 } clock ? clock[0] : Unknown);
            if (name == "IF")
            {
                if (count is < 2 or > 3) return Value.Of(ValueErr);
                var condition = Truth(ToFormula(Evaluate(call.Args[0], depth + 1)));
                if (Failed(condition)) return Value.Of(condition);
                return condition.Number != 0 ? Evaluate(call.Args[1], depth + 1) : count == 3 ? Evaluate(call.Args[2], depth + 1) : Value.Of(False);
            }
            if (name is "IFERROR" or "IFNA")
            {
                if (count != 2) return Value.Of(ValueErr);
                var primary = Evaluate(call.Args[0], depth + 1);
                var scalar = ToFormula(primary);
                return scalar.Kind == FormulaValueKind.Error && (name == "IFERROR" || scalar.Text == "#N/A")
                    ? Evaluate(call.Args[1], depth + 1) : primary;
            }
            if (name is "ROW" or "COLUMN")
            {
                if (count == 0) return Value.Of(Number(name == "ROW" ? _row : _column));
                if (count == 1 && call.Args[0] is RefNode reference)
                    return Value.Of(_resolve("#" + name + ":" + reference.Raw) is { Count: 1 } coordinate ? coordinate[0] : Unknown);
                return Value.Of(Unknown);
            }
            var arity = name switch
            {
                "TRUE" or "FALSE" => (0, 0),
                "NOT" or "ISNUMBER" or "ISTEXT" or "ISNONTEXT" or "ISBLANK" or "ISLOGICAL" or "ISERROR" or "ISERR" or "ISNA" or
                    "N" or "VALUE" or "ABS" or "INT" or "SIGN" or "LEN" or "LOWER" or "UPPER" or "TRIM" or "COUNTBLANK" or "YEAR" or "MONTH" or "DAY" => (1, 1),
                "MOD" or "ROUND" or "ROUNDUP" or "ROUNDDOWN" or "EXACT" or "COUNTIF" => (2, 2),
                "TRUNC" or "LEFT" or "RIGHT" or "WEEKDAY" => (1, 2), "MID" or "DATE" => (3, 3), "SEARCH" or "FIND" or "SUMIF" => (2, 3),
                "COUNTIFS" => (2, MaxArguments), "SUMIFS" => (3, MaxArguments),
                "AND" or "OR" or "XOR" => (1, MaxArguments),
                "SUM" or "AVERAGE" or "MIN" or "MAX" or "COUNT" or "COUNTA" => (0, MaxArguments), _ => (-1, -1),
            };
            if (arity.Item1 < 0) return UnknownValue;
            if (count < arity.Item1 || count > arity.Item2 || name == "COUNTIFS" && count % 2 != 0 || name == "SUMIFS" && count % 2 != 1) return Value.Of(ValueErr);
            var args = call.Args.Select(arg => Evaluate(arg, depth + 1)).ToArray();
            foreach (var arg in args)
                foreach (var item in Items(arg)) { Check(); if (item.Kind == FormulaValueKind.Unknown) return UnknownValue; }
            FormulaValue Scalar(int index) => index < args.Length ? ToFormula(args[index]) : Blank;
            if (name is "SUM" or "AVERAGE" or "MIN" or "MAX" or "COUNT" or "COUNTA" or "COUNTBLANK") return Value.Of(Aggregate(name, args));
            if (name is "COUNTIF" or "SUMIF" or "COUNTIFS" or "SUMIFS") return Value.Of(CriteriaAggregate(name, args));
            if (name is "AND" or "OR" or "XOR")
            {
                var logical = new List<bool>();
                foreach (var arg in args)
                    foreach (var item in Items(arg))
                    {
                        Check();
                        if (arg.IsRange && item.Kind is FormulaValueKind.Blank or FormulaValueKind.Text) continue;
                        var boolean = Truth(item); if (Failed(boolean)) return Value.Of(boolean);
                        logical.Add(boolean.Number != 0);
                    }
                return Value.Of(logical.Count == 0 ? ValueErr : Boolean(name == "AND" ? logical.All(value => value) : name == "OR" ? logical.Any(value => value) : logical.Count(value => value) % 2 == 1));
            }
            var first = Scalar(0);
            if (first.Kind == FormulaValueKind.Unknown) return UnknownValue;
            if (name.StartsWith("IS", StringComparison.Ordinal)) return Value.Of(Boolean(name switch
            {
                "ISNUMBER" => first.Kind == FormulaValueKind.Number, "ISTEXT" => first.Kind == FormulaValueKind.Text,
                "ISNONTEXT" => first.Kind != FormulaValueKind.Text, "ISBLANK" => first.Kind == FormulaValueKind.Blank,
                "ISLOGICAL" => first.Kind == FormulaValueKind.Boolean, "ISERROR" => first.Kind == FormulaValueKind.Error,
                "ISERR" => first.Kind == FormulaValueKind.Error && first.Text != "#N/A", "ISNA" => first.Kind == FormulaValueKind.Error && first.Text == "#N/A", _ => false,
            }));
            if (name == "TRUE" || name == "FALSE") return Value.Of(name == "TRUE" ? True : False);
            if (name == "NOT") { var boolean = Truth(first); return Value.Of(Failed(boolean) ? boolean : Boolean(boolean.Number == 0)); }
            if (name == "N") return Value.Of(Failed(first) ? first : first.Kind is FormulaValueKind.Number or FormulaValueKind.Boolean ? Number(first.Number) : Number(0));
            foreach (var arg in args) if (Failed(ToFormula(arg))) return Value.Of(ToFormula(arg));
            if (name is "DATE" or "YEAR" or "MONTH" or "DAY" or "WEEKDAY") return Value.Of(DateFunction(name, args));
            if (name is "LEN" or "LOWER" or "UPPER" or "TRIM" or "LEFT" or "RIGHT" or "MID" or "FIND" or "SEARCH" or "EXACT")
                return Value.Of(TextFunction(name, args));
            var number = Numeric(first); if (Failed(number)) return Value.Of(number);
            if (name == "VALUE") return Value.Of(number);
            if (name is "ABS" or "INT" or "SIGN") return Value.Of(Number(name == "ABS" ? Math.Abs(number.Number) : name == "INT" ? Math.Floor(number.Number) : Math.Sign(number.Number)));
            var second = count > 1 ? Numeric(Scalar(1)) : Number(0); if (Failed(second)) return Value.Of(second);
            if (name == "MOD") return Value.Of(second.Number == 0 ? Div0 : Number(number.Number - second.Number * Math.Floor(number.Number / second.Number)));
            var digits = Math.Truncate(second.Number);
            if (digits > 308) return Value.Of(number);
            if (digits < -308) return Value.Of(Number(0));
            var factor = Math.Pow(10, digits); var scaled = number.Number * factor;
            if (!double.IsFinite(scaled)) return Value.Of(NumErr);
            var rounded = name == "ROUND" ? Math.Round(scaled, 0, MidpointRounding.AwayFromZero) : Math.CopySign(name == "ROUNDUP" ? Math.Ceiling(Math.Abs(scaled)) : Math.Floor(Math.Abs(scaled)), scaled);
            return Value.Of(Number(rounded / factor));
        }

        private FormulaValue Aggregate(string name, IReadOnlyList<Value> args)
        {
            var numbers = new List<double>(); var count = 0;
            foreach (var arg in args)
                foreach (var item in Items(arg))
                {
                    Check();
                    if (name == "COUNTA") { if (item.Kind != FormulaValueKind.Blank) count++; continue; }
                    if (name == "COUNTBLANK") { if (item.Kind == FormulaValueKind.Blank || item.Kind == FormulaValueKind.Text && item.Text.Length == 0) count++; continue; }
                    if (item.Kind == FormulaValueKind.Error) { if (name == "COUNT" && arg.IsRange) continue; return item; }
                    if (item.Kind == FormulaValueKind.Number) numbers.Add(item.Number);
                    else if (!arg.IsRange && item.Kind != FormulaValueKind.Blank)
                    {
                        var coerced = Numeric(item);
                        if (Failed(coerced)) { if (name == "COUNT") continue; return coerced; }
                        numbers.Add(coerced.Number);
                    }
                }
            return name switch
            {
                "COUNT" => Number(numbers.Count), "COUNTA" or "COUNTBLANK" => Number(count), "SUM" => Number(numbers.Sum()),
                "AVERAGE" => numbers.Count == 0 ? Div0 : Number(numbers.Average()), "MIN" => Number(numbers.Count == 0 ? 0 : numbers.Min()),
                _ => Number(numbers.Count == 0 ? 0 : numbers.Max()),
            };
        }

        private FormulaValue CriteriaAggregate(string name, IReadOnlyList<Value> args)
        {
            var sum = name is "SUMIF" or "SUMIFS";
            var multiple = name is "COUNTIFS" or "SUMIFS";
            var ranges = new List<IReadOnlyList<FormulaValue>>(); var criteria = new List<FormulaValue>();
            if (!multiple) { ranges.Add(Items(args[0])); criteria.Add(ToFormula(args[1])); }
            else for (var index = sum ? 1 : 0; index < args.Count; index += 2) { ranges.Add(Items(args[index])); criteria.Add(ToFormula(args[index + 1])); }
            foreach (var criterion in criteria) if (Failed(criterion)) return criterion;
            var values = sum ? Items(multiple ? args[0] : args.Count > 2 ? args[2] : args[0]) : ranges[0];
            if (ranges.Any(range => range.Count != ranges[0].Count) || sum && values.Count != ranges[0].Count) return Unknown;
            var total = 0d;
            for (var index = 0; index < ranges[0].Count; index++)
            {
                Check();
                if (!Enumerable.Range(0, ranges.Count).All(pair => CriterionMatches(ranges[pair][index], criteria[pair]))) continue;
                if (!sum) total++;
                else if (values[index].Kind == FormulaValueKind.Error) return values[index];
                else if (values[index].Kind == FormulaValueKind.Number) total += values[index].Number;
            }
            return Number(total);
        }

        private bool CriterionMatches(FormulaValue value, FormulaValue criterion)
        {
            Check();
            var op = "="; var wanted = criterion;
            if (criterion.Kind == FormulaValueKind.Text)
            {
                var text = criterion.Text;
                foreach (var candidate in new[] { "<=", ">=", "<>", "=", "<", ">" })
                    if (text.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; text = text[candidate.Length..]; break; }
                wanted = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? Number(number) : bool.TryParse(text, out var boolean) ? Boolean(boolean) : new(FormulaValueKind.Text, text);
            }
            if (criterion.Kind == FormulaValueKind.Blank) wanted = Number(0);
            if (wanted.Kind == FormulaValueKind.Text)
            {
                var actual = value.Kind is FormulaValueKind.Text or FormulaValueKind.Error ? value.Text : value.Kind == FormulaValueKind.Blank ? "" : null;
                if (op is "=" or "<>")
                {
                    var equal = actual is not null && Wildcard(actual, wanted.Text);
                    return op == "=" ? equal : !equal;
                }
                return actual is not null && Compare(op, new(FormulaValueKind.Text, actual), wanted).Number != 0;
            }
            if (wanted.Kind == FormulaValueKind.Number && value.Kind is FormulaValueKind.Text or FormulaValueKind.Blank)
                value = Numeric(value);
            if (value.Kind != wanted.Kind) return op == "<>";
            return Compare(op, value, wanted).Number != 0;
        }

        private bool Wildcard(string text, string pattern)
        {
            var at = 0; var current = 0; var star = -1; var retry = 0;
            while (at < text.Length)
            {
                Check();
                var escaped = current + 1 < pattern.Length && pattern[current] == '~' && pattern[current + 1] is '*' or '?' or '~';
                var literal = escaped ? pattern[current + 1] : current < pattern.Length ? pattern[current] : '\0';
                if (!escaped && current < pattern.Length && literal == '*') { star = current++; retry = at; continue; }
                if (current < pattern.Length && (!escaped && literal == '?' || char.ToUpperInvariant(literal) == char.ToUpperInvariant(text[at])))
                { at++; current += escaped ? 2 : 1; continue; }
                if (star < 0) return false;
                current = star + 1; at = ++retry;
            }
            while (current < pattern.Length && pattern[current] == '*') { Check(); current++; }
            return current == pattern.Length;
        }

        private FormulaValue TextFunction(string name, IReadOnlyList<Value> args)
        {
            var first = Text(ToFormula(args[0]));
            FormulaValue N(int index, double fallback) => index < args.Count ? Numeric(ToFormula(args[index])) : Number(fallback);
            if (name == "LEN") return Number(first.Length);
            if (name == "LOWER") return new(FormulaValueKind.Text, first.ToLowerInvariant());
            if (name == "UPPER") return new(FormulaValueKind.Text, first.ToUpperInvariant());
            if (name == "TRIM")
            {
                var output = new StringBuilder(); var space = true;
                foreach (var ch in first) { Check(); if (ch != ' ') { output.Append(ch); space = false; } else if (!space) { output.Append(ch); space = true; } }
                return new(FormulaValueKind.Text, output.ToString().TrimEnd(' '));
            }
            if (name == "EXACT") return Boolean(first.Equals(Text(ToFormula(args[1])), StringComparison.Ordinal));
            var amount = N(name is "FIND" or "SEARCH" ? 2 : 1, 1);
            if (Failed(amount)) return amount;
            if (amount.Number < 0 || amount.Number > int.MaxValue) return ValueErr;
            var count = (int)Math.Truncate(amount.Number);
            if (name is "LEFT" or "RIGHT") return new(FormulaValueKind.Text, name == "LEFT" ? first[..Math.Min(count, first.Length)] : first[Math.Max(0, first.Length - count)..]);
            if (name == "MID")
            {
                var length = N(2, 0); if (Failed(length)) return length;
                if (count < 1 || length.Number < 0 || length.Number > int.MaxValue) return ValueErr;
                return new(FormulaValueKind.Text, count > first.Length ? "" : first.Substring(count - 1, Math.Min((int)length.Number, first.Length - count + 1)));
            }
            var haystack = Text(ToFormula(args[1]));
            if (count < 1 || count > haystack.Length + 1) return ValueErr;
            if (first.Length == 0) return Number(count);
            if (name == "FIND") { var found = haystack.IndexOf(first, count - 1, StringComparison.Ordinal); return found < 0 ? ValueErr : Number(found + 1); }
            for (var index = count - 1; index <= haystack.Length; index++)
            { Check(); if (Wildcard(haystack[index..], first + "*")) return Number(index + 1); }
            return ValueErr;
        }
    }
}

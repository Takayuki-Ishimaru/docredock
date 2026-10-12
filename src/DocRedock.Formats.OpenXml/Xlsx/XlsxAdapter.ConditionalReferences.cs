using System.Globalization;
using System.Xml;

namespace DocRedock.Formats.OpenXml.Xlsx;

public sealed partial class XlsxAdapter
{
    private sealed record ConditionalName(string Name, string? Sheet, string Formula);
    private sealed record ConditionalWorkbook(IReadOnlyDictionary<string, IReadOnlyDictionary<(int Row, int Column), XlsxCellRecord>> Sheets,
        IReadOnlyList<ConditionalName> Names, bool Date1904)
    {
        private int remaining = 10_000_000;
        public DateTime Clock { get; } = DateTime.Now;
        public bool Spend(int work) { remaining -= work; return remaining >= 0; }
    }

    private static List<ConditionalName> ReadConditionalNames(Dictionary<string, byte[]> package, WorkbookInfo workbook)
    {
        if (!package.TryGetValue("xl/workbook.xml", out var bytes)) return [];
        var names = new List<ConditionalName>();
        var doc = new XmlDocument();
        using (var reader = XmlReader.Create(new MemoryStream(bytes), SafeXml)) doc.Load(reader);
        foreach (var name in doc.GetElementsByTagName("definedName", doc.DocumentElement?.NamespaceURI ?? "").OfType<XmlElement>())
        {
            var local = AttributeInt(name, "localSheetId");
            if (name.HasAttribute("localSheetId") && (local is null or < 0 || local >= workbook.Sheets.Count)) continue;
            names.Add(new(name.GetAttribute("name"), local is { } index ? workbook.Sheets[index].Name : null, name.InnerText.Trim()));
        }
        return names;
    }

    private static FormulaValue FormulaCell(XlsxCellRecord? cell)
    {
        if (cell?.CellType == "e") return new(FormulaValueKind.Error, cell.Value ?? "#VALUE!");
        var value = CellOperand(cell);
        return value is null ? new(FormulaValueKind.Unknown) : value.Value.Kind switch
        {
            OperandKind.Blank => new(FormulaValueKind.Blank), OperandKind.Text => new(FormulaValueKind.Text, value.Value.Text),
            OperandKind.Boolean => new(FormulaValueKind.Boolean, Number: value.Value.Number),
            _ => new(FormulaValueKind.Number, Number: value.Value.Number),
        };
    }

    private static FormulaValue EvaluateConditionalFormula(string formula, XlsxCellRecord cell, ConditionalRange origin,
        ConditionalWorkbook workbook, CancellationToken token)
    {
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = 0;
        if (!workbook.Spend(formula.Length)) return new(FormulaValueKind.Unknown);
        return Evaluate(formula, cell.SheetName);

        FormulaValue Evaluate(string expression, string sheet) => XlsxConditionalFormula.Evaluate(expression,
            reference => Resolve(reference, sheet), cell.RowIndex, cell.ColumnIndex, token);

        IReadOnlyList<FormulaValue>? Resolve(string reference, string currentSheet)
        {
            token.ThrowIfCancellationRequested();
            if (++work > 8192) return null;
            var targetSheet = currentSheet;
            var text = reference.Trim();
            if (text == "#NOW" || text == "#TODAY")
                return [new(FormulaValueKind.Number, Number: ExcelDateSerial(text == "#TODAY" ? workbook.Clock.Date : workbook.Clock, workbook.Date1904))];
            if (text == "#DATE1904") return [new(FormulaValueKind.Boolean, Number: workbook.Date1904 ? 1 : 0)];
            var coordinateKind = text.StartsWith("#ROW:", StringComparison.Ordinal) ? "ROW" : text.StartsWith("#COLUMN:", StringComparison.Ordinal) ? "COLUMN" : null;
            if (coordinateKind is not null) text = text[(coordinateKind.Length + 2)..];
            // External workbooks, 3D and structured references require information outside
            // this cache. Never fetch data or substitute an unrelated local sheet.
            if (text.IndexOfAny(['[', ']']) >= 0) return null;
            var bang = text.LastIndexOf('!');
            if (bang >= 0)
            {
                targetSheet = text[..bang]; text = text[(bang + 1)..];
                if (targetSheet.StartsWith('\''))
                {
                    if (!targetSheet.EndsWith('\'') || targetSheet.Length < 2) return null;
                    targetSheet = targetSheet[1..^1].Replace("''", "'", StringComparison.Ordinal);
                }
                if (targetSheet.Contains(':') || !workbook.Sheets.ContainsKey(targetSheet)) return null;
            }
            if (!workbook.Sheets.TryGetValue(targetSheet, out var cells)) return null;
            var ends = text.Split(':');
            if (coordinateKind is not null)
                return ends.Length == 1 && Reference(text) is { } coordinate
                    ? [new(FormulaValueKind.Number, Number: coordinateKind == "ROW" ? coordinate.Row : coordinate.Column)] : null;
            if (ends.Length is 1 or 2 && Reference(ends[0]) is { } first && Reference(ends[^1]) is { } last)
            {
                var top = Math.Min(first.Row, last.Row); var bottom = Math.Max(first.Row, last.Row);
                var left = Math.Min(first.Column, last.Column); var right = Math.Max(first.Column, last.Column);
                var count = (long)(bottom - top + 1) * (right - left + 1);
                if (count > 8192 || !workbook.Spend((int)count)) return null;
                var values = new List<FormulaValue>((int)count);
                for (var row = top; row <= bottom; row++)
                    for (var column = left; column <= right; column++)
                    {
                        if ((values.Count & 255) == 0) token.ThrowIfCancellationRequested();
                        values.Add(FormulaCell(cells.GetValueOrDefault((row, column))));
                    }
                return values;
            }
            var named = workbook.Names.Where(name => name.Name.Equals(text, StringComparison.OrdinalIgnoreCase) &&
                (name.Sheet is null || name.Sheet.Equals(targetSheet, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(name => name.Sheet is not null).ToArray();
            if (named.Length == 0 || named.Count(name => name.Sheet == named[0].Sheet) != 1 || activeNames.Count >= 16) return null;
            var key = targetSheet + "!" + text;
            if (!activeNames.Add(key)) return null;
            try
            {
                var expression = named[0].Formula.TrimStart('=');
                // A name can be a range, not just a scalar formula.
                var direct = expression.Contains(':') || expression.Contains('!') || ConditionalReference.IsMatch(expression)
                    ? Resolve(expression, targetSheet) : null;
                return direct ?? [Evaluate(expression, targetSheet)];
            }
            finally { activeNames.Remove(key); }
        }

        (int Row, int Column)? Reference(string reference)
        {
            var match = ConditionalReference.Match(reference);
            if (!match.Success) return null;
            var coordinate = ParseCellReference(match.Groups[2].Value + match.Groups[4].Value);
            var row = coordinate.Row + (match.Groups[3].Length == 0 ? cell.RowIndex - origin.Top : 0);
            var column = coordinate.Column + (match.Groups[1].Length == 0 ? cell.ColumnIndex - origin.Left : 0);
            return row is > 0 and <= MaxExcelRows && column is > 0 and <= MaxExcelColumns ? (row, column) : null;
        }
    }

    internal static double ExcelDateSerial(DateTime date, bool date1904) => date1904 ? (date - new DateTime(1904, 1, 1)).TotalDays :
        date.ToOADate() - (date < new DateTime(1900, 3, 1) ? 1 : 0);
}

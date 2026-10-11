namespace DocRedock.Formats.OpenXml.Xlsx;

internal static partial class XlsxConditionalFormula
{
    private sealed partial class Evaluator
    {
        private FormulaValue DateFunction(string name, IReadOnlyList<Value> args)
        {
            if (_resolve("#DATE1904") is not { Count: 1 } system) return Unknown;
            var date1904 = system[0].Number != 0;
            var values = args.Select(arg => Numeric(ToFormula(arg))).ToArray();
            if (values.Any(Failed)) return values.First(Failed);
            try
            {
                if (name == "DATE")
                {
                    var year = checked((int)Math.Truncate(values[0].Number));
                    if (year is >= 0 and < 1900) year += 1900;
                    var month = checked((int)Math.Truncate(values[1].Number));
                    var day = Math.Truncate(values[2].Number);
                    var start = new DateTime(year, 1, 1).AddMonths(checked(month - 1));
                    // Adding to the serial retains Excel's fictitious 1900-02-29.
                    return Number(XlsxAdapter.ExcelDateSerial(start, date1904) + day - 1);
                }
                var serial = Math.Floor(values[0].Number);
                if (serial < 0 || serial > 2958465) return NumErr;
                if (name == "WEEKDAY")
                {
                    var sunday = (int)((serial + (date1904 ? 1462 : 0) + 6) % 7);
                    var type = values.Length > 1 ? values[1].Number : 1;
                    return type == 1 ? Number(sunday + 1) : type == 2 ? Number((sunday + 6) % 7 + 1) :
                        type == 3 ? Number((sunday + 6) % 7) : Unknown;
                }
                if (!date1904 && serial == 60) return Number(name == "YEAR" ? 1900 : name == "MONTH" ? 2 : 29);
                var date = DateTime.FromOADate(serial + (date1904 ? 1462 : serial < 60 ? 1 : 0));
                return Number(name == "YEAR" ? date.Year : name == "MONTH" ? date.Month : date.Day);
            }
            catch (Exception error) when (error is ArgumentException or OverflowException) { return NumErr; }
        }
    }
}

using System.IO.Compression;
using System.Text;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// Regression coverage for XlsxAdapter's Excel-semantics number-format renderer: scaled numbers
/// (trailing-comma divisors), zero-padded codes, scientific/engineering exponent notation, fractions,
/// percent, digit grouping, and both the positive;negative;zero sectioned-format grammar and the
/// "[&lt;op&gt;value]"-conditional section grammar. See FormatNumericSections/RenderSection/Tokenize/
/// RenderExponent/TryRenderFraction/TryParseLeadingCondition in XlsxAdapter.cs for the implementation
/// these exercise.
/// </summary>
public sealed class XlsxNumberFormatTests
{
    [Theory]
    [InlineData("000000", "123", "000123")]
    [InlineData("0000", "12", "0012")]
    [InlineData("00000", "123456", "123456")]
    [InlineData("#,##0.0,,", "1234567", "1.2")]
    [InlineData("#,##0,", "1234567", "1,235")]
    [InlineData("0.0,,\"M\"", "2500000", "2.5 M")]
    [InlineData("0.00E+00", "1234567", "1.23E+06")]
    [InlineData("0.00E+00", "0.000123", "1.23E-04")]
    [InlineData("##0.0E+0", "1234567", "1.2E+6")]
    [InlineData("##0.0E+0", "12200000", "12.2E+6")]
    [InlineData("##0.0E+0", "123456", "123.5E+3")]
    [InlineData("##0.0E+0", "0.00012", "120.0E-6")]
    // Excel's TEXT()-style fraction alignment: '#' suppresses a leading-zero integer digit (leaving
    // just the literal separator space before the numerator), so 0.5 renders with a leading space, not
    // packed as "1/2". See TryRenderFraction's doc comment in XlsxAdapter.cs for the full padding rules.
    [InlineData("# ?/?", "0.5", " 1/2")]
    [InlineData("# ?/?", "3.75", "3 3/4")]
    // An exact 3 has no fractional remainder: Excel blanks the whole "separator + numerator + '/' +
    // denominator" run with spaces (1 + 1 + 1 + 1 = 4) instead of dropping it, so the integer still
    // lines up in a column with sibling cells that do show a fraction.
    [InlineData("# ?/?", "3", "3    ")]
    // The classic single-section negative fallback (FormatNumericSections) prepends "-" in front of
    // whatever TryRenderFraction returns for the absolute value (here " 1/4"), giving "- 1/4" rather
    // than a packed "-1/4".
    [InlineData("# ?/?", "-0.25", "- 1/4")]
    // Two-wide numerator/denominator placeholders right-align the numerator ("?? " -> " 1") and
    // left-align the denominator ("?? " -> "3 "), on top of the same leading-zero-suppressed integer
    // and literal separator space as above.
    [InlineData("# ??/??", "0.333", "  1/3 ")]
    [InlineData("# ??/??", "2.6667", "2  2/3 ")]
    // A literal digit denominator ("8") is echoed as-is and never padded; only the numerator's own "?"
    // placeholder gets alignment treatment.
    [InlineData("# ?/8", "0.5", " 4/8")]
    // No space between the leading placeholder run and the numerator means there is no integer part at
    // all - the whole "?/?" run is the (single-digit-wide) numerator/denominator of an improper fraction.
    [InlineData("?/?", "1.5", "3/2")]
    // Regression for the int/numerator split fix: with no separator, "??/??" must parse as an
    // integer-less 2-wide numerator over a 2-wide denominator (not a stray 1-char "integer" placeholder
    // plus a 1-char numerator), so 0.5 right-aligns to " 1" and left-aligns to "2 ".
    [InlineData("??/??", "0.5", " 1/2 ")]
    // A "0" integer placeholder zero-fills instead of being suppressed like "#".
    [InlineData("0 ?/?", "0.5", "0 1/2")]
    // No integer part means the value must always render as a literal fraction, even at exactly zero -
    // the numerator/denominator search naturally resolves 0 to numerator 0 over the smallest
    // denominator (1), so this needs no special-casing beyond the normal render path.
    [InlineData("?/?", "0", "0/1")]
    [InlineData("# ?/?", "0", "0    ")]
    // Verified against LibreOffice Calc's "save cell contents as shown" export, which mirrors Excel.
    [InlineData("# ?/?;-# ?/?", "-0.25", "- 1/4")]
    [InlineData("# ?/?;-# ?/?", "-3.5", "-3 1/2")]
    [InlineData("# ?/?;-# ?/?", "0.5", " 1/2")]
    [InlineData("# ?/?;(# ?/?)", "-0.25", "( 1/4)")]
    [InlineData("# ?/?\" kg\"", "1.5", "1 1/2 kg")]
    [InlineData("??/??", "-3.5", "- 7/2 ")]
    [InlineData("0 ?/?", "-0.25", "-0 1/4")]
    [InlineData("# ?/8", "3.75", "3 6/8")]
    [InlineData("# ??/??", "-0.25", "-  1/4 ")]
    [InlineData("# ??/??", "12.5", "12  1/2 ")]
    [InlineData("[>=1000]#,##0,\"K\";[<1]0.00;0", "12345", "12 K")]
    [InlineData("[>=1000]#,##0,\"K\";[<1]0.00;0", "0.5", "0.50")]
    [InlineData("[>=1000]#,##0,\"K\";[<1]0.00;0", "42", "42")]
    [InlineData("[Red][<0]-0.0;0.0", "-1.25", "-1.3")]
    [InlineData("[Red][<0]-0.0;0.0", "2", "2.0")]
    [InlineData("[>100]\"big\";\"small\"", "500", "big")]
    [InlineData("[>100]\"big\";\"small\"", "5", "small")]
    [InlineData("0.0%", "0.1234", "12.3%")]
    [InlineData("0%", "0.5", "50%")]
    [InlineData("#,##0;(#,##0)", "-1234", "(1,234)")]
    [InlineData("#,##0;-#,##0;\"-\"", "0", "-")]
    [InlineData("#,##0", "-1234", "-1,234")]
    [InlineData("\"\u00a5\"#,##0", "1500", "\u00a51,500")]
    [InlineData("#,##0\"\u5186\"", "12800", "12,800 \u5186")]
    [InlineData("#,##0\" \u5186\"", "12800", "12,800 \u5186")]
    [InlineData("[Red]0.0", "1.25", "1.3")]
    [InlineData("0", "2.5", "3")]
    [InlineData("#", "0", "")]
    [InlineData("General", "1234.5", "1234.5")]
    [InlineData("@", "123", "123")]
    public void Renders_number_format_per_excel_semantics(string formatCode, string rawValue, string expectedDisplay)
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateSingleCellPackage(formatCode, rawValue)));
        var sheet = Assert.Single(result.Worksheets);
        var cell = Assert.Single(sheet.Cells, c => c.CellReference == "A1");

        Assert.Equal(rawValue, cell.Value);
        Assert.Equal(expectedDisplay, cell.DisplayValue);
    }

    [Theory]
    [InlineData(1, "2.5", "3")]
    [InlineData(2, "1234.5", "1234.50")]
    [InlineData(3, "1234567", "1,234,567")]
    [InlineData(4, "-1234.5", "-1,234.50")]
    [InlineData(37, "-1234", "(1,234)")]
    [InlineData(39, "1234.567", "1,234.57")]
    [InlineData(41, "0", "-")]
    [InlineData(43, "-1234", "(1,234.00)")]
    [InlineData(48, "1234567", "1.23E+06")]
    public void Renders_builtin_number_format_ids_without_a_custom_numFmt(int numFmtId, string rawValue, string expectedDisplay)
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateSingleCellPackage(null, rawValue, numFmtId)));
        var cell = Assert.Single(Assert.Single(result.Worksheets).Cells, c => c.CellReference == "A1");

        Assert.Equal(rawValue, cell.Value);
        Assert.Equal(expectedDisplay, cell.DisplayValue);
    }

    [Fact]
    public void Extremely_long_format_falls_back_to_raw_without_throwing()
    {
        var formatCode = new string('#', 300);
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateSingleCellPackage(formatCode, "42")));
        var cell = Assert.Single(Assert.Single(result.Worksheets).Cells);

        Assert.Equal("42", cell.DisplayValue);
    }

    // "?/?" has no integer-part placeholder, so the whole value feeds the numerator/denominator search
    // as an improper fraction (see TryRenderFraction's MaxFractionRemainderMagnitude guard). A value far
    // outside that guard's bound must fall back to raw instead of driving an unchecked cast to int.
    [Fact]
    public void Huge_value_with_an_integer_less_fraction_format_falls_back_to_raw_without_throwing()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateSingleCellPackage("?/?", "123456789")));
        var cell = Assert.Single(Assert.Single(result.Worksheets).Cells);

        Assert.Equal("123456789", cell.DisplayValue);
    }

    [Fact]
    public void Placeholder_count_beyond_the_safety_cap_is_truncated_without_throwing()
    {
        var formatCode = new string('0', 100);
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateSingleCellPackage(formatCode, "5")));
        var cell = Assert.Single(Assert.Single(result.Worksheets).Cells);

        Assert.Equal(new string('0', 63) + "5", cell.DisplayValue);
    }

    /// <summary>Builds a minimal single-sheet, single-cell package with one custom number format
    /// (numFmtId 200) applied to A1, mirroring the ZIP-building style used elsewhere in this fixture
    /// suite (see CreateFormattedPackage). The format code is XML-attribute-escaped so callers can
    /// pass the literal Excel format string (quotes, currency glyphs, brackets, etc.) unescaped.</summary>
    private static byte[] CreateSingleCellPackage(string? formatCode, string rawValue, int numFmtId = 200)
    {
        var escapedFormat = formatCode?.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        var numFmts = formatCode is null
            ? string.Empty
            : $"<numFmts count=\"1\"><numFmt numFmtId=\"{numFmtId}\" formatCode=\"{escapedFormat}\" /></numFmts>";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/styles.xml"] = $"""
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  {numFmts}
                  <fonts count="1"><font><sz val="11" /></font></fonts>
                  <fills count="1"><fill><patternFill patternType="none" /></fill></fills>
                  <borders count="1"><border/></borders>
                  <cellXfs count="1"><xf numFmtId="{numFmtId}" fontId="0" fillId="0" borderId="0"/></cellXfs>
                </styleSheet>
                """,
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"0\" t=\"n\"><v>{rawValue}</v></c></row></sheetData></worksheet>",
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8);
                writer.Write(part.Value);
            }
        return output.ToArray();
    }
}

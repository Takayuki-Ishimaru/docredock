using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

/// <summary>The number-format engine shared by worksheet cells and native charts.</summary>
public sealed class ExcelNumberFormatTests
{
    [Theory]
    [InlineData(0.25, "0%", false, "25%")]
    [InlineData(1234.5, "#,##0.00", false, "1,234.50")]
    [InlineData(-1234.5, "#,##0;(#,##0)", false, "(1,235)")]
    [InlineData(1500000, "0.0,,\"M\"", false, "1.5 M")]
    [InlineData(45292, "m/d/yyyy", false, "2024-01-01")]
    [InlineData(45292, "m/d/yyyy", true, "2028-01-02")]
    [InlineData(45292.5, "yyyy-mm-dd hh:mm", false, "2024-01-01 12:00:00")]
    [InlineData(0.75, "h:mm", false, "18:00")]
    [InlineData(1.5, "[h]:mm", false, "36:00")]
    [InlineData(12345, "0.00E+00", false, "1.23E+04")]
    public void A_number_is_formatted_as_a_cell_shows_it(double value, string formatCode, bool date1904, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Format(value, formatCode, date1904));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("General")]
    [InlineData("@")]
    public void A_code_that_does_not_shape_the_number_returns_null(string? formatCode) =>
        Assert.Null(ExcelNumberFormat.Format(12.5, formatCode, false));

    [Fact]
    public void An_oversized_code_and_a_date_out_of_range_return_null()
    {
        Assert.Null(ExcelNumberFormat.Format(1, "0." + new string('0', ExcelNumberFormat.MaxFormatCodeLength), false));
        Assert.Null(ExcelNumberFormat.Format(1e300, "yyyy-mm-dd", false));
    }

    [Fact]
    public void Built_in_ids_resolve_to_their_codes_without_inventing_a_currency()
    {
        Assert.Equal("0%", ExcelNumberFormat.ResolveBuiltIn(9));
        Assert.Equal("yyyy-MM-dd", ExcelNumberFormat.ResolveBuiltIn(14));
        Assert.Null(ExcelNumberFormat.ResolveBuiltIn(5));
        Assert.True(ExcelNumberFormat.IsGeneral(" general "));
        Assert.False(ExcelNumberFormat.IsGeneral("0"));
    }
}

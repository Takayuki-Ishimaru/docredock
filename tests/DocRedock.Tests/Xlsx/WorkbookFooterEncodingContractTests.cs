using System.Text.Json;

namespace DocRedock.Tests.Xlsx;

/// <summary>Decode SpreadsheetML header/footer strings once, independently of rendering.</summary>
public sealed class WorkbookFooterEncodingContractTests
{
    [Theory]
    [InlineData("oddHeader")]
    [InlineData("oddFooter")]
    [InlineData("evenHeader")]
    [InlineData("evenFooter")]
    [InlineData("firstHeader")]
    [InlineData("firstFooter")]
    public void Encoded_newline_is_decoded_but_an_escaped_literal_escape_is_not_decoded_twice(string variant)
    {
        var graph = WorkbookPresentationTests.Extract($"""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter differentFirst="1" differentOddEven="1">
              <{variant} xml:space="preserve">&amp;CFirst_x000A_Second _x005F_x000A_ literal</{variant}>
            </headerFooter>
            """).Graph;
        var print = graph.FormatExtensions!["xlsx_layout"].GetProperty("sheets")
            .GetProperty("sheet-Sheet, O'Brien").GetProperty("print");
        var value = print.GetProperty("headerFooter").GetProperty(variant);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal("&CFirst\nSecond _x000A_ literal", value.GetString());
    }

    [Fact]
    public void Xml_entities_are_not_html_decoded_again_after_the_xml_reader_has_read_them()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter><oddFooter>&amp;amp;</oddFooter></headerFooter>
            """).Graph;
        var footer = graph.FormatExtensions!["xlsx_layout"].GetProperty("sheets")
            .GetProperty("sheet-Sheet, O'Brien").GetProperty("print")
            .GetProperty("headerFooter").GetProperty("oddFooter");
        Assert.Equal("&amp;", footer.GetString());
    }
}

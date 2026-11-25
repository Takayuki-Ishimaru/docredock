using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Xlsx;

public sealed class WorkbookHeaderFooterContentTests
{
    private const string SheetKey = "sheet-Sheet, O'Brien";

    [Fact]
    public void Oversized_extracted_footer_is_omitted_as_a_whole_and_reported_without_a_truncated_prefix()
    {
        var graph=WorkbookPresentationTests.Extract("<sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData><headerFooter><oddFooter>"+
            new string('q',WorkbookPresentationLayoutJson.MaxHeaderFooterLength+1)+"</oddFooter></headerFooter>").Graph;
        var layout=graph.FormatExtensions!["xlsx_layout"];
        Assert.DoesNotContain(new string('q',100),layout.GetRawText(),StringComparison.Ordinal);
        var result=DocRedock.Markdown.ReadableProjectionBuilder.Build(graph);
        Assert.Contains(result.Diagnostics,d=>d.Code=="XlsxPrintHeaderFooterInvalid");
    }

    [Fact]
    public void Self_closing_variant_is_an_explicit_empty_string()
    {
        var footer=ReadPrint("<sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData><headerFooter><oddFooter>A</oddFooter><evenFooter/></headerFooter>").HeaderFooter!;
        Assert.Equal("",footer.EvenFooter);Assert.Null(footer.FirstFooter);
    }

    private static WorkbookPresentationLayout ReadLayout(string sheetXml)
    {
        var result = WorkbookPresentationTests.Extract(sheetXml);
        var metadata = result.Graph.FormatExtensions!["xlsx_layout"];
        var diagnostics = new List<string>();
        Assert.True(WorkbookPresentationLayoutJson.TryRead(metadata, out var layout, diagnostics),
            string.Join("; ", diagnostics));
        Assert.Empty(diagnostics);
        return layout!;
    }

    private static WorkbookPrintSettings ReadPrint(string sheetXml) =>
        ReadLayout(sheetXml).Sheets[SheetKey].Print!;

    [Fact]
    public void Six_parts_flags_and_page_numbers_are_extracted_verbatim()
    {
        var print = ReadPrint("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter><oddHeader>odd head</oddHeader><oddFooter>odd foot</oddFooter><evenHeader>even head</evenHeader><evenFooter>even foot</evenFooter><firstHeader>first head</firstHeader><firstFooter>first foot</firstFooter></headerFooter>
            <pageSetup paperSize="9" firstPageNumber="7" useFirstPageNumber="1"/>
            """);
        Assert.True(print.HeaderFooterPresent);
        Assert.Equal(7, print.FirstPageNumber);
        Assert.True(print.UseFirstPageNumber);
        var hf = print.HeaderFooter!;
        Assert.Equal("odd head", hf.OddHeader);
        Assert.Equal("odd foot", hf.OddFooter);
        Assert.Equal("even head", hf.EvenHeader);
        Assert.Equal("even foot", hf.EvenFooter);
        Assert.Equal("first head", hf.FirstHeader);
        Assert.Equal("first foot", hf.FirstFooter);
        Assert.False(hf.DifferentOddEven);
        Assert.False(hf.DifferentFirst);
        Assert.True(hf.AlignWithMargins);
        Assert.True(hf.ScaleWithDocument);
    }

    [Fact]
    public void HeaderFooter_flags_are_read_from_attributes()
    {
        var hf = ReadPrint("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter differentOddEven="1" differentFirst="1" alignWithMargins="0" scaleWithDoc="0"><oddHeader>x</oddHeader></headerFooter>
            """).HeaderFooter!;
        Assert.True(hf.DifferentOddEven);
        Assert.True(hf.DifferentFirst);
        Assert.False(hf.AlignWithMargins);
        Assert.False(hf.ScaleWithDocument);
    }

    [Fact]
    public void Unicode_font_color_codes_escaped_ampersands_and_newlines_survive_exactly()
    {
        var hf = ReadPrint("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter><oddHeader>  &amp;26&amp;K&amp;"Arial,Bold"&amp;UÜmlaut😀&amp;&amp;  &#10;second&#13;line</oddHeader><oddFooter>&amp;L&amp;F&amp;R&amp;D</oddFooter></headerFooter>
            """).HeaderFooter!;
        Assert.Equal("  &26&K&\"Arial,Bold\"&UÜmlaut😀&&  \nsecond\rline", hf.OddHeader);
        Assert.Equal("&L&F&R&D", hf.OddFooter);
        Assert.Null(hf.EvenHeader);
    }

    [Fact]
    public void Empty_parts_are_distinguished_from_absent_and_whitespace_counts_as_present()
    {
        var print = ReadPrint("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter><oddHeader></oddHeader><evenFooter>   </evenFooter></headerFooter>
            """);
        var hf = print.HeaderFooter!;
        Assert.Equal(string.Empty, hf.OddHeader);
        Assert.Equal("   ", hf.EvenFooter);
        Assert.Null(hf.OddFooter);
        Assert.Null(hf.FirstHeader);
        // Whitespace-only parts are not treated as content: presence stays off.
        Assert.False(print.HeaderFooterPresent!.Value);
    }

    [Fact]
    public void Absent_headerFooter_yields_no_metadata_and_no_presence_flag()
    {
        var print = ReadPrint("""<sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>""");
        Assert.Null(print.HeaderFooter);
        Assert.Null(print.FirstPageNumber);
        Assert.Null(print.UseFirstPageNumber);
        Assert.False(print.HeaderFooterPresent!.Value);
    }

    [Fact]
    public void Out_of_range_first_page_number_is_dropped_at_extraction()
    {
        var print = ReadPrint("""
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <pageSetup firstPageNumber="99999999" useFirstPageNumber="1"/>
            """);
        Assert.Null(print.FirstPageNumber);
        Assert.True(print.UseFirstPageNumber);
    }

    [Fact]
    public void Core_json_round_trip_preserves_every_field_exactly()
    {
        var sheetXml = """
            <sheetData><row r="1"><c r="A1"><v>1</v></c></row></sheetData>
            <headerFooter differentOddEven="1"><oddHeader>  &amp;P &amp;&amp; 語 &#10;</oddHeader><oddFooter>f</oddFooter><evenHeader>e h</evenHeader><evenFooter>e f</evenFooter><firstHeader>f h</firstHeader><firstFooter>f f</firstFooter></headerFooter>
            <pageSetup firstPageNumber="42" useFirstPageNumber="1"/>
            """;
        var original = ReadLayout(sheetXml);
        var roundTripped = new List<string>();
        Assert.True(WorkbookPresentationLayoutJson.TryRead(WorkbookPresentationLayoutJson.Write(original), out var back, roundTripped),
            string.Join("; ", roundTripped));
        Assert.Empty(roundTripped);
        // Record equality would compare List instances by reference; compare the canonical JSON instead.
        Assert.True(JsonElement.DeepEquals(WorkbookPresentationLayoutJson.Write(original), WorkbookPresentationLayoutJson.Write(back!)));
    }

    [Fact]
    public void Malformed_headerFooter_and_first_page_number_are_dropped_with_diagnostics()
    {
        using var document = JsonDocument.Parse("""
            {"version":1,"sheets":{"sheet-A":{"print":{"headerFooter":{"oddHeader":5,"evenFooter":"ok","differentOddEven":"yes"},
            "firstPageNumber":0,"useFirstPageNumber":"yes","headerFooterPresent":true}}}}
            """);
        var diagnostics = new List<string>();
        Assert.True(WorkbookPresentationLayoutJson.TryRead(document.RootElement, out var layout, diagnostics));
        var print = layout!.Sheets["sheet-A"].Print!;
        Assert.True(print.HeaderFooterPresent);
        Assert.Equal("ok", print.HeaderFooter!.EvenFooter);
        Assert.Null(print.HeaderFooter.OddHeader);
        Assert.False(print.HeaderFooter.DifferentOddEven);
        Assert.True(print.HeaderFooter.AlignWithMargins);
        Assert.Null(print.FirstPageNumber);
        Assert.Null(print.UseFirstPageNumber);
        Assert.InRange(diagnostics.Count, 2, WorkbookPresentationLayoutJson.MaxDiagnostics);
    }

    [Fact]
    public void Oversized_header_string_is_rejected_not_truncated()
    {
        using var document = JsonDocument.Parse("{\"version\":1,\"sheets\":{\"sheet-A\":{\"print\":{\"headerFooter\":{\"oddHeader\":\"" +
            new string('x', WorkbookPresentationLayoutJson.MaxHeaderFooterLength + 1) + "\",\"evenFooter\":\"ok\"}}}}}");
        var diagnostics = new List<string>();
        Assert.True(WorkbookPresentationLayoutJson.TryRead(document.RootElement, out var layout, diagnostics));
        var hf = layout!.Sheets["sheet-A"].Print!.HeaderFooter!;
        Assert.Null(hf.OddHeader);
        Assert.Equal("ok", hf.EvenFooter);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Contains("oddHeader", StringComparison.Ordinal));
    }

    [Fact]
    public void Boundary_header_string_lengths_are_kept_exactly()
    {
        var exact = new string('y', WorkbookPresentationLayoutJson.MaxHeaderFooterLength);
        using var document = JsonDocument.Parse("{\"version\":1,\"sheets\":{\"sheet-A\":{\"print\":{\"headerFooter\":{\"oddHeader\":\"" + exact + "\"}}}}}");
        Assert.True(WorkbookPresentationLayoutJson.TryRead(document.RootElement, out var layout, new List<string>()));
        Assert.Equal(exact, layout!.Sheets["sheet-A"].Print!.HeaderFooter!.OddHeader);
    }
}

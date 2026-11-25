using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>
/// Source print contracts use known physical examples, not a second copy of the pagination algorithm.
/// These tests intentionally call internal pure helpers and the public HTML writer.
/// </summary>
public sealed class WorkbookSourcePrintContractTests
{
    private const double Tolerance = 0.00001;

    [Fact]
    public void Total_page_offset_and_first_active_superscript_follow_the_file_format_codes()
    {
        var runs=Assert.Single(WorkbookHeaderFooterParser.Parse("&N+3 &Xup&Ystill-up&Xnormal&Ydown")).Runs;
        Assert.Equal(3,Assert.Single(runs,r=>r.Field=='N').PageOffset);
        Assert.True(Assert.Single(runs,r=>r.Text=="up").Superscript);
        Assert.True(Assert.Single(runs,r=>r.Text=="still-up").Superscript);
        Assert.False(Assert.Single(runs,r=>r.Text=="still-up").Subscript);
        Assert.False(Assert.Single(runs,r=>r.Text=="normal").Superscript);
        Assert.True(Assert.Single(runs,r=>r.Text=="down").Subscript);
    }

    [Fact]
    public void Explicit_over_then_down_order_changes_only_the_physical_page_order()
    {
        var print=new WorkbookPrintSettings(PaperSize:1,Margins:ZeroMargins(),RowBreaks:[3],ColumnBreaks:[2]);
        var down=WorkbookPagePlan.Create(Scene(4,6),Sheet(print,width:10));
        var over=WorkbookPagePlan.Create(Scene(4,6),Sheet(print with{PageOrder="overThenDown"},width:10));
        Assert.Equal(4,down.Count);Assert.Equal(4,over.Count);
        Close(0,down[1].Columns[0].SourceStart);Close(60,down[1].Rows[0].SourceStart);
        Close(140,over[1].Columns[0].SourceStart);Close(0,over[1].Rows[0].SourceStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Absent_or_empty_footer_has_no_runs(string? text)
    {
        Assert.Empty(WorkbookHeaderFooterParser.Parse(text));
    }

    [Fact]
    public void Escaped_ampersands_are_literal_and_do_not_turn_the_following_letter_into_a_field()
    {
        var sections = WorkbookHeaderFooterParser.Parse("&LResearch && Development&C&&P / &&&P");
        Assert.Equal("Research & Development", Literal(Section(sections, "left")));
        var center = Section(sections, "center");
        Assert.Equal("&P / &", Literal(center));
        var page = Assert.Single(center.Runs, run => run.Field != '\0');
        Assert.Equal('P', page.Field);
        Assert.Equal(0, page.PageOffset);
    }

    [Fact]
    public void Repeated_alignment_codes_append_to_their_original_left_center_and_right_sections()
    {
        var sections = WorkbookHeaderFooterParser.Parse("&RRight&LLeft&Cmiddle&L again&R end");
        Assert.Equal(3, sections.Count);
        Assert.Equal("Left again", Literal(Section(sections, "left")));
        Assert.Equal("middle", Literal(Section(sections, "center")));
        Assert.Equal("Right end", Literal(Section(sections, "right")));
    }

    [Fact]
    public void Font_color_and_style_are_typed_and_are_retained_on_dynamic_page_fields()
    {
        var center = Section(WorkbookHeaderFooterParser.Parse(
            "&C&\"Consolas,Bold Italic\"&12&K12AB34code &P+3 / &N"), "center");
        Assert.Equal("code  / ", Literal(center));
        Assert.All(center.Runs, run =>
        {
            Assert.Equal("Consolas", run.Font);
            Assert.Equal(12d, run.Size);
            Assert.Equal("#12AB34", run.Color);
            Assert.True(run.Bold);
            Assert.True(run.Italic);
        });
        var page = Assert.Single(center.Runs, run => run.Field == 'P');
        Assert.Equal(3, page.PageOffset);
        Assert.Equal(0, Assert.Single(center.Runs, run => run.Field == 'N').PageOffset);
    }

    [Theory]
    [InlineData("&P+3", 3)]
    [InlineData("&P-2", -2)]
    [InlineData("&P", 0)]
    public void Page_offset_is_a_number_on_the_field_and_not_visible_literal_text(string text, int expected)
    {
        var run = Assert.Single(Assert.Single(WorkbookHeaderFooterParser.Parse(text)).Runs);
        Assert.Equal('P', run.Field);
        Assert.Equal(expected, run.PageOffset);
        Assert.Equal("", run.Text);
    }

    [Fact]
    public void Header_footer_variants_keep_explicit_empty_separate_from_absent_in_layout_round_trip()
    {
        var raw = FurnitureLayout(new
        {
            differentFirst = true,
            differentOddEven = true,
            oddFooter = "ODD_FOOTER",
            evenFooter = "",
            firstFooter = (string?)null,
        });
        Assert.True(WorkbookPresentationLayoutJson.TryRead(raw, out var layout));
        var written = WorkbookPresentationLayoutJson.Write(layout!);
        var footer = written.GetProperty("sheets").GetProperty("sheet-Print")
            .GetProperty("print").GetProperty("headerFooter");

        Assert.Equal("ODD_FOOTER", footer.GetProperty("oddFooter").GetString());
        Assert.Equal(JsonValueKind.String, footer.GetProperty("evenFooter").ValueKind);
        Assert.Equal("", footer.GetProperty("evenFooter").GetString());
        Assert.True(!footer.TryGetProperty("firstFooter", out var first) || first.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Selected_blank_first_and_even_variants_do_not_fall_back_to_the_odd_footer(string? firstFooter)
    {
        var html = FooterHtml(new
        {
            differentFirst = true,
            differentOddEven = true,
            oddFooter = "ODD_FOOTER",
            evenFooter = "",
            firstFooter,
        });
        var active = ActiveFurniture(html);
        Assert.Equal(new[] { "first", "even", "odd" }, active.Select(item => item.Variant));
        Assert.DoesNotContain("ODD_FOOTER", active[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ODD_FOOTER", active[1].Text, StringComparison.Ordinal);
        Assert.Contains("ODD_FOOTER", active[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void First_and_even_values_are_ignored_when_their_flags_are_disabled()
    {
        var html = FooterHtml(new
        {
            differentFirst = false,
            differentOddEven = false,
            oddFooter = "ODD_FOOTER",
            evenFooter = "EVEN_FOOTER",
            firstFooter = "FIRST_FOOTER",
        });
        var active = ActiveFurniture(html);
        Assert.Equal(new[] { "odd", "odd", "odd" }, active.Select(item => item.Variant));
        Assert.All(active, item =>
        {
            Assert.Contains("ODD_FOOTER", item.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("EVEN_FOOTER", item.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("FIRST_FOOTER", item.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Footer_source_markup_is_literal_text_even_though_the_viewer_contains_trusted_script()
    {
        var html = FooterHtml(new { oddFooter = "&R<script>FOOTER_PAYLOAD</script>" });
        Assert.DoesNotContain("<script>FOOTER_PAYLOAD", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;FOOTER_PAYLOAD&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_column_widths_ten_and_twenty_are_seventy_and_one_hundred_forty_pixels()
    {
        var axis = new WorkbookSourceAxis(new WorkbookSheetPresentation(
            DefaultColumnWidth: 10,
            ColumnWidths: new Dictionary<int, double> { [1] = 10, [2] = 20 }), true, true);
        Close(70, axis.Size(1));
        Close(140, axis.Size(2));
        Close(210, axis.Start(3));
        Assert.Equal(1, axis.Index(69.5));
        Assert.Equal(2, axis.Index(70));
        Assert.Equal(3, axis.Index(210));
    }

    [Fact]
    public void Hidden_column_and_row_interiors_collapse_without_moving_later_values_into_the_wrong_source_cell()
    {
        var sheet = new WorkbookSheetPresentation(
            DefaultColumnWidth: 10, DefaultRowHeight: 15,
            ColumnWidths: new Dictionary<int, double> { [2] = 20 },
            RowHeights: new Dictionary<int, double> { [2] = 30 },
            HiddenColumns: [2], HiddenRows: [2]);
        var fullX = new WorkbookSourceAxis(sheet, true, true);
        var visibleX = new WorkbookSourceAxis(sheet, true, false);
        Close(140, fullX.Size(2));
        Close(0, visibleX.Size(2));
        Close(210, fullX.Start(3));
        Close(70, visibleX.Start(3));
        Close(70, visibleX.Map(105, fullX)); // An interior point of the hidden B column.
        Close(105, visibleX.Map(245, fullX)); // C + 35px stays C + 35px.
        Close(245, fullX.Map(105, visibleX));
        Assert.Equal(3, visibleX.Index(70));

        var fullY = new WorkbookSourceAxis(sheet, false, true);
        var visibleY = new WorkbookSourceAxis(sheet, false, false);
        Close(40, fullY.Size(2)); // 30 points at 96dpi.
        Close(0, visibleY.Size(2));
        Close(20, visibleY.Map(40, fullY));
        Close(30, visibleY.Map(70, fullY));
        Close(70, fullY.Map(30, visibleY));
        Assert.Equal(3, visibleY.Index(20));
    }

    [Fact]
    public void Manual_row_and_column_breaks_make_four_tiles_with_titles_repeated_in_their_original_coordinates()
    {
        var sheet = Sheet(new WorkbookPrintSettings(
            PaperSize: 1, Margins: ZeroMargins(),
            Areas: ["$A$1:$D$6"], TitleRows: "$1:$1", TitleColumns: "$A:$A",
            RowBreaks: [3], ColumnBreaks: [2]), width: 10);
        var pages = WorkbookPagePlan.Create(Scene(4, 6), sheet);

        Assert.Equal(4, pages.Count);
        // A:B and rows 1:3; the manual break is AFTER the recorded source index.
        ExpectPage(pages, [(0, 140, 0)], [(0, 60, 0)]);
        // Columns C:D repeat A; rows 4:6 repeat row 1.
        ExpectPage(pages, [(0, 70, 0), (140, 140, 70)], [(0, 60, 0)]);
        ExpectPage(pages, [(0, 140, 0)], [(0, 20, 0), (60, 60, 20)]);
        ExpectPage(pages, [(0, 70, 0), (140, 140, 70)], [(0, 20, 0), (60, 60, 20)]);
        AssertPhysicalCapacity(pages);
    }

    [Theory]
    [InlineData("portrait", 100, 4)]
    [InlineData("landscape", 100, 3)]
    [InlineData("portrait", 50, 1)]
    [InlineData("landscape", 50, 2)]
    [InlineData("portrait", 200, 12)]
    public void Known_letter_paper_grid_has_the_expected_page_count_after_orientation_and_scaling(
        string orientation, int scale, int expectedPages)
    {
        // 3 columns * 350px and 100 rows * 20px, on 816 x 1056px Letter paper.
        // These physical examples leave margins at zero so all expected dimensions are explicit.
        var sheet = Sheet(new WorkbookPrintSettings(
            PaperSize: 1, Orientation: orientation, Margins: ZeroMargins(), Scale: scale), width: 50);
        var pages = WorkbookPagePlan.Create(Scene(3, 100), sheet);
        Assert.Equal(expectedPages, pages.Count);
        Assert.All(pages, page =>
        {
            Close(orientation == "landscape" ? 1056 : 816, page.PaperWidth);
            Close(orientation == "landscape" ? 816 : 1056, page.PaperHeight);
            Close(scale / 100d, page.Scale);
        });
        AssertPhysicalCapacity(pages);
        // Every source cell center is printed exactly once; the count alone could hide a gap or duplicate.
        for (var column = 0; column < 3; column++)
        for (var row = 0; row < 100; row++)
        {
            var x = column * 350 + 175;
            var y = row * 20 + 10;
            Assert.Single(pages, page => Covers(page.Columns, x) && Covers(page.Rows, y));
        }
    }

    [Fact]
    public void Fit_to_one_page_uses_the_limiting_physical_dimension_and_keeps_all_source_extent()
    {
        var sheet = Sheet(new WorkbookPrintSettings(
            PaperSize: 1, Orientation: "portrait", Margins: ZeroMargins(),
            FitToPage: true, FitToWidth: 1, FitToHeight: 1), width: 50);
        var page = Assert.Single(WorkbookPagePlan.Create(Scene(3, 100), sheet));
        Close(1056d / 2000d, page.Scale);
        var column = Assert.Single(page.Columns);
        var row = Assert.Single(page.Rows);
        Close(0, column.SourceStart); Close(1050, column.Length);
        Close(0, row.SourceStart); Close(2000, row.Length);
        AssertPhysicalCapacity([page]);
    }

    [Fact]
    public void An_oversized_source_column_is_continuously_tiled_and_the_following_column_is_not_dropped()
    {
        var sheet = Sheet(new WorkbookPrintSettings(PaperSize: 1, Margins: ZeroMargins()), width: 10) with
        {
            ColumnWidths = new Dictionary<int, double> { [1] = 255, [2] = 10 },
        };
        // The first column is 1785px wide and the second is 70px: all 1855px must reach paper.
        var pages = WorkbookPagePlan.Create(Scene(2, 1), sheet);
        Assert.Equal(3, pages.Count);
        var bands = pages.Select(page => Assert.Single(page.Columns)).OrderBy(band => band.SourceStart).ToArray();
        var cursor = 0d;
        foreach (var band in bands)
        {
            Close(cursor, band.SourceStart);
            Assert.True(band.Length > 0);
            cursor += band.Length;
        }
        Close(1855, cursor);
        Close(70, cursor - 1785);
        Assert.Single(pages, page => Covers(page.Columns, 1820)); // Center of the ordinary B column.
        AssertPhysicalCapacity(pages);
    }

    private static WorkbookFooterSection Section(IReadOnlyList<WorkbookFooterSection> sections, string alignment) =>
        Assert.Single(sections, section => section.Alignment == alignment);

    private static string Literal(WorkbookFooterSection section) =>
        string.Concat(section.Runs.Where(run => run.Field == '\0').Select(run => run.Text));

    private static WorkbookPrintMargins ZeroMargins() => new(0, 0, 0, 0);

    private static WorkbookSheetPresentation Sheet(WorkbookPrintSettings print, double width) =>
        new(Name: "Print", DefaultColumnWidth: width, DefaultRowHeight: 15, Print: print);

    private static ReadableWorksheetScene Scene(int columns, int rows) =>
        new(new ReadableTable(
            Enumerable.Range(1, columns).Select(column => new ReadableColumn(column)).ToArray(),
            Enumerable.Range(1, rows).Select(row => new ReadableTableRow(
                Enumerable.Range(1, columns).Select(column =>
                    new ReadableTableCell(ReadableContent.Text($"r{row}c{column}"), SourceColumn: column)).ToArray(),
                SourceRow: row)).ToArray(), HeaderRowCount: 0),
            new WorkbookDrawingScene("Print", []));

    private static JsonElement FurnitureLayout(object furniture) =>
        JsonSerializer.SerializeToElement(new
        {
            version = 1,
            sheets = new Dictionary<string, object>
            {
                ["sheet-Print"] = new
                {
                    name = "Print", defaultColumnWidth = 10, defaultRowHeight = 15,
                    print = new
                    {
                        paperSize = 1,
                        margins = new { left = 0, right = 0, top = 0, bottom = 0 },
                        rowBreaks = new[] { 1, 2 },
                        headerFooterPresent = true,
                        headerFooter = furniture,
                    },
                },
            },
        });

    private static string FooterHtml(object furniture)
    {
        var scene = Scene(1, 3);
        var section = new ReadableSection("print-sheet", ReadableContent.Text("Print"), [scene])
        {
            Origin = new("sheet-Print", "Print"),
        };
        return ReadableHtmlWriter.WriteDocument(new ReadableDocument(
            "Print contract", "en", DocumentFormatKind.Xlsx, [section], "source.xlsx")
        {
            WorkbookLayout = FurnitureLayout(furniture),
        }).Html;
    }

    private static IReadOnlyList<(string Variant, string Text)> ActiveFurniture(string html)
    {
        var result = new List<(string Variant, string Text)>();
        foreach (Match opening in Regex.Matches(html,
            "<div\\b[^>]*\\bclass=\"source-print-furniture\"[^>]*>", RegexOptions.CultureInvariant))
        {
            if (Regex.IsMatch(opening.Value, "\\shidden(?:\\s|>|=)", RegexOptions.CultureInvariant)) continue;
            var variant = Regex.Match(opening.Value, "\\bdata-furniture-variant=\"([^\"]+)\"",
                RegexOptions.CultureInvariant).Groups[1].Value;
            var depth = 1;
            var end = -1;
            foreach (Match tag in Regex.Matches(html[opening.Index..], "</?div\\b[^>]*>",
                RegexOptions.CultureInvariant).Cast<Match>().Skip(1))
            {
                depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
                if (depth == 0) { end = opening.Index + tag.Index; break; }
            }
            Assert.True(end >= opening.Index, "A source print furniture container must be balanced.");
            var content = html[(opening.Index + opening.Length)..end];
            result.Add((variant, WebUtility.HtmlDecode(Regex.Replace(content, "<[^>]+>", "",
                RegexOptions.CultureInvariant))));
        }
        return result;
    }

    private static bool Covers(IReadOnlyList<WorkbookSourceBand> bands, double point) =>
        bands.Any(band => point >= band.SourceStart && point < band.SourceStart + band.Length);

    private static void ExpectPage(IReadOnlyList<WorkbookPrintPage> pages,
        (double Source, double Length, double Destination)[] columns,
        (double Source, double Length, double Destination)[] rows) =>
        Assert.Single(pages, page => SameBands(page.Columns, columns) && SameBands(page.Rows, rows));

    private static bool SameBands(IReadOnlyList<WorkbookSourceBand> actual,
        (double Source, double Length, double Destination)[] expected) =>
        actual.Count == expected.Length && actual.Zip(expected).All(pair =>
            Math.Abs(pair.First.SourceStart - pair.Second.Source) < Tolerance &&
            Math.Abs(pair.First.Length - pair.Second.Length) < Tolerance &&
            Math.Abs(pair.First.DestinationStart - pair.Second.Destination) < Tolerance);

    private static void AssertPhysicalCapacity(IReadOnlyList<WorkbookPrintPage> pages) =>
        Assert.All(pages, page =>
        {
            Assert.True(page.Columns.Sum(band => band.Length) * page.Scale <= page.ContentWidth + Tolerance);
            Assert.True(page.Rows.Sum(band => band.Length) * page.Scale <= page.ContentHeight + Tolerance);
        });

    private static void Close(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}

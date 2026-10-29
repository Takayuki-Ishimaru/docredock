using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Formats.Pdf;
using DocRedock.Tests.Pdf;

namespace DocRedock.Tests.Api;

/// <summary>
/// Struck-through text reads as struck through (GFM ~~) in every format, tables included, so a model
/// is not handed a cancelled value as a valid one: Excel cell fonts and rich-text runs, Word single and
/// double strikethrough, PowerPoint runs, and in PDF a strikethrough drawn over text or a StrikeOut
/// annotation. A PDF strikethrough over part of a show strikes the characters under it when the font
/// gives its glyph widths; when it gives none, it is reported instead of guessed. Underlines and
/// strikethroughs never turn into drawings.
/// </summary>
public sealed class StrikethroughRecognitionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-strike-").FullName;
    private readonly DocumentService service = new(null, null, discoverPdfRasterizer: false);
    public void Dispose() => Directory.Delete(root, true);

    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private string Write(string name, IReadOnlyDictionary<string, string> parts)
    {
        var path = Path.Combine(root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }

    private async Task<(string Markdown, IReadOnlyList<DocRedock.Core.Reporting.Diagnostic> Diagnostics)> Export(string source)
    {
        var output = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + ".md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        return ((await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), result.Diagnostics);
    }

    private string Workbook(string name, string sheetData, string sharedStrings = "", string fonts = "<font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font>",
        string cellXfs = "<xf fontId=\"0\"/><xf fontId=\"1\"/>") => Write(name, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
        ["xl/workbook.xml"] = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Items\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
        ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{SpreadsheetNs}\">{sharedStrings}</sst>",
        ["xl/styles.xml"] = $"<styleSheet xmlns=\"{SpreadsheetNs}\"><fonts>{fonts}</fonts><cellXfs>{cellXfs}</cellXfs></styleSheet>",
        ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{SpreadsheetNs}\"><sheetData>{sheetData}</sheetData></worksheet>",
    });

    private static string Inline(string reference, string runs, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>{runs}</is></c>";

    private static string Row(int number, params string[] cells) => $"<row r=\"{number}\">{string.Concat(cells)}</row>";

    [Fact]
    public async Task Excel_inline_rich_text_keeps_every_run_and_never_the_phonetic_guide()
    {
        // The last <t> used to overwrite the others: "旧: 30箱 → 35箱" read "→ 35箱", and an inline
        // string with a phonetic guide read as its kana.
        var source = Workbook("inline.xlsx", Row(1, Inline("A1", "<t>品目</t>", 1), Inline("B1", "<t>備考</t>", 1)) +
            Row(2, Inline("A2", "<t>ぶどう</t>"), Inline("B2", "<r><t xml:space=\"preserve\">旧: </t></r><r><t>30箱</t></r><r><t xml:space=\"preserve\"> → 35箱</t></r>")) +
            Row(3, Inline("A3", "<t>りんご</t><rPh sb=\"0\" eb=\"3\"><t>リンゴ</t></rPh>"), Inline("B3", "<r><t>Hello</t></r><r><t xml:space=\"preserve\"> </t></r><r><t>World</t></r>")));
        var (markdown, _) = await Export(source);
        Assert.Contains("| ぶどう | 旧: 30箱 → 35箱 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| りんご | Hello World |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("リンゴ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_struck_cells_and_struck_runs_read_as_struck()
    {
        var shared = "<si><t>品目</t></si><si><t>数量</t></si><si><t>備考</t></si><si><t>みかん</t></si><si><t>取消</t></si>" +
            "<si><r><t xml:space=\"preserve\">旧: </t></r><r><rPr><strike/></rPr><t>30箱</t></r><r><t xml:space=\"preserve\"> → 35箱</t></r></si>" +
            "<si><r><rPr><strike val=\"0\"/></rPr><t>残す</t></r><r><t>消す</t></r></si>" +
            "<si><t>りんご</t></si><si><t>確定</t></si>";
        var fonts = "<font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font><font><strike/><sz val=\"11\"/></font>";
        var xfs = "<xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"2\"/><xf fontId=\"2\" numFmtId=\"3\"/>";
        string Shared(string reference, int index, int style = 0) => $"<c r=\"{reference}\" s=\"{style}\" t=\"s\"><v>{index}</v></c>";
        var source = Workbook("strike.xlsx",
            Row(1, Shared("A1", 0, 1), Shared("B1", 1, 1), Shared("C1", 2, 1)) +
            Row(2, Shared("A2", 7), "<c r=\"B2\"><v>10</v></c>", Shared("C2", 8)) +
            Row(3, Shared("A3", 3, 2), "<c r=\"B3\" s=\"3\"><v>1200</v></c>", Shared("C3", 4, 2)) +
            Row(4, Shared("A4", 7), "<c r=\"B4\"><v>30</v></c>", Shared("C4", 5)) +
            Row(5, Shared("A5", 7), "<c r=\"B5\"><v>40</v></c>", Shared("C5", 6, 2)),
            shared, fonts, xfs);
        var (markdown, _) = await Export(source);
        // A struck font strikes the cell however it is shown (a number with its format included).
        Assert.Contains("| ~~みかん~~ | ~~1,200~~ | ~~取消~~ |", markdown, StringComparison.Ordinal);
        // A struck run strikes only itself; a run whose own properties say no strike is not struck.
        Assert.Contains("| りんご | 30 | 旧: ~~30箱~~ → 35箱 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| りんご | 40 | 残す~~消す~~ |", markdown, StringComparison.Ordinal);
        Assert.Contains("| りんご | 10 | 確定 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_literal_tildes_stay_literal_next_to_struck_text()
    {
        var source = Workbook("tilde.xlsx",
            Row(1, Inline("A1", "<t>見出し</t>", 1), Inline("B1", "<t>値</t>", 1)) +
            Row(2, Inline("A2", "<t>~~not struck~~</t>"), Inline("B2", "<r><rPr><strike/></rPr><t>a~b</t></r>")) +
            Row(3, Inline("A3", "<t>x</t>"), Inline("B3", "<t>y</t>")));
        var (markdown, _) = await Export(source);
        Assert.Contains("| \\~\\~not struck\\~\\~ | ~~a\\~b~~ |", markdown, StringComparison.Ordinal);
    }

    private const string WordNamespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private string Document(string name, string body) => Write(name, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """,
        ["word/document.xml"] = $"<w:document {WordNamespaces}><w:body>{body}</w:body></w:document>",
    });

    private static string Run(string text, string properties = "") =>
        $"<w:r>{(properties.Length == 0 ? "" : "<w:rPr>" + properties + "</w:rPr>")}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";

    private static string Cell(params string[] runs) => $"<w:tc><w:p>{string.Concat(runs)}</w:p></w:tc>";

    [Fact]
    public async Task Word_single_and_double_strikethrough_read_as_struck_in_paragraphs_and_table_cells()
    {
        var source = Document("strike.docx",
            $"<w:p>{Run("普通の文。")}{Run("一重", "<w:strike/>")}{Run(" と ")}{Run("二重", "<w:dstrike/>")}{Run(" の後。")}</w:p>" +
            $"<w:p>{Run("消さない", "<w:strike w:val=\"0\"/>")}</w:p>" +
            "<w:tbl><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>" +
            $"<w:tr>{Cell(Run("項目"))}{Cell(Run("値"))}</w:tr>" +
            $"<w:tr>{Cell(Run("旧価格"))}{Cell(Run("1,000円", "<w:strike/>"), Run(" 1,200円"))}</w:tr>" +
            $"<w:tr>{Cell(Run("取消", "<w:dstrike/>"))}{Cell(Run("-"))}</w:tr></w:tbl>");
        var (markdown, _) = await Export(source);
        Assert.Contains("普通の文。~~一重~~ と ~~二重~~ の後。", markdown, StringComparison.Ordinal);
        Assert.Contains("\n消さない\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| 旧価格 | ~~1,000円~~ 1,200円 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| ~~取消~~ | - |", markdown, StringComparison.Ordinal);
    }

    private string Presentation(string name, string shapes) => Write(name, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """,
        ["ppt/presentation.xml"] = """
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>
            </p:presentation>
            """,
        ["ppt/_rels/presentation.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/>
            </Relationships>
            """,
        ["ppt/slides/slide1.xml"] = $"""
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld><p:spTree>{shapes}</p:spTree></p:cSld>
            </p:sld>
            """,
    });

    private static string SlideRun(string text, string attributes = "") =>
        $"<a:r><a:rPr lang=\"en-US\"{attributes}/><a:t>{text}</a:t></a:r>";

    [Fact]
    public async Task PowerPoint_runs_keep_their_spaces_and_struck_table_cells_read_as_struck()
    {
        var textBox = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"6000000\" cy=\"600000\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:p>" +
            SlideRun("Keep this ") + SlideRun("word", " b=\"1\"") + SlideRun(" and drop") + SlideRun(" that one ", " strike=\"sngStrike\"") +
            SlideRun("please.") + "</a:p></p:txBody></p:sp>";
        string TableCell(string runs) => $"<a:tc><a:txBody><a:bodyPr/><a:p>{runs}</a:p></a:txBody></a:tc>";
        var table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"0\" y=\"1000000\"/><a:ext cx=\"4000000\" cy=\"800000\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>" +
            "<a:tblGrid><a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/></a:tblGrid>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("項目"))}{TableCell(SlideRun("状態"))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("案A"))}{TableCell(SlideRun("採用", " strike=\"sngStrike\"") + SlideRun(" → 保留"))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("案B"))}{TableCell(SlideRun("却下", " strike=\"dblStrike\""))}</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var (markdown, _) = await Export(Presentation("strike.pptx", textBox + table));
        // A formatting change used to join the words around it ("Keep thisword").
        Assert.Contains("Keep this **word** and drop ~~that one~~ please.", markdown, StringComparison.Ordinal);
        Assert.Contains("| 案A | ~~採用~~ → 保留 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 案B | ~~却下~~ |", markdown, StringComparison.Ordinal);
    }

    // A PDF page with Helvetica text (size from Tf) and the given drawing and annotations.
    private static byte[] Pdf(string content, string annotations = "") => Encoding.Latin1.GetBytes(
        "%PDF-1.4\n1 0 obj << /Type /Page /MediaBox [0 0 595 842] /Contents 2 0 R" + (annotations.Length == 0 ? "" : " /Annots [3 0 R]") +
        " >> endobj\n2 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n" +
        (annotations.Length == 0 ? "" : "3 0 obj " + annotations + " endobj\n") + "%%EOF");

    private static string Text(double x, double y, string text, int size = 12) => $"BT /F1 {size} Tf 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    [Fact]
    public void Pdf_strikethrough_over_a_whole_show_marks_it_and_never_becomes_a_drawing()
    {
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n",
            Text(72, 760, "Price list"),
            Text(72, 720, "Old price"), Text(140, 720, "1000 yen"), "140 723.6 49.4 0.8 re f",   // Word: a filled bar
            Text(72, 690, "New price 1200 yen"),
            Text(72, 660, "Removed item"), "0.8 w 72 664 m 150 664 l S",                       // a stroked line
            Text(72, 630, "Underlined item"), "0.8 w 72 628 m 160 628 l S")));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new DocRedock.Core.Documents.TextRange(0, 12)], regions.Single(region => region.Text == "Removed item").Struck);
        // Merged into one line with the show before it, the struck show keeps its own characters.
        var line = regions.Single(region => region.Text.StartsWith("Old price", StringComparison.Ordinal));
        Assert.EndsWith("1000 yen", line.Text, StringComparison.Ordinal);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(line.Text.Length - "1000 yen".Length, line.Text.Length)], line.Struck);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Null(regions.Single(region => region.Text == "Underlined item").Struck);
        // Underlines and strikethroughs are formatting: no fallback path, no unresolved connector.
        Assert.True(result.VisualGraphs is null || !result.VisualGraphs.ContainsKey(1));
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pdf_struck_lines_are_written_struck_and_a_part_struck_show_is_reported()
    {
        var source = Path.Combine(root, "strike.pdf");
        await File.WriteAllBytesAsync(source, Pdf(string.Join("\n",
            Text(72, 720, "Old price 1000 yen"), "125.4 723.6 49.4 0.8 re f",
            Text(72, 660, "Removed item"), "0.8 w 72 664 m 150 664 l S")));
        var (markdown, diagnostics) = await Export(source);
        Assert.Contains("\n~~Removed item~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\nOld price 1000 yen\n", markdown, StringComparison.Ordinal);
        var unplaced = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "PdfStrikethroughUnplaced");
        Assert.Contains("\"Old price 1000 yen\"", unplaced.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("図の抽出結果", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" /F 2", false)]   // hidden annotation
    public void Pdf_strikeout_annotation_over_a_whole_show_marks_it(string flags, bool struck)
    {
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n", Text(72, 720, "Keep this."), Text(160, 720, "Delete this."), Text(260, 720, "Keep that.")),
            "<< /Type /Annot /Subtype /StrikeOut /Rect [158 716 240 732] /QuadPoints [158 732 240 732 158 716 240 716]" + flags + " >>"));
        var line = Assert.Single(result.Pages[0].Regions);
        var start = line.Text.IndexOf("Delete this.", StringComparison.Ordinal);
        if (struck) Assert.Equal([new DocRedock.Core.Documents.TextRange(start, start + "Delete this.".Length)], line.Struck);
        else Assert.Null(line.Struck);
    }

    [Fact]
    public void Pdf_struck_table_cells_carry_their_ranges_into_the_table()
    {
        var grid = "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S";
        var cells = string.Join("\n", Text(78, 680, "Name", 11), Text(208, 680, "Qty", 11), Text(338, 680, "Note", 11),
            Text(78, 650, "Apple", 11), Text(208, 650, "10", 11), Text(338, 650, "ok", 11),
            Text(78, 620, "Grape", 11), Text(208, 620, "30", 11), Text(338, 620, "cancelled", 11),
            "208 623.5 12.1 0.7 re f");   // "30" struck
        var result = PdfTextExtractor.Extract(Pdf(grid + "\n" + cells));
        var table = Assert.Single(result.Tables![1]);
        var struck = table.Rows.SelectMany(row => row.Cells).Single(cell => cell.Struck is not null);
        Assert.Equal(("30", 0, 2), (struck.Text, struck.Struck![0].Start, struck.Struck[0].End));
    }

    [Fact]
    public void Pdf_double_strikethroughs_and_lines_over_one_show_mark_it_once()
    {
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n",
            Text(72, 760, "Double struck line", 11), "0.4 w 72 763.3 m 159.4 763.3 l S 72 762 m 159.4 762 l S",   // LibreOffice
            Text(72, 730, "Word double strike", 11), "72 733 87.4 0.5 re f 72 731.6 87.4 0.5 re f",               // two filled bars
            Text(72, 700, "Double underlined", 11), "0.4 w 72 698.8 m 162 698.8 l S 72 697.5 m 162 697.5 l S",
            // A line over part of the show, then one over all of it, then a StrikeOut annotation on it.
            Text(72, 670, "Delete this."), "0.8 w 100 675.6 m 125 675.6 l S", "0.8 w 72 672.4 m 140 672.4 l S"),
            "<< /Type /Annot /Subtype /StrikeOut /Rect [70 666 142 682] /QuadPoints [70 682 142 682 70 666 142 666] >>"));
        var regions = result.Pages[0].Regions;
        foreach (var text in new[] { "Double struck line", "Word double strike", "Delete this." })
            Assert.Equal([new DocRedock.Core.Documents.TextRange(0, text.Length)], regions.Single(region => region.Text == text).Struck);
        Assert.Null(regions.Single(region => region.Text == "Double underlined").Struck);
        // The part-crossing line adds nothing to a show another line strikes whole: no report.
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.True(result.VisualGraphs is null || !result.VisualGraphs.ContainsKey(1));
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_double_strikethrough_over_part_of_a_show_is_reported_once()
    {
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n", Text(72, 640, "Old price 1000 yen"),
            "0.4 w 125.4 643.6 m 174.8 643.6 l S 125.4 642.3 m 174.8 642.3 l S")));
        Assert.Null(result.Pages[0].Regions.Single().Struck);
        var unplaced = Assert.Single(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Contains("\"Old price 1000 yen\"", unplaced, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_paired_lines_whose_ends_meet_other_lines_stay_drawings()
    {
        // The second line of a pair does not count as meeting the first, but the ticks at their ends do.
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n", Text(72, 720, "Bracketed pair"),
            "0.4 w 72 724 m 140 724 l S 72 722.7 m 140 722.7 l S 72 716 m 72 732 l S 140 716 m 140 732 l S")));
        Assert.Null(result.Pages[0].Regions.Single(region => region.Text == "Bracketed pair").Struck);
        Assert.True(result.VisualGraphs?.ContainsKey(1));
    }

    private static string DashedGrid(Func<double, double, double, double, string> rule, double baselineAboveRule = 10)
    {
        double[] xs = [72, 202, 332, 462], ys = [700, 670, 640, 610];
        var commands = new List<string> { "0.8 w", "72 610 390 90 re S" };
        foreach (var y in ys[1..^1]) commands.Add(rule(xs[0], y, xs[^1], y));
        foreach (var x in xs[1..^1]) commands.Add(rule(x, ys[0], x, ys[^1]));
        string[][] cells = [["Name", "Qty", "Note"], ["Apple", "10", "ok"], ["Grape", "30", "ok"]];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                commands.Add(Text(xs[column] + 6, ys[row + 1] + baselineAboveRule, cells[row][column], 11));
        return string.Join("\n", commands);
    }

    // One dash every 7 points, each 4 long, drawn as separate strokes; ends wherever the pattern stops.
    private static string Dashes(double x0, double y0, double x1, double y1) => DashesOf(4, 7)(x0, y0, x1, y1);

    private static Func<double, double, double, double, string> DashesOf(double length, double period) => (x0, y0, x1, y1) =>
    {
        var output = new StringBuilder();
        if (y0 == y1)
            for (var x = x0; x < x1; x += period) output.Append(CultureInfo.InvariantCulture, $"{x} {y0} m {Math.Min(x + length, x1)} {y0} l S\n");
        else
            for (var y = y0; y > y1; y -= period) output.Append(CultureInfo.InvariantCulture, $"{x0} {y} m {x0} {Math.Max(y - length, y1)} l S\n");
        return output.ToString().TrimEnd();
    };

    [Theory]
    [InlineData("solid")]
    [InlineData("dash pattern")]
    [InlineData("separate dashes")]
    public void Pdf_tables_with_dashed_inner_rules_are_tables(string style)
    {
        var content = DashedGrid(style switch
        {
            "solid" => (x0, y0, x1, y1) => $"{x0} {y0} m {x1} {y1} l S",
            "dash pattern" => (x0, y0, x1, y1) => $"[4 3] 0 d {x0} {y0} m {x1} {y1} l S [] 0 d",
            _ => Dashes,
        });
        var result = PdfTextExtractor.Extract(Pdf(content));
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(["Name", "Qty", "Note", "Apple", "10", "ok", "Grape", "30", "ok"], table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
        // Every dash is part of the table, none is left as a fallback path or an unresolved connector.
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_short_strokes_that_are_not_an_even_dash_run_stay_apart()
    {
        // Two strokes, or strokes with a gap far wider than the others, are not a dashed rule.
        var content = DashedGrid((x0, y0, x1, y1) => y0 == y1
            ? $"{x0} {y0} m {x0 + 10} {y0} l S\n{x1 - 10} {y0} m {x1} {y0} l S"
            : $"{x0} {y0} m {x0} {y1} l S");
        var result = PdfTextExtractor.Extract(Pdf(content));
        Assert.True(result.Tables is null || !result.Tables.ContainsKey(1) || result.Tables[1].Count == 0);
    }

    [Theory]
    [InlineData(4, 7)]
    [InlineData(6, 9)]
    public void Pdf_dashed_rules_right_under_text_stay_rules_not_underlines(double length, double period)
    {
        // Rows set tight: each row's baseline 3 points above the dashed rule under it, where an
        // underline would sit; the dashes are judged as one rule, not as underlines one by one.
        var result = PdfTextExtractor.Extract(Pdf(DashedGrid(DashesOf(length, period), baselineAboveRule: 3)));
        var table = Assert.Single(result.Tables![1]);
        Assert.Equal(["Name", "Qty", "Note", "Apple", "10", "ok", "Grape", "30", "ok"], table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
        Assert.All(table.Rows.SelectMany(row => row.Cells), cell => Assert.Null(cell.Struck));
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("Visual", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_small_boxes_side_by_side_are_not_a_dashed_grid()
    {
        // Ten-point boxes five points apart: their edges are short, evenly spaced and collinear, but
        // the edge of a box is never a dash.
        var commands = new List<string> { "0.8 w" };
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                double x = 72 + column * 15, y = 700 - row * 15;
                commands.Add(string.Create(CultureInfo.InvariantCulture, $"{x} {y} 10 10 re S"));
                commands.Add(Text(x + 3, y + 2, (row * 3 + column + 1).ToString(CultureInfo.InvariantCulture), 8));
            }
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n", commands)));
        Assert.True(result.Tables is null || !result.Tables.ContainsKey(1) || result.Tables[1].Count == 0);
    }

    [Fact]
    public void Pdf_lines_over_the_letters_or_between_texts_are_not_decorations()
    {
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n",
            Text(72, 720, "reset"), "0.6 w 72 726.6 m 100 726.6 l S",       // an overline at 0.55 em
            Text(72, 690, "enable"), "0.6 w 72 697.8 m 105 697.8 l S",      // and at 0.65 em
            Text(200, 724, "1"), "0.5 w 198 721 m 208 721 l S", Text(200, 711.4, "2"),   // a fraction bar
            Text(72, 660, "struck"), "0.6 w 72 663.6 m 105 663.6 l S")));    // a strikethrough at 0.3 em
        var regions = result.Pages[0].Regions;
        foreach (var text in new[] { "reset", "enable", "1", "2" })
            Assert.Null(regions.Single(region => region.Text == text).Struck);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(0, 6)], regions.Single(region => region.Text == "struck").Struck);
        // The overlines and the bar stay drawings for the reader to compare; only the strikethrough leaves.
        var paths = result.VisualGraphs![1].Paths ?? [];
        Assert.Equal(3, paths.Count);
        Assert.DoesNotContain(paths, path => path.Points is { } points && points.All(point => Math.Abs(point.Y - 663.6) < 0.01));
    }

    [Fact]
    public async Task Pdf_a_strikethrough_over_part_of_a_show_asks_for_source_comparison()
    {
        var source = Path.Combine(root, "partial.pdf");
        await File.WriteAllBytesAsync(source, Pdf(string.Join("\n", Text(72, 720, "Old price 1000 yen"), "125.4 723.6 49.4 0.8 re f")));
        var output = Path.Combine(root, "partial.md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        var markdown = await File.ReadAllTextAsync(output);
        Assert.Contains("a strikethrough crosses part of 1 text fragment(s) above and which characters it strikes could not be determined - compare with the source page",
            markdown, StringComparison.Ordinal);
        Assert.Equal(1, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
        var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
        Assert.Equal(ReviewElementKind.StrikethroughUnplaced, Assert.Single(page.Elements).Kind);
        Assert.Equal("1ページ目：文字1件は、一部に取り消し線がありますが、どの文字が取り消されているかを確定できませんでした（取り消し線の印を付けずにMarkdownに含めています）。" +
            "照合画像を作成できなかったため、原本PDFの1ページ目で内容を確認してください。", ExportReviewText.DescribeJapanese(page));
        Assert.Equal("page 1: 1 text item(s) with a strikethrough over part of them whose struck characters could not be determined; kept in the Markdown without the mark",
            ExportReviewText.DescribeEnglish(page));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PdfReviewImageUnavailable" &&
            diagnostic.Message.StartsWith("PDF page 1: text with a strikethrough that could not be placed needs source comparison", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_text_drawn_upright_through_two_flips_is_checked_for_strikethroughs()
    {
        // As Chrome writes pages: the CTM turns the page upside down and the text matrix turns the text back.
        var result = PdfTextExtractor.Extract(Pdf(string.Join("\n", "1 0 0 -1 0 842 cm",
            "BT /F1 12 Tf 1 0 0 -1 72 122 Tm (Removed item) Tj ET", "0.8 w 72 118 m 150 118 l S")));
        var region = Assert.Single(result.Pages[0].Regions);
        Assert.Equal(12, region.FontSize);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(0, 12)], region.Struck);
    }

    // A show in a font that gives its glyph widths (Helvetica's), and the page x where a part of it starts and ends.
    private static string MeasuredText(double x, double y, string text, double size = 12) =>
        $"BT /F1 {MeasuredPdf.N(size)} Tf 1 0 0 1 {MeasuredPdf.N(x)} {MeasuredPdf.N(y)} Tm ({text}) Tj ET";

    private static (string Left, string Right) Span(double x, string text, string part, double size = 12)
    {
        var start = x + MeasuredPdf.Width(text[..text.IndexOf(part, StringComparison.Ordinal)], size);
        return (MeasuredPdf.N(start), MeasuredPdf.N(start + MeasuredPdf.Width(part, size)));
    }

    [Fact]
    public void Pdf_a_strikethrough_over_part_of_a_measured_show_strikes_the_characters_under_it()
    {
        var (barLeft, barRight) = Span(72, "Old price 1000 yen", "1000 yen");
        var (lineLeft, lineRight) = Span(72, "Keep the word price here", "price");
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(string.Join("\n",
            MeasuredText(72, 720, "Old price 1000 yen"),
            $"{barLeft} 723.6 {MeasuredPdf.N(MeasuredPdf.Width("1000 yen", 12))} 0.8 re f",   // Word: a filled bar
            MeasuredText(72, 690, "Keep the word price here"), $"0.6 w {lineLeft} 693.6 m {lineRight} 693.6 l S")));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new DocRedock.Core.Documents.TextRange(10, 18)], regions.Single(region => region.Text == "Old price 1000 yen").Struck);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(14, 19)], regions.Single(region => region.Text == "Keep the word price here").Struck);
        // Placed, so nothing asks for source comparison.
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
        Assert.Null(result.Pages[0].UncertainText);
        Assert.True(result.VisualGraphs is null || !result.VisualGraphs.ContainsKey(1));
    }

    [Fact]
    public async Task Pdf_part_struck_measured_text_is_written_struck_and_needs_no_review()
    {
        var source = Path.Combine(root, "measured.pdf");
        var (left, _) = Span(72, "Old price 1000 yen", "1000 yen");
        await File.WriteAllBytesAsync(source, MeasuredPdf.Page(string.Join("\n", MeasuredText(72, 720, "Old price 1000 yen"),
            $"{left} 723.6 {MeasuredPdf.N(MeasuredPdf.Width("1000 yen", 12))} 0.8 re f")));
        var output = Path.Combine(root, "measured.md");
        var result = await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        Assert.Contains("\nOld price ~~1000 yen~~\n", (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PdfStrikethroughUnplaced");
        Assert.Equal(0, ExportSummaryBuilder.Build(result.Graph, result.Diagnostics).ReviewPages);
    }

    [Fact]
    public void Pdf_double_strikethroughs_and_strikeout_annotations_over_part_of_a_measured_show_mark_its_characters()
    {
        var (doubleLeft, doubleRight) = Span(72, "Old price 1000 yen", "1000 yen");
        var (noteLeft, noteRight) = Span(72, "Keep this. Delete this. Keep that.", "Delete this.");
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(string.Join("\n",
                MeasuredText(72, 640, "Old price 1000 yen"),
                $"0.4 w {doubleLeft} 643.6 m {doubleRight} 643.6 l S {doubleLeft} 642.3 m {doubleRight} 642.3 l S",
                MeasuredText(72, 720, "Keep this. Delete this. Keep that.")),
            annotation: $"<< /Type /Annot /Subtype /StrikeOut /Rect [{noteLeft} 716 {noteRight} 732] " +
                $"/QuadPoints [{noteLeft} 732 {noteRight} 732 {noteLeft} 716 {noteRight} 716] >>"));
        var regions = result.Pages[0].Regions;
        Assert.Equal([new DocRedock.Core.Documents.TextRange(10, 18)], regions.Single(region => region.Text == "Old price 1000 yen").Struck);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(11, 23)], regions.Single(region => region.Text.StartsWith("Keep this.", StringComparison.Ordinal)).Struck);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_part_struck_measured_table_cells_carry_their_ranges_into_the_table()
    {
        var grid = "0.8 w 72 610 390 90 re S\n72 670 m 462 670 l S\n72 640 m 462 640 l S\n202 610 m 202 700 l S\n332 610 m 332 700 l S";
        var (left, _) = Span(208, "1,000 1,200", "1,000", 11);
        var cells = string.Join("\n", MeasuredText(78, 680, "Name", 11), MeasuredText(208, 680, "Price", 11), MeasuredText(338, 680, "Note", 11),
            MeasuredText(78, 650, "Apple", 11), MeasuredText(208, 650, "120", 11), MeasuredText(338, 650, "ok", 11),
            MeasuredText(78, 620, "Grape", 11), MeasuredText(208, 620, "1,000 1,200", 11), MeasuredText(338, 620, "raised", 11),
            $"{left} 623.5 {MeasuredPdf.N(MeasuredPdf.Width("1,000", 11))} 0.7 re f");   // "1,000" struck
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(grid + "\n" + cells));
        var table = Assert.Single(result.Tables![1]);
        var struck = table.Rows.SelectMany(row => row.Cells).Single(cell => cell.Struck is not null);
        Assert.Equal(("1,000 1,200", 0, 5), (struck.Text, struck.Struck![0].Start, struck.Struck[0].End));
    }

    [Fact]
    public void Pdf_the_characters_one_glyph_draws_are_struck_together()
    {
        // Code 1 draws the "ffi" ligature and code 2 a supplementary character (two UTF-16 units). A
        // strikethrough ending part way over either glyph takes every character it draws, never half
        // of them (a mark between the two halves of a surrogate pair would break the text).
        var widths = Enumerable.Range(1, 126).Select(code => code switch { 1 => 800, 2 => 1000, < 32 => 0, _ => MeasuredPdf.Advance((char)code) });
        var objects = new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /TrueType /BaseFont /Ligatures /FirstChar 1 /Widths [" + string.Join(" ", widths) + "] /ToUnicode 5 0 R >>",
            [5] = MeasuredPdf.Stream("1 begincodespacerange <00> <FF> endcodespacerange\n2 beginbfchar <01> <006600660069> <02> <D840DC0B> endbfchar"),
        };
        // At 10 points "o" is 5.56 wide, the ligature 8, "ce " 13.34 and the supplementary character 10.
        var ligature = 72 + 5.56;
        var supplementary = ligature + 8 + 13.34;
        string Quad(double left, double right) => $"{MeasuredPdf.N(left)} 708 {MeasuredPdf.N(right)} 708 {MeasuredPdf.N(left)} 698 {MeasuredPdf.N(right)} 698";
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page("BT /F1 10 Tf 1 0 0 1 72 700 Tm (o\\001ce \\002 end) Tj ET", objects: objects,
            annotation: $"<< /Type /Annot /Subtype /StrikeOut /Rect [72 698 140 708] /QuadPoints [{Quad(72, ligature + 4.8)} {Quad(supplementary, supplementary + 6)}] >>"));
        var region = Assert.Single(result.Pages[0].Regions);
        Assert.Equal("office \U0002000B end", region.Text);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(0, 4), new DocRedock.Core.Documents.TextRange(7, 9)], region.Struck);
    }

    [Fact]
    public void Pdf_a_strikethrough_over_nothing_but_a_space_strikes_nothing()
    {
        // Word spacing widens the space after "Keep" to 22.78 points; the StrikeOut covers its middle only.
        var space = 72 + MeasuredPdf.Width("Keep", 10);
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page("BT /F1 10 Tf 20 Tw 1 0 0 1 72 700 Tm (Keep both) Tj ET",
            annotation: $"<< /Type /Annot /Subtype /StrikeOut /QuadPoints [{MeasuredPdf.N(space + 5)} 708 {MeasuredPdf.N(space + 17)} 708 " +
                $"{MeasuredPdf.N(space + 5)} 698 {MeasuredPdf.N(space + 17)} 698] >>"));
        var region = Assert.Single(result.Pages[0].Regions);
        Assert.Equal("Keep both", region.Text);
        Assert.Null(region.Struck);
        Assert.DoesNotContain(result.Diagnostics ?? [], diagnostic => diagnostic.StartsWith("PdfStrikethroughUnplaced", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_measured_text_drawn_upright_through_two_flips_is_struck_where_the_line_crosses_it()
    {
        // As Chrome writes pages: the CTM turns the page upside down and the text matrix turns the text back.
        var (left, right) = Span(72, "Old price 1000 yen", "1000 yen");
        var result = PdfTextExtractor.Extract(MeasuredPdf.Page(string.Join("\n", "1 0 0 -1 0 842 cm",
            "BT /F1 12 Tf 1 0 0 -1 72 122 Tm (Old price 1000 yen) Tj ET", $"0.8 w {left} 118.4 m {right} 118.4 l S")));
        var region = Assert.Single(result.Pages[0].Regions);
        Assert.Equal([new DocRedock.Core.Documents.TextRange(10, 18)], region.Struck);
    }

    [Fact]
    public void Pdf_text_positions_and_strikethroughs_do_not_depend_on_the_culture()
    {
        var content = string.Join("\n", "BT /F1 12 Tf 1 0 0 1 72.5 720.25 Tm (Removed item) Tj ET", "0.8 w 72.5 724.25 m 150.5 724.25 l S",
            "BT /F1 12 Tf 72.5 690.75 Td (Kept item) Tj ET");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var regions = PdfTextExtractor.Extract(Pdf(content)).Pages[0].Regions;
            var removed = regions.Single(region => region.Text == "Removed item");
            Assert.Equal((72.5, 720.25), (removed.BoundingBox.X, removed.BoundingBox.Y));
            Assert.Equal([new DocRedock.Core.Documents.TextRange(0, 12)], removed.Struck);
            var kept = regions.Single(region => region.Text == "Kept item");
            Assert.Equal((72.5, 690.75), (kept.BoundingBox.X, kept.BoundingBox.Y));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Excel_struck_titles_headings_and_notes_outside_tables_read_as_struck()
    {
        var source = Workbook("struck-outside.xlsx", string.Concat(
                Row(1, Inline("A1", "<t>取消済みの見積書</t>", 2)),
                Row(3, Inline("A3", "<t>1. 概要</t>", 2)),
                Row(4, Inline("A4", "<t>品名</t>"), Inline("B4", "<t>単価</t>")),
                Row(5, Inline("A5", "<t>りんご</t>"), "<c r=\"B5\"><v>100</v></c>"),
                Row(6, Inline("A6", "<t>みかん</t>"), "<c r=\"B6\"><v>80</v></c>"),
                Row(8, Inline("A8", "<t>上記の単価は2026年10月から適用します。</t>", 2))),
            fonts: "<font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font><font><strike/><sz val=\"11\"/></font>",
            cellXfs: "<xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"2\"/>");
        var (markdown, _) = await Export(source);
        Assert.StartsWith("# struck-outside\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### ~~取消済みの見積書~~\n", markdown, StringComparison.Ordinal);
        // Written once, as the title; the heading keeps its place above the rows it heads.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, "取消済みの見積書"));
        Assert.Contains("\n### ~~1. 概要~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n~~上記の単価は2026年10月から適用します。~~\n", markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf("1. 概要", StringComparison.Ordinal) < markdown.IndexOf("りんご", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Noncharacters_in_the_document_never_turn_into_strikethroughs()
    {
        // U+FDD0 and U+FDD1 mark struck text while it is written; in the document they are kept out.
        var source = Workbook("nonchar.xlsx", string.Concat(
                Row(1, Inline("A1", "<t>品名</t>"), Inline("B1", "<t>備考</t>")),
                Row(2, Inline("A2", "<t>りんご</t>"), Inline("B2", "<t>a\uFDD0b\uFDD1c</t>")),
                Row(3, Inline("A3", "<t>みかん</t>"), Inline("B3", "<t>取消</t>", 2))),
            fonts: "<font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font><font><strike/><sz val=\"11\"/></font>",
            cellXfs: "<xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"2\"/>");
        var (markdown, _) = await Export(source);
        Assert.Contains("a\uFFFDb\uFFFDc", markdown, StringComparison.Ordinal);
        Assert.Contains("~~取消~~", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFDD0', markdown);
        Assert.DoesNotContain('\uFDD1', markdown);

        // Text that reaches the Markdown as node text (a Word paragraph), not as a cell value.
        var (word, _) = await Export(Document("nonchar-word.docx",
            $"<w:p>{Run("x\uFDD0y\uFDD1z ")}{Run("消す", "<w:strike/>")}</w:p>"));
        Assert.Contains("x\uFFFDy\uFFFDz ~~消す~~", word, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFDD0', word);
    }
}

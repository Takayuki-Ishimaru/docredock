using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Formats.OpenXml.Pptx;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;
using DocRedock.Render;
using Markdig;

namespace DocRedock.Tests.Api;

/// <summary>
/// Underlined text reads as underlined (&lt;u&gt;) in every format, tables included: Excel cell fonts and
/// rich-text runs, Word runs and table cells, PowerPoint runs and table cells. An underline that only
/// styles a hyperlink is not emphasis, and headings, titles and the names of columns and labels are not
/// underlined at all. The serializer reads "underline", "underline_ranges" and "underline_cells" from any
/// adapter, the way it reads the strikethrough ones; where both cover the same text, underline goes
/// outside (&lt;u&gt;~~text~~&lt;/u&gt;).
/// </summary>
public sealed class UnderlineRecognitionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-underline-").FullName;
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

    private async Task<string> Export(string source)
    {
        var output = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + ".md");
        await service.ExportReadableAsync(new ReadableDocumentExportOptions(source, output, EnableOcr: false));
        return (await File.ReadAllTextAsync(output)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // Fonts of the workbooks below: 0 plain, 1 bold, 2 single underline (<u/> is single), 3 double,
    // 4 single accounting, 5 double accounting, 6 none, 7 bold and underlined, 8 underlined and struck.
    private const string Fonts =
        "<font><sz val=\"11\"/></font><font><b/><sz val=\"11\"/></font><font><u/><sz val=\"11\"/></font>" +
        "<font><u val=\"double\"/><sz val=\"11\"/></font><font><u val=\"singleAccounting\"/><sz val=\"11\"/></font>" +
        "<font><u val=\"doubleAccounting\"/><sz val=\"11\"/></font><font><u val=\"none\"/><sz val=\"11\"/></font>" +
        "<font><b/><u/><sz val=\"11\"/></font><font><u/><strike/><sz val=\"11\"/></font>";

    // One xf per font, then 9: single underline with a number format.
    private const string CellXfs =
        "<xf fontId=\"0\"/><xf fontId=\"1\"/><xf fontId=\"2\"/><xf fontId=\"3\"/><xf fontId=\"4\"/><xf fontId=\"5\"/><xf fontId=\"6\"/>" +
        "<xf fontId=\"7\"/><xf fontId=\"8\"/><xf fontId=\"2\" numFmtId=\"3\"/>";

    private string Workbook(string name, string sheetData, string sharedStrings = "", string afterSheetData = "") => Write(name, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
        ["xl/workbook.xml"] = $"<workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Items\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
        ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{SpreadsheetNs}\">{sharedStrings}</sst>",
        ["xl/styles.xml"] = $"<styleSheet xmlns=\"{SpreadsheetNs}\"><fonts>{Fonts}</fonts><cellXfs>{CellXfs}</cellXfs></styleSheet>",
        ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{SpreadsheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheetData>{sheetData}</sheetData>{afterSheetData}</worksheet>",
    });

    private static string Inline(string reference, string runs, int style = 0) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>{runs}</is></c>";

    private static string Shared(string reference, int index, int style = 0) => $"<c r=\"{reference}\" s=\"{style}\" t=\"s\"><v>{index}</v></c>";

    private static string Number(string reference, int value, int style = 0) => $"<c r=\"{reference}\" s=\"{style}\"><v>{value}</v></c>";

    private static string Row(int number, params string[] cells) => $"<row r=\"{number}\">{string.Concat(cells)}</row>";

    // What a Markdown renderer makes of the Markdown: the markup must pair up and nest as written.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).Build();

    private static string Html(string markdown) => Markdig.Markdown.ToHtml(markdown, Pipeline).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static void AssertNoMarkers(string markdown)
    {
        foreach (var marker in new[] { '\uFDD0', '\uFDD1', '\uFDD2', '\uFDD3' })
            Assert.DoesNotContain(marker, markdown);
    }

    [Fact]
    public async Task Excel_underlined_cell_fonts_and_rich_text_runs_read_as_underlined()
    {
        var shared =
            "<si><t>品目</t></si><si><t>数量</t></si><si><t>備考</t></si>" +   // 0, 1, 2
            "<si><r><t>これは</t></r><r><rPr><u/></rPr><t>重要</t></r><r><t>な点</t></r></si>" +   // 3
            "<si><r><rPr><u val=\"double\"/></rPr><t>二重</t></r><r><t xml:space=\"preserve\"> と </t></r><r><rPr><u val=\"none\"/></rPr><t>なし</t></r></si>" +   // 4
            "<si><r><rPr><b/></rPr><t>太字</t></r><r><t>下線のまま</t></r></si>";   // 5
        var source = Workbook("fonts.xlsx",
            Row(1, Shared("A1", 0, 1), Shared("B1", 1, 1), Shared("C1", 2, 1)) +
            Row(2, Inline("A2", "<t>みかん</t>", 2), Number("B2", 12, 3), Shared("C2", 3)) +
            Row(3, Inline("A3", "<t>ぶどう</t>", 4), Number("B3", 20, 5), Shared("C3", 4)) +
            // The run with properties of its own replaces the cell's font; the run without takes it.
            Row(4, Inline("A4", "<t>なし</t>", 6), Number("B4", 1200, 9), Shared("C4", 5, 2)),
            shared);
        var markdown = await Export(source);
        Assert.Contains("| 品目 | 数量 | 備考 |", markdown, StringComparison.Ordinal);
        // <u/> is single, and double, singleAccounting and doubleAccounting are underlines too.
        Assert.Contains("| <u>みかん</u> | <u>12</u> | これは<u>重要</u>な点 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| <u>ぶどう</u> | <u>20</u> | <u>二重</u> と なし |", markdown, StringComparison.Ordinal);
        // val="none" is none; a number is underlined as it is shown.
        Assert.Contains("| なし | <u>1,200</u> | 太字<u>下線のまま</u> |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Excel_underline_and_strikethrough_on_the_same_text_nest_underline_outside()
    {
        var shared =
            "<si><t>品目</t></si><si><t>備考</t></si>" +
            // 甲 underlined, 乙 both, 丙 struck, 丁 both again: the markup must still pair up and nest.
            "<si><r><rPr><u/></rPr><t>甲</t></r><r><rPr><u/><strike/></rPr><t>乙</t></r><r><rPr><strike/></rPr><t>丙</t></r><r><rPr><u/><strike/></rPr><t>丁</t></r></si>";
        var source = Workbook("both.xlsx",
            Row(1, Shared("A1", 0, 1), Shared("B1", 1, 1)) +
            Row(2, Inline("A2", "<t>取消</t>", 8), Shared("B2", 2)) +
            Row(3, Inline("A3", "<t>通常</t>"), Inline("B3", "<t>x</t>")),
            shared);
        var markdown = await Export(source);
        Assert.Contains("| <u>~~取消~~</u> | <u>甲~~乙~~</u>~~丙~~<u>~~丁~~</u> |", markdown, StringComparison.Ordinal);
        Assert.Contains("<td><u><del>取消</del></u></td>", Html(markdown), StringComparison.Ordinal);
        Assert.Contains("<td><u>甲<del>乙</del></u><del>丙</del><u><del>丁</del></u></td>", Html(markdown), StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Excel_hyperlink_cells_are_not_underlined_but_other_underlined_cells_are()
    {
        // The font of a link cell underlines it: that is how a link looks. Cells named by a <hyperlink>
        // (one cell or a range), a HYPERLINK formula, and the cells filled from one are links.
        var source = Workbook("links.xlsx",
            Row(1, Inline("A1", "<t>名称</t>", 1), Inline("B1", "<t>備考</t>", 1)) +
            Row(2, Inline("A2", "<t>公式</t>", 2), Inline("B2", "<t>強調</t>", 2)) +
            Row(3, Inline("A3", "<t>範囲1</t>", 2), Inline("B3", "<t>範囲2</t>", 2)) +
            Row(4, "<c r=\"A4\" s=\"2\" t=\"str\"><f>HYPERLINK(\"https://example.test/a\",\"開く\")</f><v>開く</v></c>", Inline("B4", "<t>普通</t>")) +
            Row(5, "<c r=\"A5\" s=\"2\" t=\"str\"><f t=\"shared\" ref=\"A5:A6\" si=\"0\">hyperlink(\"https://example.test/b\",\"共有1\")</f><v>共有1</v></c>", Inline("B5", "<t>普通</t>")) +
            Row(6, "<c r=\"A6\" s=\"2\" t=\"str\"><f t=\"shared\" si=\"0\"/><v>共有2</v></c>", Inline("B6", "<t>普通</t>")) +
            Row(7, "<c r=\"A7\" s=\"2\" t=\"str\"><f>CONCATENATE(\"計\",\"算\")</f><v>計算</v></c>", Inline("B7", "<t>普通</t>")),
            afterSheetData: "<hyperlinks><hyperlink ref=\"A2\" r:id=\"rId1\"/><hyperlink ref=\"A3:B3\" location=\"Items!A1\"/></hyperlinks>");
        var markdown = await Export(source);
        Assert.Contains("| 公式 | <u>強調</u> |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 範囲1 | 範囲2 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 開く | 普通 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 共有1 | 普通 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 共有2 | 普通 |", markdown, StringComparison.Ordinal);
        // A formula that is not HYPERLINK shows its underline.
        Assert.Contains("| <u>計算</u> | 普通 |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Excel_phonetic_guides_do_not_move_or_add_underlined_runs()
    {
        // The guide (rPh) is not part of the text: the underlined run is placed in the text without it.
        var shared = "<si><r><t>A</t></r><rPh sb=\"0\" eb=\"1\"><t>XXXX</t></rPh><r><rPr><u/></rPr><t>B</t></r></si>";
        var source = Workbook("phonetic.xlsx",
            Row(1, Inline("A1", "<t>名称</t>", 1), Inline("B1", "<t>読み</t>", 1)) +
            Row(2, Shared("A2", 0), Inline("B2", "<r><t>りんご</t></r><rPh sb=\"0\" eb=\"3\"><t>リンゴ</t></rPh><r><rPr><u/></rPr><t>下線</t></r>")),
            shared);
        var markdown = await Export(source);
        Assert.Contains("| A<u>B</u> | りんご<u>下線</u> |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("XXXX", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("リンゴ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_underlined_titles_headings_and_column_names_are_plain_but_their_values_and_notes_are_underlined()
    {
        // Font 2 underlines; font 7 is the bold, underlined look of a header.
        var source = Workbook("names.xlsx", string.Concat(
            Row(1, Inline("A1", "<t>取消済みの見積書</t>", 2)),
            Row(3, Inline("A3", "<t>1. 概要</t>", 2)),
            Row(4, Inline("A4", "<t>品名</t>", 7), Inline("B4", "<t>単価</t>", 7)),
            Row(5, Inline("A5", "<t>りんご</t>", 2), Number("B5", 100, 2)),
            Row(6, Inline("A6", "<t>みかん</t>"), Number("B6", 80)),
            Row(8, Inline("A8", "<t>上記の単価は2026年10月から適用します。</t>", 2))));
        var markdown = await Export(source);
        Assert.StartsWith("# 取消済みの見積書\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n### 1. 概要\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| 品名 | 単価 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| <u>りんご</u> | <u>100</u> |", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>上記の単価は2026年10月から適用します。</u>\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Excel_underlined_label_value_pairs_keep_the_value_and_not_the_label()
    {
        var source = Workbook("pairs.xlsx", string.Concat(
            Row(1, Inline("A1", "<t>文書番号</t>", 2), Inline("B1", "<t>EXPS-001</t>", 2)),
            Row(2, Inline("A2", "<t>版</t>", 2), Inline("B2", "<t>1.2</t>", 2)),
            Row(4, Inline("A4", "<t>品名</t>", 1), Inline("B4", "<t>単価</t>", 1), Inline("C4", "<t>備考</t>", 1)),
            Row(5, Inline("A5", "<t>りんご</t>"), Number("B5", 100), Inline("C5", "<t>x</t>"))));
        var markdown = await Export(source);
        Assert.Contains("- **文書番号**: <u>EXPS-001</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("- **版**: <u>1.2</u>", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Excel_underlined_formula_results_are_underlined_as_shown()
    {
        var source = Workbook("formula.xlsx", string.Concat(
            Row(1, Inline("A1", "<t>項目</t>", 1), Inline("B1", "<t>合計</t>", 1)),
            Row(2, Inline("A2", "<t>売上</t>"), "<c r=\"B2\" s=\"9\"><f>1000+234</f><v>1234</v></c>")));
        var markdown = await Export(source);
        Assert.Contains("| 売上 | <u>1,234</u> |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_typed_markup_stays_text_and_noncharacters_in_the_document_never_become_underlines()
    {
        // U+FDD2 and U+FDD3 mark underlined text while it is written; in the document they are kept out.
        // A literal "<u>" typed in a cell is text, not an underline.
        var source = Workbook("typed.xlsx", string.Concat(
            Row(1, Inline("A1", "<t>品名</t>", 1), Inline("B1", "<t>備考</t>", 1)),
            Row(2, Inline("A2", "<t>りんご</t>"), Inline("B2", "<t>a\uFDD2b\uFDD3c</t>")),
            Row(3, Inline("A3", "<t>みかん</t>"), Inline("B3", "<t>&lt;u&gt;typed&lt;/u&gt;</t>")),
            Row(4, Inline("A4", "<t>ぶどう</t>"), Inline("B4", "<t>取消</t>", 8))));
        var markdown = await Export(source);
        Assert.Contains("a\uFFFDb\uFFFDc", markdown, StringComparison.Ordinal);
        Assert.Contains("| みかん | &lt;u&gt;typed&lt;/u&gt; |", markdown, StringComparison.Ordinal);
        Assert.Contains("| ぶどう | <u>~~取消~~</u> |", markdown, StringComparison.Ordinal);
        Assert.Equal(1, markdown.Split("<u>", StringSplitOptions.None).Length - 1);
        AssertNoMarkers(markdown);
    }

    private static readonly string WordNamespaces =
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
        ["word/_rels/document.xml.rels"] = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.test/docs" TargetMode="External"/>
            </Relationships>
            """,
        ["word/document.xml"] = $"<w:document {WordNamespaces}><w:body>{body}</w:body></w:document>",
    });

    private static string Run(string text, string properties = "") =>
        $"<w:r>{(properties.Length == 0 ? "" : "<w:rPr>" + properties + "</w:rPr>")}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";

    private static string Cell(params string[] runs) => $"<w:tc><w:p>{string.Concat(runs)}</w:p></w:tc>";

    private const string Underline = "<w:u w:val=\"single\"/>";

    [Fact]
    public async Task Word_underlined_runs_read_as_underlined_in_paragraphs_list_items_and_table_cells_but_not_in_headings_or_links()
    {
        var source = Document("underline.docx",
            $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>{Run("見出し")}{Run("下線付き", Underline)}</w:p>" +
            $"<w:p>{Run("これは")}{Run("重要", Underline)}{Run("な点。")}{Run("二重", "<w:u w:val=\"double\"/><w:strike/>")}{Run("、")}{Run("なし", "<w:u w:val=\"none\"/>")}</w:p>" +
            $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>{Run("箇条書き")}{Run("下線", "<w:u/>")}</w:p>" +
            // Direct underline and colour are how a hyperlink was made to look like one.
            $"<w:p>{Run("参照: ")}<w:hyperlink r:id=\"rId9\">{Run("公式サイト", Underline + "<w:color w:val=\"0000FF\"/>")}</w:hyperlink>{Run(" と ")}{Run("強調", Underline)}</w:p>" +
            "<w:tbl><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>" +
            $"<w:tr>{Cell(Run("項目"))}{Cell(Run("値"))}</w:tr>" +
            $"<w:tr>{Cell(Run("標準"))}{Cell(Run("下線", "<w:u/>"), Run(" 通常"))}</w:tr>" +
            $"<w:tr>{Cell(Run("リンク"))}{Cell($"<w:hyperlink r:id=\"rId9\">{Run("サイト", "<w:u/>")}</w:hyperlink>", Run(" 後ろ", "<w:u/>"))}</w:tr>" +
            $"<w:tr>{Cell(Run("一部"), Run("二重", "<w:u w:val=\"double\"/><w:strike/>"))}{Cell(Run("なし", "<w:u w:val=\"none\"/>"))}</w:tr></w:tbl>");
        var markdown = await Export(source);
        Assert.Contains("\n## 見出し下線付き\n", markdown, StringComparison.Ordinal);
        Assert.Contains("これは<u>重要</u>な点。<u>~~二重~~</u>、なし", markdown, StringComparison.Ordinal);
        Assert.Contains("\n- 箇条書き<u>下線</u>\n", markdown, StringComparison.Ordinal);
        Assert.Contains("参照: [<span style=\"color:#0000FF\">公式サイト</span>](https://example.test/docs) と <u>強調</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("| 標準 | <u>下線</u> 通常 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| リンク | サイト <u>後ろ</u> |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 一部<u>~~二重~~</u> | なし |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Word_typed_markup_stays_text_and_noncharacters_never_become_underlines()
    {
        var source = Document("typed.docx",
            $"<w:p>{Run("x\uFDD2y\uFDD3z &lt;u&gt;typed&lt;/u&gt; ")}{Run("下線", Underline)}</w:p>" +
            "<w:tbl><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>" +
            $"<w:tr>{Cell(Run("項目"))}{Cell(Run("値"))}</w:tr>" +
            $"<w:tr>{Cell(Run("a\uFDD2b\uFDD3c &lt;u&gt;x&lt;/u&gt; "), Run("下線", Underline))}{Cell(Run("-"))}</w:tr></w:tbl>");
        var markdown = await Export(source);
        Assert.Contains("x\uFFFDy\uFFFDz &lt;u&gt;typed&lt;/u&gt; <u>下線</u>", markdown, StringComparison.Ordinal);
        Assert.Contains("| a\uFFFDb\uFFFDc &lt;u&gt;x&lt;/u&gt; <u>下線</u> | - |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
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
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                   xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld><p:spTree>{shapes}</p:spTree></p:cSld>
            </p:sld>
            """,
    });

    private static string SlideRun(string text, string attributes = "", string properties = "") =>
        $"<a:r><a:rPr lang=\"en-US\"{attributes}>{properties}</a:rPr><a:t>{text}</a:t></a:r>";

    [Fact]
    public async Task PowerPoint_underlined_runs_and_table_cells_read_as_underlined_but_not_titles_or_links()
    {
        var title = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/>" +
            $"<a:p>{SlideRun("タイトル", " u=\"sng\"")}</a:p></p:txBody></p:sp>";
        var textBox = "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Text\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"0\" y=\"1000000\"/><a:ext cx=\"6000000\" cy=\"600000\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:p>" +
            SlideRun("これは") + SlideRun("重要", " u=\"sng\"") + SlideRun("な点。") + "</a:p></p:txBody></p:sp>";
        string TableCell(params string[] runs) => $"<a:tc><a:txBody><a:bodyPr/><a:p>{string.Concat(runs)}</a:p></a:txBody></a:tc>";
        var table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"0\" y=\"2000000\"/><a:ext cx=\"4000000\" cy=\"800000\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>" +
            "<a:tblGrid><a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/></a:tblGrid>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("項目"))}{TableCell(SlideRun("状態"))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("案A"))}{TableCell(SlideRun("採用", " u=\"sng\""), SlideRun(" → 保留"))}</a:tr>" +
            // Wavy, double and dashed underlines are underlines; u="none" is none; a run with a:hlinkClick is a link.
            $"<a:tr h=\"400000\">{TableCell(SlideRun("案B"))}{TableCell(SlideRun("リンク", " u=\"sng\"", "<a:hlinkClick r:id=\"rId2\"/>"), SlideRun(" と "), SlideRun("二重", " u=\"dbl\" strike=\"sngStrike\""), SlideRun("なし", " u=\"none\""))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("案C"))}{TableCell(SlideRun("波線", " u=\"wavy\""), SlideRun("破線", " u=\"dash\""))}</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var markdown = await Export(Presentation("underline.pptx", title + textBox + table));
        // The slide's title is not underlined.
        Assert.Contains("\n## スライド 1 — タイトル\n", markdown, StringComparison.Ordinal);
        Assert.Contains("これは<u>重要</u>な点。", markdown, StringComparison.Ordinal);
        Assert.Contains("| 案A | <u>採用</u> → 保留 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 案B | リンク と <u>~~二重~~</u>なし |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 案C | <u>波線破線</u> |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public async Task Underline_in_a_later_paragraph_of_a_Word_or_PowerPoint_cell_keeps_its_place()
    {
        // A cell's paragraphs are one text joined by line breaks: the ranges follow the first paragraph.
        var word = Document("paragraphs.docx",
            "<w:tbl><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>" +
            $"<w:tr>{Cell(Run("項目"))}{Cell(Run("値"))}</w:tr>" +
            $"<w:tr>{Cell(Run("複数"))}<w:tc><w:p>{Run("一行目")}</w:p><w:p>{Run("二行目", "<w:u/>")}{Run("と", "<w:strike/>")}{Run("三")}</w:p></w:tc></w:tr></w:tbl>");
        Assert.Contains("| 複数 | 一行目<br><u>二行目</u>~~と~~三 |", await Export(word), StringComparison.Ordinal);

        string TableCell(params string[] paragraphs) => "<a:tc><a:txBody><a:bodyPr/>" + string.Concat(paragraphs.Select(paragraph => $"<a:p>{paragraph}</a:p>")) + "</a:txBody></a:tc>";
        var table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"0\" y=\"2000000\"/><a:ext cx=\"4000000\" cy=\"800000\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>" +
            "<a:tblGrid><a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/></a:tblGrid>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("項目"))}{TableCell(SlideRun("値"))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("複数"))}{TableCell(SlideRun("一行目"), SlideRun("二行目", " u=\"sng\"") + SlideRun("と", " strike=\"sngStrike\"") + SlideRun("三"))}</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        Assert.Contains("| 複数 | 一行目<br><u>二行目</u>~~と~~三 |", await Export(Presentation("paragraphs-slide.pptx", table)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_adapters_record_underline_in_the_extensions_the_serializer_reads()
    {
        // Excel: "underline" for a whole value, "underline_ranges" for part of it; touching runs are one range.
        var shared =
            "<si><r><t>これは</t></r><r><rPr><u/></rPr><t>重要</t></r><r><t>な点</t></r></si>" +                                          // 0
            "<si><r><rPr><u/></rPr><t>AB</t></r><r><rPr><u val=\"double\"/></rPr><t>CD</t></r><r><t>EF</t></r></si>";                  // 1
        var workbook = Workbook("contract.xlsx", string.Concat(
            Row(1, Inline("A1", "<t>全体</t>", 2), Shared("B1", 0), Shared("C1", 1)),
            Row(2, Inline("A2", "<t>リンク</t>", 2), Inline("B2", "<t>普通</t>"), Inline("C2", "<t>両方</t>", 8))),
            shared, afterSheetData: "<hyperlinks><hyperlink ref=\"A2\" r:id=\"rId1\"/></hyperlinks>");
        await using (var stream = File.OpenRead(workbook))
        {
            var cells = new XlsxAdapter().Extract(stream).Graph.Nodes.Where(node => node.Kind == NodeKind.Cell)
                .ToDictionary(node => node.Extensions!["address"].GetString()!);
            string? Extension(string address, string key) => cells[address].Extensions!.TryGetValue(key, out var value) ? value.GetRawText() : null;
            Assert.Equal(("true", null), (Extension("A1", "underline"), Extension("A1", "underline_ranges")));
            Assert.Equal((null, "[[3,5]]"), (Extension("B1", "underline"), Extension("B1", "underline_ranges")));
            Assert.Equal((null, "[[0,4]]"), (Extension("C1", "underline"), Extension("C1", "underline_ranges")));
            Assert.Equal((null, null), (Extension("A2", "underline"), Extension("A2", "underline_ranges")));   // a hyperlink cell
            Assert.Equal((null, null), (Extension("B2", "underline"), Extension("B2", "underline_ranges")));
            Assert.Equal(("true", "true"), (Extension("C2", "underline"), Extension("C2", "strike")));
        }

        // Word: "underline_cells" on the table, [row, cell, [[start, end), ...]] in the cell's text; a hyperlink's runs are left out.
        var document = Document("contract.docx",
            "<w:tbl><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>" +
            $"<w:tr>{Cell(Run("項目"))}{Cell(Run("値"))}</w:tr>" +
            $"<w:tr>{Cell(Run("A", "<w:u/>"), Run("B", "<w:u w:val=\"double\"/>"), Run("C"))}" +
            $"<w:tc><w:p>{Run("一")}</w:p><w:p>{Run("二", "<w:u/>")}<w:hyperlink r:id=\"rId9\">{Run("三", "<w:u/>")}</w:hyperlink></w:p></w:tc></w:tr></w:tbl>");
        var wordTable = Assert.Single((await new DocxAdapter().ExtractAsync(document)).Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("[[1,0,[[0,2]]],[1,1,[[2,3]]]]", wordTable.Extensions!["underline_cells"].GetRawText());

        // PowerPoint: the same, and a run with a:hlinkClick is a link.
        string TableCell(params string[] paragraphs) => "<a:tc><a:txBody><a:bodyPr/>" + string.Concat(paragraphs.Select(paragraph => $"<a:p>{paragraph}</a:p>")) + "</a:txBody></a:tc>";
        var table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
            "<p:xfrm><a:off x=\"0\" y=\"2000000\"/><a:ext cx=\"4000000\" cy=\"800000\"/></p:xfrm><a:graphic><a:graphicData><a:tbl>" +
            "<a:tblGrid><a:gridCol w=\"2000000\"/><a:gridCol w=\"2000000\"/></a:tblGrid>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("項目"))}{TableCell(SlideRun("値"))}</a:tr>" +
            $"<a:tr h=\"400000\">{TableCell(SlideRun("A", " u=\"sng\"") + SlideRun("B", " u=\"dbl\"") + SlideRun("C"))}" +
            $"{TableCell(SlideRun("一"), SlideRun("二", " u=\"sng\"") + SlideRun("三", " u=\"sng\"", "<a:hlinkClick r:id=\"rId2\"/>"))}</a:tr>" +
            "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        await using var slides = File.OpenRead(Presentation("contract.pptx", table));
        var slideTable = Assert.Single(new PptxAdapter().Extract(slides).Graph.Nodes, node => node.Kind == NodeKind.Table);
        Assert.Equal("[[1,0,[[0,2]]],[1,1,[[2,3]]]]", slideTable.Extensions!["underline_cells"].GetRawText());
    }

    // ---------------------------------------------------------------- the serializer, whatever adapter wrote the ranges

    private static Dictionary<string, JsonElement> Extensions(params (string Key, object Value)[] items) =>
        items.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal);

    private static string Serialize(DocumentFormatKind format, params DocumentNode[] nodes) =>
        new ReadableMarkdownSerializer().Serialize(new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "underline", format,
            [new DocumentPartition(format == DocumentFormatKind.Xlsx ? "sheet-Items" : "page-0001", 0, nodes)])).Replace("\r\n", "\n", StringComparison.Ordinal);

    // Nodes are written in the order of their ids when their orders are the same.
    private static DocumentNode Text(string id, string text, params (string Key, object Value)[] extensions) =>
        TextOf(id, NodeKind.Paragraph, text, extensions);

    private static DocumentNode TextOf(string id, NodeKind kind, string text, params (string Key, object Value)[] extensions) =>
        new(id, kind, null, 1, ContentLayer.Body, new TextNodeContent(text), Extensions: extensions.Length == 0 ? null : Extensions(extensions));

    [Fact]
    public void Underline_ranges_on_a_text_node_mark_the_text_apart_from_its_blanks_and_line_breaks()
    {
        const string text = "  see this and that\nsecond line  ";
        // Ranges of the raw text, blanks and the line break included: they stay outside the markup.
        var through = text.IndexOf("this", StringComparison.Ordinal);
        var line = text.IndexOf(" line", StringComparison.Ordinal);
        var markdown = Serialize(DocumentFormatKind.Pdf,
            Text("a", text, ("underline_ranges", new[] { new[] { through, text.IndexOf("ond", StringComparison.Ordinal) }, new[] { line, line + 5 } })),
            // Ranges of the whole text apply to the trimmed text the paragraph is written from.
            Text("b", " x y ", ("underline_ranges", new[] { new[] { 1, 4 } })),
            // Neither a sorted list nor a tidy one is required.
            Text("c", "abcdef", ("underline_ranges", new[] { new[] { 4, 6 }, new[] { 0, 2 }, new[] { 1, 3 } })));
        Assert.Contains("see <u>this and that</u>  \n<u>sec</u>ond <u>line</u>\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>x y</u>\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>abc</u>d<u>ef</u>\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Theory]
    [InlineData("[[2,1]]")]                 // reversed
    [InlineData("[[-1,3]]")]                // negative
    [InlineData("[[0,3],[4]]")]             // one bound
    [InlineData("[[\"a\",3]]")]             // not numbers
    [InlineData("[[0,3,5]]")]               // three bounds
    [InlineData("{\"0\":3}")]               // not a list
    [InlineData("[]")]
    [InlineData("null")]
    public void Malformed_underline_ranges_read_as_none_like_malformed_strike_ranges(string ranges)
    {
        var node = Text("a", "abcdef", ("strike_ranges", new[] { new[] { 0, 3 } })) with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["strike_ranges"] = JsonSerializer.SerializeToElement(new[] { new[] { 0, 3 } }),
                ["underline_ranges"] = JsonDocument.Parse(ranges).RootElement.Clone(),
            },
        };
        var markdown = Serialize(DocumentFormatKind.Pdf, node);
        Assert.Contains("\n~~abc~~def\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<u>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Underline_cells_on_a_table_node_mark_those_cells_and_skip_items_that_do_not_fit()
    {
        var rows = new IReadOnlyList<TableCell>[]
        {
            [new TableCell("項目"), new TableCell("値")],
            [new TableCell("標準"), new TableCell("下線 通常\n二行目")],
            [new TableCell("取消"), new TableCell("両方")],
        };
        var table = new DocumentNode("table", NodeKind.Table, null, 1, ContentLayer.Body, new TableNodeContent(rows),
            Extensions: Extensions(
                ("underline_cells", new object[]
                {
                    new object[] { 1, 1, new[] { new[] { 0, 2 }, new[] { 6, 9 } } },
                    new object[] { 1, 1, new[] { new[] { 3, 5 } } },        // the same cell again: both items apply
                    new object[] { 2, 1, new[] { new[] { 0, 2 } } },
                    new object[] { 5, 0, new[] { new[] { 0, 1 } } },        // no such row
                    new object[] { 1, 7, new[] { new[] { 0, 1 } } },        // no such cell
                    new object[] { 1, 0, new[] { new[] { 3, 1 } } },        // reversed range
                    new object[] { 1 },                                     // not an item
                }),
                ("strike_cells", new object[] { new object[] { 2, 0, new[] { new[] { 0, 2 } } }, new object[] { 2, 1, new[] { new[] { 0, 2 } } } })));
        var markdown = Serialize(DocumentFormatKind.Docx, table);
        Assert.Contains("| 項目 | 値 |\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| 標準 | <u>下線</u> <u>通常</u><br><u>二行目</u> |\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| ~~取消~~ | <u>~~両方~~</u> |\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Theory]
    // text, strike ranges, underline ranges, the Markdown written, and what a renderer makes of it
    [InlineData("ABCDEFGHIJ", "[[0,6]]", "[[0,6]]", "<u>~~ABCDEF~~</u>GHIJ", "<u><del>ABCDEF</del></u>GHIJ")]
    [InlineData("ABCDEFGHIJ", "[[3,10]]", "[[0,6]]", "<u>ABC~~DEF~~</u>~~GHIJ~~", "<u>ABC<del>DEF</del></u><del>GHIJ</del>")]
    [InlineData("ABCDEFGHIJ", "[[0,6]]", "[[3,10]]", "~~ABC~~<u>~~DEF~~GHIJ</u>", "<del>ABC</del><u><del>DEF</del>GHIJ</u>")]
    [InlineData("ABCDEF", "[[2,4]]", "[[0,6]]", "<u>AB~~CD~~EF</u>", "<u>AB<del>CD</del>EF</u>")]
    [InlineData("ABCDEF", "[[0,6]]", "[[2,4]]", "~~AB~~<u>~~CD~~</u>~~EF~~", "<del>AB</del><u><del>CD</del></u><del>EF</del>")]
    [InlineData("AB CD EF", "[[0,8]]", "[[3,5]]", "~~AB~~ <u>~~CD~~</u> ~~EF~~", "<del>AB</del> <u><del>CD</del></u> <del>EF</del>")]
    [InlineData("AB CD EF", "[[0,8]]", "[[0,3],[6,8]]", "<u>~~AB~~</u> ~~CD~~ <u>~~EF~~</u>", "<u><del>AB</del></u> <del>CD</del> <u><del>EF</del></u>")]
    [InlineData("ABCDEF", "[[0,2],[4,6]]", "[[1,5]]", "~~A~~<u>~~B~~CD~~E~~</u>~~F~~", "<del>A</del><u><del>B</del>CD<del>E</del></u><del>F</del>")]
    [InlineData("日本語の文章", "[[1,4]]", "[[3,6]]", "日~~本語~~<u>~~の~~文章</u>", "日<del>本語</del><u><del>の</del>文章</u>")]
    public void Overlapping_strike_and_underline_ranges_are_cut_so_that_the_markup_stays_balanced(string text, string strike, string underline,
        string expected, string expectedHtml)
    {
        var node = Text("a", text) with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["strike_ranges"] = JsonDocument.Parse(strike).RootElement.Clone(),
                ["underline_ranges"] = JsonDocument.Parse(underline).RootElement.Clone(),
            },
        };
        var markdown = Serialize(DocumentFormatKind.Pdf, node);
        Assert.Contains("\n" + expected + "\n", markdown, StringComparison.Ordinal);
        // Markdig reads the delimiters as intended: nothing is left literal, and u always wraps del.
        Assert.Equal("<p>" + expectedHtml + "</p>", Html(expected));
        // So does the project's own reader, which cannot read a <u> inside the ~~ of a strikethrough.
        Assert.Equal(expectedHtml, MarkdownInlineParser.Html(expected));
        AssertNoMarkers(markdown);
    }

    [Fact]
    public void Underlined_and_struck_text_across_lines_keeps_each_line_balanced()
    {
        // The underline covers "B", the line break and "CD"; the strikethrough "AB", the break and "CD".
        var markdown = Serialize(DocumentFormatKind.Pdf,
            Text("a", "AB\nCD  EF", ("underline_ranges", new[] { new[] { 1, 5 } }), ("strike_ranges", new[] { new[] { 0, 5 } })));
        Assert.Contains("\n~~A~~<u>~~B~~</u>  \n<u>~~CD~~</u>  EF\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public void Headings_and_titles_keep_their_strikethrough_but_not_their_underline()
    {
        var ranges = new[] { new[] { 0, 2 } };
        var markdown = Serialize(DocumentFormatKind.Pdf,
            TextOf("h", NodeKind.Heading, "見出し", ("underline_ranges", ranges), ("strike_ranges", new[] { new[] { 1, 3 } }), ("heading_level", 1)),
            Text("p", "本文です", ("underline_ranges", ranges)),
            // A rich heading: the run's underline is dropped, the other formatting is not.
            new DocumentNode("rich", NodeKind.Heading, null, 1, ContentLayer.Body,
                new RichTextNodeContent([new TextRun("強い", Bold: true, Underline: true), new TextRun("見出し", Underline: true, Strike: true)]),
                Extensions: Extensions(("heading_level", 2))),
            new DocumentNode("richbody", NodeKind.Paragraph, null, 1, ContentLayer.Body,
                new RichTextNodeContent([new TextRun("強い", Bold: true, Underline: true), new TextRun("本文", Underline: true)])),
            TextOf("section", NodeKind.Section, "章立て", ("underline_ranges", ranges)));
        // (Below the document's own title, a heading is one level lower than it was in the source.)
        Assert.Contains("\n## 見~~出し~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>本文</u>です\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n### **強い**~~見出し~~\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n<u>**強い**本文</u>\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n## 章立て\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public void Excel_cell_nodes_carry_underline_whole_or_in_ranges_the_way_they_carry_strikethrough()
    {
        DocumentNode Cell(string address, int row, int column, string value, params (string Key, object Value)[] extensions) =>
            new("cell-" + address, NodeKind.Cell, null, row * 1000 + column, ContentLayer.Body, new TextNodeContent(value),
                new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
                Extensions: Extensions([("row", row), ("column", column), .. extensions]));
        var markdown = Serialize(DocumentFormatKind.Xlsx,
            Cell("A1", 1, 1, "品名", ("is_bold", true), ("underline", true)), Cell("B1", 1, 2, "備考", ("is_bold", true)),
            Cell("A2", 2, 1, "全体", ("underline", true)), Cell("B2", 2, 2, "これは重要な点", ("underline_ranges", new[] { new[] { 3, 5 } })),
            // The cell is written as typed (trimmed): the ranges are those of the raw text.
            Cell("A3", 3, 1, "前", ("underline_ranges", new[] { new[] { 0, 1 } })), Cell("B3", 3, 2, "  空白あり  ", ("underline_ranges", new[] { new[] { 2, 4 } })),
            // A formula's result is not the text the ranges were made for: only the whole-cell flag applies.
            Cell("A4", 4, 1, "計算", ("formula", "1+1"), ("underline_ranges", new[] { new[] { 0, 1 } })), Cell("B4", 4, 2, "式", ("formula", "1+1"), ("underline", true)));
        Assert.Contains("| 品名 | 備考 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| <u>全体</u> | これは<u>重要</u>な点 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| <u>前</u> | <u>空白</u>あり |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 計算 | <u>式</u> |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public void Underlined_code_like_cell_text_stays_a_paragraph_that_can_show_the_underline()
    {
        DocumentNode Cell(string address, int row, string value, params (string Key, object Value)[] extensions) =>
            new("cell-" + address, NodeKind.Cell, null, row * 1000 + 1, ContentLayer.Body, new TextNodeContent(value),
                new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
                Extensions: Extensions([("row", row), ("column", 1), .. extensions]));
        var code = "{\n  \"a\": 1\n}";
        // The same text without underline is a code block, as it always was.
        var plain = Serialize(DocumentFormatKind.Xlsx, Cell("A1", 1, "試験結果"), Cell("A3", 3, code));
        Assert.Contains("```\n" + code + "\n```", plain, StringComparison.Ordinal);
        var underlined = Serialize(DocumentFormatKind.Xlsx, Cell("A1", 1, "試験結果"), Cell("A3", 3, code, ("underline", true)));
        Assert.DoesNotContain("```", underlined, StringComparison.Ordinal);
        Assert.Contains("<u>{</u>", underlined, StringComparison.Ordinal);
        Assert.Contains("<u>\"a\": 1</u>", underlined, StringComparison.Ordinal);
        Assert.Contains("<u>}</u>", underlined, StringComparison.Ordinal);
        AssertNoMarkers(underlined);
    }

    [Fact]
    public void Noncharacters_in_every_kind_of_source_text_read_as_replacement_characters()
    {
        // Each of the six texts holds all four markers.
        const string marked = "a\uFDD0b\uFDD1c\uFDD2d\uFDD3e";
        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(Title: "T" + marked)).Serialize(
            new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "marked", DocumentFormatKind.Docx,
            [
                new DocumentPartition("part-1", 0,
                [
                    Text("p", marked),
                    new DocumentNode("rich", NodeKind.Paragraph, null, 2, ContentLayer.Body, new RichTextNodeContent([new TextRun(marked)])),
                    new DocumentNode("t", NodeKind.Table, null, 3, ContentLayer.Body,
                        new TableNodeContent(new IReadOnlyList<TableCell>[] { [new TableCell(marked), new TableCell("b")] })),
                    TextOf("d", NodeKind.Diagram, "図", ("diagram_items", new[] { marked })),
                    new DocumentNode("image", NodeKind.Image, null, 5, ContentLayer.Body, new ReferenceNodeContent("media/x.png", marked)),
                ]),
            ]));
        AssertNoMarkers(markdown);
        Assert.Equal(6 * 4, markdown.Count(character => character == '\uFFFD'));
        Assert.DoesNotContain("<u>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_runs_are_not_underlined_in_the_readable_text_only()
    {
        var runs = new[]
        {
            new TextRun("See "),
            new TextRun("Project", Underline: true, LinkTarget: "https://example.test/docs"),
            new TextRun(" and "),
            new TextRun("this", Underline: true),
        };
        var node = new DocumentNode("rich", NodeKind.Paragraph, null, 1, ContentLayer.Body, new RichTextNodeContent(runs));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "link", DocumentFormatKind.Docx, [new DocumentPartition("part-1", 0, [node])]);
        Assert.Contains("See [Project](https://example.test/docs) and <u>this</u>", new ReadableMarkdownSerializer().Serialize(graph), StringComparison.Ordinal);
        // The round-trip projection still writes what it must read back.
        Assert.Contains("[<u>Project</u>](https://example.test/docs)", DocRedockInlineMarkdown.Serialize(runs), StringComparison.Ordinal);
        Assert.Contains("[<u>Project</u>](https://example.test/docs)", new DocRedockMarkdownSerializer().Serialize(graph).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Underlined_text_in_a_nested_table_stays_underlined_in_its_host_cell()
    {
        var host = new DocumentNode("host", NodeKind.Table, null, 1, ContentLayer.Body,
            new TableNodeContent(new IReadOnlyList<TableCell>[] { [new TableCell("項目"), new TableCell("値")], [new TableCell("親"), new TableCell("本文")] }));
        var child = new DocumentNode("child", NodeKind.Table, "host", 2, ContentLayer.Body,
            new TableNodeContent(new IReadOnlyList<TableCell>[] { [new TableCell("子"), new TableCell("下線 通常")] }),
            Extensions: Extensions(("nested_table_parent", "host"), ("nested_table_row", 1), ("nested_table_column", 1),
                ("underline_cells", new object[] { new object[] { 0, 1, new[] { new[] { 0, 2 } } } })));
        var markdown = Serialize(DocumentFormatKind.Docx, host, child);
        Assert.Contains("| 親 | 本文<br>子 / <u>下線</u> 通常 |", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    [Fact]
    public void Line_style_notes_name_underlined_rows_and_columns_in_plain_text()
    {
        const string name = "DESIGN-PHASE-WITH-A-VERY-LONG-NAME";
        var overlay = new { ShapeId = "1", Text = "", Kind = "arrow", Direction = "both", Axis = "horizontal", StartRow = 1, EndRow = 1, StartColumn = 1, EndColumn = 3, LineStyle = "dashed" };
        var table = new DocumentNode("table", NodeKind.Table, null, 1, ContentLayer.Body,
            new TableNodeContent(new IReadOnlyList<TableCell>[]
            {
                [new TableCell("Task"), new TableCell("Jan"), new TableCell("Feb"), new TableCell("Mar")],
                [new TableCell(name), new TableCell(""), new TableCell(""), new TableCell("")],
            }),
            Extensions: Extensions(("shape_id", "table"), ("table_overlays", new[] { overlay }),
                ("underline_cells", new object[]
                {
                    new object[] { 1, 0, new[] { new[] { 0, name.Length } } },
                    new object[] { 0, 1, new[] { new[] { 0, 3 } } },
                }),
                ("strike_cells", new object[] { new object[] { 1, 0, new[] { new[] { 0, name.Length } } } })));
        var markdown = Serialize(DocumentFormatKind.Pdf, table);
        // The cell is underlined and struck as written; the note that names its row quotes plain text.
        Assert.Contains("| <u>~~" + name + "~~</u> | ", markdown, StringComparison.Ordinal);
        Assert.Contains("> - 破線の両矢印: 行「DESIGN-PHASE-WITH-A-VERY…」、列「Jan」〜「Mar」\n", markdown, StringComparison.Ordinal);
        AssertNoMarkers(markdown);
    }

    // ---------------------------------------------------------------- AI packages

    private static DocumentNode SheetCell(string address, string text, bool bold = false, bool underline = false)
    {
        var column = address.TakeWhile(char.IsLetter).Aggregate(0, (sum, letter) => sum * 26 + letter - 'A' + 1);
        var row = int.Parse(address[address.TakeWhile(char.IsLetter).Count()..], System.Globalization.CultureInfo.InvariantCulture);
        return new DocumentNode("cell-" + address, NodeKind.Cell, null, row * 100 + column, ContentLayer.Body, new TextNodeContent(text),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]),
            Extensions: Extensions(("row", row), ("column", column), ("is_bold", bold), ("underline", underline)));
    }

    [Fact]
    public void AI_package_row_blocks_repeat_plain_column_names_over_underlined_values()
    {
        var nodes = new List<DocumentNode>
        {
            SheetCell("A1", "Item", bold: true, underline: true), SheetCell("B1", "Q1", bold: true, underline: true), SheetCell("C1", "Q2", bold: true, underline: true),
        };
        for (var row = 2; row <= 200; row++)
        {
            nodes.Add(SheetCell($"A{row}", $"Item {row}", underline: true));
            nodes.Add(SheetCell($"B{row}", (row * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            nodes.Add(SheetCell($"C{row}", (row * 20).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "book", DocumentFormatKind.Xlsx, [new DocumentPartition("sheet-Data", 0, nodes)]);
        var parts = AiPackageContentBuilder.Build(graph, new(), "book.xlsx", new AiPackagePartOptions(1500, TableRowBlocks: true));
        Assert.True(parts.Count >= 3);
        Assert.All(parts, part =>
        {
            // The header every block starts with names the columns without underline; the rows keep theirs.
            Assert.Contains("| Item | Q1 | Q2 |\n| --- | --- | --- |\n", part.Markdown, StringComparison.Ordinal);
            Assert.Contains("| <u>Item ", part.Markdown, StringComparison.Ordinal);
            Assert.All(part.Sources, source => Assert.Equal("Data", source.SheetName));
            AssertNoMarkers(part.Markdown);
        });
        Assert.DoesNotContain(parts, part => part.Markdown.Contains("<u>Item</u>", StringComparison.Ordinal));
    }

    [Fact]
    public void AI_package_titles_and_locations_are_plain_and_the_parts_keep_the_underline()
    {
        var heading = new DocumentNode("h1", NodeKind.Heading, null, 0, ContentLayer.Body,
            new RichTextNodeContent([new TextRun("概要", Underline: true)]), Extensions: Extensions(("heading_level", 1)));
        var body = new DocumentNode("p1", NodeKind.Paragraph, null, 1, ContentLayer.Body,
            new RichTextNodeContent([new TextRun("これは"), new TextRun("重要", Underline: true), new TextRun("な点")]));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Docx, [new DocumentPartition("document", 0, [heading, body])]);
        var part = Assert.Single(AiPackageContentBuilder.Build(graph, new(), "report.docx"));
        Assert.Contains("section_path: 概要\n", part.Markdown, StringComparison.Ordinal);
        Assert.Contains("\n## 概要\n", part.Markdown, StringComparison.Ordinal);
        Assert.Contains("これは<u>重要</u>な点", part.Markdown, StringComparison.Ordinal);
        Assert.All(part.Sources, source => Assert.Equal(["概要"], source.HeadingPath));
        Assert.DoesNotContain(part.Sources, source => source.Label.Contains('<', StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- balanced markup, whatever the ranges

    // The strike-only writing as it was before underline: the loop over the characters struck.
    private static string StruckOnly(string text, IReadOnlyList<(int Start, int End)> struck)
    {
        var marked = new bool[text.Length];
        foreach (var (start, end) in struck)
            for (var index = Math.Max(start, 0); index < Math.Min(end, text.Length); index++) marked[index] = true;
        var output = new StringBuilder();
        for (var index = 0; index < text.Length;)
        {
            if (!marked[index] || char.IsWhiteSpace(text[index]))
            {
                output.Append(text[index++]);
                continue;
            }
            var end = index;
            while (end < text.Length && marked[end] && text[end] is not ('\n' or '\r')) end++;
            var last = end;
            while (last > index && char.IsWhiteSpace(text[last - 1])) last--;
            output.Append("~~").Append(text, index, last - index).Append("~~").Append(text, last, end - last);
            index = end;
        }
        return output.ToString();
    }

    [Fact]
    public void Whatever_the_ranges_every_strike_and_underline_is_balanced_nested_and_on_the_right_characters()
    {
        var random = new Random(20261011);
        const string alphabet = "abc日本  \n";
        for (var round = 0; round < 3000; round++)
        {
            var length = random.Next(1, 25);
            var text = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            // The paragraph is written trimmed: keep the text free of blanks at its ends, so offsets stay as drawn.
            text = "a" + text + "b";
            List<(int Start, int End)> Ranges() => Enumerable.Range(0, random.Next(0, 4))
                .Select(_ => { var start = random.Next(0, text.Length + 1); return (start, Math.Min(text.Length + 2, start + random.Next(0, text.Length))); }).ToList();
            var struck = Ranges();
            var underlined = Ranges();
            var extensions = new List<(string Key, object Value)>();
            if (struck.Count > 0) extensions.Add(("strike_ranges", struck.Select(range => new[] { range.Start, range.End }).ToArray()));
            if (underlined.Count > 0) extensions.Add(("underline_ranges", underlined.Select(range => new[] { range.Start, range.End }).ToArray()));
            var markdown = Serialize(DocumentFormatKind.Pdf, Text("a", text, extensions.ToArray()));
            const string title = "# ドキュメント\n\n";
            Assert.StartsWith(title, markdown, StringComparison.Ordinal);
            // WriteParagraph writes a line break as two spaces and the break.
            var written = markdown[title.Length..].TrimEnd('\n').Replace("  \n", "\n", StringComparison.Ordinal);
            string Drawn(List<(int Start, int End)> ranges) => string.Join(",", ranges.Select(range => $"[{range.Start},{range.End})"));
            var context = $"text {JsonSerializer.Serialize(text)} strike {Drawn(struck)} underline {Drawn(underlined)} -> {JsonSerializer.Serialize(written)}";

            // Without the markup it is the text; without underline it is what strikethrough alone wrote.
            Assert.True(text == written.Replace("<u>", "", StringComparison.Ordinal).Replace("</u>", "", StringComparison.Ordinal).Replace("~~", "", StringComparison.Ordinal), context);
            if (underlined.Count == 0) Assert.True(StruckOnly(text, struck) == written, context);

            bool Marked(List<(int Start, int End)> ranges, int index) => ranges.Any(range => index >= range.Start && index < range.End);
            var stack = new List<char>();   // 'u' and 's', outermost first
            var position = 0;
            for (var index = 0; index < written.Length;)
            {
                if (written.AsSpan(index).StartsWith("<u>"))
                {
                    Assert.True(stack.Count == 0, "an underline opens outside everything: " + context);
                    Assert.True(index + 3 < written.Length && !char.IsWhiteSpace(written[index + 3]), "no blank after an opening: " + context);
                    stack.Add('u');
                    index += 3;
                }
                else if (written.AsSpan(index).StartsWith("</u>"))
                {
                    Assert.True(stack is ['u'], "an underline closes when nothing is open inside it: " + context);
                    Assert.True(!char.IsWhiteSpace(written[index - 1]), "no blank before a closing: " + context);
                    stack.Clear();
                    index += 4;
                }
                else if (written.AsSpan(index).StartsWith("~~"))
                {
                    if (stack.Count > 0 && stack[^1] == 's')
                    {
                        Assert.True(!char.IsWhiteSpace(written[index - 1]), "no blank before a closing: " + context);
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        Assert.True(index + 2 < written.Length && !char.IsWhiteSpace(written[index + 2]), "no blank after an opening: " + context);
                        stack.Add('s');
                    }
                    index += 2;
                }
                else
                {
                    var character = written[index];
                    Assert.Equal(text[position], character);
                    if (character == '\n') Assert.True(stack.Count == 0, "a mark ends at the line: " + context);
                    // A character is inside a mark exactly when the ranges mark it; a blank may be inside a mark that goes on after it.
                    if (!char.IsWhiteSpace(character))
                    {
                        Assert.True(Marked(struck, position) == stack.Contains('s'), $"strike at {position}: " + context);
                        Assert.True(Marked(underlined, position) == stack.Contains('u'), $"underline at {position}: " + context);
                    }
                    else
                    {
                        if (stack.Contains('s')) Assert.True(Marked(struck, position), $"unstruck blank inside a strike at {position}: " + context);
                        if (stack.Contains('u')) Assert.True(Marked(underlined, position), $"unmarked blank inside an underline at {position}: " + context);
                    }
                    index++;
                    position++;
                }
            }
            Assert.True(stack.Count == 0, "everything is closed: " + context);
            Assert.Equal(text.Length, position);
        }
    }
}

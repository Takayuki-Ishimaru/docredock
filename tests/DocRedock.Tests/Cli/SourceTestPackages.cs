using System.IO.Compression;
using System.Text;

namespace DocRedock.Tests.Cli;

/// <summary>Small Office files with known content for the tests of <c>docredock source</c> and the <c>read_source</c> tool.</summary>
internal static class SourceTestPackages
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Write(string path, IEnumerable<KeyValuePair<string, string>> parts)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static string ContentTypes(string main, string mainType, params (string Part, string Type)[] more) =>
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
        $"<Override PartName=\"/{main}\" ContentType=\"{mainType}\"/>" +
        string.Concat(more.Select(item => $"<Override PartName=\"/{item.Part}\" ContentType=\"{item.Type}\"/>")) + "</Types>";

    private static string RootRels(string main) =>
        $"<Relationships xmlns=\"{Pkg}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"{main}\"/></Relationships>";

    /// <summary>A Word file whose paragraphs are the given texts (already XML-escaped), plus an image part.</summary>
    public static string Docx(string directory, string name, params string[] paragraphs)
    {
        var path = Path.Combine(directory, name);
        var body = string.Concat(paragraphs.Select(text => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>"));
        Write(path,
        [
            new("[Content_Types].xml", ContentTypes("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            new("_rels/.rels", RootRels("word/document.xml")),
            new("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>"),
            new("word/media/image1.png", "\u0089PNG not really an image"),
            new("word/embeddings/oleObject1.bin", "binary but not a picture"),
        ]);
        return path;
    }

    /// <summary>A workbook: "Data" (A1 Item, B1 Qty, C1 Total; A2 Apple, B2 10, C2 =B2*2 cached 20; a hidden row 3; a merged
    /// A4:B4 "Merged title"; a hidden column D) and "メモ" (A1 "こんにちは").</summary>
    public static string Xlsx(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string Text(string reference, string value) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";
        Write(path,
        [
            new("[Content_Types].xml", ContentTypes("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                ("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                ("xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))),
            new("_rels/.rels", RootRels("xl/workbook.xml")),
            new("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"メモ\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            new("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{Pkg}\"><Relationship Id=\"rId1\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>"),
            new("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><cols><col min=\"4\" max=\"4\" width=\"9\" hidden=\"1\" customWidth=\"1\"/></cols><sheetData>" +
                $"<row r=\"1\">{Text("A1", "Item")}{Text("B1", "Qty")}{Text("C1", "Total")}{Text("D1", "Hid")}</row>" +
                $"<row r=\"2\">{Text("A2", "Apple")}<c r=\"B2\"><v>10</v></c><c r=\"C2\"><f>B2*2</f><v>20</v></c></row>" +
                $"<row r=\"3\" hidden=\"1\">{Text("A3", "Secret row")}</row>" +
                $"<row r=\"4\">{Text("A4", "Merged title")}</row>" +
                "</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A4:B4\"/></mergeCells></worksheet>"),
            new("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\">{Text("A1", "こんにちは")}</row></sheetData></worksheet>"),
        ]);
        return path;
    }

    /// <summary>A presentation of two slides: "Quarterly plan" (with speaker notes) and "Second slide".</summary>
    public static string Pptx(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        string Slide(string text) => $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
        const string SlideType = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
        Write(path,
        [
            new("[Content_Types].xml", ContentTypes("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                ("ppt/slides/slide1.xml", SlideType), ("ppt/slides/slide2.xml", SlideType),
                ("ppt/notesSlides/notesSlide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"))),
            new("_rels/.rels", RootRels("ppt/presentation.xml")),
            new("ppt/presentation.xml", $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{Rel}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/><p:sldId id=\"257\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>"),
            new("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{Pkg}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/slide\" Target=\"slides/slide2.xml\"/></Relationships>"),
            new("ppt/slides/slide1.xml", Slide("Quarterly plan")),
            new("ppt/slides/_rels/slide1.xml.rels", $"<Relationships xmlns=\"{Pkg}\"><Relationship Id=\"rId1\" Type=\"{Rel}/notesSlide\" Target=\"../notesSlides/notesSlide1.xml\"/></Relationships>"),
            new("ppt/notesSlides/notesSlide1.xml", Slide("Speaker note text")),
            new("ppt/slides/slide2.xml", Slide("Second slide")),
        ]);
        return path;
    }
}

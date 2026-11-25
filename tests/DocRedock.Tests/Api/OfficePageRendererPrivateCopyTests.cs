using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocRedock.Api;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>What the local Office engine is given: the private copy holds no instruction that fetches external
/// content or runs a macro, stays a consistent package, and is refused when its archive cannot be trusted.</summary>
public sealed class OfficePageRendererPrivateCopyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-office-copy-").FullName;
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Ms = "http://schemas.microsoft.com/office/2006/relationships";
    private const string WordMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string Defaults = "<Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>";

    [Fact]
    public async Task Word_fields_that_fetch_external_content_are_replaced_by_their_cached_result()
    {
        static string Run(string content) => $"<w:r>{content}</w:r>";
        static string Char(string type) => Run($"<w:fldChar w:fldCharType='{type}'/>");
        static string Code(string text) => Run($"<w:instrText xml:space='preserve'>{text}</w:instrText>");
        static string Text(string text) => Run($"<w:rPr><w:b/></w:rPr><w:t>{text}</w:t>");
        var body =
            // The field type is split over two runs, as Word writes it after an edit.
            "<w:p>" + Char("begin") + Code(" INCLUDE") + Code("PICTURE \"http://tracker.invalid/pixel.png\" \\d ") + Char("separate") + Text("CACHED_IMAGE") + Char("end") + "</w:p>" +
            "<w:p><w:fldSimple w:instr=' DDEAUTO excel \"book.xls\" r1c1 '>" + Text("CACHED_EXCHANGE") + "</w:fldSimple></w:p>" +
            "<w:p>" + Char("begin") + Code(" INCLUDETEXT \"\\\\\\\\server\\\\share\\\\a.docx\" ") + Char("separate") + Text("CACHED_TEXT_RESULT") + Char("end") + "</w:p>" +
            "<w:p>" + Char("begin") + Code(" LINK Excel.Sheet.12 \"C:\\\\book.xlsx\" \"Sheet1!R1C1\" \\a ") + Char("separate") + Text("CACHED_LINK") + Char("end") + "</w:p>" +
            // A field without a cached result leaves nothing behind.
            "<w:p>" + Char("begin") + Code(" DDE excel \"book.xls\" r2c2 ") + Char("end") + Text("AFTER_EMPTY_FIELD") + "</w:p>" +
            // An external field nested in the code of an ordinary field, and an ordinary field in an external field's result.
            "<w:p>" + Char("begin") + Code(" IF ") + Char("begin") + Code(" INCLUDETEXT \"http://tracker.invalid/a\" ") + Char("separate") + Text("NESTED_CACHED") + Char("end") +
                Code(" = \"x\" \"yes\" \"no\" ") + Char("separate") + Text("IF_RESULT") + Char("end") + "</w:p>" +
            "<w:p>" + Char("begin") + Code(" PAGE ") + Char("separate") + Text("7") + Char("end") + "</w:p>";
        var copy = await CopyOf(Docx("fields.docx", body));
        var document = Read(copy, "word/document.xml");
        foreach (var type in new[] { "INCLUDE", "PICTURE", "DDE", "LINK ", "tracker.invalid", "book.xls", "server" })
            Assert.DoesNotContain(type, document, StringComparison.OrdinalIgnoreCase);
        foreach (var kept in new[] { "CACHED_IMAGE", "CACHED_EXCHANGE", "CACHED_TEXT_RESULT", "CACHED_LINK", "AFTER_EMPTY_FIELD", "NESTED_CACHED", "IF_RESULT", "OFFICE_BODY_TEXT" })
            Assert.Contains(kept, document);
        var parsed = XDocument.Parse(document);
        XNamespace w = W;
        Assert.Empty(parsed.Descendants(w + "fldSimple"));
        // The IF and PAGE fields are intact: begin, separate and end each.
        Assert.Equal(new[] { "begin", "separate", "end", "begin", "separate", "end" },
            parsed.Descendants(w + "fldChar").Select(e => (string)e.Attribute(w + "fldCharType")!).ToArray());
        Assert.Equal(new[] { " IF ", " = \"x\" \"yes\" \"no\" ", " PAGE " }, parsed.Descendants(w + "instrText").Select(e => e.Value).ToArray());
        // No run is left without content, and the result keeps its formatting.
        Assert.DoesNotContain(parsed.Descendants(w + "r"), run => !run.Elements().Any(e => e.Name != w + "rPr"));
        Assert.Equal(8, parsed.Descendants(w + "b").Count());
    }

    [Fact]
    public async Task Excel_connections_queries_and_external_links_are_removed_with_every_reference_to_them()
    {
        var path = Path.Combine(root, "data.xlsx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "[Content_Types].xml", $"<Types xmlns='{ContentTypes}'>{Defaults}" +
                "<Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/>" +
                "<Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/>" +
                "<Override PartName='/xl/connections.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.connections+xml'/>" +
                "<Override PartName='/xl/queryTables/queryTable1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.queryTable+xml'/>" +
                "<Override PartName='/xl/externalLinks/externalLink1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml'/>" +
                // A link part outside the usual folder is found by its content type and relationship.
                "<Override PartName='/xl/other/link2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml'/>" +
                "<Override PartName='/xl/tables/table1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml'/>" +
                "<Override PartName='/xl/pivotCache/pivotCacheDefinition1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml'/></Types>");
            Add(archive, "_rels/.rels", $"<Relationships xmlns='{PackageRels}'><Relationship Id='rId1' Type='{R}/officeDocument' Target='xl/workbook.xml'/></Relationships>");
            Add(archive, "xl/workbook.xml", $"<workbook xmlns='{S}' xmlns:r='{R}'><sheets><sheet name='Sheet1' sheetId='1' r:id='rId1'/></sheets>" +
                "<externalReferences><externalReference r:id='rId3'/><externalReference r:id='rId6'/></externalReferences>" +
                "<definedNames><definedName name='Remote'>[1]Sheet1!$A$1</definedName><definedName name='Quoted'>'[2]Other sheet'!$B$2</definedName><definedName name='Local'>Sheet1!$A$1</definedName></definedNames>" +
                "<pivotCaches><pivotCache cacheId='1' r:id='rId5'/></pivotCaches></workbook>");
            Add(archive, "xl/_rels/workbook.xml.rels", $"<Relationships xmlns='{PackageRels}'>" +
                $"<Relationship Id='rId1' Type='{R}/worksheet' Target='worksheets/sheet1.xml'/>" +
                $"<Relationship Id='rId2' Type='{R}/connections' Target='connections.xml'/>" +
                $"<Relationship Id='rId3' Type='{R}/externalLink' Target='externalLinks/externalLink1.xml'/>" +
                $"<Relationship Id='rId5' Type='{R}/pivotCacheDefinition' Target='pivotCache/pivotCacheDefinition1.xml'/>" +
                $"<Relationship Id='rId6' Type='{R}/externalLink' Target='/xl/other/link2.xml'/></Relationships>");
            Add(archive, "xl/connections.xml", $"<connections xmlns='{S}'><connection id='1' name='Web' type='4' refreshOnLoad='1'><webPr url='http://tracker.invalid/q'/></connection></connections>");
            Add(archive, "xl/worksheets/sheet1.xml", $"<worksheet xmlns='{S}' xmlns:r='{R}'><sheetData><row r='1'><c r='A1'><v>41</v></c></row></sheetData><tableParts count='1'><tablePart r:id='rId2'/></tableParts></worksheet>");
            Add(archive, "xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns='{PackageRels}'>" +
                $"<Relationship Id='rId1' Type='{R}/queryTable' Target='../queryTables/queryTable1.xml'/>" +
                $"<Relationship Id='rId2' Type='{R}/table' Target='../tables/table1.xml'/></Relationships>");
            Add(archive, "xl/queryTables/queryTable1.xml", $"<queryTable xmlns='{S}' name='Web' connectionId='1' refreshOnLoad='1'/>");
            Add(archive, "xl/tables/table1.xml", $"<table xmlns='{S}' id='1' name='T' displayName='T' ref='A1:A2' tableType='queryTable' connectionId='1'><tableColumns count='1'><tableColumn id='1' name='A' queryTableFieldId='1'/></tableColumns></table>");
            Add(archive, "xl/tables/_rels/table1.xml.rels", $"<Relationships xmlns='{PackageRels}'><Relationship Id='rId1' Type='{R}/queryTable' Target='../queryTables/queryTable1.xml'/></Relationships>");
            Add(archive, "xl/externalLinks/externalLink1.xml", $"<externalLink xmlns='{S}' xmlns:r='{R}'><externalBook r:id='rId1'/></externalLink>");
            Add(archive, "xl/externalLinks/_rels/externalLink1.xml.rels", $"<Relationships xmlns='{PackageRels}'><Relationship Id='rId1' Type='{R}/externalLinkPath' Target='file:///C:/other.xlsx' TargetMode='External'/></Relationships>");
            Add(archive, "xl/other/link2.xml", $"<externalLink xmlns='{S}'><ddeLink ddeService='cmd' ddeTopic='x'/></externalLink>");
            Add(archive, "xl/pivotCache/pivotCacheDefinition1.xml", $"<pivotCacheDefinition xmlns='{S}' refreshOnLoad='1' recordCount='1'><cacheSource type='external' connectionId='1'/><cacheFields count='0'/></pivotCacheDefinition>");
        }
        var copy = await CopyOf(path);
        Assert.DoesNotContain(copy.Entries, e => e.FullName.Contains("connections", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Contains("queryTable", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains("externalLink", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Contains("link2", StringComparison.OrdinalIgnoreCase));
        AssertConsistentPackage(copy);
        var workbook = XDocument.Parse(Read(copy, "xl/workbook.xml"));
        XNamespace s = S;
        Assert.Empty(workbook.Descendants(s + "externalReferences"));
        Assert.Empty(workbook.Descendants(s + "externalReference"));
        Assert.Equal(new[] { "Local" }, workbook.Descendants(s + "definedName").Select(e => (string)e.Attribute("name")!).ToArray());
        Assert.Single(workbook.Descendants(s + "sheet"));
        Assert.Single(workbook.Descendants(s + "pivotCache"));
        var rels = Read(copy, "xl/_rels/workbook.xml.rels");
        Assert.DoesNotContain("connections", rels);
        Assert.DoesNotContain("externalLink", rels);
        Assert.Contains("/worksheet'", rels.Replace('"', '\''));
        Assert.DoesNotContain("queryTable", Read(copy, "xl/worksheets/_rels/sheet1.xml.rels"));
        // The saved rows stay; the instruction to fetch them again does not.
        Assert.Contains("<v>41</v>", Read(copy, "xl/worksheets/sheet1.xml"));
        var table = Read(copy, "xl/tables/table1.xml");
        Assert.DoesNotContain("queryTable", table);
        Assert.DoesNotContain("connectionId", table);
        var cache = Read(copy, "xl/pivotCache/pivotCacheDefinition1.xml");
        Assert.DoesNotContain("refreshOnLoad", cache);
        Assert.DoesNotContain("connectionId", cache);
        Assert.DoesNotContain(copy.Entries, e => Read(copy, e.FullName).Contains("tracker.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linked_objects_and_media_leave_no_dangling_reference()
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var path = Path.Combine(root, "linked.pptx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "[Content_Types].xml", $"<Types xmlns='{ContentTypes}'>{Defaults}<Default Extension='png' ContentType='image/png'/>" +
                "<Override PartName='/ppt/slides/slide1.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.slide+xml'/></Types>");
            Add(archive, "ppt/slides/slide1.xml", $"<p:sld xmlns:p='{p}' xmlns:a='{a}' xmlns:r='{R}'><p:cSld><p:spTree>" +
                "<p:pic><p:nvPicPr><p:cNvPr id='2' name='Linked'/><p:cNvPicPr/><p:nvPr><a:videoFile r:link='rId3'/></p:nvPr></p:nvPicPr><p:blipFill><a:blip r:embed='rId1' r:link='rId2'/><a:stretch/></p:blipFill><p:spPr/></p:pic>" +
                "<p:pic><p:nvPicPr><p:cNvPr id='3' name='OnlyLinked'/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:link='rId2'/><a:stretch/></p:blipFill><p:spPr/></p:pic>" +
                "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id='4' name='Ole'/></p:nvGraphicFramePr><a:graphic><a:graphicData uri='http://schemas.openxmlformats.org/presentationml/2006/ole'>" +
                "<p:oleObj progId='Excel.Sheet.12' r:id='rId4'><p:link updateAutomatic='1'/></p:oleObj></a:graphicData></a:graphic></p:graphicFrame>" +
                "<p:sp><p:txBody><a:p><a:r><a:rPr><a:hlinkClick r:id='rId5'/></a:rPr><a:t>SLIDE_TEXT</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>");
            Add(archive, "ppt/slides/_rels/slide1.xml.rels", $"<Relationships xmlns='{PackageRels}'>" +
                $"<Relationship Id='rId1' Type='{R}/image' Target='../media/image1.png'/>" +
                $"<Relationship Id='rId2' Type='{R}/image' Target='http://tracker.invalid/pixel.png' TargetMode='External'/>" +
                $"<Relationship Id='rId3' Type='{R}/video' Target='file:///C:/clip.mp4' TargetMode='External'/>" +
                $"<Relationship Id='rId4' Type='{R}/oleObject' Target='file:///C:/book.xlsx' TargetMode='External'/>" +
                $"<Relationship Id='rId5' Type='{R}/hyperlink' Target='https://example.com/kept' TargetMode='External'/>" +
                // An address that is not declared external must not be taken for a part either.
                $"<Relationship Id='rId6' Type='{R}/image' Target='http://tracker.invalid/undeclared.png'/></Relationships>");
            Add(archive, "ppt/media/image1.png", "PNG");
        }
        var copy = await CopyOf(path);
        AssertConsistentPackage(copy);
        var rels = Read(copy, "ppt/slides/_rels/slide1.xml.rels");
        Assert.DoesNotContain("tracker.invalid", rels);
        Assert.DoesNotContain("file:", rels);
        Assert.Contains("https://example.com/kept", rels);
        var slide = XDocument.Parse(Read(copy, "ppt/slides/slide1.xml"));
        XNamespace r = R, d = a;
        var removed = new[] { "rId2", "rId3", "rId4", "rId6" };
        Assert.DoesNotContain(slide.Descendants().Attributes(), attribute => attribute.Name.Namespace == r && removed.Contains(attribute.Value));
        Assert.Equal(new[] { "rId1" }, slide.Descendants(d + "blip").Select(e => (string)e.Attribute(r + "embed")!).ToArray());
        Assert.Empty(slide.Descendants(d + "videoFile"));
        Assert.Equal("rId5", (string)slide.Descendants(d + "hlinkClick").Single().Attribute(r + "id")!);
        Assert.Contains("SLIDE_TEXT", slide.ToString());
    }

    [Theory]
    [InlineData("docm")]
    [InlineData("xlsm")]
    [InlineData("pptm")]
    public async Task Macro_enabled_copy_declares_a_macro_free_package(string kind)
    {
        var (folder, main, macroType, plainType, rootElement) = kind switch
        {
            "docm" => ("word", "document.xml", "application/vnd.ms-word.document.macroEnabled.main+xml", WordMain,
                $"<w:document xmlns:w='{W}'><w:body><w:p><w:r><w:t>BODY</w:t></w:r></w:p></w:body></w:document>"),
            "xlsm" => ("xl", "workbook.xml", "application/vnd.ms-excel.sheet.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                $"<workbook xmlns='{S}'><sheets/></workbook>"),
            _ => ("ppt", "presentation.xml", "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                "<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main'/>"),
        };
        var path = Path.Combine(root, "macro." + kind);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "[Content_Types].xml", $"<Types xmlns='{ContentTypes}'>{Defaults}<Default Extension='bin' ContentType='application/vnd.ms-office.vbaProject'/>" +
                $"<Override PartName='/{folder}/{main}' ContentType='{macroType}'/>" +
                $"<Override PartName='/{folder}/vbaProject.bin' ContentType='application/vnd.ms-office.vbaProject'/>" +
                $"<Override PartName='/{folder}/vbaProjectSignature.bin' ContentType='application/vnd.ms-office.vbaProjectSignature'/>" +
                $"<Override PartName='/{folder}/vbaData.xml' ContentType='application/vnd.ms-word.vbaData+xml'/>" +
                $"<Override PartName='/{folder}/customizations.xml' ContentType='application/vnd.ms-word.keyMapCustomizations+xml'/>" +
                $"<Override PartName='/{folder}/attachedToolbars.bin' ContentType='application/vnd.ms-word.attachedToolbars'/></Types>");
            Add(archive, "_rels/.rels", $"<Relationships xmlns='{PackageRels}'><Relationship Id='rId1' Type='{R}/officeDocument' Target='{folder}/{main}'/></Relationships>");
            Add(archive, $"{folder}/{main}", rootElement);
            Add(archive, $"{folder}/_rels/{main}.rels", $"<Relationships xmlns='{PackageRels}'>" +
                $"<Relationship Id='rId1' Type='{Ms}/vbaProject' Target='vbaProject.bin'/>" +
                $"<Relationship Id='rId2' Type='{Ms}/keyMapCustomizations' Target='customizations.xml'/>" +
                $"<Relationship Id='rId3' Type='{Ms}/attachedToolbars' Target='attachedToolbars.bin'/></Relationships>");
            Add(archive, $"{folder}/vbaProject.bin", "VBA");
            Add(archive, $"{folder}/_rels/vbaProject.bin.rels", $"<Relationships xmlns='{PackageRels}'>" +
                $"<Relationship Id='rId1' Type='{Ms}/wordVbaData' Target='vbaData.xml'/>" +
                $"<Relationship Id='rId2' Type='{Ms}/vbaProjectSignature' Target='vbaProjectSignature.bin'/></Relationships>");
            Add(archive, $"{folder}/vbaProjectSignature.bin", "SIG");
            Add(archive, $"{folder}/vbaData.xml", "<wne:vbaSuppData xmlns:wne='http://schemas.microsoft.com/office/word/2006/wordml'><wne:mcds><wne:mcd wne:macroName='PROJECT.MODULE.AUTOOPEN'/></wne:mcds></wne:vbaSuppData>");
            Add(archive, $"{folder}/customizations.xml", "<wne:tcg xmlns:wne='http://schemas.microsoft.com/office/word/2006/wordml'><wne:keymaps><wne:keymap wne:kcmPrimary='0041'><wne:macro wne:macroName='PROJECT.MODULE.RUN'/></wne:keymap></wne:keymaps></wne:tcg>");
            Add(archive, $"{folder}/attachedToolbars.bin", "BAR");
        }
        var renderer = new CapturingRenderer();
        await renderer.RenderAsync(path);
        Assert.EndsWith("input." + kind[..3] + "x", renderer.InputPath);
        using var copy = new ZipArchive(new MemoryStream(renderer.Input!), ZipArchiveMode.Read);
        Assert.Equal(new[] { "[Content_Types].xml", "_rels/.rels", $"{folder}/_rels/{main}.rels", $"{folder}/{main}" }, copy.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
        AssertConsistentPackage(copy);
        var types = XDocument.Parse(Read(copy, "[Content_Types].xml"));
        XNamespace t = ContentTypes;
        var over = Assert.Single(types.Descendants(t + "Override"));
        Assert.Equal($"/{folder}/{main}", (string)over.Attribute("PartName")!);
        Assert.Equal(plainType, (string)over.Attribute("ContentType")!);
        Assert.DoesNotContain("macro", types.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vba", types.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(XDocument.Parse(Read(copy, $"{folder}/_rels/{main}.rels")).Root!.Elements());
    }

    [Fact]
    public async Task Private_profile_never_updates_links_or_runs_active_content()
    {
        var renderer = new CapturingRenderer();
        await renderer.RenderAsync(Docx("profile.docx", ""));
        var registry = XDocument.Parse(renderer.Registry!.TrimStart());
        XNamespace oor = "http://openoffice.org/2001/registry";
        string? Value(string path, string name) => registry.Root!.Elements("item").Where(i => (string)i.Attribute(oor + "path")! == path)
            .Elements("prop").Where(p => (string)p.Attribute(oor + "name")! == name).Select(p => p.Element("value")!.Value).SingleOrDefault();
        Assert.Equal("3", Value("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "DisableActiveContent"));
        Assert.Equal("false", Value("/org.openoffice.Office.Common/Misc", "FirstRun"));
        Assert.Equal("false", Value("/org.openoffice.Office.Common/Misc", "CrashReport"));
        // "Never" is 2 in Writer and 1 in Calc.
        Assert.Equal("2", Value("/org.openoffice.Office.Writer/Content/Update", "Link"));
        Assert.Equal("1", Value("/org.openoffice.Office.Calc/Content/Update", "Link"));
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("word/../../outside.xml")]
    [InlineData("word\\..\\..\\outside.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("C:/absolute.xml")]
    [InlineData("word/document.xml")] // a second entry with the name of an existing part
    [InlineData("WORD/Document.xml")]
    public async Task Unsafe_or_duplicate_entry_names_are_refused_before_the_engine_starts(string name)
    {
        var path = Docx("names.docx", "", (name, "<x/>"));
        var renderer = new CapturingRenderer();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(path));
        Assert.Contains("entry name", failure.Message);
        Assert.Null(renderer.Input);
    }

    [Fact]
    public async Task Private_copy_never_holds_more_than_an_entry_declares()
    {
        // The limits on declared sizes hold only while an entry cannot decompress past its declared size: the copy is
        // either refused or cut at that size. (The runtime's archive reader cuts it; the copy also counts the bytes itself.)
        var path = Path.Combine(root, "bomb.docx");
        File.WriteAllBytes(path, UnderstatedPackage(1, 40));
        var renderer = new CapturingRenderer();
        try { await renderer.RenderAsync(path); }
        catch (InvalidDataException failure) { Assert.Contains("decompression budget", failure.Message); Assert.Null(renderer.Input); return; }
        using var copy = new ZipArchive(new MemoryStream(renderer.Input!), ZipArchiveMode.Read);
        using var media = copy.GetEntry("word/media/big0.bin")!.Open();
        var length = 0L;
        var buffer = new byte[81920];
        for (int count; (count = media.Read(buffer, 0, buffer.Length)) > 0;) length += count;
        Assert.Equal(1_000_000, length);
    }

    [Fact]
    public async Task Auto_layout_keeps_the_structured_document_when_the_private_copy_is_refused()
    {
        // The conversion's own package check accepts part names that differ only in case; they are one part name to Office.
        var renderer = new CapturingRenderer();
        var service = new DocumentService(null, null, false, renderer);
        var result = await service.ExportReadableAsync(new(Docx("refused.docx", "", ("word/media/twice.bin", "A"), ("word/media/Twice.bin", "B")), Path.Combine(root, "refused.html"),
            OutputFormat: ReadableOutputFormat.Html, Language: "en", OfficeLayout: OfficeRenderingMode.Auto));
        var failed = Assert.Single(result.Diagnostics, d => d.Code == "OfficeSourcePagesFailed");
        Assert.Contains("entry name", failed.Message);
        Assert.Null(renderer.Input);
        var html = File.ReadAllText(result.MarkdownPath);
        Assert.Contains("OFFICE_BODY_TEXT", html);
        Assert.DoesNotContain("data-office-page=", html);
    }

    // Every relationship names a part that is there (or an external hyperlink), and every override a part that is there.
    private static void AssertConsistentPackage(ZipArchive copy)
    {
        var names = copy.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        XNamespace t = ContentTypes, rel = PackageRels;
        foreach (var over in XDocument.Parse(Read(copy, "[Content_Types].xml")).Descendants(t + "Override"))
            Assert.Contains(((string)over.Attribute("PartName")!).TrimStart('/'), names);
        foreach (var entry in copy.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = entry.FullName[..entry.FullName.LastIndexOf("_rels/", StringComparison.Ordinal)];
            var owner = folder + Path.GetFileNameWithoutExtension(entry.FullName);
            if (owner.Length > 0) Assert.Contains(owner, names);
            foreach (var relationship in XDocument.Parse(Read(copy, entry.FullName)).Descendants(rel + "Relationship"))
            {
                if ((string?)relationship.Attribute("TargetMode") == "External")
                { Assert.EndsWith("/hyperlink", (string)relationship.Attribute("Type")!); continue; }
                var target = new Uri(new Uri("http://package.invalid/" + owner), (string)relationship.Attribute("Target")!);
                Assert.Equal("package.invalid", target.Host);
                Assert.Contains(Uri.UnescapeDataString(target.AbsolutePath).TrimStart('/'), names);
            }
        }
    }

    private async Task<ZipArchive> CopyOf(string path)
    {
        var renderer = new CapturingRenderer();
        await renderer.RenderAsync(path);
        return new ZipArchive(new MemoryStream(renderer.Input!), ZipArchiveMode.Read);
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader((archive.GetEntry(name) ?? throw new InvalidDataException("Missing entry " + name)).Open());
        return reader.ReadToEnd();
    }

    private string Docx(string name, string bodyXml, params (string Name, string Content)[] parts)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddDocxParts(archive, bodyXml);
        foreach (var part in parts) Add(archive, part.Name, part.Content);
        return path;
    }

    private static void AddDocxParts(ZipArchive archive, string bodyXml)
    {
        Add(archive, "[Content_Types].xml", $"<Types xmlns='{ContentTypes}'>{Defaults}<Default Extension='bin' ContentType='application/octet-stream'/><Override PartName='/word/document.xml' ContentType='{WordMain}'/></Types>");
        Add(archive, "_rels/.rels", $"<Relationships xmlns='{PackageRels}'><Relationship Id='r1' Type='{R}/officeDocument' Target='word/document.xml'/></Relationships>");
        Add(archive, "word/document.xml", $"<w:document xmlns:w='{W}'><w:body><w:p><w:r><w:t>OFFICE_BODY_TEXT</w:t></w:r></w:p>" +
            bodyXml + "<w:sectPr><w:pgSz w:w='12240' w:h='15840'/></w:sectPr></w:body></w:document>");
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // A package whose media entries declare 1 MB each, in the local header and in the central directory, but hold more.
    private static byte[] UnderstatedPackage(int entries, int mebibytes)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddDocxParts(archive, "");
            var block = new byte[1024 * 1024];
            for (var i = 0; i < entries; i++)
            {
                using var stream = archive.CreateEntry($"word/media/big{i}.bin", CompressionLevel.Optimal).Open();
                for (var written = 0; written < mebibytes; written++) stream.Write(block);
            }
        }
        var bytes = buffer.ToArray();
        var patched = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'P' || bytes[i + 1] != (byte)'K') continue;
            var (sizeOffset, lengthOffset, nameOffset) = (bytes[i + 2], bytes[i + 3]) switch { (3, 4) => (22, 26, 30), (1, 2) => (24, 28, 46), _ => (0, 0, 0) };
            if (sizeOffset == 0) continue;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + lengthOffset));
            if (i + nameOffset + length > bytes.Length || !Encoding.ASCII.GetString(bytes, i + nameOffset, length).StartsWith("word/media/big", StringComparison.Ordinal)) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + sizeOffset), 1_000_000);
            patched++;
        }
        Assert.Equal(entries * 2, patched);
        return bytes;
    }

    // A structurally complete one-page PDF, so that a render with this fake engine succeeds.
    private static byte[] BuildPdf()
    {
        const string stream = "BT /F1 12 Tf 72 720 Td (Hello) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [5 0 R] /Count 1 >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 3 0 R >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>Stands in for the engine process: keeps the private copy and the profile it was started with.</summary>
    private sealed class CapturingRenderer() : LibreOfficePageRenderer("ignored", new FakeRasterizer())
    {
        protected override async Task<int> RunProcessAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            InputPath = info.ArgumentList[^1];
            Input = await File.ReadAllBytesAsync(InputPath, cancellationToken);
            var profile = new Uri(info.ArgumentList.First(a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal))["-env:UserInstallation=".Length..]).LocalPath;
            Registry = await File.ReadAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(info.WorkingDirectory, "input.pdf"), BuildPdf(), cancellationToken);
            return 0;
        }
        public byte[]? Input { get; private set; }
        public string? InputPath { get; private set; }
        public string? Registry { get; private set; }
    }

    private sealed class FakeRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("fake-rasterizer", new Version(1, 0), 1,
            new HashSet<string>(StringComparer.Ordinal) { "pdf-rasterize" }, "MIT", string.Empty, false);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath,
            IReadOnlyList<int> pageNumbers, PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            var png = PngRasterImage.Encode(8, 8, new byte[8 * 8 * 3]);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(
                pageNumbers.Select(n => new RasterizedPdfPage(n, "image/png", png, 8, 8)).ToList());
        }
    }
}

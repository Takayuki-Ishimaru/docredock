using System.IO.Compression;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;

namespace DocRedock.Tests.Docx;

/// <summary>
/// Japanese Word stores list and code semantics in style <em>names</em> (List Number, コード) and in
/// style-supplied numbering, while the id a paragraph applies is opaque (a4, 21). Code is also often
/// only typed in a monospace face. These regressions keep that projection, and the round trip.
/// </summary>
public sealed class DocxLocalizedStyleRegressionTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Numbering =
        "<w:numbering xmlns:w=\"" + W + "\"><w:abstractNum w:abstractNumId=\"0\">" +
        "<w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl>" +
        "<w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"lowerLetter\"/></w:lvl>" +
        "<w:lvl w:ilvl=\"2\"><w:numFmt w:val=\"bullet\"/></w:lvl>" +
        "</w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";

    [Fact]
    public async Task OpaqueStyleIdNamedListNumberProjectsNumberedItems()
    {
        var source = await WriteAsync(
            P("First", "a4") + P("Second", "a4"),
            Style("a1", null) + Style("a4", "List Number", pPr: "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.ListItem, Node(result, "First").Kind);
            Assert.Equal(0, Node(result, "First").Extensions!["list_level"].GetInt32());
            Assert.Equal("ordered", Node(result, "First").Extensions!["list_format"].GetString());
            Assert.Equal(1, Node(result, "First").Extensions!["list_number"].GetInt32());
            Assert.Equal(2, Node(result, "Second").Extensions!["list_number"].GetInt32());
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task BasedOnChainSuppliesTheListNameAndItsLevel()
    {
        var source = await WriteAsync(
            P("Nested", "a4"),
            Style("a1", null) + Style("a3", "List Bullet 2") + Style("a4", null, basedOn: "a3"));
        try
        {
            var result = await ExtractAsync(source);
            var node = Node(result, "Nested");
            Assert.Equal(NodeKind.ListItem, node.Kind);
            Assert.Equal(1, node.Extensions!["list_level"].GetInt32());
            Assert.False(node.Extensions.ContainsKey("list_format"));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task InheritedStyleNumberingCarriesTheLevelAndFormat()
    {
        var source = await WriteAsync(
            P("Sub step", "a9") + P("Bullet step", "a8"),
            Style("a1", null) +
            Style("a9", null, pPr: "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>") +
            Style("a8", null, pPr: "<w:numPr><w:ilvl w:val=\"2\"/><w:numId w:val=\"1\"/></w:numPr>"));
        try
        {
            var result = await ExtractAsync(source);
            var ordered = Node(result, "Sub step");
            Assert.Equal(NodeKind.ListItem, ordered.Kind);
            Assert.Equal(1, ordered.Extensions!["list_level"].GetInt32());
            Assert.Equal("ordered", ordered.Extensions["list_format"].GetString());
            var bullet = Node(result, "Bullet step");
            Assert.Equal(NodeKind.ListItem, bullet.Kind);
            Assert.Equal(2, bullet.Extensions!["list_level"].GetInt32());
            Assert.False(bullet.Extensions.ContainsKey("list_format"));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task DirectNumberingStillWinsOverTheStyleChain()
    {
        var source = await WriteAsync(
            Direct("<w:numPr><w:ilvl w:val=\"2\"/><w:numId w:val=\"1\"/></w:numPr>", "a9"),
            Style("a9", null, pPr: "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        try
        {
            var result = await ExtractAsync(source);
            var node = Node(result, "Direct");
            Assert.Equal(NodeKind.ListItem, node.Kind);
            Assert.Equal(2, node.Extensions!["list_level"].GetInt32());
            Assert.False(node.Extensions.ContainsKey("list_format"));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task JapaneseCodeStyleNameBehindAnOpaqueIdProjectsCode()
    {
        var source = await WriteAsync(
            P("Dim x As Integer", "a7"),
            Style("a1", null) + Style("a7", "コード", properties: "<w:rFonts w:ascii=\"ＭＳ ゴシック\" w:eastAsia=\"ＭＳ ゴシック\"/>"));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "Dim x As Integer").Kind);
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData("Consolas")]
    [InlineData("ＭＳ ゴシック")]
    [InlineData("MS Gothic")]
    [InlineData("BIZ UDゴシック")]
    [InlineData("Osaka－等幅")]
    [InlineData("Noto Sans Mono CJK")]
    public async Task ContiguousMonospaceNormalParagraphsProjectOneCodeBlock(string font)
    {
        var source = await WriteAsync(
            P("Prose before") +
            Code("if (a) {", font) + Code("    return 1;", font) + Blank() + Code("}", font) +
            P("Prose after"));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.Paragraph, Node(result, "Prose before").Kind);
            Assert.Equal(NodeKind.Paragraph, Node(result, "Prose after").Kind);
            var first = Node(result, "if (a) {");
            var blank = Node(result, string.Empty);
            Assert.Equal(NodeKind.CodeBlock, first.Kind);
            Assert.Equal(NodeKind.CodeBlock, blank.Kind);
            Assert.Equal(first.Extensions!["code_block_group"].GetInt32(), Node(result, "}").Extensions!["code_block_group"].GetInt32());
            Assert.Equal("monospace-runs", first.Extensions["code_block_source"].GetString());
            Assert.Equal(0, first.Extensions["code_block_line"].GetInt32());
            Assert.Equal(1, Node(result, "    return 1;").Extensions!["code_block_line"].GetInt32());
            Assert.Equal(2, blank.Extensions!["code_block_line"].GetInt32());
            Assert.Equal(3, Node(result, "}").Extensions!["code_block_line"].GetInt32());
            // Indentation and the blank line survive in the projected text itself.
            Assert.Equal("    return 1;", Text(Node(result, "    return 1;")));
            Assert.Equal(string.Empty, Text(blank));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task IsolatedOrMixedMonospaceStaysProse()
    {
        var source = await WriteAsync(
            P("Ordinary sentence") +
            Code("isolated()", "Consolas") +
            Mixed("Call " + new string(' ', 10) + "now") +
            P("Another ordinary sentence"));
        try
        {
            var result = await ExtractAsync(source);
            foreach (var text in new[] { "Ordinary sentence", "isolated()", "Call " + new string(' ', 10) + "now in prose", "Another ordinary sentence" })
                Assert.Equal(NodeKind.Paragraph, Node(result, text).Kind);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task CyclicBasedOnChainStopsAndStillProjectsTheParagraph()
    {
        var source = await WriteAsync(
            P("Looping", "a4") + Code("first", "Consolas") + Code("second", "Consolas"),
            Style("a1", null, basedOn: "a4") + Style("a4", "List Bullet 2", basedOn: "a1"));
        try
        {
            var result = await ExtractAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NodeKind.ListItem, Node(result, "Looping").Kind);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "first").Kind);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task MonospaceParagraphCarryingATrackedRevisionStaysProseAndKeepsItsRevision()
    {
        var source = await WriteAsync(
            "<w:p><w:pPr><w:rPr><w:ins w:id=\"1\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"/></w:rPr></w:pPr>" +
            "<w:ins w:id=\"2\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:rPr><w:rFonts w:ascii=\"Consolas\"/></w:rPr><w:t>inserted()</w:t></w:r></w:ins></w:p>" +
            Code("plain()", "Consolas"));
        try
        {
            var result = await ExtractAsync(source);
            var revised = Node(result, "inserted()");
            Assert.Equal(NodeKind.Paragraph, revised.Kind);
            Assert.True(revised.Extensions!.ContainsKey("revision_changes"));
            Assert.Equal(NodeKind.Paragraph, Node(result, "plain()").Kind);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task LocalizedListsAndCodeRoundTripByteForByte()
    {
        var source = await WriteAsync(
            P("First", "a4") + P("Plain prose") + Code("keep()", "Consolas") + Code("keep2()", "Consolas"),
            Style("a4", "List Number", pPr: "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        try
        {
            var original = await File.ReadAllBytesAsync(source);
            var adapter = new DocxAdapter();
            var extraction = await adapter.ExtractAsync(source);
            var restored = Path.Combine(Path.GetDirectoryName(source)!, "restored.docx");
            Assert.True((await adapter.RestoreAsync(extraction, extraction.Graph, restored)).Succeeded);
            Assert.Equal(original, await File.ReadAllBytesAsync(restored));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task NumberingPropertiesInheritIndependentlyThroughStylesAndDirectFormatting()
    {
        var source = await WriteAsync(Direct("<w:numPr><w:numId w:val=\"1\"/></w:numPr>", "child"),
            Style("base", "List Number", pPr: "<w:numPr><w:numId w:val=\"1\"/></w:numPr>") +
            Style("child", "Nested custom", "base", pPr: "<w:numPr><w:ilvl w:val=\"1\"/></w:numPr>"));
        try
        {
            var node = Node(await ExtractAsync(source), "Direct");
            Assert.Equal(NodeKind.ListItem, node.Kind);
            Assert.Equal(1, node.Extensions!["list_level"].GetInt32());
            Assert.Equal("ordered", node.Extensions["list_format"].GetString());
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZeroNumIdCancelsInheritedListSemantics(bool direct)
    {
        var cancel = "<w:numPr><w:numId w:val=\"0\"/></w:numPr>";
        var source = await WriteAsync(direct ? Direct(cancel, "base") : P("Direct", "child"),
            Style("base", "List Number", pPr: "<w:numPr><w:numId w:val=\"1\"/></w:numPr>") +
            Style("child", "Custom", "base", pPr: cancel));
        try { Assert.Equal(NodeKind.Paragraph, Node(await ExtractAsync(source), "Direct").Kind); }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task NameOnlyNumberedListUsesOrderedCountersAndInheritedNesting()
    {
        var source = await WriteAsync(P("First", "child") + P("Second", "child"),
            Style("base", "List Number 2") + Style("child", "Custom", "base"));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(1, Node(result, "First").Extensions!["list_level"].GetInt32());
            Assert.Equal("ordered", Node(result, "First").Extensions!["list_format"].GetString());
            Assert.Equal(2, Node(result, "Second").Extensions!["list_number"].GetInt32());
        }
        finally { Cleanup(source); }
    }

    [Theory]
    [InlineData("ＭＳ Ｐゴシック")]
    [InlineData("MS PGothic")]
    [InlineData("BIZ UDPゴシック")]
    [InlineData("BIZ UDPGothic")]
    public async Task ProportionalJapaneseFacesDoNotCreateCode(string font)
    {
        var source = await WriteAsync(Code("alpha()", font) + Code("beta()", font));
        try { Assert.DoesNotContain((await ExtractAsync(source)).Graph.Nodes, node => node.Kind == NodeKind.CodeBlock); }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task MixedFontSlotsAndProseBreakAnOtherwiseMonospaceRun()
    {
        var mixed = "<w:p><w:r><w:rPr><w:rFonts w:ascii=\"Consolas\" w:eastAsia=\"Yu Gothic\"/></w:rPr><w:t>mixed()</w:t></w:r></w:p>";
        var source = await WriteAsync(Code("alpha()", "Consolas") + mixed + Code("beta()", "Consolas") + P("Plain prose") + Code("gamma()", "Consolas"));
        try { Assert.DoesNotContain((await ExtractAsync(source)).Graph.Nodes, node => node.Kind == NodeKind.CodeBlock); }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task ParagraphStyleInheritsMonospaceFontButJapaneseBodyProseIsNotCode()
    {
        var source = await WriteAsync(P("alpha()", "derived") + P("  beta()", "derived") + P("Boundary") +
            P("通常の日本語本文です。", "Normal") + P("次の説明段落です。", "Normal"),
            Style("mono", "FixedFace", properties: Font("Consolas")) + Style("derived", "Custom", "mono") +
            Style("Normal", "Normal", properties: Font("ＭＳ ゴシック")));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "alpha()").Kind);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "  beta()").Kind);
            Assert.Equal(NodeKind.Paragraph, Node(result, "通常の日本語本文です。").Kind);
            Assert.Equal(NodeKind.Paragraph, Node(result, "次の説明段落です。").Kind);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task HiddenParagraphIsABarrierAndDoesNotCreateAnEmptyVisibleCodeLine()
    {
        var hidden = "<w:p><w:r><w:rPr><w:vanish/>" + Font("Consolas") + "</w:rPr><w:t>SECRET</w:t></w:r></w:p>";
        var source = await WriteAsync(Code("first()", "Consolas") + hidden + Code("last()", "Consolas"));
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.Paragraph, Node(result, "first()").Kind);
            Assert.Equal(NodeKind.Paragraph, Node(result, "last()").Kind);
            Assert.DoesNotContain("SECRET", new DocRedock.Markdown.ReadableMarkdownSerializer().Serialize(result.Graph));
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task MonospaceYamlExportsOneFenceWithIndentationAndInternalBlankLines()
    {
        var source = await WriteAsync(P("Prose before") + Code("root:", "Consolas") + Code("  value: 1", "Consolas") +
            Blank() + Code("  enabled: true", "Consolas") + P("Prose after"));
        try
        {
            var markdown = new DocRedock.Markdown.ReadableMarkdownSerializer().Serialize((await ExtractAsync(source)).Graph);
            Assert.Contains("```\nroot:\n  value: 1\n\n  enabled: true\n```", markdown, StringComparison.Ordinal);
            Assert.Equal(2, markdown.Split("```", StringSplitOptions.None).Length - 1);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task DefaultJapaneseFontDoesNotAbsorbProseBesideCode()
    {
        var normal = Style("Normal", "Normal", properties: Font("ＭＳ ゴシック"))
            .Replace("w:type=\"paragraph\"", "w:type=\"paragraph\" w:default=\"1\"", StringComparison.Ordinal);
        var source = await WriteAsync(P("通常の本文です。") + P("root:") + P("  value: 1") + P("説明が続きます。"), normal);
        try
        {
            var result = await ExtractAsync(source);
            Assert.Equal(NodeKind.Paragraph, Node(result, "通常の本文です。").Kind);
            Assert.Equal(NodeKind.Paragraph, Node(result, "説明が続きます。").Kind);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "root:").Kind);
            Assert.Equal(NodeKind.CodeBlock, Node(result, "  value: 1").Kind);
        }
        finally { Cleanup(source); }
    }

    [Fact]
    public async Task DirectJapaneseBodyFontDoesNotTurnProseIntoInlineCode()
    {
        var source = await WriteAsync(Code("通常の本文です。", "ＭＳ ゴシック") + Code("説明が続きます。", "ＭＳ ゴシック"), string.Empty);
        try
        {
            var result = await ExtractAsync(source);
            Assert.All(result.Graph.Nodes.Where(node => Text(node).Length > 0), node => Assert.Equal(NodeKind.Paragraph, node.Kind));
            var markdown = new DocRedock.Markdown.ReadableMarkdownSerializer().Serialize(result.Graph);
            Assert.Contains("通常の本文です。", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("`", markdown, StringComparison.Ordinal);
        }
        finally { Cleanup(source); }
    }

    private static async Task<DocxExtractionResult> ExtractAsync(string source)
    {
        var result = await new DocxAdapter().ExtractAsync(source, new(ShowTrackedRevisions: true));
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        return result;
    }

    private static DocumentNode Node(DocxExtractionResult result, string text) =>
        Assert.Single(result.Graph.Nodes, node => Text(node) == text);

    private static string Text(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => string.Empty
    };

    private static string P(string text, string? style = null) =>
        "<w:p>" + (style is null ? string.Empty : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") +
        $"<w:r><w:t>{text}</w:t></w:r></w:p>";

    private static string Mixed(string text) =>
        "<w:p><w:r><w:rPr>" + Font("Consolas") + "</w:rPr><w:t xml:space=\"preserve\">" + text + "</w:t></w:r>" +
        "<w:r><w:rPr>" + Font("Yu Gothic") + "</w:rPr><w:t xml:space=\"preserve\"> in prose</w:t></w:r></w:p>";

    private static string Direct(string numbering, string style) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{numbering}</w:pPr><w:r><w:t>Direct</w:t></w:r></w:p>";

    private static string Code(string text, string font) =>
        "<w:p><w:r><w:rPr>" + Font(font) + "</w:rPr>" +
        $"<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string Blank() => "<w:p><w:pPr><w:rPr>" + Font("Consolas") + "</w:rPr></w:pPr></w:p>";

    private static string Font(string font) =>
        $"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:eastAsia=\"{font}\" w:cs=\"{font}\"/>";

    private static string Style(string id, string? name, string? basedOn = null, string properties = "", string pPr = "")
    {
        var builder = new StringBuilder("<w:style w:type=\"paragraph\" w:styleId=\"" + id + "\">");
        if (name is not null) builder.Append("<w:name w:val=\"" + name + "\"/>");
        if (basedOn is not null) builder.Append("<w:basedOn w:val=\"" + basedOn + "\"/>");
        if (pPr.Length > 0) builder.Append("<w:pPr>" + pPr + "</w:pPr>");
        if (properties.Length > 0) builder.Append("<w:rPr>" + properties + "</w:rPr>");
        return builder.Append("</w:style>").ToString();
    }

    private static async Task<string> WriteAsync(string body, string styles = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), "docredock-localized-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.docx");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        await WriteEntryAsync(zip, "[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        await WriteEntryAsync(zip, "_rels/.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"doc\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        await WriteEntryAsync(zip, "word/document.xml",
            $"<w:document xmlns:w=\"{W}\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\"><w:body>{body}<w:sectPr/></w:body></w:document>");
        await WriteEntryAsync(zip, "word/styles.xml", $"<w:styles xmlns:w=\"{W}\">{styles}</w:styles>");
        await WriteEntryAsync(zip, "word/numbering.xml", Numbering);
        return path;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }

    private static void Cleanup(string source) => Directory.Delete(Path.GetDirectoryName(source)!, true);
}

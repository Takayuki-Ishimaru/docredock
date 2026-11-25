using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRedock.Api;

namespace DocRedock.Tests.Api;

public sealed class WorkbookReviewExportTests
{
    [Fact]
    public async Task ExportWorkbookReviewAsync_WritesSheetsChangeLogManifests_AndKeepsSourceUnchanged()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "book.xlsx");
        var historyRows = string.Join(string.Empty, Enumerable.Range(1, 40)
            .Select(i => $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\" t=\"inlineStr\"><is><t>H{i}</t></is></c>"
                + $"<c r=\"B{i + 1}\" t=\"inlineStr\"><is><t>note-{i}</t></is></c></row>"));
        WriteWorkbook(source,
            ("Overview", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Overview text</t></is></c></row>"),
            ("A|B", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>pipe</t></is></c></row>"),
            ("A_B", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>under</t></is></c></row>"),
            ("データ一覧", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>日本語値</t></is></c></row>"),
            ("変更履歴", historyRows));
        var before = Hash(source);
        var target = Path.Combine(root, "out");
        var service = new DocumentService();
        var result = await service.ExportWorkbookReviewAsync(new WorkbookReviewExportOptions(
            new ReadableDocumentExportOptions(source, Path.Combine(root, "unused.md")), target));

        Assert.Equal(target, result.DirectoryPath);
        Assert.True(Directory.Exists(target));
        var sheetFiles = Directory.GetFiles(Path.Combine(target, "sheets")).Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(5, sheetFiles.Length);
        Assert.Contains(sheetFiles, name => name!.StartsWith("001_") && name.EndsWith(".md"));
        Assert.Contains("004_データ一覧.md", sheetFiles);
        // "A|B" and "A_B" must not collide: distinct numbered files, invalid characters removed.
        Assert.DoesNotContain("|", string.Concat(sheetFiles));
        Assert.Equal(5, sheetFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var overview = File.ReadAllText(Path.Combine(target, "sheets", sheetFiles[0]!));
        Assert.Contains("book.xlsx", overview);
        Assert.Contains("Overview text", overview);
        Assert.Contains("../CHANGELOG.md", overview);
        Assert.Contains("日本語値", File.ReadAllText(Path.Combine(target, "sheets", "004_データ一覧.md")));
        var changeLog = File.ReadAllText(Path.Combine(target, "CHANGELOG.md"));
        for (var i = 1; i <= 40; i++) Assert.Contains($"H{i}", changeLog);
        Assert.Contains("note-40", changeLog);
        var readme = File.ReadAllText(Path.Combine(target, "README.md"));
        Assert.Contains("layout.html", readme);
        foreach (var link in Directory.GetFiles(Path.Combine(target, "sheets")).Select(Path.GetFileName)!)
            Assert.Contains(link!, readme);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")));
        Assert.Equal("book.xlsx", manifest.RootElement.GetProperty("sourceFile").GetString());
        Assert.Equal(before, manifest.RootElement.GetProperty("sourceSha256").GetString());
        Assert.Equal(5, manifest.RootElement.GetProperty("sheets").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(target, "source-index.json")));
        Assert.Contains("not proof", File.ReadAllText(Path.Combine(target, "conversion-report.json")));
        Assert.Contains("<html", File.ReadAllText(Path.Combine(target, "layout.html")));
        Assert.Equal(before, Hash(source));
        Assert.Empty(Directory.GetDirectories(root).Except(new[] { target, Path.Combine(root, "sheets") }));
    }

    [Fact]
    public async Task ExportWorkbookReviewAsync_PortableRecords_KeepsEveryColumnValue()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "wide.xlsx");
        var cells = string.Join(string.Empty, Enumerable.Range(0, 17)
            .Select(i => $"<c r=\"{Column(i)}1\" t=\"inlineStr\"><is><t>V{i}</t></is></c>"));
        WriteWorkbook(source, ("Wide", $"<row r=\"1\">{cells}</row>"));
        var target = Path.Combine(root, "out");
        var service = new DocumentService();
        await service.ExportWorkbookReviewAsync(new WorkbookReviewExportOptions(
            new ReadableDocumentExportOptions(source, Path.Combine(root, "unused.md")), target));
        var markdown = File.ReadAllText(Directory.GetFiles(Path.Combine(target, "sheets")).Single());
        for (var i = 0; i < 17; i++) Assert.Contains($"V{i}", markdown);
        Assert.DoesNotContain("<table", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportWorkbookReviewAsync_ExistingDestination_RefusesAndLeavesItUntouched()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "book.xlsx");
        WriteWorkbook(source, ("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>x</t></is></c></row>"));
        var target = Path.Combine(root, "out");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var service = new DocumentService();
        await Assert.ThrowsAnyAsync<IOException>(() => service.ExportWorkbookReviewAsync(
            new WorkbookReviewExportOptions(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "unused.md")), target)));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(target).Select(Path.GetFileName));
        Assert.DoesNotContain(Directory.GetDirectories(root), name => Path.GetFileName(name).Contains(".tmp"));
    }

    [Fact]
    public async Task ExportWorkbookReviewAsync_PreCancelled_LeavesNoOutput()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "book.xlsx");
        WriteWorkbook(source, ("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>x</t></is></c></row>"));
        var target = Path.Combine(root, "out");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var service = new DocumentService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportWorkbookReviewAsync(
            new WorkbookReviewExportOptions(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "unused.md")), target), cancel.Token));
        Assert.False(Directory.Exists(target));
        Assert.DoesNotContain(Directory.GetDirectories(root), name => Path.GetFileName(name).Contains(".tmp"));
    }

    [Fact]
    public async Task ExportWorkbookReviewAsync_Concurrent_ExactlyOneWinner()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "book.xlsx");
        WriteWorkbook(source, ("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>x</t></is></c></row>"));
        var target = Path.Combine(root, "out");
        var service = new DocumentService();
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() => service.ExportWorkbookReviewAsync(
            new WorkbookReviewExportOptions(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "unused.md")), target)))).ToArray();
        var results = await Task.WhenAll(attempts.Select(async attempt =>
        {
            try { await attempt; return "ok"; }
            catch (IOException) { return "refused"; }
        }));
        Assert.Equal(1, results.Count(result => result == "ok"));
        Assert.Equal(1, results.Count(result => result == "refused"));
        Assert.True(Directory.Exists(target));
    }

    private static string Column(int index) => ((char)('A' + index)).ToString();

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteWorkbook(string path, params (string Name, string RowsXml)[] sheets)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        void Write(string entryPath, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Write("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            </Types>
            """);
        Write("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        var sheetElements = string.Join(string.Empty, sheets.Select((sheet, index) =>
            $"<sheet name=\"{sheet.Name}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"));
        Write("xl/workbook.xml", $"""
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/spreadsheetml/2006/relationships">
              <sheets>{sheetElements}</sheets>
            </workbook>
            """);
        var relationships = string.Join(string.Empty, sheets.Select((_, index) =>
            $"<Relationship Id=\"rId{index + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>"));
        Write("xl/_rels/workbook.xml.rels", $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}</Relationships>
            """);
        for (var index = 0; index < sheets.Length; index++)
            Write($"xl/worksheets/sheet{index + 1}.xml", $"""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  {sheets[index].RowsXml}
                </sheetData></worksheet>
                """);
    }
}

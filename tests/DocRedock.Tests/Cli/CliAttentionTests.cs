using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Cli;

namespace DocRedock.Tests.Cli;

/// <summary>Regressions for the 2026-11-07 evaluation of v0.3.10: automation had to combine the exit code, the warnings
/// and the review flag itself, a batch hid a later input's review stop behind an earlier input's warnings, a missing
/// cached formula value came under a generic code, and nothing in the CLI said that a large worksheet table can be cut
/// between rows.</summary>
public sealed class CliAttentionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("docredock-cli-attention-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static CliApplication App(StringWriter output, StringWriter? error = null) =>
        new(output, error ?? new StringWriter(), new DocumentService(null, null, discoverPdfRasterizer: false));

    [Fact]
    public async Task A_review_stop_of_a_later_input_is_the_batch_exit_code()
    {
        var warning = Workbook("a-warning.xlsx", """<row r="1"><c r="A1"><f>SUM(1,1)</f></c></row>""");
        var revised = Document("b-revised.docx", "<w:p><w:r><w:t>Before </w:t></w:r><w:ins><w:r><w:t>new</w:t></w:r></w:ins></w:p>");

        foreach (var inputs in new[] { new[] { warning, revised }, new[] { revised, warning } })
        {
            var target = Path.Combine(root, "out-" + Path.GetFileNameWithoutExtension(inputs[0]));
            var exit = await App(new StringWriter()).RunAsync(
                ["export", .. inputs, "--output-dir", target, "--ocr", "off", "--fail-on-review"]);
            Assert.Equal((int)ExitCode.ReviewRequired, exit);
        }
    }

    [Fact]
    public async Task Json_results_say_whether_and_why_a_person_is_needed()
    {
        var clean = Document("clean.docx", "<w:p><w:r><w:t>Plain text</w:t></w:r></w:p>");
        var warning = Workbook("cacheless.xlsx", """<row r="1"><c r="A1"><f>SUM(1,1)</f></c></row>""");
        var revised = Document("revised.docx", "<w:p><w:r><w:t>Before </w:t></w:r><w:ins><w:r><w:t>new</w:t></w:r></w:ins></w:p>");
        var output = new StringWriter();

        await App(output).RunAsync(["export", clean, warning, revised, "--output-dir", Path.Combine(root, "out"), "--json", "--ocr", "off"]);

        using var json = JsonDocument.Parse(output.ToString());
        var results = json.RootElement.GetProperty("results").EnumerateArray()
            .ToDictionary(item => Path.GetFileName(item.GetProperty("input").GetString()!), item => item.Clone());
        Assert.False(results["clean.docx"].GetProperty("needs_attention").GetBoolean());
        Assert.Empty(results["clean.docx"].GetProperty("attention").EnumerateArray());

        var cacheless = results["cacheless.xlsx"];
        Assert.True(cacheless.GetProperty("needs_attention").GetBoolean());
        Assert.Equal(["warnings"], cacheless.GetProperty("attention").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(cacheless.GetProperty("diagnostics").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == "XlsxFormulaCachedValueMissing");

        var attention = results["revised.docx"].GetProperty("attention").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("revision_sharing", attention);
        Assert.True(results["revised.docx"].GetProperty("review_required").GetBoolean());
    }

    [Fact]
    public async Task A_failed_input_is_reported_as_failed_attention()
    {
        var output = new StringWriter();
        var broken = Path.Combine(root, "broken.docx");
        await File.WriteAllTextAsync(broken, "not a zip");

        await App(output).RunAsync(["export", broken, "--output", Path.Combine(root, "broken.md"), "--json", "--ocr", "off"]);

        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("results")[0];
        Assert.True(result.GetProperty("needs_attention").GetBoolean());
        Assert.Equal(["failed"], result.GetProperty("attention").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task A_worksheet_table_over_the_target_points_to_row_blocks()
    {
        var rows = string.Join("", Enumerable.Range(1, 120).Select(row =>
            $"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>{(row == 1 ? "ID" : $"REQ-{row:0000}")}</t></is></c><c r="B{row}" t="inlineStr"><is><t>{(row == 1 ? "Requirement" : $"The system shall handle case {row} within the agreed time.")}</t></is></c></row>"""));
        var workbook = Workbook("large.xlsx", rows);

        var whole = new StringWriter();
        Assert.Equal(0, await App(whole).RunAsync(["export", workbook, "--ai-package", "dir", "--chunk-chars", "1000",
            "--ocr", "off", "--output", Path.Combine(root, "whole")]));
        Assert.Contains("--table-row-blocks", whole.ToString(), StringComparison.Ordinal);
        Assert.Contains("--table-row-blocks", await File.ReadAllTextAsync(Path.Combine(root, "whole", "review.md")), StringComparison.Ordinal);

        var blocked = new StringWriter();
        Assert.Equal(0, await App(blocked).RunAsync(["export", workbook, "--ai-package", "dir", "--chunk-chars", "1000",
            "--table-row-blocks", "--ocr", "off", "--output", Path.Combine(root, "blocked")]));
        Assert.Contains("Parts: ", blocked.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Add --table-row-blocks", blocked.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.GetFiles(Path.Combine(root, "blocked", "parts")).Length > 1);
    }

    [Fact]
    public async Task The_package_size_line_follows_the_language()
    {
        var rows = string.Join("", Enumerable.Range(1, 80).Select(row =>
            $"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>Row {row} with enough text to pass the target</t></is></c></row>"""));
        var workbook = Workbook("lang.xlsx", rows);
        var output = new StringWriter();

        await App(output).RunAsync(["export", workbook, "--ai-package", "dir", "--chunk-chars", "500", "--ocr", "off",
            "--lang", "ja", "--output", Path.Combine(root, "ja")]);

        Assert.Contains("分割: 1件", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--table-row-blocks で行単位に分割できます", output.ToString(), StringComparison.Ordinal);
    }

    private string Document(string name, string body)
    {
        var path = Path.Combine(root, name);
        WriteZip(path, new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>",
        });
        return path;
    }

    private string Workbook(string name, string rows)
    {
        var path = Path.Combine(root, name);
        WriteZip(path, new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """,
            ["xl/workbook.xml"] = """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Data" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """,
            ["xl/worksheets/sheet1.xml"] = $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>""",
        });
        return path;
    }

    private static void WriteZip(string path, IReadOnlyDictionary<string, string> parts)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var writer = new StreamWriter(archive.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
            writer.Write(part.Value);
        }
    }
}

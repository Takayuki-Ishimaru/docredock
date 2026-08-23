using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rtmd.Api;
using Rtmd.Core.Documents;
using Rtmd.Core.Reporting;
using Rtmd.Markdown;
using Rtmd.Providers.Abstractions.Providers;

var root = "/Users/takayuki/git/RTMD/outputs/japanese-system-design-ocr-check";
var source = Path.Combine(root, "japanese-system-design-ocr-sample.xlsx");
var markdown = Path.Combine(root, "japanese-system-design-ocr-sample.md");
var workspace = Path.Combine(root, "japanese-system-design-ocr-sample.rtmd");
var report = Path.Combine(root, "ocr-accuracy-report.md");
var visionExecutable = Path.Combine(root, "support", "vision-ocr");

if (args.Contains("--probe-diff", StringComparer.Ordinal))
{
    var baseline = DeterministicJson.Deserialize<DocumentGraph>(await File.ReadAllTextAsync(Path.Combine(workspace, "graph", "index.json")))!;
    var projected = await File.ReadAllTextAsync(markdown);
    var edit = new MarkdownGraphEditor().Apply(baseline, projected);
    foreach (var operation in edit.Diff.PatchSet.Operations)
    {
        var before = operation.Before?.Content as TextNodeContent;
        var after = operation.After?.Content as TextNodeContent;
        Console.WriteLine(JsonSerializer.Serialize(new { operation.NodeId, before = before?.Text, after = after?.Text,
            beforeLength = before?.Text.Length, afterLength = after?.Text.Length,
            beforeCodePoints = before?.Text.Select(character => (int)character), afterCodePoints = after?.Text.Select(character => (int)character) }));
    }
    return;
}

if (!File.Exists(source) || !File.Exists(visionExecutable))
    throw new FileNotFoundException("The workbook or Vision OCR helper is missing.");

var engine = new VisionOcrEngine(visionExecutable);
var service = new DocumentService(engine);
var exported = await service.ExportAsync(new DocumentExportOptions(
    source,
    workspace,
    markdown,
    EnableOcr: true,
    OcrLanguages: ["jpn", "eng"],
    ContentPolicy: "visible"));

var expected = exported.Graph.Partitions
    .Where(partition => partition.Id.Contains("OCR期待値", StringComparison.Ordinal))
    .SelectMany(partition => partition.Nodes)
    .Where(node => node.Kind == NodeKind.Cell && CellAddress(node) is { } address && IsExpectedAddress(address))
    .OrderBy(node => int.Parse(CellAddress(node)![1..], CultureInfo.InvariantCulture))
    .Select(node => ((TextNodeContent)node.Content).Text)
    .ToArray();

var ocrNode = exported.Graph.Nodes.Single(node => node.Kind == NodeKind.ImageText);
var ocrText = ((TextNodeContent)ocrNode.Content).Text;
var scores = expected.Select(line => BestMatch(line, engine.LastRows)).ToArray();
var totalCharacters = scores.Sum(score => score.ExpectedLength);
var totalErrors = scores.Sum(score => score.Distance);
var cer = totalCharacters == 0 ? 1d : (double)totalErrors / totalCharacters;
var exact = scores.Count(score => score.Distance == 0);
var averageConfidence = engine.LastRegions.Select(region => region.Confidence ?? 0d).DefaultIfEmpty().Average();

var verification = await exported.Workspace.VerifyAsync(markdown, requireUnchangedProjection: true);
var formulaCells = exported.Graph.Nodes.Where(node => node.Kind == NodeKind.Cell &&
    node.Extensions is not null && node.Extensions.ContainsKey("formula")).ToArray();
var formulaProjectionCount = formulaCells.Length;
var formulaResultCount = formulaCells.Count(node => node.Content is TextNodeContent text && text.Text.Length > 0);
var mermaidDiagrams = exported.Graph.Nodes.Where(node => node.Kind == NodeKind.Diagram &&
    node.Extensions is not null && node.Extensions.TryGetValue("diagram_language", out var language) &&
    string.Equals(language.GetString(), "mermaid", StringComparison.OrdinalIgnoreCase)).ToArray();
var sequenceDiagramCount = mermaidDiagrams.Count(node => node.Extensions!["diagram_type"].GetString() == "sequence");
var flowchartCount = mermaidDiagrams.Count(node => node.Extensions!["diagram_type"].GetString() == "flowchart");

var reportText = new StringBuilder()
    .AppendLine("# OCR精度・RTMD変換確認レポート")
    .AppendLine()
    .AppendLine("## 実行条件")
    .AppendLine()
    .AppendLine("- OCRエンジン: macOS Vision（accurate / ja-JP + en-US）")
    .AppendLine("- OCR対象: XLSX内 `xl/media/image.png`（1200×760 px）")
    .AppendLine("- 評価対象: `OCR期待値` シートの主要25文字列")
    .AppendLine("- 指標: 各期待文字列をOCR行内の最良部分列へ対応付けた key-line CER（空白はNFKC正規化後に除外）")
    .AppendLine()
    .AppendLine("## 結果")
    .AppendLine()
    .AppendLine($"- 完全文字列一致: {exact}/{expected.Length}（{(double)exact / expected.Length:P1}）")
    .AppendLine($"- 文字誤り率（CER）: {cer:P2}（{totalErrors}/{totalCharacters}文字）")
    .AppendLine($"- 文字精度: {Math.Max(0, 1 - cer):P2}")
    .AppendLine($"- OCR平均信頼度: {averageConfidence:P1}")
    .AppendLine($"- OCR検出行数: {engine.LastRegions.Count}")
    .AppendLine($"- RTMDワークスペース整合性: {(verification.IsValid ? "OK" : "NG")}")
    .AppendLine($"- Markdown内の数式投影: {formulaProjectionCount}件（うち計算結果併記 {formulaResultCount}件）")
    .AppendLine($"- Mermaid図投影: {mermaidDiagrams.Length}件（`sequenceDiagram` {sequenceDiagramCount}件、`flowchart TD` {flowchartCount}件）")
    .AppendLine()
    .AppendLine("## 誤認識・差分")
    .AppendLine();

foreach (var score in scores.Where(score => score.Distance > 0))
{
    reportText.Append("- 期待: `").Append(score.Expected).Append("` / OCR: `")
        .Append(score.Recognized).Append("` / 編集距離: ").AppendLine(score.Distance.ToString(CultureInfo.InvariantCulture));
}

reportText.AppendLine()
    .AppendLine("## OCR抽出テキスト")
    .AppendLine()
    .AppendLine("```text")
    .AppendLine(ocrText)
    .AppendLine("```");

await File.WriteAllTextAsync(report, reportText.ToString(), new UTF8Encoding(false));

Console.WriteLine(JsonSerializer.Serialize(new
{
    markdown,
    workspace,
    report,
    expected = expected.Length,
    exact,
    cer,
    averageConfidence,
    ocrLines = engine.LastRegions.Count,
    workspaceValid = verification.IsValid,
    formulaProjectionCount,
    formulaResultCount,
    mermaidDiagramCount = mermaidDiagrams.Length,
    sequenceDiagramCount,
    flowchartCount,
}, new JsonSerializerOptions { WriteIndented = true }));

static string? CellAddress(DocumentNode node) =>
    node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value?.ToUpperInvariant();

static bool IsExpectedAddress(string address) =>
    address.Length >= 2 && address[0] == 'B' && int.TryParse(address[1..], out var row) && row is >= 5 and <= 29;

static MatchScore BestMatch(string expected, IReadOnlyList<string> recognizedRows)
{
    var normalizedExpected = Normalize(expected);
    var best = new MatchScore(expected, string.Empty, normalizedExpected.Length, normalizedExpected.Length);
    foreach (var row in recognizedRows)
    {
        var normalizedRow = Normalize(row);
        var candidate = ClosestSubstring(normalizedExpected, normalizedRow);
        if (candidate.Distance < best.Distance)
            best = new MatchScore(expected, candidate.Substring, normalizedExpected.Length, candidate.Distance);
        if (best.Distance == 0) break;
    }
    return best;
}

static (string Substring, int Distance) ClosestSubstring(string expected, string source)
{
    if (source.Length == 0) return (string.Empty, expected.Length);
    if (source.Length <= expected.Length) return (source, Levenshtein(expected, source));
    var minimumLength = Math.Max(1, expected.Length - 4);
    var maximumLength = Math.Min(source.Length, expected.Length + 4);
    var bestText = source;
    var bestDistance = Levenshtein(expected, source);
    var lengths = Enumerable.Range(minimumLength, maximumLength - minimumLength + 1)
        .OrderBy(length => Math.Abs(length - expected.Length));
    foreach (var length in lengths)
    {
        for (var start = 0; start + length <= source.Length; start++)
        {
            var candidate = source.Substring(start, length);
            var distance = Levenshtein(expected, candidate);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestText = candidate;
            if (distance == 0) return (bestText, 0);
        }
    }
    return (bestText, bestDistance);
}

static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC)
    .Where(character => !char.IsWhiteSpace(character)).ToArray());

static int Levenshtein(string left, string right)
{
    var previous = Enumerable.Range(0, right.Length + 1).ToArray();
    var current = new int[right.Length + 1];
    for (var i = 1; i <= left.Length; i++)
    {
        current[0] = i;
        for (var j = 1; j <= right.Length; j++)
            current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
        (previous, current) = (current, previous);
    }
    return previous[right.Length];
}

sealed record MatchScore(string Expected, string Recognized, int ExpectedLength, int Distance);
sealed record VisionLine(string Text, double Confidence, double X, double Y, double Width, double Height);

sealed class VisionOcrEngine(string executable) : IOcrEngine
{
    public ProviderDescriptor Descriptor { get; } = new(
        "rtmd.ocr.apple-vision.demo",
        new Version(1, 0, 0),
        1,
        new HashSet<string>(StringComparer.Ordinal) { "ocr.text", "ocr.jpn", "ocr.eng" },
        "Apple-System-Framework",
        "local-system-framework",
        true);

    public IReadOnlyList<OcrTextRegion> LastRegions { get; private set; } = [];
    public IReadOnlyList<string> LastRows { get; private set; } = [];

    public async ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"rtmd-vision-{Guid.NewGuid():N}.png");
        try
        {
            await using (var output = File.Create(temporary)) await input.Image.CopyToAsync(output, cancellationToken);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(temporary);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Vision OCR could not start.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                return new OcrAttemptResult(OcrProcessingStatus.Failed, null,
                    [new OcrDiagnostic("VisionProcessFailed", stderr, DiagnosticSeverity.Warning)]);

            var lines = JsonSerializer.Deserialize<VisionLine[]>(stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            var orderedRows = ArrangeRows(lines);
            LastRows = orderedRows;
            LastRegions = lines.Select(line => new OcrTextRegion(
                line.Text,
                new Geometry("vision-normalized-bottom-left", line.X, line.Y, line.Width, line.Height),
                line.Confidence)).ToArray();
            return new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult(string.Join('\n', orderedRows), LastRegions), []);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static string[] ArrangeRows(IReadOnlyList<VisionLine> lines)
    {
        var pending = lines.OrderByDescending(line => line.Y + line.Height / 2).ToList();
        var rows = new List<List<VisionLine>>();
        foreach (var line in pending)
        {
            var center = line.Y + line.Height / 2;
            var row = rows.FirstOrDefault(candidate => Math.Abs(candidate.Average(item => item.Y + item.Height / 2) - center) <= 0.012);
            if (row is null) rows.Add([line]); else row.Add(line);
        }
        return rows.OrderByDescending(row => row.Average(item => item.Y + item.Height / 2))
            .Select(row => string.Join(' ', row.OrderBy(item => item.X).Select(item => item.Text)))
            .ToArray();
    }
}

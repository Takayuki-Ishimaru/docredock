namespace DocRedock.Cli;

/// <summary>The messages of the CLI a person reads most, in English or, with --lang ja, in Japanese. Codes, paths and
/// option names stay as they are in both.</summary>
internal sealed class CliText(bool japanese)
{
    public bool Japanese { get; } = japanese;

    /// <summary>Japanese when the command line asks for it: --lang ja (or --lang=ja) anywhere after the command.</summary>
    public static CliText For(IReadOnlyList<string> args)
    {
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] == "--lang=ja") return new(true);
            if (args[index] == "--lang" && index + 1 < args.Count && args[index + 1] == "ja") return new(true);
        }
        return new(false);
    }

    public string Exported(string path) => Japanese ? $"書き出しました: {path}" : $"Exported: {path}";

    public string ReviewLine(bool required, int warnings) => Japanese
        ? $"確認: {(required ? "必要" : "不要")}、警告: {warnings}件"
        : $"Review: {(required ? "required" : "none")}; warnings: {warnings}";

    public string OcrUnavailable => Japanese ? "画像内の文字は読み取っていません（OCRを利用できません）。" : "Image text was not read (OCR unavailable).";

    public string ReviewRequiredExit(int code) => Japanese
        ? $"確認が必要です: 出力を使う前に、確認項目を原本と照合してください（終了コード {code}、--fail-on-review）。"
        : $"Review required: compare the review items with the source before using the output (exit code {code}, --fail-on-review).";

    /// <summary>One line about the parts of an AI package, and how to split a large worksheet table when it stayed whole.</summary>
    public string PackageSize(DocRedock.Api.AiPackageSizeSummary size, bool rowBlocksAvailable)
    {
        var line = Japanese
            ? $"分割: {size.PartCount}件（最大 {size.MaximumPartCharacters:N0}文字、目安 {size.TargetCharacters:N0}文字、目安超過 {size.ExceedingParts}件）"
            : $"Parts: {size.PartCount} (largest {size.MaximumPartCharacters:N0} characters; target {size.TargetCharacters:N0}; over target {size.ExceedingParts})";
        if (!rowBlocksAvailable || size.ExceedingParts == 0) return line;
        return line + (Japanese
            ? "。大きな表は --table-row-blocks で行単位に分割できます。"
            : ". Add --table-row-blocks to split large worksheet tables between rows.");
    }

    public string EmptyProjection => Japanese ? "WARNING EmptyProjection: 抽出できる内容がありませんでした。" : "WARNING EmptyProjection: no extractable content was found.";

    public string PdfDisabled(string file) => Japanese
        ? $"PDFの変換は既定で無効です: {file}。PDFを変換するには --allow-pdf を付けてください（または環境変数 DOCREDOCK_ENABLE_EXPERIMENTAL=1）。"
        : $"PDF export is off by default: {file}. Add --allow-pdf to convert PDF files (or set DOCREDOCK_ENABLE_EXPERIMENTAL=1).";

    public string ProfileDisabled(string profile) => Japanese
        ? $"{profile} 形式の書き出しは試験的機能のため無効です。環境変数 DOCREDOCK_ENABLE_EXPERIMENTAL=1 を設定すると有効になります。"
        : $"{profile} export is experimental and disabled. Set DOCREDOCK_ENABLE_EXPERIMENTAL=1 to enable it explicitly.";

    public string CommandDisabled(string command) => Japanese
        ? $"{command} は試験的機能のため無効です。環境変数 DOCREDOCK_ENABLE_EXPERIMENTAL=1 を設定すると有効になります。"
        : $"{command} is experimental and disabled. Set DOCREDOCK_ENABLE_EXPERIMENTAL=1 to enable it explicitly.";

    public string LegacyOffice(string file) => Japanese
        ? $"旧形式のOfficeファイルは変換できません: {file}。Microsoft OfficeまたはLibreOfficeで開き、DOCX／XLSX／PPTX形式で保存し直してください。"
        : $"Legacy Office files are not supported: {file}. Open the file in Microsoft Office or LibreOffice and save it as DOCX, XLSX, or PPTX first.";

    public string Collision(IEnumerable<(string Output, IReadOnlyList<string> Inputs)> collisions) => Japanese
        ? "複数の入力が同じ出力先になります: " + string.Join("; ", collisions.Select(item => $"{string.Join("、", item.Inputs)} → {item.Output}")) +
          "。ファイル名を変えるか、--keep-source-extension を付けてください（例: 名前.pdf.md）。"
        : "Multiple inputs would produce the same output path: " + string.Join("; ", collisions.Select(item => $"{string.Join(", ", item.Inputs)} -> {item.Output}")) +
          ". Rename them, or add --keep-source-extension to name outputs like name.pdf.md.";

    public string ReadableMarkdownNotDrmd(string command, string file) => Japanese
        ? $"{file} は閲覧用Markdown（一方向の書き出し）です。{command} は --profile roundtrip で書き出したDRMD（.drmd を伴うMarkdown）を確認するコマンドで、閲覧用Markdownには確認するものがありません。"
        : $"{file} is readable Markdown (a one-way export). {command} checks DRMD files written with `export --profile roundtrip` (Markdown with a .drmd sidecar); a readable Markdown file has nothing to {command}.";

    public string UnknownCommand(string command, IReadOnlyList<string> suggestions) => Japanese
        ? $"不明なコマンドです: '{command}'。" + (suggestions.Count > 0 ? $"もしかして: {string.Join("、", suggestions)}。" : "") + "docredock --help で一覧を表示します。"
        : $"Unknown command '{command}'." + (suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "") + " Run docredock --help for the list.";
}

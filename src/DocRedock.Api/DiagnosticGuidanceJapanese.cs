namespace DocRedock.Api;

/// <summary>
/// Japanese summary and next step for diagnostic codes. The GUI result panel and the CLI with --lang ja
/// both read this table, so one code is explained the same way in each.
/// </summary>
public static class DiagnosticGuidanceJapanese
{
    /// <summary>
    /// The Japanese summary and action for a diagnostic. A code without its own entry gets a generic
    /// explanation that names the code and quotes the original message.
    /// </summary>
    public static (string Summary, string Action) Describe(string code, string originalMessage, string? partUri = null) =>
        Find(code, originalMessage, partUri) ?? (
            $"変換時の注意事項があります（{code}）。",
            $"元文書と生成結果の該当箇所を見比べてください。{Environment.NewLine}詳細（英語）: {originalMessage}");

    /// <summary>
    /// As <see cref="Describe"/>, with a next step a command-line user can take: the GUI's buttons and its
    /// 「該当ページを確認」 view become the matching options or the review image that review.md links.
    /// </summary>
    public static (string Summary, string Action) DescribeForCli(string code, string originalMessage, string? partUri = null)
    {
        var (summary, action) = Describe(code, originalMessage, partUri);
        if (CliActions.TryGetValue(code, out var cliAction)) return (summary, cliAction);
        return (summary, action
            .Replace("「該当ページを確認」または", string.Empty, StringComparison.Ordinal)
            .Replace("「該当ページを確認」で", "照合画像または原本の該当ページで", StringComparison.Ordinal));
    }

    // Next steps of the GUI entries that name one of its buttons or settings, in command-line terms.
    private static readonly Dictionary<string, string> CliActions = new(StringComparer.Ordinal)
    {
        ["DeletedRevisionContentIncluded"] = "削除した文字を共有しないときは、--revisions final を付けて書き出し直してください。",
        ["TrackedRevisionsPresent"] = "最終版だけを渡すときは、--revisions final を付けて書き出してください。",
        ["HiddenContentIncluded"] = "共有してよい内容か確認してください。共有しないときは --content-policy visible で書き出してください。",
    };

    /// <summary>True when the code has its own entry, not the generic one that <see cref="Describe"/> falls back to.</summary>
    public static bool IsKnown(string code, string? partUri = null, string originalMessage = "") =>
        Find(code, originalMessage, partUri) is not null;

    internal static bool IsPdfDiagnostic(string? partUri, string message) =>
        partUri?.StartsWith("pdf:", StringComparison.Ordinal) == true || message.Contains("PDF page", StringComparison.Ordinal);

    private static (string Summary, string Action)? Find(string code, string originalMessage, string? partUri)
    {
        // PDF has no editable connectors, so its guidance points at the review image or the source
        // page instead of asking the user to reconnect shapes.
        if (IsPdfDiagnostic(partUri, originalMessage) && PdfGuidance(code) is { } pdf) return pdf;
        return code switch
        {
            "VisualConnectorUnresolved" => ("図のコネクタの接続先を一意に判断できませんでした。", "元文書を確認し、必要ならコネクタ端点を図形へ接続してください。"),
            "VisualEdgeLabelUnresolved" => ("図のラベルを一つの接続線へ一意に対応付けできませんでした。", "ラベルを対象の接続線へ近づけ、周囲の線から離してください。"),
            "VisualNodeLabelMissing" => ("図形の文字を一つの図形へ一意に対応付けできませんでした。", "元文書で図形と文字の対応を確認してください。"),
            "VisualSemanticProjectionPartial" => ("図の一部だけを意味構造として変換しました。", "Markdownの図、代替表示、元文書を見比べてください。"),
            "VisualSemanticProjectionFallback" => ("図を意味構造として変換できず、代替表示にしました。", "Markdownの代替表示と元文書を見比べてください。"),
            "VisualSemanticProjectionUnavailable" => ("図の一部を意味構造として変換できませんでした。", "元文書の該当箇所を確認してください。"),
            "VisualFallbackUsed" => ("図の一部を代替表示として保持しました。", "Markdownの代替表示と元文書を見比べてください。"),
            "VisualEdgeDirectionUnknown" => ("接続線の向きを確定できないため、無向線として保持しました。", "元文書の矢印方向を確認してください。"),
            "XlsxFormulaCachedValueMissing" => ("保存済みの計算結果がない数式セルがあります。", "Excel等で再計算して保存してから、もう一度変換してください。"),
            "XlsxRecalculationRequired" => ("編集したセルを参照する数式の計算結果が、再計算されるまで編集前の値のままです。", "Excelで開くと再計算されます。保存済みの計算結果を読むツールに渡す前に、Excel等で開いて保存してください。"),
            "XlsxCalculationChainReset" => ("数式を編集したため、ブックの計算順序の情報を初期化しました。", "対応は不要です。Excelが開くときに作り直します。"),
            "XlsxLegacyCommentsUnsupported" => ("旧形式のXLSXコメントは本文へ展開されません。", "必要なコメントは元ブックで確認してください。"),
            "DeletedRevisionContentIncluded" => ("Wordの変更履歴で削除された文字も、<del>の印付きで出力に含まれています。", "削除した文字を共有しないときは「最終版で書き出し直す」を押すか、詳細設定の変更履歴で「最終版のみ」を選んでください。"),
            "TrackedRevisionsPresent" => ("Wordの変更履歴があります。挿入は<ins>、削除は<del>の印で出力しました。", "最終版だけを渡すときは「最終版で書き出し直す」を使ってください。"),
            "PdfColumnLayoutDetected" => ("PDFの段組みを推定して、左の段から順に並べました。", "「該当ページを確認」で、読み順が原本と合っているか確認してください。"),
            "PdfTableReconstructionFailed" => ("PDFの罫線から表を組み立てられませんでした。", "「該当ページを確認」で、セルの対応を原本と照合してください。"),
            "PdfNativeTextUnavailable" => ("PDFのページから文字を抽出できませんでした（画像だけのページの可能性があります）。", "OCRを有効にするか、原本の画像を確認してください。"),
            "PdfEmbeddedImageOmitted" => ("PDFに埋め込まれた画像の文字は読み取っていません。", "画像内の文字が必要なときはOCRを有効にしてください。"),
            "HiddenContentIncluded" => ("非表示の内容を出力に含めました。", "共有してよい内容か確認してください。共有しないときは「表示中の内容」で書き出してください。"),
            "ReadableImageEmbedSkipped" => ("一部の画像を埋め込めませんでした。", "画像は元文書で確認してください。"),
            "PdfRasterizerUnavailable" => ("画像PDFの読み取りに必要なrasterizerを利用できません。", "ネイティブテキストを使うか、対応rasterizerを構成してください。"),
            "PdfRasterizationFailed" => ("PDF rasterizerの実行に失敗しました。", "pdftoppmまたはmutoolの実行ファイルと入力PDFを確認してください。"),
            "PdfReviewImageUnavailable" => ("原本照合用のページ画像を作成できませんでした。", "原本PDFの該当ページを開いて確認してください。画像を添付するにはpdftoppmまたはmutoolを構成してください。"),
            "PdfFormXObjectUnparsed" => ("PDFの描画部品（Form XObject）の中身を解析できず、その文字・図はMarkdownに含まれていません。", "「該当ページを確認」または照合画像で、原本の該当箇所を確認してください。"),
            "PdfFontResourceAmbiguous" => ("PDFの文字が使うフォントを特定できず、文字が正しく変換されていない可能性があります（Markdownには含めています）。", "「該当ページを確認」または原本PDFで、該当する文字を確認してください。"),
            "PdfTextUndecodable" => ("PDFの文字が使うフォントに、文字コードを文字へ変換する情報（ToUnicode）がなく、文字を確定できませんでした。Markdownには文字化けした文字が含まれている可能性があります。", "「該当ページを確認」または原本PDFで、該当する文字を確認してください。"),
            "PdfLayerVisibilityUnknown" => ("PDFのレイヤー（Optional Content）の表示状態を判定できなかった文字を、Markdownに含めました。", "原本PDFで、その文字が画面に表示されているかを確認してください。"),
            "PdfStrikethroughUnplaced" => ("PDFの行の一部に取り消し線があり、どの文字が取り消されているかを判定できなかったため、取り消し線の印を付けずに出力しました。", "原本PDFの該当ページで、取り消された文字を確認してください。"),
            "OcrProviderUnavailable" => ("OCRエンジンを利用できません。", "OCRを無効にするか、利用可能なOCRエンジンを構成してください。"),
            "EmptyProjection" => ("変換できる内容が見つかりませんでした。", "元文書が対応形式で、内容が非表示または画像のみでないか確認してください。"),
            "ImageNotWritten" => ("抽出した画像が本文から参照されていません。", "原本の図と出力を見比べてください。"),
            "XlsxDuplicateCellReference" => ("同じセル番地が重複していたため、最後のセルを使いました。", "元ブックの該当セルを確認してください。"),
            _ => null,
        };
    }

    private static (string Summary, string Action)? PdfGuidance(string code) => code switch
    {
        "VisualConnectorUnresolved" => ("PDFの線や矢印の接続先・意味を自動で確定できませんでした。", "「該当ページを確認」または照合画像で、線の意味を原本と照合してください。"),
        "VisualEdgeLabelUnresolved" => ("PDFの文字を、どの線の説明か確定できませんでした。", "照合画像または原本PDFで、文字と線の対応を確認してください。"),
        "VisualNodeLabelMissing" => ("PDFの図形の文字を一つの図形へ対応付けできませんでした。", "照合画像または原本PDFで、図形と文字の対応を確認してください。"),
        "VisualEdgeDirectionUnknown" => ("PDFの線の向きを確定できないため、向きのない線として保持しました。", "照合画像または原本PDFで矢印の向きを確認してください。"),
        _ => null,
    };
}

using System.Globalization;
using DocRedock.Core.Documents;

namespace DocRedock.Api;

/// <summary>Short, user-facing explanations of an <see cref="ExportReview"/>: what could not be
/// converted, whether the surrounding content was still exported, and where to look. Diagnostic
/// codes, object IDs, and confidences are deliberately absent; they stay in the detailed report.</summary>
public static class ExportReviewText
{
    public static string Location(ReviewPage page) => page.Format switch
    {
        DocumentFormatKind.Pdf => $"{page.Number}ページ目",
        DocumentFormatKind.Pptx => $"スライド{page.Number}",
        DocumentFormatKind.Xlsx => $"シート「{SheetName(page.PartitionId)}」",
        _ => "本文",
    };

    /// <summary>One to three short Japanese sentences for a page, e.g. 「1ページ目：表の上の斜めの線1件を
    /// 表の記号に変換できませんでした。表の文字は書き出されています。照合画像で線の意味を確認してください。」</summary>
    public static string DescribeJapanese(ReviewPage page)
    {
        var parts = new List<string>();
        foreach (var kind in Enum.GetValues<ReviewElementKind>())
        {
            var elements = page.Elements.Where(element => element.Kind == kind).ToArray();
            if (elements.Length == 0) continue;
            var count = elements.Length.ToString(CultureInfo.InvariantCulture);
            var style = StyleSuffixJapanese(elements);
            parts.Add(kind switch
            {
                ReviewElementKind.TableDiagonalLine => $"表の上の斜めの線{style}{count}件を表の記号に変換できませんでした",
                ReviewElementKind.TableDiagonalArrow => $"表の上の斜めの矢印{style}{count}件を表の記号に変換できませんでした",
                ReviewElementKind.Line => $"線{style}{count}件の接続先や意味を確定できませんでした",
                ReviewElementKind.Arrow => $"矢印{style}{count}件の接続先や意味を確定できませんでした",
                ReviewElementKind.Label => $"文字{count}件を、どの線や図形の説明か確定できませんでした",
                _ => $"図形{style}{count}件を図として再構成できませんでした",
            });
        }
        if (parts.Count == 0) parts.Add("図の一部を変換できませんでした");
        var text = Location(page) + "：" + string.Join("。", parts) + "。";
        if (page.HasTables && page.Elements.Any(element => element.Kind is ReviewElementKind.TableDiagonalLine or ReviewElementKind.TableDiagonalArrow))
            text += "表の文字は書き出されています。";
        var subject = page.Elements.Count > 0 && page.Elements.All(element => element.Kind is not (ReviewElementKind.Label or ReviewElementKind.Shape))
            ? "線の意味" : "内容";
        text += page.ReviewImageReference is not null
            ? $"照合画像で{subject}を確認してください。"
            : page.Format == DocumentFormatKind.Pdf
                ? page.ReviewImageUnavailable
                    ? $"照合画像を作成できなかったため、原本PDFの{page.Number}ページ目で{subject}を確認してください。"
                    : $"原本PDFの{page.Number}ページ目で{subject}を確認してください。"
                : $"元のファイルで{subject}を確認してください。";
        return text;
    }

    /// <summary>The counts a person acts on: pages, attached review images, elements, and OCR items.</summary>
    public static string CountsJapanese(ExportReview review)
    {
        var parts = new List<string>();
        if (review.Pages.Count > 0)
        {
            parts.Add($"要確認 {review.Pages.Count}ページ");
            parts.Add($"照合画像 {review.ReviewImagePages}ページ添付");
            if (review.Elements > 0) parts.Add($"未解決の図形 {review.Elements}件");
        }
        if (review.Ocr.Required) parts.Add($"OCR確認 {review.Ocr.ReviewItems}件");
        return string.Join("／", parts);
    }

    public static string OcrJapanese(OcrReviewSummary ocr) =>
        $"OCR：信頼度80%未満または不明の認識結果{ocr.ReviewItems}件を原画像と照合してください（Markdownの「OCR照合情報」に行番号付きで記載）。推測による自動修正はしていません。";

    public static string DescribeEnglish(ReviewPage page)
    {
        var parts = new List<string>();
        foreach (var kind in Enum.GetValues<ReviewElementKind>())
        {
            var elements = page.Elements.Where(element => element.Kind == kind).ToArray();
            if (elements.Length == 0) continue;
            var count = elements.Length;
            var style = StyleSuffixEnglish(elements);
            parts.Add(kind switch
            {
                ReviewElementKind.TableDiagonalLine => $"{count} diagonal line(s){style} across a table not expressible as table symbols",
                ReviewElementKind.TableDiagonalArrow => $"{count} diagonal arrow(s){style} across a table not expressible as table symbols",
                ReviewElementKind.Line => $"{count} line(s){style} with undetermined endpoints or meaning",
                ReviewElementKind.Arrow => $"{count} arrow(s){style} with undetermined endpoints or meaning",
                ReviewElementKind.Label => $"{count} label(s) not assignable to one line or shape",
                _ => $"{count} shape(s){style} kept as vector fallback",
            });
        }
        if (parts.Count == 0) parts.Add("part of a figure could not be converted");
        var location = page.Format switch
        {
            DocumentFormatKind.Pdf => $"page {page.Number}",
            DocumentFormatKind.Pptx => $"slide {page.Number}",
            DocumentFormatKind.Xlsx => $"sheet '{SheetName(page.PartitionId)}'",
            _ => "document",
        };
        var tableNote = page.HasTables && page.Elements.Any(element => element.Kind is ReviewElementKind.TableDiagonalLine or ReviewElementKind.TableDiagonalArrow)
            ? " (table text was exported)" : string.Empty;
        return $"{location}: {string.Join("; ", parts)}{tableNote}";
    }

    // Only a style shared by every element of the kind is named; a mix stays unqualified.
    private static string StyleSuffixJapanese(IReadOnlyList<ReviewElement> elements) =>
        elements.All(element => element.LineStyle == VisualLineStyles.Dashed) ? "（破線）"
        : elements.All(element => element.LineStyle == VisualLineStyles.Dotted) ? "（点線）"
        : string.Empty;

    private static string StyleSuffixEnglish(IReadOnlyList<ReviewElement> elements) =>
        elements.All(element => element.LineStyle == VisualLineStyles.Dashed) ? " (dashed)"
        : elements.All(element => element.LineStyle == VisualLineStyles.Dotted) ? " (dotted)"
        : string.Empty;

    private static string SheetName(string partitionId)
    {
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (partitionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return partitionId[prefix.Length..];
        return partitionId;
    }
}

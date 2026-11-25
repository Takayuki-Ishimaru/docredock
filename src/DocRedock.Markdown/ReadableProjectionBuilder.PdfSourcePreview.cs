using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

public static partial class ReadableProjectionBuilder
{
    private sealed partial class Projection
    {
        private bool WithholdSanitizedPdfPageRaster(DocumentNode node)
        {
            if (graph.Format != DocumentFormatKind.Pdf ||
                DocumentContentPolicyRules.Parse(options.ContentPolicy) != DocumentContentPolicy.Sanitized ||
                node.Kind != NodeKind.Image || !ExtensionBool(node, "pdf_page_raster")) return false;
            withheldImages.Add(node.Id);
            Add("PdfSourcePagePreviewWithheld",
                "The original PDF page image is withheld by sanitized policy because its pixels may contain excluded furniture or annotations.",
                node.Id, MarkdownDiagnosticSeverity.Info);
            return true;
        }

        private List<ReadableBlock> PreferPdfSourcePage(DocumentPartition page, List<ReadableBlock> blocks)
        {
            if (graph.Format != DocumentFormatKind.Pdf ||
                DocumentContentPolicyRules.Parse(options.ContentPolicy) == DocumentContentPolicy.Sanitized) return blocks;

            // Auto-OCR also marks decoded embedded pictures pdf_review_image. Only a whole-page
            // review image can preserve the source page composition and become the primary view.
            var sources = page.Nodes.Where(node => node.Kind == NodeKind.Image &&
                ExtensionBool(node, "pdf_page_raster") && ExtensionBool(node, "pdf_review_image") &&
                !ExtensionBool(node, "image_omitted")).ToDictionary(node => node.Id, StringComparer.Ordinal);
            if (sources.Count == 0) return blocks;
            var previews = blocks.OfType<ReadableImage>()
                .Where(image => image.Origin?.NodeIds?.Any(sources.ContainsKey) == true).ToArray();
            if (previews.Length == 0) return blocks;

            var previewSet = previews.ToHashSet();
            var primary = previews.Select(image => (ReadableBlock)SourcePageExtent(image,
                sources[image.Origin!.NodeIds!.First(sources.ContainsKey)])).ToArray();
            var description = blocks.Where(block => block is not ReadableImage image || !previewSet.Contains(image)).ToArray();
            var result = new List<ReadableBlock>
            {
                new ReadableDetails("pdf-source-preview",
                    ReadableContent.Text(ja ? "元PDFのページ表示" : "Original PDF page"),
                    primary, true) { Origin = previews[0].Origin }
            };
            if (description.Length > 0)
                result.Add(new ReadableDetails("pdf-source-description",
                    ReadableContent.Text(ja ? "抽出テキスト・構造の説明" : "Extracted text and structure"),
                    description) { Origin = new(page.Id) });
            Add("PdfSourcePagePreviewShown",
                "The renderer-generated original page image is shown first. Extracted text and reconstructed structure follow separately.",
                sources.Keys.First(), MarkdownDiagnosticSeverity.Info);
            return result;
        }

        private static ReadableImage SourcePageExtent(ReadableImage image, DocumentNode source)
        {
            var box = Extension(source, "pdf_page_box");
            var width = Number(Property(box, "Width", "width"));
            var height = Number(Property(box, "Height", "height"));
            if (width is not (> 0 and <= 10_000_000) || height is not (> 0 and <= 10_000_000)) return image;
            var rotation = ExtensionInt(source, "pdf_page_rotation") ?? 0;
            if ((rotation % 180 + 180) % 180 == 90) (width, height) = (height, width);
            return image with { WidthPixels = width.Value * 96 / 72, HeightPixels = height.Value * 96 / 72 };
        }
    }
}

using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.RoundTrip;

namespace DocRedock.Api;

public sealed partial class DocumentService
{
    /// <summary>The image files the PDF extractor decoded (see <see cref="PdfExtractedImage"/>) as workspace assets, one per
    /// distinct picture. Their ids are the references the graph's image nodes carry, so an export writes them as
    /// Word's pictures are written.</summary>
    internal static IReadOnlyList<WorkspaceAsset> ExtractedPdfImageAssets(IEnumerable<PdfPageText> pages)
    {
        var assets = new List<WorkspaceAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in pages.SelectMany(page => page.ExtractedImages ?? []))
        {
            if (!seen.Add(image.AssetKey)) continue;
            var extension = image.MediaType == "image/jpeg" ? ".jpg" : ".png";
            // Not "pdf:page:N": that part uri names a rasterized page, whose OCR is compared with the page's own text.
            assets.Add(new WorkspaceAsset(image.AssetKey, image.AssetKey + extension, image.MediaType, Hash(image.Content),
                image.Content, "pdf:image:" + image.AssetKey));
        }
        return assets;
    }

    /// <summary>The assets of the rasterized pages and the assets of the images the extractor decoded, as one list.
    /// When OCR is only automatic (<paramref name="ocrAuto"/>), the decoded pictures are attachments, not text sources: a
    /// chart's axis labels read back as a table between a figure and its caption would bury the caption. The nodes say
    /// so the way a source-comparison raster does (<c>pdf_review_image</c>, which OCR skips); with OCR asked for, they are
    /// read like any picture of a Word document.</summary>
    internal static (IReadOnlyList<WorkspaceAsset> Assets, DocumentGraph Graph) WithExtractedPdfImages(
        IReadOnlyList<WorkspaceAsset> assets, DocumentGraph graph, IReadOnlyList<PdfPageText> pdfPages, bool ocrAuto)
    {
        var extracted = ExtractedPdfImageAssets(pdfPages);
        if (extracted.Count == 0) return (assets, graph);
        if (ocrAuto)
            graph = graph with
            {
                Partitions = graph.Partitions.Select(partition => partition with
                {
                    Nodes = partition.Nodes.Select(node => HasTrueExtension(node, "pdf_extracted_image")
                        ? node with
                        {
                            Extensions = new Dictionary<string, JsonElement>(node.Extensions!, StringComparer.Ordinal)
                            {
                                ["pdf_review_image"] = JsonSerializer.SerializeToElement(true),
                            }
                        }
                        : node).ToArray()
                }).ToArray()
            };
        return ([.. extracted, .. assets], graph);
    }
}

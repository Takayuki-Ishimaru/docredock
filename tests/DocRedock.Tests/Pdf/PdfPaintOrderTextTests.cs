using System.Text;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfPaintOrderTextTests
{
    [Fact]
    public void Rotated_fragments_keep_their_visible_content_stream_order()
    {
        const string caption="DGM-CACHED-GROUP-CHILD";
        var stream=new StringBuilder("BT /F1 12 Tf 0.94 -0.342 0.342 0.94 80 700 Tm ");
        foreach(var letter in caption)stream.Append('(').Append(letter).Append(") Tj ");
        stream.Append("ET BT /F1 12 Tf 1 0 0 1 10 10 Tm 3 Tr (HIDDEN-LABEL) Tj ET");
        var file=Path.Combine(Path.GetTempPath(),"docredock-paint-order-"+Guid.NewGuid().ToString("N")+".pdf");
        try
        {
            File.WriteAllBytes(file,Pdf(stream.ToString()));
            var result=PdfTextExtractor.Extract(file);
            var page=Assert.Single(result.Pages);
            Assert.Equal(caption,page.PaintOrderText);
            Assert.DoesNotContain("HIDDEN-LABEL",page.PaintOrderText);
            Assert.Contains("HIDDEN-LABEL",page.Text); // Preserve the existing OCR text extraction contract.
        }
        finally{File.Delete(file);}
    }

    private static byte[] Pdf(string stream)
    {
        var objects=new[]{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"};
        var builder=new StringBuilder("%PDF-1.4\n");var offsets=new List<int>{0};
        foreach(var item in objects){offsets.Add(builder.Length);builder.Append(offsets.Count-1).Append(" 0 obj\n").Append(item).Append("\nendobj\n");}
        var xref=builder.Length;builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach(var offset in offsets.Skip(1))builder.Append(offset.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}

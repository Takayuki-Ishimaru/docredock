using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using static DocRedock.Tests.Pdf.PdfImageFixtures;

namespace DocRedock.Tests.Pdf;

/// <summary>An Image XObject a page draws beside its text is decoded into a PNG or JPEG file (and a graph image node); what
/// cannot be decoded safely keeps the placeholder and PdfEmbeddedImageOmitted, and nothing a damaged file holds throws.</summary>
public sealed class PdfEmbeddedImageExtractionTests
{
    private const string Draw = "q 60 0 0 40 20 100 cm /Im1 Do Q";

    private static PdfPageText Single(byte[] pdf) => Assert.Single(PdfTextExtractor.Extract(pdf).Pages);

    private static PdfExtractedImage Extracted(byte[] pdf) => Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<PdfExtractedImage>>(Single(pdf).ExtractedImages));

    private static void AssertSamples(byte[] expected, Png png, int channels)
    {
        Assert.Equal(channels, png.Channels);
        Assert.Equal(expected, png.Samples);
    }

    [Fact]
    public void Flate_device_rgb_becomes_a_png_with_the_same_pixels_at_the_drawn_rectangle()
    {
        var pixels = Gradient(5, 4, 3);
        var pdf = OnePage(Draw, Image(5, 5, 4, "/DeviceRGB", 8, pixels));

        var page = Single(pdf);
        var image = Assert.Single(page.ExtractedImages!);

        Assert.Equal(0, page.EmbeddedImageCount);
        Assert.Null(page.EmbeddedImages);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("pdf-img-0005", image.AssetKey);
        Assert.Equal((5, 4), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(new Geometry("pdf-user-space", 20, 100, 60, 40), image.Bounds);
        Assert.Equal((60d, 40d), (image.DrawnWidth, image.DrawnHeight));
        var png = ReadPng(image.Content);
        Assert.Equal(2, png.ColorType);
        AssertSamples(pixels, png, 3);
        Assert.DoesNotContain(PdfTextExtractor.Extract(pdf).Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted", StringComparison.Ordinal));
    }

    [Fact]
    public void Png_predictors_are_undone_row_by_row_with_every_filter_type()
    {
        var pixels = Gradient(7, 10, 3);
        var predicted = PngPredicted(pixels, 7, 10, 3);
        var pdf = OnePage(Draw, Image(5, 7, 10, "/DeviceRGB", 8, predicted, "/DecodeParms << /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns 7 >>"));

        AssertSamples(pixels, ReadPng(Extracted(pdf).Content), 3);
    }

    [Fact]
    public void The_tiff_predictor_is_undone()
    {
        var pixels = Gradient(6, 3, 3);
        var differences = new byte[pixels.Length];
        for (var row = 0; row < 3; row++)
            for (var index = 0; index < 18; index++)
                differences[row * 18 + index] = unchecked((byte)(pixels[row * 18 + index] - (index >= 3 ? pixels[row * 18 + index - 3] : 0)));
        var pdf = OnePage(Draw, Image(5, 6, 3, "/DeviceRGB", 8, differences, "/DecodeParms << /Predictor 2 /Colors 3 /BitsPerComponent 8 /Columns 6 >>"));

        AssertSamples(pixels, ReadPng(Extracted(pdf).Content), 3);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(1)]
    public void Gray_samples_of_any_depth_become_an_eight_bit_grey_png(int bits)
    {
        // Width 5 is not a multiple of the byte, so each row's padding bits have to be skipped.
        const int width = 5, height = 3;
        var maximum = (1 << bits) - 1;
        var values = new int[width * height];
        for (var index = 0; index < values.Length; index++) values[index] = index * 3 % (maximum + 1);
        var rowBytes = (width * bits + 7) / 8;
        var packed = new byte[rowBytes * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var bit = column * bits;
                packed[row * rowBytes + bit / 8] |= (byte)(values[row * width + column] << (8 - bits - bit % 8));
            }
        var pdf = OnePage(Draw, Image(5, width, height, "/DeviceGray", bits, packed));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(0, png.ColorType);
        Assert.Equal(values.Select(value => (byte)(value * 255 / maximum)).ToArray(), png.Samples);
    }

    [Fact]
    public void A_decode_array_that_inverts_grey_is_applied()
    {
        var pdf = OnePage(Draw, Image(5, 2, 1, "/DeviceGray", 8, [0, 200], "/Decode [1 0]"));

        Assert.Equal(new byte[] { 255, 55 }, ReadPng(Extracted(pdf).Content).Samples);
    }

    [Theory]
    [InlineData(8, "hex")]
    [InlineData(4, "hex")]
    [InlineData(8, "stream")]
    public void Indexed_images_keep_their_palette(int bits, string table)
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        var indexes = new byte[] { 0, 1, 2, 3, 3, 2 };
        var packed = bits == 8 ? indexes : [0x01, 0x20, 0x33, 0x20];
        var colorSpace = table == "hex"
            ? $"[/Indexed /DeviceRGB 3 <{Convert.ToHexString(palette)}>]"
            : "[/Indexed /DeviceRGB 3 9 0 R]";
        var objects = new List<PdfObject> { Image(5, 3, 2, colorSpace, bits, packed) };
        if (table == "stream") objects.Add(new PdfObject(9, "/Filter /FlateDecode", Deflate(palette)));
        var pdf = OnePage(Draw, [.. objects]);

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(3, png.ColorType);
        Assert.Equal(indexes, png.Samples);
        Assert.Equal(palette, png.Palette);
    }

    [Fact]
    public void An_image_whose_dictionary_is_longer_than_a_header_window_is_still_found()
    {
        // A 256-entry palette written as hex, plus a long private entry, puts the object's start thousands of bytes before its stream.
        var palette = Enumerable.Range(0, 256).SelectMany(index => new[] { (byte)index, (byte)(255 - index), (byte)(index / 2) }).ToArray();
        var indexes = Enumerable.Range(0, 12).Select(index => (byte)(index * 21)).ToArray();
        var pdf = OnePage(Draw, Image(5, 4, 3, $"[/Indexed /DeviceRGB 255 <{Convert.ToHexString(palette)}>]", 8, indexes,
            "/Private (" + new string('x', 6000) + ")"));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(indexes, png.Samples);
        Assert.Equal(palette, png.Palette);
    }

    [Fact]
    public void An_indexed_image_over_grey_gets_a_grey_palette()
    {
        var pdf = OnePage(Draw, Image(5, 2, 1, "[/Indexed /DeviceGray 1 <20E0>]", 8, [1, 0]));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(3, png.ColorType);
        Assert.Equal(new byte[] { 0x20, 0x20, 0x20, 0xE0, 0xE0, 0xE0 }, png.Palette);
        Assert.Equal(new byte[] { 1, 0 }, png.Samples);
    }

    [Theory]
    [InlineData(3, 3, 2)]
    [InlineData(1, 1, 0)]
    public void An_icc_profile_of_one_or_three_components_is_read_as_grey_or_rgb(int components, int channels, int colorType)
    {
        var pixels = Gradient(4, 2, channels);
        var pdf = OnePage(Draw, Image(5, 4, 2, "[/ICCBased 8 0 R]", 8, pixels), new PdfObject(8, $"/N {components} /Alternate /DeviceRGB", [0, 1, 2]));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(colorType, png.ColorType);
        AssertSamples(pixels, png, channels);
    }

    [Fact]
    public void A_soft_mask_becomes_the_alpha_channel()
    {
        var pixels = Gradient(3, 2, 3);
        byte[] alpha = [0, 64, 128, 192, 255, 7];
        var pdf = OnePage(Draw, Image(5, 3, 2, "/DeviceRGB", 8, pixels, "/SMask 6 0 R"), Image(6, 3, 2, "/DeviceGray", 8, alpha));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(6, png.ColorType);
        for (var pixel = 0; pixel < 6; pixel++)
            Assert.Equal([pixels[pixel * 3], pixels[pixel * 3 + 1], pixels[pixel * 3 + 2], alpha[pixel]], png.Pixel(pixel % 3, pixel / 3));
    }

    [Fact]
    public void A_soft_mask_of_another_size_is_resampled_and_a_grey_image_gets_a_grey_alpha_png()
    {
        byte[] pixels = [10, 20, 30, 40];
        byte[] mask = [0, 255];
        var pdf = OnePage(Draw, Image(5, 2, 2, "/DeviceGray", 8, pixels, "/SMask 6 0 R"), Image(6, 2, 1, "/DeviceGray", 8, mask));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(4, png.ColorType);
        Assert.Equal(new byte[] { 10, 0, 20, 255, 30, 0, 40, 255 }, png.Samples);
    }

    [Fact]
    public void An_indexed_image_with_a_soft_mask_is_written_as_rgba()
    {
        var pdf = OnePage(Draw, Image(5, 2, 1, "[/Indexed /DeviceRGB 1 <FF000000FF00>]", 8, [0, 1], "/SMask 6 0 R"), Image(6, 2, 1, "/DeviceGray", 8, [100, 200]));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(6, png.ColorType);
        Assert.Equal(new byte[] { 255, 0, 0, 100, 0, 255, 0, 200 }, png.Samples);
    }

    [Fact]
    public void A_jpeg_is_kept_byte_for_byte()
    {
        var jpeg = Jpeg(30, 20, 3);
        var pdf = OnePage(Draw, new PdfObject(5, "/Type /XObject /Subtype /Image /Width 30 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));

        var image = Extracted(pdf);

        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(jpeg, image.Content);
        Assert.Equal((30, 20), (image.PixelWidth, image.PixelHeight));
    }

    [Fact]
    public void A_grey_jpeg_behind_ascii85_and_a_progressive_one_are_kept()
    {
        var jpeg = Jpeg(8, 8, 1, marker: 0xC2);
        var pdf = OnePage(Draw, new PdfObject(5, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter [/ASCIIHexDecode /DCTDecode]",
            Encoding.ASCII.GetBytes(Convert.ToHexString(jpeg) + ">")));

        Assert.Equal(jpeg, Extracted(pdf).Content);
    }

    [Fact]
    public void A_cmyk_jpeg_is_not_extracted()
    {
        var pdf = OnePage(Draw, new PdfObject(5, "/Type /XObject /Subtype /Image /Width 30 /Height 20 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode", Jpeg(30, 20, 4)));

        AssertPlaceholder(pdf, "CMYK");
    }

    private static void AssertPlaceholder(byte[] pdf, string reason)
    {
        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);
        Assert.Null(page.ExtractedImages);
        Assert.Equal(1, page.EmbeddedImageCount);
        Assert.Single(page.EmbeddedImages!);
        var diagnostic = Assert.Single(result.Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted:", StringComparison.Ordinal));
        Assert.Contains("PDF page 1: 1 embedded image(s) were not extracted", diagnostic, StringComparison.Ordinal);
        Assert.Contains(reason, diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Filter /JPXDecode", "JPXDecode")]
    [InlineData("/Filter /JBIG2Decode", "JBIG2Decode")]
    [InlineData("/Filter /CCITTFaxDecode", "CCITTFaxDecode")]
    [InlineData("/Filter /LZWDecode", "LZWDecode")]
    [InlineData("/Filter /RunLengthDecode", "RunLengthDecode")]
    public void A_filter_without_a_decoder_keeps_the_placeholder_and_the_diagnostic(string filter, string reason)
    {
        var pdf = OnePage(Draw, new PdfObject(5, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 " + filter, [1, 2, 3, 4, 5, 6, 7, 8]));

        AssertPlaceholder(pdf, reason);
    }

    [Theory]
    [InlineData("/DeviceCMYK", 8, 4, "DeviceCMYK")]
    [InlineData("[/Separation /Spot /DeviceGray << >>]", 8, 1, "Separation")]
    [InlineData("[/ICCBased 8 0 R]", 8, 4, "DeviceCMYK")]
    [InlineData("/DeviceRGB", 16, 6, "16 bits")]
    public void Other_colour_spaces_and_depths_keep_the_placeholder(string colorSpace, int bits, int channelBytes, string reason)
    {
        var pdf = OnePage(Draw, Image(5, 2, 2, colorSpace, bits, new byte[4 * channelBytes]), new PdfObject(8, "/N 4", [0]));

        AssertPlaceholder(pdf, reason);
    }

    [Fact]
    public void A_decode_array_that_remaps_colours_and_a_stencil_mask_keep_the_placeholder()
    {
        AssertPlaceholder(OnePage(Draw, Image(5, 2, 1, "/DeviceRGB", 8, new byte[6], "/Decode [0 0.5 0 1 0 1]")), "/Decode");
        AssertPlaceholder(OnePage(Draw, Image(5, 8, 1, "/DeviceGray", 1, [0xA5], "/ImageMask true")), "stencil");
    }

    [Fact]
    public void A_soft_mask_that_cannot_be_applied_keeps_the_placeholder_instead_of_an_opaque_picture()
    {
        var pdf = OnePage(Draw, Image(5, 2, 1, "/DeviceGray", 8, [1, 2], "/SMask 6 0 R"),
            new PdfObject(6, "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", Jpeg(2, 1, 1)));

        AssertPlaceholder(pdf, "soft mask");
    }

    [Fact]
    public void Damaged_streams_fall_back_without_throwing()
    {
        var truncated = Deflate(Gradient(8, 8, 3))[..20];
        var tooShort = Deflate(new byte[10]);
        var cases = new[]
        {
            // Not zlib data at all.
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", "this is not a zlib stream"u8.ToArray()),
            // A zlib stream cut off in the middle.
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", truncated),
            // Fewer samples than the size says.
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", tooShort),
            // A size that would need gigabytes.
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 100000 /Height 100000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", tooShort),
            // No size, an unreadable colour space, an unclosed palette, an impossible predictor, a JPEG that is no JPEG.
            new PdfObject(5, "/Type /XObject /Subtype /Image /ColorSpace /DeviceRGB /BitsPerComponent 8", [1, 2, 3]),
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace [/Indexed /DeviceRGB /BitsPerComponent 8", [1, 2, 3]),
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Columns 9 >>", Deflate(new byte[40])),
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", "not a jpeg at all"u8.ToArray()),
            // A dictionary with no stream.
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8"),
        };

        foreach (var broken in cases)
        {
            var result = PdfTextExtractor.Extract(OnePage(Draw, broken));
            var page = Assert.Single(result.Pages);
            Assert.Null(page.ExtractedImages);
            Assert.Equal(1, page.EmbeddedImageCount);
            Assert.Contains(result.Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted:", StringComparison.Ordinal));
            Assert.Contains("Body text", result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_page_with_one_decodable_and_one_undecodable_image_reports_exactly_the_one_left()
    {
        var content = "q 60 0 0 40 20 140 cm /Im1 Do Q\nq 60 0 0 40 20 60 cm /Im2 Do Q";
        var pdf = Build([new PdfPage(content, "/Im1 5 0 R /Im2 6 0 R")],
        [
            Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4]),
            new PdfObject(6, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", [1, 2, 3]),
        ]);

        var result = PdfTextExtractor.Extract(pdf);
        var page = Assert.Single(result.Pages);

        Assert.Equal(1, page.EmbeddedImageCount);
        Assert.Equal(60d, Assert.Single(page.EmbeddedImages!).Y, 6);
        Assert.Equal(140d, Assert.Single(page.ExtractedImages!).Bounds.Y, 6);
        Assert.Contains("PDF page 1: 1 embedded image(s) were not extracted", Assert.Single(result.Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted:", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_image_is_still_only_counted()
    {
        var pdf = OnePage("q 120 0 0 60 30 100 cm BI /W 4 /H 4 /CS /G /BPC 8 ID 0123456789abcdef EI Q");

        AssertPlaceholder(pdf, "inline");
    }

    [Fact]
    public void Images_are_listed_in_reading_order_and_each_keeps_its_own_rectangle()
    {
        var content = "q 30 0 0 20 10 20 cm /Im1 Do Q\nq 30 0 0 20 100 120 cm /Im2 Do Q\nq 30 0 0 20 10 120 cm /Im3 Do Q";
        var pdf = Build([new PdfPage(content, "/Im1 5 0 R /Im2 6 0 R /Im3 7 0 R")],
            [Image(5, 1, 1, "/DeviceGray", 8, [1]), Image(6, 1, 1, "/DeviceGray", 8, [2]), Image(7, 1, 1, "/DeviceGray", 8, [3])]);

        var images = Assert.IsAssignableFrom<IReadOnlyList<PdfExtractedImage>>(Single(pdf).ExtractedImages);

        Assert.Equal(["pdf-img-0007", "pdf-img-0006", "pdf-img-0005"], images.Select(image => image.AssetKey));
    }

    [Fact]
    public void A_picture_at_the_same_place_on_every_page_is_furniture_and_nobody_reports_it()
    {
        var shared = Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4]);
        var unique = Image(6, 2, 2, "/DeviceGray", 8, [9, 8, 7, 6]);
        var pages = new[]
        {
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", "/Im1 5 0 R", "First"),
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q\nq 60 0 0 40 20 60 cm /Im2 Do Q", "/Im1 5 0 R /Im2 6 0 R", "Second"),
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", "/Im1 5 0 R", "Third"),
        };

        var result = PdfTextExtractor.Extract(Build(pages, [shared, unique]));

        Assert.Equal(new string?[] { null, "pdf-img-0006", null }, result.Pages.Select(page => page.ExtractedImages?.Single().AssetKey));
        Assert.All(result.Pages, page => Assert.Equal(0, page.EmbeddedImageCount));
        Assert.DoesNotContain(result.Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted", StringComparison.Ordinal));
    }

    [Fact]
    public void A_running_logo_that_cannot_be_decoded_is_not_reported_on_every_page_either()
    {
        var logo = new PdfObject(5, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", [1, 2, 3]);
        var pages = new[]
        {
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "First"),
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "Second"),
        };

        var result = PdfTextExtractor.Extract(Build(pages, [logo]));

        Assert.All(result.Pages, page => Assert.Equal(0, page.EmbeddedImageCount));
        Assert.DoesNotContain(result.Diagnostics!, item => item.StartsWith("PdfEmbeddedImageOmitted", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_picture_at_different_places_is_content_each_time_and_shares_one_file()
    {
        var pages = new[]
        {
            new PdfPage("q 20 0 0 20 10 170 cm /Im1 Do Q", Text: "First"),
            new PdfPage("q 20 0 0 20 100 100 cm /Im1 Do Q", Text: "Second"),
        };

        var result = PdfTextExtractor.Extract(Build(pages, [Image(5, 1, 1, "/DeviceGray", 8, [1])]));

        Assert.All(result.Pages, page => Assert.Equal("pdf-img-0005", Assert.Single(page.ExtractedImages!).AssetKey));
        Assert.Single(DocumentService.ExtractedPdfImageAssets(result.Pages));
    }

    [Fact]
    public void A_vertically_flipped_image_has_its_rows_reversed()
    {
        var pdf = OnePage("q 40 0 0 -30 10 130 cm /Im1 Do Q", Image(5, 3, 2, "/DeviceGray", 8, [10, 20, 30, 40, 50, 60]));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(new byte[] { 40, 50, 60, 10, 20, 30 }, png.Samples);
    }

    [Fact]
    public void A_horizontally_flipped_image_has_each_row_reversed()
    {
        var pdf = OnePage("q -40 0 0 30 50 100 cm /Im1 Do Q", Image(5, 3, 2, "/DeviceGray", 8, [10, 20, 30, 40, 50, 60]));

        var png = ReadPng(Extracted(pdf).Content);

        Assert.Equal(new byte[] { 30, 20, 10, 60, 50, 40 }, png.Samples);
    }

    [Fact]
    public void An_image_turned_a_quarter_is_written_upright_and_sized_as_drawn()
    {
        // 0 1 -1 0: the picture's x axis runs up the page and its y axis (its top) to the left, a 90 degree turn counter-clockwise.
        var pdf = OnePage("q 0 40 -30 0 80 100 cm /Im1 Do Q", Image(5, 3, 2, "/DeviceGray", 8, [10, 20, 30, 40, 50, 60]));

        var image = Extracted(pdf);
        var png = ReadPng(image.Content);

        Assert.Equal((2, 3), (png.Width, png.Height));
        Assert.Equal(new byte[] { 30, 60, 20, 50, 10, 40 }, png.Samples);
        Assert.Equal((30d, 40d), (image.DrawnWidth, image.DrawnHeight));
    }

    [Fact]
    public void A_mirrored_jpeg_cannot_be_turned_and_keeps_the_placeholder()
    {
        var pdf = OnePage("q 40 0 0 -30 10 130 cm /Im1 Do Q",
            new PdfObject(5, "/Type /XObject /Subtype /Image /Width 30 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", Jpeg(30, 20, 3)));

        AssertPlaceholder(pdf, "mirrored");
    }

    [Fact]
    public void An_image_only_page_keeps_its_count_and_extracts_nothing()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page /Contents 2 0 R /Resources << /XObject << /Im1 5 0 R >> >> >> endobj\n" +
            "2 0 obj << /Length 28 >> stream\nq 60 0 0 40 20 100 cm /Im1 Do Q\nendstream endobj\n" +
            "5 0 obj << /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >> stream\n\u0001\nendstream endobj\n%%EOF");

        var page = Single(pdf);

        Assert.True(page.IsImageOnly);
        Assert.Equal(1, page.EmbeddedImageCount);
        Assert.Null(page.ExtractedImages);
    }

    [Fact]
    public void The_graph_has_an_image_node_with_alt_text_display_size_and_reading_position()
    {
        // Text at the top, the picture below it, a caption below that: the node reads between the two lines.
        var content = "q 60 0 0 30 20 120 cm /Im1 Do Q\nBT 10 100 Td (Caption below) Tj ET";
        var pdf = Build([new PdfPage(content)], [Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4])]);
        var extraction = PdfTextExtractor.Extract(pdf);

        var graph = PdfDocumentGraphProjection.CreateGraph(extraction, new string('b', 64));
        var nodes = Assert.Single(graph.Partitions).Nodes.OrderBy(node => node.Order).ToArray();

        Assert.Equal([NodeKind.Paragraph, NodeKind.Image, NodeKind.Paragraph], nodes.Select(node => node.Kind));
        var image = nodes[1];
        var reference = Assert.IsType<ReferenceNodeContent>(image.Content);
        Assert.Equal(("pdf-img-0005", "Image 1 on page 1"), (reference.Reference, reference.AltText));
        Assert.Equal(ContentLayer.Body, image.Layer);
        Assert.Equal(80d, image.Extensions!["display_width_px"].GetDouble(), 6);
        Assert.Equal(40d, image.Extensions["display_height_px"].GetDouble(), 6);
        Assert.Equal(120d, image.Geometry!.Y, 6);
        Assert.DoesNotContain(nodes, node => node.Extensions?.ContainsKey("pdf_embedded_image_placeholder") == true);
        Assert.DoesNotContain(PdfDocumentGraphProjection.Diagnostics(extraction), item => item.Code == "PdfEmbeddedImageOmitted");
    }

    [Fact]
    public void A_tagged_pdfs_figure_description_names_its_image()
    {
        var pdf = Build([new PdfPage(Draw)], [Image(5, 1, 1, "/DeviceGray", 8, [1]),
            new PdfObject(30, "/Type /StructElem /S /Figure /Pg 100 0 R /Alt (A red square)")]);

        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(pdf), new string('c', 64));

        var image = Assert.Single(graph.Nodes, node => node.Kind == NodeKind.Image);
        Assert.Equal("A red square", Assert.IsType<ReferenceNodeContent>(image.Content).AltText);
    }

    [Fact]
    public void The_graph_keeps_a_placeholder_only_for_the_images_that_were_not_extracted()
    {
        var content = "q 60 0 0 40 20 140 cm /Im1 Do Q\nq 60 0 0 40 20 60 cm /Im2 Do Q";
        var pdf = Build([new PdfPage(content, "/Im1 5 0 R /Im2 6 0 R")],
        [
            Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4]),
            new PdfObject(6, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", [1, 2, 3]),
        ]);

        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(pdf), new string('d', 64));

        Assert.Single(graph.Nodes, node => node.Kind == NodeKind.Image);
        var placeholder = Assert.Single(graph.Nodes, node => node.Extensions?.ContainsKey("pdf_embedded_image_placeholder") == true);
        Assert.Contains("1 embedded image(s) not extracted", Assert.IsType<TextNodeContent>(placeholder.Content).Text, StringComparison.Ordinal);
        Assert.Equal(60d, placeholder.Geometry!.Y, 6);
    }
}

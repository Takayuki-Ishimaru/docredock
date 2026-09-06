using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class PngRasterImageTests
{
    /// <summary>A gradient with a distinct value in every channel of every pixel, so a transposed
    /// row, a dropped filter byte or a swapped channel all show up as a mismatch.</summary>
    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 3;
                rgb[at] = (byte)(x * 7 + 1);
                rgb[at + 1] = (byte)(y * 11 + 2);
                rgb[at + 2] = (byte)(x * y + 3);
            }
        return rgb;
    }

    [Fact]
    public void Encoded_pixels_survive_a_decode_unchanged()
    {
        var rgb = Gradient(13, 7);

        var decoded = PngRasterImage.Decode(PngRasterImage.Encode(13, 7, rgb));

        Assert.Equal(13, decoded.Width);
        Assert.Equal(7, decoded.Height);
        Assert.Equal(rgb, decoded.RgbBytes);
    }

    [Fact]
    public void Encoded_bytes_are_a_png_with_the_chunks_and_crcs_a_decoder_requires()
    {
        var png = PngRasterImage.Encode(4, 4, Gradient(4, 4));

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        // Length 13, "IHDR", 4x4, 8-bit truecolour, no interlacing.
        Assert.Equal(new byte[] { 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 4, 0, 0, 0, 4, 8, 2, 0, 0, 0 }, png[8..29]);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x41, 0x54 }, png[37..41]);
        // A zero-length IEND with its fixed CRC - proof the checksum really is the PNG polynomial
        // and not one a decoder would reject.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, png[^12..]);
    }

    [Fact]
    public void Crop_returns_only_the_requested_rectangle()
    {
        var source = PngRasterImage.Decode(PngRasterImage.Encode(10, 8, Gradient(10, 8)));

        var cropped = source.Crop(3, 2, 4, 5);

        Assert.Equal(4, cropped.Width);
        Assert.Equal(5, cropped.Height);
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 4; x++)
            {
                var expected = ((y + 2) * 10 + (x + 3)) * 3;
                var actual = (y * 4 + x) * 3;
                Assert.Equal(source.RgbBytes[expected..(expected + 3)], cropped.RgbBytes[actual..(actual + 3)]);
            }
        // The crop carries encoded bytes of its own, so a caller can hand it straight to OCR.
        Assert.Equal(cropped.RgbBytes, PngRasterImage.Decode(cropped.PngBytes).RgbBytes);
    }

    [Fact]
    public void Crop_refuses_a_rectangle_that_leaves_the_image()
    {
        var source = PngRasterImage.Decode(PngRasterImage.Encode(6, 6, Gradient(6, 6)));

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Crop(4, 0, 4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Crop(0, 0, 0, 2));
    }

    [Fact]
    public void Encode_rejects_a_buffer_that_does_not_match_the_dimensions()
    {
        Assert.Throws<ArgumentException>(() => PngRasterImage.Encode(4, 4, new byte[4 * 4 * 3 - 1]));
    }
}

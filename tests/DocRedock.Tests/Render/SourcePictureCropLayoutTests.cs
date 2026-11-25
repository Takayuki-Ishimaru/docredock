using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

/// <summary>The page layout of a cropped picture the converter cannot decode (JPEG, GIF, WebP, BMP). A browser that
/// magnifies a picture blends each sample from the two source pixels whose centres surround it, so the first pixel
/// a crop excludes shows through wherever the clip edge lies less than half a pixel inside the first retained one.
/// The expected windows follow from that rule and the pixel sizes below, not from the writer.</summary>
public sealed class SourcePictureCropLayoutTests
{
    [Theory]
    // 100 px, a third off each side: the crop runs from 33.33 to 66.67 and retains pixels 33 to 66.
    [InlineData(100, 33, 67, 300)]
    // 101 px: from 33.67 to 67.33, retaining pixels 34 to 66.
    [InlineData(101, 34, 67, 300)]
    // Shown at one CSS pixel per source pixel, where the browser's rounding to device pixels weighs most.
    [InlineData(100, 33, 67, 34)]
    public void A_third_cropped_JPEG_is_clipped_inside_the_centres_of_its_edge_pixels(int width, int firstRetained, int endRetained, double frameWidth)
    {
        const double third = .33333;
        var (start, end, top, bottom) = Window(Html(Jpeg(width, 20), "image/jpeg", frameWidth, 60, third, 0, third, 0), width, 20);
        // No sample of the visible part can reach the excluded neighbour on either side.
        Assert.True(start >= firstRetained + .5, $"left edge {start} lets pixel {firstRetained - 1} show");
        Assert.True(end <= endRetained - .5, $"right edge {end} lets pixel {endRetained} show");
        // The visible crop stays within one source pixel of the requested one.
        Assert.InRange(start - third * width, 0, 1);
        Assert.InRange((1 - third) * width - end, 0, 1);
        // The uncropped axis keeps the whole picture.
        Assert.Equal(0, top, 6);
        Assert.Equal(20, bottom, 6);
    }

    [Fact]
    public void The_crop_window_is_stretched_over_the_frame_so_the_clip_edges_are_the_window_edges()
    {
        var html = Html(Jpeg(100, 20), "image/jpeg", 300, 60, .33333, .25, .33333, .25);
        var style = PictureStyle(html);
        var (start, end, top, bottom) = Window(html, 100, 20);
        // Pixels 33 to 66 are retained and shown at 300 / 34 CSS px each. Each edge moves in by half a source pixel
        // and half a CSS pixel (34 / 300 of a source pixel): the window is 33.5567 to 66.4433, and the picture box
        // is the frame times 100 / 32.8867, shifted left by 33.5567 / 32.8867 of the frame.
        Assert.Equal(304.074, style.Width, 2);
        Assert.Equal(-102.037, style.Left, 2);
        Assert.Equal(33.5567, start, 3);
        Assert.Equal(66.4433, end, 3);
        // 20 px high, a quarter off the top and the bottom: rows 5 to 14 are retained, and the clip stays inside their centres.
        Assert.True(top >= 5.5 && bottom <= 14.5, $"rows {top} to {bottom}");
        Assert.DoesNotContain("data:image/png", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reduced_picture_moves_its_edge_by_at_most_one_display_pixel()
    {
        // 1000 px shown in 100 CSS px: ten source pixels to the display pixel. The crop edge at 250 keeps its pixel
        // boundary and moves in by half a source pixel and half a display pixel, never more than one display pixel.
        var (start, end, _, _) = Window(Html(Jpeg(1000, 20), "image/jpeg", 100, 20, .25, 0, .25, 0), 1000, 20);
        Assert.InRange(start, 250.5, 255);
        Assert.InRange(end, 745, 749.5);
    }

    [Fact]
    public void A_crop_to_a_single_pixel_column_shows_that_column_from_its_centre()
    {
        // Three columns, the middle one retained: the only place no neighbour is blended in is its centre, 1.5.
        var (start, end, _, _) = Window(Html(Jpeg(3, 3), "image/jpeg", 30, 30, .33333, 0, .33333, 0), 3, 3);
        Assert.InRange(start, 1.5, 1.52);
        Assert.InRange(end, start, 1.52);
        Assert.True(end > start);
    }

    [Fact]
    public void A_picture_whose_header_cannot_be_read_keeps_the_fractional_clip()
    {
        // The start of image marker alone: the type matches and the picture is embedded, but no frame header says its size.
        byte[] corrupt = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9];
        var style = PictureStyle(Html(corrupt, "image/jpeg", 300, 60, .33333, 0, .33333, 0));
        const double shown = 1 - .33333 - .33333;
        Assert.Equal(100 / shown, style.Width, 3);
        Assert.Equal(-100 * .33333 / shown, style.Left, 3);
        Assert.Equal(100, style.Height, 3);
        Assert.Equal(0, style.Top, 3);
    }

    [Fact]
    public void An_uncropped_picture_and_a_cropped_PNG_are_laid_out_as_before()
    {
        var plain = PictureStyle(Html(Jpeg(100, 20), "image/jpeg", 300, 60, 0, 0, 0, 0));
        Assert.Equal((100d, 100d, 0d, 0d), (plain.Width, plain.Height, plain.Left, plain.Top));
        // A PNG is still cropped to its retained pixels before the browser sees it, and fills the frame.
        var rgb = Enumerable.Range(0, 100).SelectMany(x => x is >= 33 and < 67 ? new byte[] { 0, 0, 255 } : new byte[] { 255, 0, 0 }).ToArray();
        var html = Html(PngRasterImage.Encode(100, 1, rgb), "image/png", 300, 60, .33333, 0, .33333, 0);
        var png = PictureStyle(html);
        Assert.Equal((100d, 100d, 0d, 0d), (png.Width, png.Height, png.Left, png.Top));
        var cropped = PngRasterImage.Decode(Convert.FromBase64String(Regex.Match(html, "src=\"data:image/png;base64,([^\"]+)\"").Groups[1].Value));
        Assert.Equal(34, cropped.Width);
        Assert.All(Enumerable.Range(0, 34), x => Assert.Equal(new byte[] { 0, 0, 255 }, cropped.RgbBytes.AsSpan(x * 3, 3).ToArray()));
    }

    [Fact]
    public void JPEG_size_comes_from_the_frame_header_after_other_segments_and_follows_the_Exif_orientation()
    {
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(Jpeg(640, 480), out var width, out var height));
        Assert.Equal((640, 480), (width, height));
        // A progressive frame (SOF2) behind a comment segment.
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(Jpeg(301, 77, marker: 0xC2, before: [0xFF, 0xFE, 0x00, 0x05, 1, 2, 3]), out width, out height));
        Assert.Equal((301, 77), (width, height));
        // Orientation 6 (turned a quarter): the browser lays the picture out 480 wide and 640 high.
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(Jpeg(640, 480, before: Exif(6)), out width, out height));
        Assert.Equal((480, 640), (width, height));
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(Jpeg(640, 480, before: Exif(3)), out width, out height));
        Assert.Equal((640, 480), (width, height));
    }

    [Fact]
    public void GIF_size_is_the_logical_screen()
    {
        byte[] gif = [.. "GIF89a"u8, 0x2C, 0x01, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x3B];
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(gif, out var width, out var height));
        Assert.Equal((300, 200), (width, height));
        Assert.True(SourcePictureCropWindow.TryReadPixelSize([.. "GIF87a"u8, 0x01, 0x00, 0x02, 0x00], out width, out height));
        Assert.Equal((1, 2), (width, height));
    }

    [Fact]
    public void WebP_size_is_read_from_the_lossy_lossless_and_extended_headers()
    {
        // VP8: frame tag, start code 9D 01 2A, then 14-bit width and height (the two top bits are a scale).
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(WebP("VP8 ", [0x10, 0x00, 0x00, 0x9D, 0x01, 0x2A, 0x90, 0x41, 0xC8, 0x80]), out var width, out var height));
        Assert.Equal((400, 200), (width, height));
        // VP8L: signature 2F, then width - 1 and height - 1 in 14 bits each: 399 | 199 << 14.
        var bits = 399u | 199u << 14;
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(WebP("VP8L", [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)]), out width, out height));
        Assert.Equal((400, 200), (width, height));
        // VP8X: flags, three reserved bytes, then canvas width - 1 and height - 1 in 24 bits each.
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(WebP("VP8X", [0x10, 0, 0, 0, 0x8F, 0x01, 0x00, 0xC7, 0x00, 0x00]), out width, out height));
        Assert.Equal((400, 200), (width, height));
    }

    [Fact]
    public void BMP_size_is_read_from_either_header_and_a_top_down_bitmap_is_as_tall()
    {
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(Bmp(40, 123, -45), out var width, out var height));
        Assert.Equal((123, 45), (width, height));
        var core = new byte[26]; core[0] = (byte)'B'; core[1] = (byte)'M'; core[14] = 12; core[18] = 7; core[20] = 9;
        Assert.True(SourcePictureCropWindow.TryReadPixelSize(core, out width, out height));
        Assert.Equal((7, 9), (width, height));
    }

    [Fact]
    public void Corrupt_truncated_and_foreign_headers_give_no_size()
    {
        var jpeg = Jpeg(640, 480);
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(jpeg.AsSpan(0, jpeg.Length - 8), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(Jpeg(0, 480), out _, out _));
        // A segment that claims to be longer than the file.
        Assert.False(SourcePictureCropWindow.TryReadPixelSize([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0x00], out _, out _));
        // A scan before any frame header.
        Assert.False(SourcePictureCropWindow.TryReadPixelSize([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9], out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize([.. "GIF89a"u8, 0x00, 0x00, 0x05, 0x00], out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(WebP("VP8 ", [0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x90, 0x01, 0xC8, 0x00]), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(WebP("VP8L", [0x00, 1, 2, 3, 4]), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(WebP("ALPH", new byte[10]), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(Bmp(40, -5, 10), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize(PngRasterImage.Encode(1, 1, [1, 2, 3]), out _, out _));
        Assert.False(SourcePictureCropWindow.TryReadPixelSize([], out _, out _));
        Assert.False(SourcePictureCropWindow.TryFit(0, 10, .1, 0, .1, 0, 100, 100, out _));
        Assert.False(SourcePictureCropWindow.TryFit(10, 10, -.1, 0, .1, 0, 100, 100, out _));
        Assert.False(SourcePictureCropWindow.TryFit(10, 10, .6, 0, .6, 0, 100, 100, out _));
        Assert.False(SourcePictureCropWindow.TryFit(10, 10, double.NaN, 0, 0, 0, 100, 100, out _));
    }

    private static string Html(byte[] picture, string type, double frameWidth, double frameHeight, double left, double top, double right, double bottom)
    {
        var item = new WorkbookDrawingObject("picture", "picture", frameWidth, frameHeight, new(1, 0, 0, 1, 10, 10))
        {
            ImageReference = "data:" + type + ";base64," + Convert.ToBase64String(picture), Text = "PHOTO",
            CropLeft = left, CropTop = top, CropRight = right, CropBottom = bottom,
        };
        return ReadableHtmlWriter.WriteDocument(new("doc", "en", DocumentFormatKind.Pptx,
            [new ReadableSourceDrawingScene(new("source", 800, 400, [item]))])).Html;
    }

    private static (double Width, double Height, double Left, double Top) PictureStyle(string html)
    {
        var match = Regex.Match(html, "<div class=\"source-picture-clip\"><div style=\"position:absolute;width:([-0-9.]+)%;height:([-0-9.]+)%;left:([-0-9.]+)%;top:([-0-9.]+)%\"><img ");
        Assert.True(match.Success, "the cropped picture is an embedded <img> in a clip box");
        double Value(int group) => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        return (Value(1), Value(2), Value(3), Value(4));
    }

    // The part of the picture, in source pixels, that the clip box shows: the box is 100% of the frame, and the
    // picture is laid out at the written percentages of it.
    private static (double Start, double End, double Top, double Bottom) Window(string html, int width, int height)
    {
        var style = PictureStyle(html);
        var start = -style.Left / style.Width * width;
        var top = -style.Top / style.Height * height;
        return (start, start + 100 / style.Width * width, top, top + 100 / style.Height * height);
    }

    private static byte[] Jpeg(int width, int height, byte marker = 0xC0, byte[]? before = null)
    {
        var frame = new byte[] { 0xFF, marker, 0x00, 0x0B, 0x08, 0, 0, 0, 0, 0x01, 0x01, 0x11, 0x00 };
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(7), (ushort)width);
        return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0, .. before ?? [], .. frame, 0xFF, 0xD9];
    }

    // An APP1 segment whose only directory entry is the orientation.
    private static byte[] Exif(ushort orientation)
    {
        byte[] tiff = [.. "MM"u8, 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, 0, 0, 0, 0];
        var length = 2 + 6 + tiff.Length;
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. tiff];
    }

    private static byte[] WebP(string chunk, byte[] data)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        var riff = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riff, (uint)(12 + data.Length));
        return [.. "RIFF"u8, .. riff, .. "WEBP"u8, .. System.Text.Encoding.ASCII.GetBytes(chunk), .. size, .. data];
    }

    private static byte[] Bmp(uint header, int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        return bytes;
    }
}

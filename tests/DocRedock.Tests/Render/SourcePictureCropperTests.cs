using System.Buffers.Binary;
using System.IO.Compression;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class SourcePictureCropperTests
{
    [Theory]
    [InlineData(.33333)]
    [InlineData(.35)]
    public void Fractional_crops_resample_the_retained_region_without_outside_color_bleed(double side)
    {
        var rgb = Enumerable.Range(0, 300).SelectMany(i => i % 30 < 10 ? new byte[] { 255, 0, 0 } : i % 30 < 20 ? new byte[] { 0, 255, 0 } : new byte[] { 0, 0, 255 }).ToArray();
        var source = PngRasterImage.Encode(30, 10, rgb);
        var copy = source.ToArray();
        var cropped = PngSourceCropper.TryCrop(source, side, 0, side, 0);
        Assert.NotNull(cropped);
        var result = PngRasterImage.Decode(cropped);
        Assert.InRange(result.Width, 9, 10);
        Assert.Equal(10, result.Height);
        Assert.All(Enumerable.Range(0, result.Width * result.Height), i =>
            Assert.Equal(new byte[] { 0, 255, 0 }, result.RgbBytes.AsSpan(i * 3, 3).ToArray()));
        Assert.Equal(copy, source);
    }

    [Fact]
    public void A_crop_between_two_source_pixels_copies_whole_pixels_instead_of_blending_neighbours()
    {
        // 100 px is not divisible by three: a 33.333% crop starts a third of the way into pixel 33.
        var rgb = Enumerable.Range(0, 100).SelectMany(x => x % 2 == 0 ? new byte[] { 0, 0, 0 } : new byte[] { 255, 255, 255 }).ToArray();
        var result = PngRasterImage.Decode(PngSourceCropper.TryCrop(PngRasterImage.Encode(100, 1, rgb), .33333, 0, .33333, 0)!);
        Assert.Equal(34, result.Width);
        Assert.Equal(rgb.AsSpan(33 * 3, 34 * 3).ToArray(), result.RgbBytes);
    }

    [Fact]
    public void Cropped_transparency_is_retained_instead_of_compositing_onto_white()
    {
        var source = RgbaPng(2, 1, [255, 0, 0, 128, 0, 0, 255, 0]);
        var red = PngSourceCropper.TryCrop(source, 0, 0, .5, 0)!;
        var clear = PngSourceCropper.TryCrop(source, .5, 0, 0, 0)!;
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, RawRgba(red));
        Assert.Equal(0, RawRgba(clear)[3]);
    }

    [Fact]
    public async Task Crop_is_cancellable_stateless_and_rejects_invalid_regions()
    {
        var png = PngRasterImage.Encode(4, 1, [0,0,0,255,0,0,0,255,0,0,0,255]);
        Assert.Null(PngSourceCropper.TryCrop(png, -.1, 0, 0, 0));
        Assert.Null(PngSourceCropper.TryCrop(png, .7, 0, .7, 0));
        Assert.Null(PngSourceCropper.TryCrop(png, double.NaN, 0, 0, 0));
        Assert.Null(PngSourceCropper.TryCrop([1, 2, 3], .2, 0, .2, 0));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PngSourceCropper.TryCrop(png, .25, 0, .25, 0, cancelled.Token));
        var outputs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PngSourceCropper.TryCrop(png, .25, 0, .25, 0))));
        Assert.All(outputs, output => Assert.Equal(new byte[] { 255,0,0,0,255,0 }, PngRasterImage.Decode(output!).RgbBytes));
    }

    [Fact]
    public void Corrupt_or_oversized_PNG_headers_do_not_throw_or_produce_a_derived_image()
    {
        var original = PngRasterImage.Encode(1, 1, [255, 0, 0]);
        Assert.Null(PngSourceCropper.TryCrop(original[..^12], .1, 0, .1, 0));
        var invalid = original.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(16,4), uint.MaxValue);
        Assert.Null(PngSourceCropper.TryCrop(invalid, .1, 0, .1, 0));
        var corrupt = original.ToArray(); corrupt[29] ^= 1;
        Assert.Null(PngSourceCropper.TryCrop(corrupt, .1, 0, .1, 0));
    }

    private static byte[] RgbaPng(int width, int height, byte[] pixels)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        void Chunk(string type, byte[] data)
        {
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); output.Write(size);
            var tag = System.Text.Encoding.ASCII.GetBytes(type); output.Write(tag); output.Write(data);
            uint crc = 0xffffffff; foreach (var b in tag.Concat(data)) { crc ^= b; for (var i=0;i<8;i++) crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1; }
            BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); output.Write(size);
        }
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header,width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height); header[8]=8;header[9]=6;Chunk("IHDR",header);
        using var dataStream = new MemoryStream(); using (var z = new ZLibStream(dataStream, CompressionLevel.Fastest, true))
            for (var y=0;y<height;y++) { z.WriteByte(0); z.Write(pixels,y*width*4,width*4); }
        Chunk("IDAT",dataStream.ToArray());Chunk("IEND",[]);return output.ToArray();
    }
    private static byte[] RawRgba(byte[] png)
    {
        Assert.Equal(6, png[25]);
        using var data = new MemoryStream();
        for (var at=8;at<png.Length;) { var size=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at)); if(System.Text.Encoding.ASCII.GetString(png,at+4,4)=="IDAT")data.Write(png,at+8,size);at+=12+size; }
        data.Position=0; using var z=new ZLibStream(data,CompressionMode.Decompress);using var decoded=new MemoryStream();z.CopyTo(decoded);
        var bytes=decoded.ToArray();Assert.Equal(0,bytes[0]);return bytes.AsSpan(1,4).ToArray();
    }
}

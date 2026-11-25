using System.Buffers.Binary;

namespace DocRedock.Render;

/// <summary>Lays out a fractional crop of a picture the converter cannot decode (JPEG, GIF, WebP, BMP) so
/// that the browser's own resampling cannot show the pixels the crop excludes. The pixel size comes from
/// the file header; the crop window is snapped to the whole source pixels it retains (the same rounding as
/// <see cref="PngSourceCropper"/>) and then drawn in by <see cref="Inset"/>, and the caller stretches that
/// window over the picture frame. Stateless; an unreadable header yields false and the caller keeps the
/// plain fractional clip.</summary>
public static class SourcePictureCropWindow
{
    /// <summary>The narrowest window, in source pixels, that a crop retaining a single pixel row or column
    /// is stretched from. Narrower would only enlarge the picture's layout box.</summary>
    private const double MinimumWindow = 1.0 / 64.0;

    /// <summary>A crop window as fractions of the whole picture: the part that fills the frame.</summary>
    public readonly record struct Window(double Left, double Top, double Width, double Height);

    /// <summary>The inward shift of a snapped crop edge, in source pixels, for a picture shown at
    /// <paramref name="displayPixelsPerSourcePixel"/> CSS pixels per source pixel.
    /// Half a source pixel: a browser magnifies with bilinear sampling, which blends the two pixels whose
    /// centres surround the sample, so the excluded neighbour (centre half a pixel outside the edge)
    /// stops contributing only from the retained pixel's own centre inward.
    /// Plus half a display pixel: the browser rounds the picture box and the clip box to device pixels
    /// independently (each by up to half a device pixel, and a device pixel is at most a CSS pixel) while the
    /// first visible sample sits half a device pixel inside the clip, so the sample can land up to half a
    /// display pixel outside the laid-out edge. That term also covers a reduced picture, whose filter spans
    /// about one display pixel of source instead of one source pixel.
    /// Measured in Chrome 2026-11 over scales 0.13 to 9.1, four sub-pixel offsets and device scale factors
    /// 1, 1.25, 1.5 and 2: the snapped window alone bleeds up to 50%; with this inset no excluded colour shows.</summary>
    public static double Inset(double displayPixelsPerSourcePixel) =>
        double.IsFinite(displayPixelsPerSourcePixel) && displayPixelsPerSourcePixel > 0 ? 0.5 + 0.5 / displayPixelsPerSourcePixel : 0.5;

    /// <summary>Fits the window for a picture of <paramref name="width"/> by <paramref name="height"/> source
    /// pixels cropped by the given fractions and shown in a frame of the given size in CSS pixels.
    /// Each edge moves inward from the requested position by at most one source pixel, or one CSS pixel
    /// where the picture is reduced and that is the larger of the two.</summary>
    public static bool TryFit(int width, int height, double left, double top, double right, double bottom,
        double frameWidth, double frameHeight, out Window window)
    {
        window = default;
        if (width <= 0 || height <= 0) return false;
        if (!IsInset(left) || !IsInset(top) || !IsInset(right) || !IsInset(bottom)) return false;
        if (left + right >= 1.0 || top + bottom >= 1.0) return false;
        var (x, w) = Axis(width, left, right, frameWidth);
        var (y, h) = Axis(height, top, bottom, frameHeight);
        window = new(x / width, y / height, w / width, h / height);
        return true;
    }

    private static (double Start, double Length) Axis(int size, double before, double after, double frame)
    {
        if (before == 0 && after == 0) return (0, size);
        var requestedStart = before * size;
        var requestedEnd = (1.0 - after) * size;
        // The whole pixels the fractional window retains, as PngSourceCropper rounds them.
        var first = Math.Clamp((int)Math.Round(requestedStart, MidpointRounding.AwayFromZero), 0, size - 1);
        var last = Math.Clamp((int)Math.Round(requestedEnd, MidpointRounding.AwayFromZero) - 1, first, size - 1);
        var scale = double.IsFinite(frame) && frame > 0 ? frame / (last - first + 1) : 1.0;
        var inset = Inset(scale);
        // Never further from the requested edge than one pixel of the coarser grid; an edge that keeps
        // the picture's own border has no excluded neighbour and stays where it is.
        var limit = Math.Max(1.0, 1.0 / scale);
        var start = before == 0 ? 0 : Math.Min(first + inset, requestedStart + limit);
        var end = after == 0 ? size : Math.Max(last + 1 - inset, requestedEnd - limit);
        var minimum = Math.Min(last - first + 1, MinimumWindow);
        if (end - start < minimum)
        {
            // Too few pixels for both insets. The window begins at a pixel centre rather than straddling
            // it: a renderer that truncates its filter weights (Skia's software sampler keeps four bits)
            // gives a sample just before a centre a sixteenth of the pixel before it.
            start = Math.Clamp((start + end) / 2.0, first, last + 1 - minimum);
            end = start + minimum;
        }
        return (start, end - start);
    }

    private static bool IsInset(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 && value < 1.0;

    /// <summary>Reads the pixel size a browser lays the picture out with from a JPEG (any SOFn frame header,
    /// turned by an Exif orientation of 5 to 8), GIF (logical screen), WebP (VP8, VP8L or VP8X) or BMP
    /// header. No pixel data is decoded.</summary>
    public static bool TryReadPixelSize(ReadOnlySpan<byte> source, out int width, out int height)
    {
        width = height = 0;
        var read = source.Length >= 3 && source[0] == 0xFF && source[1] == 0xD8 && source[2] == 0xFF ? Jpeg(source, out width, out height)
            : source.Length >= 10 && (source[..6].SequenceEqual("GIF87a"u8) || source[..6].SequenceEqual("GIF89a"u8)) ? Gif(source, out width, out height)
            : source.Length >= 16 && source[..4].SequenceEqual("RIFF"u8) && source.Slice(8, 4).SequenceEqual("WEBP"u8) ? WebP(source, out width, out height)
            : source.Length >= 26 && source[0] == (byte)'B' && source[1] == (byte)'M' && Bmp(source, out width, out height);
        if (read && width > 0 && height > 0) return true;
        width = height = 0;
        return false;
    }

    private static bool Jpeg(ReadOnlySpan<byte> source, out int width, out int height)
    {
        width = height = 0;
        var orientation = 1;
        var offset = 2;
        while (offset + 4 <= source.Length)
        {
            if (source[offset] != 0xFF) return false;
            var marker = source[offset + 1];
            if (marker == 0xFF) { offset++; continue; }
            // Markers without a length; a scan or the end before any frame header means there is none.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { offset += 2; continue; }
            if (marker is 0xD8 or 0xD9 or 0xDA or 0x00) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(source[(offset + 2)..]);
            if (length < 2 || length > source.Length - offset - 2) return false;
            var segment = source.Slice(offset + 4, length - 2);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (segment.Length < 5) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                if (orientation >= 5) (width, height) = (height, width);
                return true;
            }
            if (marker == 0xE1 && ExifOrientation(segment) is { } found) orientation = found;
            offset += 2 + length;
        }
        return false;
    }

    private static int? ExifOrientation(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 14 || !segment[..6].SequenceEqual("Exif\0\0"u8)) return null;
        var tiff = segment[6..];
        bool little;
        if (tiff[..2].SequenceEqual("II"u8)) little = true;
        else if (tiff[..2].SequenceEqual("MM"u8)) little = false;
        else return null;
        if (U16(tiff[2..], little) != 42) return null;
        var directory = U32(tiff[4..], little);
        if (directory > tiff.Length - 2) return null;
        var at = (int)directory;
        var count = U16(tiff[at..], little);
        at += 2;
        for (var entry = 0; entry < count && at + 12 <= tiff.Length; entry++, at += 12)
        {
            if (U16(tiff[at..], little) != 0x0112) continue;
            // A SHORT with one value, stored in the entry itself.
            if (U16(tiff[(at + 2)..], little) != 3 || U32(tiff[(at + 4)..], little) != 1) return null;
            var value = U16(tiff[(at + 8)..], little);
            return value is >= 1 and <= 8 ? value : null;
        }
        return null;
    }

    private static int U16(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint U32(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static bool Gif(ReadOnlySpan<byte> source, out int width, out int height)
    {
        width = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        return true;
    }

    private static bool WebP(ReadOnlySpan<byte> source, out int width, out int height)
    {
        width = height = 0;
        var chunk = source.Slice(12, 4);
        var data = source[20..];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            if (data.Length < 10) return false;
            width = 1 + (data[4] | data[5] << 8 | data[6] << 16);
            height = 1 + (data[7] | data[8] << 8 | data[9] << 16);
            return true;
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (data.Length < 5 || data[0] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            width = 1 + (int)(bits & 0x3FFF);
            height = 1 + (int)(bits >> 14 & 0x3FFF);
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // A key frame: three bytes of frame tag (bit 0 clear), the start code, then 14-bit sizes.
            if (data.Length < 10 || (data[0] & 1) != 0 || data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF;
            return true;
        }
        return false;
    }

    private static bool Bmp(ReadOnlySpan<byte> source, out int width, out int height)
    {
        width = height = 0;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(source[14..]);
        if (header == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(source[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(source[20..]);
            return true;
        }
        if (header < 40) return false;
        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(source[18..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(source[22..]);
        // A negative height stores the rows top down; the picture is as tall either way.
        if (signedWidth <= 0 || signedHeight == 0 || signedHeight == int.MinValue) return false;
        width = signedWidth;
        height = Math.Abs(signedHeight);
        return true;
    }
}

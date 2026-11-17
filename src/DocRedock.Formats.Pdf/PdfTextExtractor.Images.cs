using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Formats.Pdf;

// Raster images a page draws, decoded into files a Markdown or HTML export can show. Only the BCL is used: a JPEG is
// kept as it is, and Flate-coded samples are written as PNG (see PdfPngWriter). What cannot be decoded safely stays
// counted and reported (PdfEmbeddedImageOmitted) exactly as before; nothing here throws for a damaged image.
public static partial class PdfTextExtractor
{
    /// <summary>The most pixels one image may have to be decoded; a larger one would need hundreds of megabytes.</summary>
    private const long MaxImagePixels = 40_000_000;

    /// <summary>The most bytes of decoded image files one document may keep.</summary>
    private const long MaxTotalImageBytes = 192L * 1024 * 1024;

    /// <summary>On how many pages one picture must sit at one place to be a running logo, not content.</summary>
    private const int FurniturePageCount = 2;

    private enum PdfImageColor { Gray, Rgb, Indexed }

    /// <summary>What an image is made of: its colour model, and for an indexed one its palette as RGB triples
    /// and the stream (or the literal) that holds it.</summary>
    private sealed record PdfImageColorSpace(PdfImageColor Kind, byte[]? Palette = null, int Hival = 0, int? LookupObject = null, PdfImageColor Base = PdfImageColor.Rgb);

    /// <summary>One decoded image file and its size in pixels.</summary>
#pragma warning disable CA1819
    private sealed record PdfDecodedImage(string MediaType, byte[] Content, int Width, int Height);
#pragma warning restore CA1819

    /// <summary>A decoded image or the reason it was not.</summary>
    private readonly record struct PdfImageOutcome(PdfDecodedImage? Image, string? Reason)
    {
        public static PdfImageOutcome Fail(string reason) => new(null, reason);
    }

    /// <summary>Samples read from an image: 8 bits per channel, grey or RGB or palette indexes, optional alpha.</summary>
    private sealed record PdfSamples(int Width, int Height, PdfImageColor Kind, byte[] Data, byte[]? Palette, byte[]? Alpha);

    private sealed record PdfImageSources(IReadOnlyDictionary<int, string> Objects, IReadOnlyDictionary<int, byte[]> Streams, long MaxExpandedBytes);

    /// <summary>Decides which of every page's image draws is page furniture, which decode, and which stay a count, then
    /// stores the result on the pages and reports the images that stayed.</summary>
    private static void ExtractPageImages(List<PdfPageText> pages, Dictionary<int, List<PdfImageDraw>> draws, byte[] raw, string latin,
        (IReadOnlyList<(int Id, string Body)> Ordered, IReadOnlyDictionary<int, string> ById) index, PdfExtractionOptions options,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (draws.Count == 0) return;
        // The same picture at the same place on several pages is a logo in a running header or footer.
        var places = new Dictionary<(int Object, long X, long Y, long Width, long Height), HashSet<int>>();
        foreach (var (pageNumber, list) in draws)
            foreach (var draw in list.Where(draw => draw is { ObjectId: not null, Bounds: not null }))
            {
                var bounds = draw.Bounds!;
                var key = (draw.ObjectId!.Value, (long)Math.Round(bounds.X), (long)Math.Round(bounds.Y),
                    (long)Math.Round(bounds.Width), (long)Math.Round(bounds.Height));
                if (!places.TryGetValue(key, out var seen)) places[key] = seen = [];
                seen.Add(pageNumber);
            }
        bool IsFurniture(PdfImageDraw draw) => draw is { ObjectId: not null, Bounds: not null } &&
            places.TryGetValue((draw.ObjectId!.Value, (long)Math.Round(draw.Bounds!.X), (long)Math.Round(draw.Bounds.Y),
                (long)Math.Round(draw.Bounds.Width), (long)Math.Round(draw.Bounds.Height)), out var seen) && seen.Count >= FurniturePageCount;

        // Read the streams of what is to be decoded in one pass over the file.
        var wanted = new HashSet<int>();
        foreach (var draw in draws.Values.SelectMany(list => list).Where(draw => draw.ObjectId is not null && !IsFurniture(draw)))
        {
            wanted.Add(draw.ObjectId!.Value);
            if (!index.ById.TryGetValue(draw.ObjectId.Value, out var body)) continue;
            var entries = ReadDictionaryEntries(body);
            if (TryReadReference(EntryValue(entries, "SMask") ?? string.Empty, out var maskId)) wanted.Add(maskId);
            if (ParseImageColorSpace(EntryValue(entries, "ColorSpace"), index.ById, 0, out _) is { LookupObject: { } lookup }) wanted.Add(lookup);
        }
        IReadOnlyDictionary<int, byte[]> streams;
        try { streams = wanted.Count == 0 ? new Dictionary<int, byte[]>() : ReadRawStreamPayloads(raw, latin, options, wanted, index.ById, cancellationToken); }
        catch (Exception exception) when (exception is PdfExtractionException or RegexMatchTimeoutException or ArgumentException or OutOfMemoryException)
        { streams = new Dictionary<int, byte[]>(); }
        var sources = new PdfImageSources(index.ById, streams, options.MaxExpandedStreamBytes);

        var decoded = new Dictionary<(int Object, int Orientation), PdfImageOutcome>();
        long totalBytes = 0;
        for (var at = 0; at < pages.Count; at++)
        {
            var page = pages[at];
            if (page.IsImageOnly || !draws.TryGetValue(page.PageNumber, out var list)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var extracted = new List<PdfExtractedImage>();
            var remaining = new List<PdfImageDraw>();
            var reasons = new List<string>();
            foreach (var draw in list)
            {
                if (IsFurniture(draw)) continue;
                var failure = draw.ObjectId is null ? draw.Bounds is null ? "its placement could not be resolved" : "it is an inline image or its resource could not be resolved"
                    : draw.Bounds is null ? "its placement could not be resolved" : null;
                if (failure is null)
                {
                    var orientation = Orientation(draw.Ctm);
                    var key = (draw.ObjectId!.Value, orientation.Code);
                    if (!decoded.TryGetValue(key, out var outcome))
                    {
                        outcome = totalBytes >= MaxTotalImageBytes
                            ? PdfImageOutcome.Fail("the document's decoded images would exceed " + MaxTotalImageBytes / (1024 * 1024) + " MiB")
                            : DecodeImageObject(draw.ObjectId.Value, orientation, sources);
                        decoded[key] = outcome;
                        if (outcome.Image is { } fresh) totalBytes += fresh.Content.Length;
                    }
                    if (outcome.Image is { } image)
                    {
                        var (width, height) = DrawnSize(draw.Ctm, orientation);
                        extracted.Add(new PdfExtractedImage(
                            "pdf-img-" + draw.ObjectId.Value.ToString("D4", CultureInfo.InvariantCulture) + (orientation.Code == 0 ? "" : "-o" + orientation.Code.ToString(CultureInfo.InvariantCulture)),
                            image.MediaType, image.Content, draw.Bounds!, width, height, image.Width, image.Height));
                        continue;
                    }
                    failure = outcome.Reason;
                }
                remaining.Add(draw);
                if (failure is not null && !reasons.Contains(failure)) reasons.Add(failure);
            }
            var remainingBounds = remaining.Where(draw => draw.Bounds is not null).Select(draw => draw.Bounds!).ToArray();
            pages[at] = page with
            {
                EmbeddedImageCount = remaining.Count,
                EmbeddedImages = remainingBounds.Length == 0 ? null : remainingBounds,
                ExtractedImages = extracted.Count == 0 ? null
                    : extracted.OrderByDescending(image => image.Bounds.Y + image.Bounds.Height).ThenBy(image => image.Bounds.X).ToArray(),
            };
            if (remaining.Count > 0)
                diagnostics.Add($"PdfEmbeddedImageOmitted: PDF page {page.PageNumber}: {remaining.Count} embedded image(s) were not extracted; native text was retained. " +
                    "Run with --ocr on and a configured rasterizer to recover image text." +
                    (reasons.Count == 0 ? "" : $" Not decoded: {string.Join("; ", reasons)}."));
        }
    }

    /// <summary>How an image's matrix turns it: whether the matrix swaps the picture's axes (a quarter turn) and which of
    /// the two axes of the upright result run backwards. <see cref="Code"/> 0 is the picture as stored.</summary>
    private readonly record struct PdfImageOrientation(bool Swap, bool ReverseX, bool ReverseY)
    {
        public int Code => (Swap ? 4 : 0) | (ReverseY ? 2 : 0) | (ReverseX ? 1 : 0);
    }

    private static PdfImageOrientation Orientation(PdfMatrix ctm)
    {
        // The picture's x axis runs along (A,B) and its y axis (up the page, from the bottom row) along (C,D). Of the
        // stored picture, only an axis-aligned placement is turned back upright; a slanted one keeps its pixels.
        if (Math.Abs(ctm.A * ctm.D) >= Math.Abs(ctm.B * ctm.C))
            return new PdfImageOrientation(false, ctm.A < 0, ctm.D < 0);
        return new PdfImageOrientation(true, ctm.C > 0, ctm.B > 0);
    }

    /// <summary>The size in points the page draws the upright picture at.</summary>
    private static (double Width, double Height) DrawnSize(PdfMatrix ctm, PdfImageOrientation orientation)
    {
        var alongX = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B);
        var alongY = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
        return orientation.Swap ? (alongY, alongX) : (alongX, alongY);
    }

    /// <summary>The raw (still filtered) bytes of the streams of <paramref name="wanted"/> objects. A stream's own
    /// <c>/Length</c> is trusted when <c>endstream</c> follows it, because image samples may hold any byte, line ends
    /// included; otherwise one end-of-line before <c>endstream</c> is dropped.</summary>
    private static Dictionary<int, byte[]> ReadRawStreamPayloads(byte[] bytes, string latin, PdfExtractionOptions options,
        HashSet<int> wanted, IReadOnlyDictionary<int, string> objects, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, byte[]>();
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");
        var offset = 0;
        while ((offset = IndexOf(bytes, marker, offset)) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = offset + marker.Length;
            if (start < bytes.Length && bytes[start] == '\r') start++;
            if (start < bytes.Length && bytes[start] == '\n') start++;
            var end = IndexOf(bytes, endMarker, start);
            if (end < 0) break;
            // An image dictionary can hold a long palette string: look further back when the object's own header is out of reach.
            var header = ReadContainingObjectHeader(latin, offset, 4096);
            if (!header.Contains(" obj", StringComparison.Ordinal) && offset > 4096)
                header = ReadContainingObjectHeader(latin, offset, 262_144);
            if (!TryReadContainingObjectId(header, options.EffectiveRegexTimeout, out var objectId) || !wanted.Contains(objectId))
            {
                offset = end + endMarker.Length;
                continue;
            }
            var payloadEnd = end;
            if (payloadEnd > start && bytes[payloadEnd - 1] == '\n') payloadEnd--;
            if (payloadEnd > start && bytes[payloadEnd - 1] == '\r') payloadEnd--;
            var declaredText = objects.TryGetValue(objectId, out var body) ? EntryValue(ReadDictionaryEntries(body), "Length") : null;
            if (ReadNumber(ResolveValue(declaredText, objects)) is { } declared && declared >= 0 && declared <= bytes.Length - start && declared == Math.Truncate(declared))
            {
                var declaredEnd = start + (int)declared;
                var markerAt = declaredEnd;
                while (markerAt < bytes.Length && bytes[markerAt] is 10 or 13 or 32 or 9) markerAt++;
                if (markerAt <= bytes.Length - endMarker.Length && bytes.AsSpan(markerAt, endMarker.Length).SequenceEqual(endMarker))
                {
                    payloadEnd = declaredEnd;
                    end = markerAt;
                }
            }
            result[objectId] = bytes[start..payloadEnd];
            offset = end + endMarker.Length;
        }
        return result;
    }

    /// <summary>Decodes one Image XObject into a file, or says why it cannot be. Never throws for a damaged image.</summary>
    private static PdfImageOutcome DecodeImageObject(int objectId, PdfImageOrientation orientation, PdfImageSources sources)
    {
        try
        {
            if (!sources.Objects.TryGetValue(objectId, out var body)) return PdfImageOutcome.Fail("the image object is missing");
            if (!sources.Streams.TryGetValue(objectId, out var stream)) return PdfImageOutcome.Fail("the image has no stream data");
            var entries = ReadDictionaryEntries(body);
            var width = ReadInteger(ResolveValue(EntryValue(entries, "Width"), sources.Objects));
            var height = ReadInteger(ResolveValue(EntryValue(entries, "Height"), sources.Objects));
            if (width <= 0 || height <= 0) return PdfImageOutcome.Fail("its size is missing");
            if ((long)width * height > MaxImagePixels)
                return PdfImageOutcome.Fail($"it has {(long)width * height:N0} pixels, more than the {MaxImagePixels:N0}-pixel limit");
            if (ResolveValue(EntryValue(entries, "ImageMask"), sources.Objects)?.Trim() == "true")
                return PdfImageOutcome.Fail("it is a stencil mask");
            var filters = ReadImageFilters(entries, sources.Objects);
            var cap = sources.MaxExpandedBytes;

            if (filters.Count > 0 && filters[^1].Name == "DCTDecode")
                return DecodeJpegImage(entries, filters, stream, width, height, orientation, sources, cap);

            var color = ParseImageColorSpace(EntryValue(entries, "ColorSpace"), sources.Objects, 0, out var colorFailure);
            if (color is null) return PdfImageOutcome.Fail(colorFailure ?? "its colour space is not supported");
            if (color is { Kind: PdfImageColor.Indexed, Palette: null })
            {
                // The palette table is a stream of its own.
                if (color.LookupObject is not { } tableId || !sources.Objects.TryGetValue(tableId, out var tableBody) ||
                    !sources.Streams.TryGetValue(tableId, out var tableStream))
                    return PdfImageOutcome.Fail("its palette table has no stream data");
                var tableData = tableStream;
                foreach (var (name, _) in ReadImageFilters(ReadDictionaryEntries(tableBody), sources.Objects))
                    tableData = name switch
                    {
                        "FlateDecode" => Inflate(tableData, (color.Hival + 1L) * 3 + 1024),
                        "ASCII85Decode" => DecodeAscii85(tableData, cap),
                        "ASCIIHexDecode" => DecodeAsciiHex(tableData),
                        _ => throw new InvalidDataException(name + " palette"),
                    };
                color = color with { Palette = PaletteFromTable(tableData, color.Base, color.Hival) };
            }
            var samples = ReadSamples(entries, filters, stream, width, height, color, sources, out var failure);
            if (samples is null) return PdfImageOutcome.Fail(failure ?? "its samples could not be read");

            // A soft mask becomes the alpha channel.
            if (ResolveValue(EntryValue(entries, "Matte"), sources.Objects) is not null)
                return PdfImageOutcome.Fail("its soft mask is premultiplied (/Matte)");
            if (EntryValue(entries, "SMask") is { } maskText && TryReadReference(maskText, out var maskId))
            {
                var alpha = ReadSoftMask(maskId, width, height, sources, out failure);
                if (alpha is null) return PdfImageOutcome.Fail(failure ?? "its soft mask could not be read");
                samples = samples with { Alpha = alpha };
            }
            return new PdfImageOutcome(WritePng(samples, orientation), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PdfImageOutcome.Fail("it is damaged (" + exception.GetType().Name + ")");
        }
    }

    private static PdfImageOutcome DecodeJpegImage(IReadOnlyList<(string Key, string Value)> entries, List<(string Name, string? Parameters)> filters,
        byte[] stream, int width, int height, PdfImageOrientation orientation, PdfImageSources sources, long cap)
    {
        if (orientation.Code != 0) return PdfImageOutcome.Fail("it is drawn turned or mirrored and a JPEG cannot be turned without decoding it");
        var data = stream;
        foreach (var (name, _) in filters.Take(filters.Count - 1))
        {
            data = name switch
            {
                "ASCII85Decode" => DecodeAscii85(data, cap),
                "ASCIIHexDecode" => DecodeAsciiHex(data),
                _ => throw new InvalidDataException(name + " before DCTDecode"),
            };
        }
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF) return PdfImageOutcome.Fail("its JPEG data is damaged");
        if (ReadJpegFrame(data) is not { } frame) return PdfImageOutcome.Fail("its JPEG header is damaged or not a baseline/progressive JPEG");
        if (frame.Precision != 8) return PdfImageOutcome.Fail("its JPEG is not 8 bits per sample");
        if (frame.Components is not (1 or 3)) return PdfImageOutcome.Fail($"its JPEG has {frame.Components} colour components (CMYK is not supported)");
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > MaxImagePixels) return PdfImageOutcome.Fail("its JPEG size is out of range");
        // The colour space must agree with what the JPEG holds; a palette or a CMYK space over a JPEG is not read.
        if (EntryValue(entries, "ColorSpace") is { } colorText)
        {
            var color = ParseImageColorSpace(colorText, sources.Objects, 0, out var colorFailure);
            if (color is null) return PdfImageOutcome.Fail(colorFailure ?? "its colour space is not supported");
            if (color.Kind == PdfImageColor.Indexed || (color.Kind == PdfImageColor.Gray ? 1 : 3) != frame.Components)
                return PdfImageOutcome.Fail("its colour space does not match its JPEG data");
        }
        if (ResolveValue(EntryValue(entries, "Decode"), sources.Objects) is { } decode && ReadNumbers(decode) is { } numbers &&
            !(numbers.Count >= 2 && Enumerable.Range(0, numbers.Count / 2).All(pair => numbers[pair * 2] == 0 && numbers[pair * 2 + 1] == 1)))
            return PdfImageOutcome.Fail("it has a /Decode array");
        _ = width;
        _ = height;
        return new PdfImageOutcome(new PdfDecodedImage("image/jpeg", data, frame.Width, frame.Height), null);
    }

    private readonly record struct JpegFrame(int Precision, int Width, int Height, int Components);

    /// <summary>The first start-of-frame of a JPEG (baseline, extended or progressive), or null when there is none.</summary>
    private static JpegFrame? ReadJpegFrame(byte[] data)
    {
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF) { at++; continue; }
            var marker = data[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) { at += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) return null;
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2) return null;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (at + 10 > data.Length) return null;
                return new JpegFrame(data[at + 4], data[at + 7] << 8 | data[at + 8], data[at + 5] << 8 | data[at + 6], data[at + 9]);
            }
            if (marker is >= 0xC3 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return null;
            at += 2 + length;
        }
        return null;
    }

    /// <summary>Reads an image's samples into 8-bit grey, RGB or palette indexes.</summary>
    private static PdfSamples? ReadSamples(IReadOnlyList<(string Key, string Value)> entries, List<(string Name, string? Parameters)> filters,
        byte[] stream, int width, int height, PdfImageColorSpace color, PdfImageSources sources, out string? failure)
    {
        failure = null;
        var components = color.Kind == PdfImageColor.Rgb ? 3 : 1;
        var bits = ReadInteger(ResolveValue(EntryValue(entries, "BitsPerComponent"), sources.Objects));
        if (bits == 0) bits = 8;
        if (color.Kind == PdfImageColor.Rgb ? bits != 8 : bits is not (1 or 2 or 4 or 8))
        {
            failure = $"{bits} bits per component is not supported";
            return null;
        }
        var rowBytes = checked((width * components * bits + 7) / 8);
        var expected = checked((long)rowBytes * height);
        var data = stream;
        // Samples cannot expand past what the image declares (plus a PNG predictor's tag byte per row).
        var cap = Math.Min(sources.MaxExpandedBytes, expected + height + 1024);
        foreach (var (name, parameters) in filters)
        {
            switch (name)
            {
                case "ASCII85Decode": data = DecodeAscii85(data, cap); break;
                case "ASCIIHexDecode": data = DecodeAsciiHex(data); break;
                case "FlateDecode":
                    data = Inflate(data, cap);
                    var predicted = ApplyPredictor(data, parameters, width, components, bits, sources.Objects, out failure);
                    if (predicted is null) return null;
                    data = predicted;
                    break;
                default:
                    failure = name + " is not supported";
                    return null;
            }
        }
        if (data.Length < expected)
        {
            failure = "its sample data is shorter than its size says";
            return null;
        }
        if (!ReadDecodeArray(entries, color, bits, components, sources.Objects, out var invert, out failure)) return null;

        var count = checked(width * height);
        byte[] pixels;
        if (color.Kind == PdfImageColor.Rgb)
        {
            pixels = new byte[checked(count * 3)];
            Buffer.BlockCopy(data, 0, pixels, 0, pixels.Length);
            if (invert is not null)
                for (var at = 0; at < pixels.Length; at++)
                    if (invert[at % 3]) pixels[at] = (byte)(255 - pixels[at]);
            return new PdfSamples(width, height, PdfImageColor.Rgb, pixels, null, null);
        }

        pixels = new byte[count];
        var maximum = (1 << bits) - 1;
        for (var row = 0; row < height; row++)
        {
            var source = row * rowBytes;
            var target = row * width;
            for (var column = 0; column < width; column++)
            {
                int sample;
                if (bits == 8) sample = data[source + column];
                else
                {
                    var bit = column * bits;
                    sample = data[source + (bit >> 3)] >> (8 - bits - (bit & 7)) & maximum;
                }
                pixels[target + column] = color.Kind == PdfImageColor.Indexed
                    ? (byte)Math.Min(sample, color.Hival)
                    : (byte)((invert is { } flags && flags[0] ? maximum - sample : sample) * 255 / maximum);
            }
        }
        return new PdfSamples(width, height, color.Kind, pixels, color.Palette, null);
    }

    /// <summary>The <c>/Decode</c> array as which components run backwards; false when it is anything but the default or an
    /// inversion, which this reader does not map.</summary>
    private static bool ReadDecodeArray(IReadOnlyList<(string Key, string Value)> entries, PdfImageColorSpace color, int bits, int components,
        IReadOnlyDictionary<int, string> objects, out bool[]? invert, out string? failure)
    {
        invert = null;
        failure = null;
        if (ResolveValue(EntryValue(entries, "Decode"), objects) is not { } text) return true;
        var numbers = ReadNumbers(text);
        if (numbers is null || numbers.Count != components * 2)
        {
            failure = "its /Decode array is not understood";
            return false;
        }
        var flags = new bool[components];
        for (var component = 0; component < components; component++)
        {
            var (low, high) = (numbers[component * 2], numbers[component * 2 + 1]);
            if (color.Kind == PdfImageColor.Indexed)
            {
                if (low != 0 || high != (1 << bits) - 1) { failure = "its /Decode array re-maps palette indexes"; return false; }
            }
            else if (low == 0 && high == 1) { }
            else if (low == 1 && high == 0) flags[component] = true;
            else { failure = "its /Decode array is not the default or an inversion"; return false; }
        }
        invert = flags.Any(flag => flag) ? flags : null;
        return true;
    }

    /// <summary>A soft mask as one 8-bit alpha value per pixel of the image it covers (resampled by nearest neighbour when it
    /// has another size).</summary>
    private static byte[]? ReadSoftMask(int maskId, int width, int height, PdfImageSources sources, out string? failure)
    {
        failure = null;
        if (!sources.Objects.TryGetValue(maskId, out var body) || !sources.Streams.TryGetValue(maskId, out var stream))
        {
            failure = "its soft mask has no stream data";
            return null;
        }
        var entries = ReadDictionaryEntries(body);
        var maskWidth = ReadInteger(ResolveValue(EntryValue(entries, "Width"), sources.Objects));
        var maskHeight = ReadInteger(ResolveValue(EntryValue(entries, "Height"), sources.Objects));
        if (maskWidth <= 0 || maskHeight <= 0 || (long)maskWidth * maskHeight > MaxImagePixels)
        {
            failure = "its soft mask has no usable size";
            return null;
        }
        if (ResolveValue(EntryValue(entries, "Matte"), sources.Objects) is not null)
        {
            failure = "its soft mask is premultiplied (/Matte)";
            return null;
        }
        var filters = ReadImageFilters(entries, sources.Objects);
        if (filters.Any(filter => filter.Name == "DCTDecode"))
        {
            failure = "its soft mask is a JPEG";
            return null;
        }
        var samples = ReadSamples(entries, filters, stream, maskWidth, maskHeight, new PdfImageColorSpace(PdfImageColor.Gray), sources, out failure);
        if (samples is null)
        {
            failure = "its soft mask: " + failure;
            return null;
        }
        if (maskWidth == width && maskHeight == height) return samples.Data;
        var resized = new byte[checked(width * height)];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = (int)((long)row * maskHeight / height);
            for (var column = 0; column < width; column++)
                resized[row * width + column] = samples.Data[sourceRow * maskWidth + (int)((long)column * maskWidth / width)];
        }
        return resized;
    }

    /// <summary>Undoes a Flate stream's predictor (none, TIFF 2, or the PNG row filters 10 to 15) over rows of
    /// <paramref name="width"/> pixels; null with a reason when the stream's parameters do not fit the image.</summary>
    private static byte[]? ApplyPredictor(byte[] data, string? parameters, int width, int components, int bits,
        IReadOnlyDictionary<int, string> objects, out string? failure)
    {
        failure = null;
        var entries = parameters is null ? [] : ReadDictionaryEntries(parameters);
        var predictor = ReadInteger(ResolveValue(EntryValue(entries, "Predictor"), objects));
        if (predictor <= 1) return data;
        var colors = ReadInteger(ResolveValue(EntryValue(entries, "Colors"), objects));
        var bitsPerComponent = ReadInteger(ResolveValue(EntryValue(entries, "BitsPerComponent"), objects));
        var columns = ReadInteger(ResolveValue(EntryValue(entries, "Columns"), objects));
        if (colors == 0) colors = 1;
        if (bitsPerComponent == 0) bitsPerComponent = 8;
        if (columns == 0) columns = 1;
        if (colors != components || bitsPerComponent != bits || columns != width)
        {
            failure = "its predictor parameters do not match the image";
            return null;
        }
        var rowBytes = (width * components * bits + 7) / 8;
        var bytesPerPixel = Math.Max(1, (components * bits + 7) / 8);
        if (predictor == 2)
        {
            if (bits != 8)
            {
                failure = "the TIFF predictor is only read for 8-bit samples";
                return null;
            }
            var rows = data.Length / rowBytes;
            var output = new byte[rows * rowBytes];
            for (var row = 0; row < rows; row++)
                for (var index = 0; index < rowBytes; index++)
                {
                    var at = row * rowBytes + index;
                    output[at] = unchecked((byte)(data[at] + (index >= bytesPerPixel ? output[at - bytesPerPixel] : 0)));
                }
            return output;
        }
        if (predictor < 10 || predictor > 15)
        {
            failure = $"predictor {predictor} is not supported";
            return null;
        }
        var stride = rowBytes + 1;
        var lines = data.Length / stride;
        var result = new byte[lines * rowBytes];
        for (var row = 0; row < lines; row++)
        {
            var filter = data[row * stride];
            var source = row * stride + 1;
            var target = row * rowBytes;
            for (var index = 0; index < rowBytes; index++)
            {
                var left = index >= bytesPerPixel ? result[target + index - bytesPerPixel] : 0;
                var up = row > 0 ? result[target - rowBytes + index] : 0;
                var upLeft = row > 0 && index >= bytesPerPixel ? result[target - rowBytes + index - bytesPerPixel] : 0;
                int predicted;
                switch (filter)
                {
                    case 0: predicted = 0; break;
                    case 1: predicted = left; break;
                    case 2: predicted = up; break;
                    case 3: predicted = (left + up) / 2; break;
                    case 4:
                        var estimate = left + up - upLeft;
                        var (distanceLeft, distanceUp, distanceUpLeft) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
                        predicted = distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft ? left : distanceUp <= distanceUpLeft ? up : upLeft;
                        break;
                    default:
                        failure = $"PNG row filter {filter} is not valid";
                        return null;
                }
                result[target + index] = unchecked((byte)(data[source + index] + predicted));
            }
        }
        return result;
    }

    /// <summary>The filters of an image stream with each one's <c>/DecodeParms</c> dictionary text.</summary>
    private static List<(string Name, string? Parameters)> ReadImageFilters(IReadOnlyList<(string Key, string Value)> entries, IReadOnlyDictionary<int, string> objects)
    {
        var result = new List<(string Name, string? Parameters)>();
        var filter = ResolveValue(EntryValue(entries, "Filter") ?? EntryValue(entries, "F"), objects);
        if (filter is null) return result;
        var names = new List<string>();
        var cursor = SkipSpace(filter, 0);
        if (cursor < filter.Length && filter[cursor] == '[')
        {
            for (cursor++; ;)
            {
                cursor = SkipSpace(filter, cursor);
                if (cursor >= filter.Length || filter[cursor] == ']') break;
                var end = SkipValue(filter, cursor);
                if (ReadName(filter[cursor..end]) is { } listed) names.Add(listed);
                cursor = end;
            }
        }
        else if (ReadName(filter) is { } single) names.Add(single);
        var parameters = ResolveValue(EntryValue(entries, "DecodeParms") ?? EntryValue(entries, "DP"), objects);
        var parts = new List<string?>();
        if (parameters is not null)
        {
            var at = SkipSpace(parameters, 0);
            if (at < parameters.Length && parameters[at] == '[')
            {
                for (at++; ;)
                {
                    at = SkipSpace(parameters, at);
                    if (at >= parameters.Length || parameters[at] == ']') break;
                    var end = SkipValue(parameters, at);
                    var part = ResolveValue(parameters[at..end], objects);
                    parts.Add(part is not null && part.TrimStart().StartsWith("<<", StringComparison.Ordinal) ? part : null);
                    at = end;
                }
            }
            else parts.Add(parameters.TrimStart().StartsWith("<<", StringComparison.Ordinal) ? parameters : null);
        }
        for (var index = 0; index < names.Count; index++)
            result.Add((names[index] switch { "Fl" => "FlateDecode", "A85" => "ASCII85Decode", "AHx" => "ASCIIHexDecode", "DCT" => "DCTDecode", var other => other },
                index < parts.Count ? parts[index] : null));
        return result;
    }

    /// <summary>The colour space an image is stored in, or null with a reason: device grey and RGB (and the calibrated
    /// ones, read as their device space), ICC profiles of one or three components, and a palette over either.</summary>
    private static PdfImageColorSpace? ParseImageColorSpace(string? value, IReadOnlyDictionary<int, string> objects, int depth, out string? failure)
    {
        failure = null;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) { failure = "it declares no colour space"; return null; }
        if (depth > 4) { failure = "its colour space is nested too deep"; return null; }
        if (TryReadReference(value, out var id))
        {
            if (objects.TryGetValue(id, out var target)) return ParseImageColorSpace(target.Trim(), objects, depth + 1, out failure);
            failure = "its colour space object is missing";
            return null;
        }
        if (value[0] == '/')
        {
            var name = ReadName(value);
            return name switch
            {
                "DeviceGray" or "G" or "CalGray" => new PdfImageColorSpace(PdfImageColor.Gray),
                "DeviceRGB" or "RGB" or "CalRGB" => new PdfImageColorSpace(PdfImageColor.Rgb),
                _ => Unsupported(name, out failure),
            };
        }
        if (value[0] != '[') { failure = "its colour space is not understood"; return null; }
        var elements = new List<string>();
        for (var at = 1; ;)
        {
            at = SkipSpace(value, at);
            if (at >= value.Length || value[at] == ']') break;
            var end = SkipValue(value, at);
            elements.Add(value[at..end]);
            at = end;
        }
        if (elements.Count == 0) { failure = "its colour space is empty"; return null; }
        switch (ReadName(elements[0]))
        {
            case "CalGray": return new PdfImageColorSpace(PdfImageColor.Gray);
            case "CalRGB": return new PdfImageColorSpace(PdfImageColor.Rgb);
            case "DeviceGray" or "G": return new PdfImageColorSpace(PdfImageColor.Gray);
            case "DeviceRGB" or "RGB": return new PdfImageColorSpace(PdfImageColor.Rgb);
            case "ICCBased":
                if (elements.Count < 2 || !TryReadReference(elements[1], out var profile) || !objects.TryGetValue(profile, out var profileBody))
                {
                    failure = "its ICC profile is missing";
                    return null;
                }
                return ReadInteger(EntryValue(ReadDictionaryEntries(profileBody), "N")) switch
                {
                    1 => new PdfImageColorSpace(PdfImageColor.Gray),
                    3 => new PdfImageColorSpace(PdfImageColor.Rgb),
                    var other => Unsupported(other == 4 ? "DeviceCMYK" : $"ICCBased with {other} components", out failure),
                };
            case "Indexed" or "I":
                if (elements.Count < 4) { failure = "its palette is incomplete"; return null; }
                var baseSpace = ParseImageColorSpace(elements[1], objects, depth + 1, out failure);
                if (baseSpace is null) return null;
                if (baseSpace.Kind == PdfImageColor.Indexed) { failure = "its palette is over a palette"; return null; }
                var hival = ReadInteger(ResolveValue(elements[2], objects));
                if (hival is < 0 or > 255) { failure = "its palette size is out of range"; return null; }
                var lookup = elements[3].Trim();
                byte[]? table = null;
                int? lookupObject = null;
                if (lookup.StartsWith('(') || lookup.StartsWith('<')) table = StringBytes(lookup);
                else if (TryReadReference(lookup, out var lookupId))
                {
                    var lookupBody = objects.GetValueOrDefault(lookupId)?.TrimStart();
                    if (lookupBody is not null && (lookupBody.StartsWith('(') || lookupBody.StartsWith('<') && !lookupBody.StartsWith("<<", StringComparison.Ordinal)))
                        table = StringBytes(lookupBody.TrimEnd());
                    else lookupObject = lookupId;
                }
                else { failure = "its palette table is not understood"; return null; }
                return new PdfImageColorSpace(PdfImageColor.Indexed, Palette: table is null ? null : PaletteFromTable(table, baseSpace.Kind, hival), Hival: hival,
                    LookupObject: lookupObject, Base: baseSpace.Kind);
            default:
                return Unsupported(ReadName(elements[0]), out failure);
        }

        static PdfImageColorSpace? Unsupported(string? name, out string? reason)
        {
            reason = $"the {name} colour space is not supported";
            return null;
        }
    }

    /// <summary>The RGB triples of a palette table over grey or RGB; entries the table lacks are black.</summary>
    private static byte[] PaletteFromTable(byte[] table, PdfImageColor baseKind, int hival)
    {
        var palette = new byte[(hival + 1) * 3];
        for (var entry = 0; entry <= hival; entry++)
            for (var channel = 0; channel < 3; channel++)
            {
                var at = baseKind == PdfImageColor.Rgb ? entry * 3 + channel : entry;
                palette[entry * 3 + channel] = at < table.Length ? table[at] : (byte)0;
            }
        return palette;
    }

    private static int ReadInteger(string? text) => ReadNumber(text) is { } value && value is >= 0 and <= int.MaxValue ? (int)value : 0;

    private static byte[] DecodeAsciiHex(byte[] payload)
    {
        var output = new List<byte>(payload.Length / 2);
        var high = -1;
        foreach (var value in payload)
        {
            if (value == '>') break;
            if (!char.IsAsciiHexDigit((char)value)) continue;
            var digit = Convert.ToInt32(((char)value).ToString(), 16);
            if (high < 0) high = digit;
            else
            {
                output.Add((byte)(high << 4 | digit));
                high = -1;
            }
        }
        if (high >= 0) output.Add((byte)(high << 4));
        return output.ToArray();
    }

    /// <summary>Turns decoded samples into the PNG file the export shows, after turning the picture upright.</summary>
    private static PdfDecodedImage WritePng(PdfSamples samples, PdfImageOrientation orientation)
    {
        var (width, height) = (samples.Width, samples.Height);
        // Channels of the interleaved 8-bit picture: palette indexes stay one channel unless there is alpha.
        var kind = samples.Kind;
        var data = samples.Data;
        var palette = samples.Palette;
        var channels = kind == PdfImageColor.Rgb ? 3 : 1;
        if (samples.Alpha is { } alpha)
        {
            if (kind == PdfImageColor.Indexed)
            {
                var rgb = new byte[checked(width * height * 3)];
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    var entry = Math.Min(data[pixel], (palette!.Length / 3) - 1);
                    rgb[pixel * 3] = palette[entry * 3];
                    rgb[pixel * 3 + 1] = palette[entry * 3 + 1];
                    rgb[pixel * 3 + 2] = palette[entry * 3 + 2];
                }
                (data, kind, palette, channels) = (rgb, PdfImageColor.Rgb, null, 3);
            }
            var withAlpha = new byte[checked(width * height * (channels + 1))];
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                Buffer.BlockCopy(data, pixel * channels, withAlpha, pixel * (channels + 1), channels);
                withAlpha[pixel * (channels + 1) + channels] = alpha[pixel];
            }
            data = withAlpha;
            channels++;
        }
        if (orientation.Code != 0)
            (data, width, height) = Turn(data, width, height, channels, orientation);
        byte colorType = kind switch
        {
            PdfImageColor.Indexed => 3,
            PdfImageColor.Gray => (byte)(channels == 2 ? 4 : 0),
            _ => (byte)(channels == 4 ? 6 : 2),
        };
        return new PdfDecodedImage("image/png", PdfPngWriter.Encode(width, height, channels, colorType, data, palette), width, height);
    }

    /// <summary>The picture with its axes turned and reversed as <paramref name="orientation"/> says, so it reads upright.</summary>
    private static (byte[] Data, int Width, int Height) Turn(byte[] data, int width, int height, int channels, PdfImageOrientation orientation)
    {
        var (targetWidth, targetHeight) = orientation.Swap ? (height, width) : (width, height);
        var output = new byte[data.Length];
        for (var row = 0; row < targetHeight; row++)
            for (var column = 0; column < targetWidth; column++)
            {
                int sourceColumn, sourceRow;
                if (orientation.Swap)
                {
                    sourceRow = orientation.ReverseX ? height - 1 - column : column;
                    sourceColumn = orientation.ReverseY ? width - 1 - row : row;
                }
                else
                {
                    sourceColumn = orientation.ReverseX ? width - 1 - column : column;
                    sourceRow = orientation.ReverseY ? height - 1 - row : row;
                }
                Buffer.BlockCopy(data, (sourceRow * width + sourceColumn) * channels, output, (row * targetWidth + column) * channels, channels);
            }
        return (output, targetWidth, targetHeight);
    }
}

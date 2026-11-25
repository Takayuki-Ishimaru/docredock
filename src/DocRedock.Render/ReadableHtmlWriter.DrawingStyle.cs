using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private string SourcePaint(DrawingPaint? paint, string fallback, string id, WorkbookDrawingObject drawing, string mode = "norm")
        {
            if (paint is null) return ShadeSourceColor(fallback, mode);
            if (paint.Kind == "none") return "none";
            if (paint.Kind == "solid") return HexColorValue(paint.Color ?? "") ? ShadeSourceColor(paint.Color!, mode) : fallback;
            if (paint.Kind == "pattern") return SourcePattern(paint, id, mode);
            if (paint.Kind == "picture") return SourcePicture(paint, id, drawing);
            if (paint.Kind == "radial" && paint.Stops.Count > 0) return SourceRadial(paint, id, drawing, mode);
            if (paint.Kind != "linear" || paint.Stops.Count == 0) return fallback;
            var radians = paint.AngleDegrees * Math.PI / 180;
            var dx = Math.Cos(radians); var dy = Math.Sin(radians);
            var w = Math.Max(1, drawing.Width); var h = Math.Max(1, drawing.Height);
            if (paint.Scaled) { dx *= w; dy *= h; }
            var length = Math.Sqrt(dx * dx + dy * dy); dx /= length; dy /= length;
            var half = (Math.Abs(dx) * w + Math.Abs(dy) * h) / 2;
            var cx = w / 2; var cy = h / 2;
            string? transform = null;
            if (!paint.RotateWithShape)
            {
                var m = drawing.Transform; var determinant = m.A * m.D - m.B * m.C;
                if (Math.Abs(determinant) > 1e-12)
                {
                    var world = m.Point(cx, cy); cx = world.X; cy = world.Y;
                    half = (Math.Abs(dx * m.A + dy * m.B) * w + Math.Abs(dx * m.C + dy * m.D) * h) / 2;
                    transform = Matrix(new(m.D / determinant, -m.B / determinant, -m.C / determinant, m.A / determinant,
                        (m.C * m.F - m.D * m.E) / determinant, (m.B * m.E - m.A * m.F) / determinant));
                }
            }
            _body.Append("<linearGradient id=\"").Append(id).Append("\" gradientUnits=\"userSpaceOnUse\" x1=\"")
                .Append(Px(cx - dx * half)).Append("\" y1=\"").Append(Px(cy - dy * half)).Append("\" x2=\"")
                .Append(Px(cx + dx * half)).Append("\" y2=\"").Append(Px(cy + dy * half)).Append('"');
            if (transform is not null) _body.Append(" gradientTransform=\"matrix(").Append(transform).Append(")\"");
            _body.Append('>');
            SourceStops(paint, mode);
            _body.Append("</linearGradient>");
            return "url(#" + id + ")";
        }

        private void SourceStops(DrawingPaint paint, string mode)
        {
            foreach (var stop in paint.Stops)
                _body.Append("<stop offset=\"").Append(Px(Math.Clamp(stop.Position, 0, 1))).Append("\" stop-color=\"")
                    .Append(HexColorValue(stop.Color) ? ShadeSourceColor(stop.Color, mode) : "#000000")
                    .Append("\" stop-opacity=\"").Append(Px(Math.Clamp(stop.Opacity, 0, 1))).Append("\"/>");
        }

        /// <summary>The opacity of the element a paint is applied to: a pattern and a picture carry theirs inside the tile.</summary>
        private static double SourcePaintOpacity(DrawingPaint? paint) =>
            paint is null || paint.Kind is "pattern" or "picture" || !double.IsFinite(paint.Opacity) ? 1 : Math.Clamp(paint.Opacity, 0, 1);

        /// <summary>
        /// A path gradient (a:path): position 0 lies at the centre of a:fillToRect and the last position on an
        /// ellipse of the frame's proportions through the frame's farthest corner, so every point of the frame is covered.
        /// </summary>
        private string SourceRadial(DrawingPaint paint, string id, WorkbookDrawingObject drawing, string mode)
        {
            var w = Math.Max(1, drawing.Width); var h = Math.Max(1, drawing.Height);
            var focus = paint.Focus ?? new(.5, .5, .5, .5);
            static double Centre(double near, double far) => double.IsFinite(near) && double.IsFinite(far) ? Math.Clamp((near + 1 - far) / 2, -10, 11) : .5;
            var cx = Centre(focus.Left, focus.Right); var cy = Centre(focus.Top, focus.Bottom);
            // In units of the frame, so that userSpaceOnUse also paints the outline of a flat shape, which has no bounding box.
            var radius = double.Hypot(Math.Max(Math.Abs(cx), Math.Abs(1 - cx)), Math.Max(Math.Abs(cy), Math.Abs(1 - cy)));
            _body.Append("<radialGradient id=\"").Append(id).Append("\" gradientUnits=\"userSpaceOnUse\" cx=\"0\" cy=\"0\" r=\"").Append(Px(radius))
                .Append("\" gradientTransform=\"matrix(").Append(Px(w)).Append(",0,0,").Append(Px(h)).Append(',').Append(Px(cx * w)).Append(',').Append(Px(cy * h)).Append(")\">");
            SourceStops(paint, mode);
            _body.Append("</radialGradient>");
            return "url(#" + id + ")";
        }

        /// <summary>
        /// A preset pattern (a:pattFill) as its 8x8 bitmap, one bitmap pixel per CSS pixel: Office draws these bitmaps
        /// at the resolution of the page, not scaled with the shape. The foreground pixels lie over the background.
        /// </summary>
        private string SourcePattern(DrawingPaint paint, string id, string mode)
        {
            var fore = HexColorValue(paint.Color ?? "") ? ShadeSourceColor(paint.Color!, mode) : "#000000";
            var back = HexColorValue(paint.BackColor ?? "") ? ShadeSourceColor(paint.BackColor!, mode) : "#FFFFFF";
            // A scene from elsewhere may name a pattern this version has no bitmap for.
            if (!DrawingPatternPresets.IsKnown(paint.Pattern)) return fore;
            _body.Append("<pattern id=\"").Append(id).Append("\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\" shape-rendering=\"crispEdges\">")
                .Append("<rect width=\"8\" height=\"8\" fill=\"").Append(back).Append("\" fill-opacity=\"")
                .Append(Px(double.IsFinite(paint.BackOpacity) ? Math.Clamp(paint.BackOpacity, 0, 1) : 1)).Append("\"/><path d=\"");
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    if (!DrawingPatternPresets.IsForeground(paint.Pattern, x, y)) continue;
                    var start = x;
                    while (x < 7 && DrawingPatternPresets.IsForeground(paint.Pattern, x + 1, y)) x++;
                    _body.Append('M').Append(start).Append(' ').Append(y).Append('h').Append(x - start + 1).Append("v1h-").Append(x - start + 1).Append('z');
                }
            _body.Append("\" fill=\"").Append(fore).Append("\" fill-opacity=\"")
                .Append(Px(double.IsFinite(paint.Opacity) ? Math.Clamp(paint.Opacity, 0, 1) : 1)).Append("\"/></pattern>");
            return "url(#" + id + ")";
        }

        private readonly Dictionary<string, (string? Uri, int Width, int Height)> _fillPictures = new(StringComparer.Ordinal);

        /// <summary>
        /// The picture of a picture fill as an embeddable data: URI with its pixel size (0 when unknown), under the same
        /// type, signature and size limits as any other image; counted once however many shapes it fills.
        /// </summary>
        private (string? Uri, int Width, int Height) FillPicture(string reference)
        {
            if (_fillPictures.TryGetValue(reference, out var known)) return known;
            string? uri = null; byte[] bytes = [];
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (IsSafeDataImage(reference) && reference.Length / 4L * 3 <= _options.MaxImageBytes + 32)
                {
                    try { bytes = Convert.FromBase64String(reference[(reference.IndexOf(',') + 1)..]); } catch (FormatException) { }
                    var type = reference[5..reference.IndexOf(';')].ToLowerInvariant();
                    if (bytes.Length > 0 && SignatureMatches(type, bytes)) uri = reference;
                }
            }
            else if (TryFindAsset(reference, out var key, out var asset))
            {
                var type = (asset.MediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
                if (type == "image/jpg") type = "image/jpeg";
                if (Array.IndexOf(EmbeddableTypes, type) >= 0 && SignatureMatches(type, asset.Content.Span) && asset.Content.Length <= _options.MaxImageBytes)
                {
                    bytes = asset.Content.ToArray();
                    if (!_dataUris.TryGetValue(key, out uri)) _dataUris[key] = uri = "data:" + type + ";base64," + Convert.ToBase64String(bytes);
                }
            }
            if (uri is not null && _embeddedBytes + bytes.Length > _options.MaxTotalImageBytes) uri = null;
            var size = (Width: 0, Height: 0);
            if (uri is not null)
            {
                _embeddedCount++; _embeddedBytes += bytes.Length;
                // PNG: the IHDR chunk follows the signature. The other formats are read as for a cropped picture.
                if (bytes.Length >= 24 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
                    size = (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
                else if (SourcePictureCropWindow.TryReadPixelSize(bytes, out var pixelWidth, out var pixelHeight)) size = (pixelWidth, pixelHeight);
                if (size.Width is <= 0 or > 1_000_000 || size.Height is <= 0 or > 1_000_000) size = (0, 0);
            }
            return _fillPictures[reference] = (uri, size.Width, size.Height);
        }

        /// <summary>
        /// A picture fill (a:blipFill on a shape) as an SVG pattern, which the shape's outline clips. Stretched: the
        /// part a:srcRect keeps fills the rectangle a:fillRect insets from the frame, once. Tiled: the picture repeats
        /// at its pixel size (a picture pixel is taken as one CSS pixel) times sx and sy, anchored by algn and moved by
        /// tx and ty; flip mirrors every second tile, so the pattern repeats after two tiles on that axis.
        /// </summary>
        private string SourcePicture(DrawingPaint paint, string id, WorkbookDrawingObject drawing)
        {
            var picture = paint.ImageReference is { Length: > 0 } reference ? FillPicture(reference) : default;
            if (picture.Uri is null)
            {
                if (_sceneWarnings.Add("fill-picture:" + drawing.Id))
                    _diagnostics.Add(new("HtmlSourceFillPictureUnavailable", "The picture of a picture fill is missing, not an embeddable image or over the image limits: " + drawing.Id + ". The shape is drawn without that fill."));
                return "none";
            }
            var w = Math.Max(1, drawing.Width); var h = Math.Max(1, drawing.Height);
            var crop = paint.Crop is { } source && new[] { source.Left, source.Top, source.Right, source.Bottom }.All(double.IsFinite) &&
                1 - source.Left - source.Right > 1e-6 && 1 - source.Top - source.Bottom > 1e-6 ? source : new DrawingInsets();
            var cropWidth = 1 - crop.Left - crop.Right; var cropHeight = 1 - crop.Top - crop.Bottom;
            var opacity = double.IsFinite(paint.Opacity) ? Math.Clamp(paint.Opacity, 0, 1) : 1;
            // The nested svg shows the kept part of the picture (the unit square is the whole picture) in the given box.
            void Image(double x, double y, double width, double height, bool mirrorX, bool mirrorY)
            {
                if (mirrorX || mirrorY)
                    _body.Append("<g transform=\"translate(").Append(Px(mirrorX ? 2 * x + width : 0)).Append(',').Append(Px(mirrorY ? 2 * y + height : 0))
                        .Append(") scale(").Append(mirrorX ? -1 : 1).Append(',').Append(mirrorY ? -1 : 1).Append(")\">");
                _body.Append("<svg x=\"").Append(Px(x)).Append("\" y=\"").Append(Px(y)).Append("\" width=\"").Append(Px(width)).Append("\" height=\"").Append(Px(height))
                    .Append("\" viewBox=\"").Append(Px(crop.Left)).Append(' ').Append(Px(crop.Top)).Append(' ').Append(Px(cropWidth)).Append(' ').Append(Px(cropHeight))
                    .Append("\" preserveAspectRatio=\"none\"><image width=\"1\" height=\"1\" preserveAspectRatio=\"none\" opacity=\"").Append(Px(opacity))
                    .Append("\" href=\"").Append(picture.Uri).Append("\"/></svg>");
                if (mirrorX || mirrorY) _body.Append("</g>");
            }
            void Open(double x, double y, double width, double height) =>
                _body.Append("<pattern id=\"").Append(id).Append("\" patternUnits=\"userSpaceOnUse\" x=\"").Append(Px(x)).Append("\" y=\"").Append(Px(y))
                    .Append("\" width=\"").Append(Px(width)).Append("\" height=\"").Append(Px(height)).Append("\">");
            if (paint.Tile is { } tile)
            {
                var tileWidth = picture.Width * cropWidth * Math.Abs(tile.ScaleX); var tileHeight = picture.Height * cropHeight * Math.Abs(tile.ScaleY);
                if (double.IsFinite(tileWidth) && double.IsFinite(tileHeight) && tileWidth is >= .01 and <= 1e6 && tileHeight is >= .01 and <= 1e6 &&
                    double.IsFinite(tile.OffsetX) && double.IsFinite(tile.OffsetY))
                {
                    var mirrorX = tile.Flip is "x" or "xy"; var mirrorY = tile.Flip is "y" or "xy";
                    var periodX = tileWidth * (mirrorX ? 2 : 1); var periodY = tileHeight * (mirrorY ? 2 : 1);
                    var originX = tile.OffsetX + tile.Alignment switch { "t" or "ctr" or "b" => (w - tileWidth) / 2, "tr" or "r" or "br" => w - tileWidth, _ => 0 };
                    var originY = tile.OffsetY + tile.Alignment switch { "l" or "ctr" or "r" => (h - tileHeight) / 2, "bl" or "b" or "br" => h - tileHeight, _ => 0 };
                    Open(originX - Math.Floor(originX / periodX) * periodX, originY - Math.Floor(originY / periodY) * periodY, periodX, periodY);
                    Image(0, 0, tileWidth, tileHeight, false, false);
                    if (mirrorX) Image(tileWidth, 0, tileWidth, tileHeight, true, false);
                    if (mirrorY) Image(0, tileHeight, tileWidth, tileHeight, false, true);
                    if (mirrorX && mirrorY) Image(tileWidth, tileHeight, tileWidth, tileHeight, true, true);
                    _body.Append("</pattern>");
                    return "url(#" + id + ")";
                }
                if (_sceneWarnings.Add("fill-tile:" + drawing.Id))
                    _diagnostics.Add(new("HtmlSourceFillPictureStretched", "The pixel size of a tiled picture fill could not be read: " + drawing.Id + ". The picture is stretched over the shape instead."));
            }
            var stretch = paint.Stretch is { } rect && new[] { rect.Left, rect.Top, rect.Right, rect.Bottom }.All(double.IsFinite) &&
                1 - rect.Left - rect.Right > 1e-6 && 1 - rect.Top - rect.Bottom > 1e-6 ? rect : new DrawingInsets();
            var left = stretch.Left * w; var top = stretch.Top * h;
            var width = (1 - stretch.Left - stretch.Right) * w; var height = (1 - stretch.Top - stretch.Bottom) * h;
            // One tile over the frame and the picture together, so that the picture is not repeated beside itself.
            var tileLeft = Math.Min(0, left); var tileTop = Math.Min(0, top);
            Open(tileLeft, tileTop, Math.Max(w, left + width) - tileLeft, Math.Max(h, top + height) - tileTop);
            Image(left - tileLeft, top - tileTop, width, height, false, false);
            _body.Append("</pattern>");
            return "url(#" + id + ")";
        }

        private readonly record struct SourceLineEnd(string Type, double Length, double Width, double Trim);

        /// <summary>
        /// The size of a line end as Office draws it: w and len (sm, med, lg) are 2, 3 and 5 line widths, the open
        /// "arrow" half a line width more, and a line thinner than 2 pt gets the ends of a 2 pt line (the factors and
        /// the floor are those of LibreOffice's import, oox/source/drawingml/lineproperties.cxx). Trim is how far
        /// before the end the stroke stops so that it does not show beside the tip: triangle, stealth and arrow have
        /// their tip at the end of the line, an oval and a diamond are centred on it.
        /// </summary>
        private static SourceLineEnd? LineEnd(string? type, string? width, string? length, double strokeWidth, bool opaque)
        {
            if (type is null or "none" or "" || !double.IsFinite(strokeWidth) || strokeWidth <= 0) return null;
            var open = type == "arrow";
            double Factor(string? size) => (size switch { "sm" => 2, "lg" => 5, _ => 3 }) + (open ? .5 : 0);
            var line = Math.Max(Math.Min(strokeWidth, 128), 2 * 96d / 72);
            var l = Factor(length) * line; var w = Factor(width) * line;
            return new(type, l, w, type switch
            {
                // The stroke ends just inside the head; where it is translucent the two must not overlap, or the
                // overlap would show darker: then it ends at the rim of an oval or diamond and at a stealth head's notch.
                "oval" or "diamond" => opaque ? 0 : l / 2,
                "arrow" => Math.Min(strokeWidth, 128) / 2,
                "stealth" => l * (opaque ? .55 : .6),
                _ => l - (opaque ? Math.Min(1, l / 4) : 0),
            });
        }

        /// <summary>Writes the marker of one line end. <paramref name="trimmed"/> is how far the stroke was actually shortened.</summary>
        private void Marker(string id, SourceLineEnd end, double trimmed, string color, double opacity, double strokeWidth)
        {
            var l = end.Length; var w = end.Width; var centred = end.Type is "oval" or "diamond";
            _body.Append("<marker id=\"").Append(id).Append("\" viewBox=\"0 0 ").Append(Px(l)).Append(' ').Append(Px(w)).Append("\" refX=\"")
                .Append(Px((centred ? l / 2 : l) - trimmed)).Append("\" refY=\"").Append(Px(w / 2)).Append("\" markerWidth=\"").Append(Px(l)).Append("\" markerHeight=\"").Append(Px(w))
                .Append("\" orient=\"auto-start-reverse\" markerUnits=\"userSpaceOnUse\">");
            if (end.Type == "oval")
                _body.Append("<ellipse cx=\"").Append(Px(l / 2)).Append("\" cy=\"").Append(Px(w / 2)).Append("\" rx=\"").Append(Px(l / 2)).Append("\" ry=\"").Append(Px(w / 2))
                    .Append("\" fill=\"").Append(color).Append("\" fill-opacity=\"").Append(Px(opacity)).Append("\"/>");
            else if (end.Type == "arrow")
            {
                // Two strokes of the line's own width meeting at the tip, kept inside the marker by their round ends.
                var half = Math.Min(Math.Min(strokeWidth, 128), Math.Min(l, w)) / 2;
                _body.Append("<path d=\"M ").Append(Px(half)).Append(' ').Append(Px(half)).Append(" L ").Append(Px(l - half)).Append(' ').Append(Px(w / 2)).Append(" L ")
                    .Append(Px(half)).Append(' ').Append(Px(w - half)).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-opacity=\"").Append(Px(opacity))
                    .Append("\" stroke-width=\"").Append(Px(half * 2)).Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
            }
            else
                _body.Append("<path d=\"").Append(end.Type switch
                    {
                        "diamond" => $"M 0 {Px(w / 2)} L {Px(l / 2)} 0 L {Px(l)} {Px(w / 2)} L {Px(l / 2)} {Px(w)} Z",
                        // The notch of a stealth head lies 60 % of its length behind the tip.
                        "stealth" => $"M {Px(l)} {Px(w / 2)} L 0 0 L {Px(l * .4)} {Px(w / 2)} L 0 {Px(w)} Z",
                        _ => $"M {Px(l)} {Px(w / 2)} L 0 0 L 0 {Px(w)} Z",
                    }).Append("\" fill=\"").Append(color).Append("\" fill-opacity=\"").Append(Px(opacity)).Append("\"/>");
            _body.Append("</marker>");
        }

        private static double? SourceLength(double? value, double minimum) =>
            value is { } number && double.IsFinite(number) ? Math.Clamp(number, minimum, 5376) : null;

        /// <summary>
        /// The Unicode character a one-character bullet of a symbol-encoded font draws (the common list bullets of
        /// Wingdings, Wingdings 2, Wingdings 3, Webdings and Symbol), or null to leave the marker in its font. Such fonts
        /// address a glyph as 0x20..0xFF or U+F020..U+F0FF.
        /// </summary>
        private static string? SymbolBullet(string? font, string marker)
        {
            if (font is null || marker.Length != 1) return null;
            var code = marker[0] is >= '\uF020' and <= '\uF0FF' ? marker[0] - 0xF000 : marker[0];
            return font.Trim().ToUpperInvariant() switch
            {
                "WINGDINGS" => code switch
                {
                    0x6C => "\u25CF", 0x6E => "\u25A0", 0x6F => "\u25A1", 0x71 => "\u2751", 0x72 => "\u2752", 0x75 => "\u25C6",
                    0x76 => "\u2756", 0x77 => "\u2B25", 0x9F => "\u2022", 0xA1 => "\u25CB", 0xA7 => "\u25AA", 0xA8 => "\u25FB",
                    0xD8 => "\u27A2", 0xE0 => "\u2794", 0xE8 => "\u2794", 0xF0 => "\u21E8", 0xFC => "\u2713", 0xFE => "\u2611",
                    _ => null,
                },
                // The marks and boxes of the O..S keys and the round bullet of the Office themes.
                "WINGDINGS 2" => code switch
                {
                    0x4F => "\u2717", 0x50 => "\u2713", 0x51 => "\u2612", 0x52 => "\u2611", 0x53 => "\u2612", 0x97 => "\u25CF",
                    _ => null,
                },
                // Arrows (f..i), triangles (p, q, t, u) and the small triangle bullet of the Office themes.
                "WINGDINGS 3" => code switch
                {
                    0x66 => "\u2190", 0x67 => "\u2192", 0x68 => "\u2191", 0x69 => "\u2193", 0x70 => "\u25B2", 0x71 => "\u25BC",
                    0x74 => "\u25C0", 0x75 => "\u25B6", 0x7D => "\u25B6",
                    _ => null,
                },
                "WEBDINGS" => code switch
                {
                    0x33 => "\u25C0", 0x34 => "\u25B6", 0x35 => "\u25B2", 0x36 => "\u25BC", 0x3C => "\u25A0", 0x3D => "\u25CF",
                    0x59 => "\u2665", 0x61 => "\u2714", 0x63 => "\u25A1", 0x67 => "\u25A0", 0x6E => "\u25CF", 0x72 => "\u2716",
                    _ => null,
                },
                "SYMBOL" => code switch
                {
                    0xB7 => "\u2022", 0xA8 => "\u2666", 0xAE => "\u2192", 0xDE => "\u21D2", 0x2D => "\u2212", 0xB0 => "\u00B0",
                    _ => null,
                },
                _ => null,
            };
        }

        /// <summary>
        /// Writes the paragraphs of a shape. A right-to-left paragraph (source rtl) is set with direction:rtl; its marL
        /// is then the margin on the right, and l and r name its leading and trailing edge, as Office and LibreOffice
        /// (editeng ImpEditEngine::GetJustification) draw them.
        /// Tabs: the default stops (defTabSz) become CSS tab-size, which a browser counts from the paragraph's text edge
        /// (marL), not from the text rectangle as Office does. Explicit stops (a:tabLst) are exact while the text fits
        /// before them: the line up to a tab is an inline block at least as wide as the stop is away, so what follows
        /// starts at the stop; at a right or centre stop the text up to the next tab is shifted back by its whole or half
        /// width, and at a decimal stop its part before the first full stop. The n-th tab of a line takes the n-th stop
        /// beyond the line start; a tab after the last stop is left to tab-size. Text wider than the way to its stop
        /// runs on from where it ends instead of taking the next stop.
        /// </summary>
        /// <param name="flat">The text rectangle has no extent on one axis: a paragraph keeps its own size there instead of
        /// shrinking into the rectangle, so that the anchor and the alignment place the whole text about it.</param>
        private void SourceCaption(IReadOnlyList<DrawingTextParagraph> paragraphs, bool flat = false)
        {
            foreach (var paragraph in paragraphs)
            {
                var rtl = paragraph.RightToLeft;
                _body.Append("<div class=\"source-caption-paragraph\" style=\"min-height:1em;").Append(flat ? "flex:none;" : "").Append("text-align:")
                    .Append(paragraph.Alignment switch { "ctr" or "center" => "center", "r" or "right" => rtl ? "left" : "right", "just" or "justify" => "justify", _ => rtl ? "right" : "left" })
                    .Append(rtl ? ";padding-right:" : ";padding-left:");
                // Source marL is where the text starts and indent where the first line (the bullet) starts, relative to it.
                // Without either, each level steps in by a fixed amount.
                var hanging = 0d;
                double textStart = Math.Clamp(paragraph.Level, 0, 8) * 20, firstLineStart = textStart;
                if (SourceLength(paragraph.MarginLeft, 0) is null && SourceLength(paragraph.Indent, -5376) is null)
                    _body.Append(Math.Clamp(paragraph.Level, 0, 8) * 20).Append("px");
                else
                {
                    var margin = SourceLength(paragraph.MarginLeft, 0) ?? 0;
                    var indent = Math.Max(SourceLength(paragraph.Indent, -5376) ?? 0, -margin);
                    hanging = Math.Max(0, -indent);
                    textStart = margin; firstLineStart = margin + indent;
                    _body.Append(Px(margin)).Append("px;text-indent:").Append(Px(indent)).Append("px");
                }
                // Single spacing is this canvas's line height of 1.15; an exact source pitch is taken as it is.
                if (SourceLength(paragraph.LineSpacingExact, 0) is { } pitch) _body.Append(";line-height:").Append(Px(pitch)).Append("px");
                else if (SourceLength(paragraph.LineSpacingAtLeast, 0) is { } least)
                {
                    // Word lineRule atLeast: a line is as high as its text needs, but not lower than the pitch. The natural
                    // line is approximated as this canvas's single spacing (1.15 times the run size), not the font's own
                    // metrics. A pitch above the natural line of the largest run is the line height; one below that of the
                    // smallest run changes nothing, so the paragraph keeps the canvas's line height. Between the two (runs
                    // of mixed sizes) every line gets the natural line of the largest run, also a line of small runs only.
                    var sizes = paragraph.Runs.Where(run => double.IsFinite(run.FontSize)).Select(run => Math.Clamp(run.FontSize, 1, 4096) * 1.15).ToArray();
                    if (sizes.Length == 0 || least > sizes.Max()) _body.Append(";line-height:").Append(Px(least)).Append("px");
                    else if (least > sizes.Min()) _body.Append(";line-height:").Append(Px(sizes.Max())).Append("px");
                }
                else if (paragraph.LineSpacingPercent is { } spacing && double.IsFinite(spacing))
                    _body.Append(";line-height:").Append(Px(1.15 * Math.Clamp(spacing, 0, 13200) / 100));
                if (SourceLength(paragraph.SpaceBefore, 0) is { } before) _body.Append(";margin-top:").Append(Px(before)).Append("px");
                if (SourceLength(paragraph.SpaceAfter, 0) is { } after) _body.Append(";margin-bottom:").Append(Px(after)).Append("px");
                if (SourceLength(paragraph.MarginRight, 0) is { } marginRight)
                    _body.Append(rtl ? ";padding-left:" : ";padding-right:").Append(Px(marginRight)).Append("px");
                if (rtl) _body.Append(";direction:rtl");
                var tabbed = paragraph.Runs.Any(run => run.Text.Contains('\t'));
                if (tabbed && SourceLength(paragraph.DefaultTabSize, 0) is > 0 and var tabSize) _body.Append(";tab-size:").Append(Px(tabSize)).Append("px");
                _body.Append("\">");
                var marked = false;
                if (paragraph.BulletText is { Length: > 0 } marker)
                {
                    marked = true;
                    // A hanging bullet fills the room between the first-line start and the text; text-indent must not reach into it.
                    _body.Append("<span class=\"source-caption-marker\" style=\"display:inline-block;");
                    if (hanging > 0) _body.Append("text-indent:0;min-width:").Append(Px(hanging)).Append("px");
                    else _body.Append("margin-right:.4em").Append(SourceLength(paragraph.Indent, -5376) is null ? "" : ";text-indent:0");
                    var bulletSize = paragraph.BulletSize is { } size && double.IsFinite(size) ? Math.Clamp(size, 1, 4096) : (double?)null;
                    // A picture bullet (a:buBlip) is as high as its size and keeps its proportions; without a usable
                    // picture the character that stands for it is drawn.
                    if (paragraph.BulletImage is { Length: > 0 } image && FillPicture(image).Uri is { } picture)
                        _body.Append("\"><img alt=\"\" src=\"").Append(picture).Append("\" style=\"height:")
                            .Append(bulletSize is null ? "1em" : Px(bulletSize.Value) + "px").Append("\"></span>");
                    else
                    {
                        // A symbol font's bullet is written as the Unicode character it draws; a viewer need not have that font.
                        var symbol = SymbolBullet(paragraph.BulletFontFamily, marker);
                        if (symbol is null && paragraph.BulletFontFamily is { Length: > 0 } bulletFont)
                            _body.Append(";font-family:").Append(Enc(SafeFont(bulletFont)));
                        if (bulletSize is not null) _body.Append(";font-size:").Append(Px(bulletSize.Value)).Append("px");
                        if (paragraph.BulletColor is { } bulletColor && HexColorValue(bulletColor)) _body.Append(";color:").Append(bulletColor);
                        if (paragraph.BulletOpacity is { } bulletOpacity && double.IsFinite(bulletOpacity) && bulletOpacity < 1)
                            _body.Append(";opacity:").Append(Px(Math.Clamp(bulletOpacity, 0, 1)));
                        _body.Append("\">").Append(Enc(symbol ?? marker)).Append("</span>");
                    }
                }
                // Source fontAlgn: where runs of different sizes sit in the line instead of on a common baseline.
                var fontAlignment = paragraph.FontAlignment switch { "t" => "vertical-align:top;", "ctr" => "vertical-align:middle;", "b" => "vertical-align:bottom;", _ => "" };
                void Run(DrawingTextRun run, string text)
                {
                    var link = run.LinkTarget is {} target && IsSafeAbsoluteLink(target) ? target : null;
                    if (link is not null) _body.Append("<a href=\"").Append(Enc(link)).Append("\" rel=\"noreferrer noopener\">");
                    else if (run.LinkTarget is {} cell && cell.StartsWith('#') && _sourceSheetName is {} sheet && (link = WorkbookLink(cell, sheet)) is not null)
                        _body.Append("<a href=\"").Append(Enc(link)).Append("\">");
                    else if (run.LinkTarget is {} jump && TrySlideLink(jump, out link) && link is not null)
                        _body.Append("<a href=\"").Append(Enc(link)).Append("\">");
                    // Office draws superscript and subscript runs at about two thirds of the run size.
                    var raised = double.IsFinite(run.Baseline) && run.Baseline != 0;
                    _body.Append("<span style=\"font-family:").Append(Enc(SafeFont(run.FontFamily))).Append(";font-size:")
                        .Append(Px(Math.Clamp(run.FontSize, 1, 4096) * (raised ? 2d / 3 : 1))).Append("px;")
                        .Append(raised ? run.Baseline > 0 ? "vertical-align:super;" : "vertical-align:sub;" : fontAlignment).Append("color:")
                        .Append(HexColorValue(run.Color) ? run.Color : "#000000").Append(";opacity:").Append(Px(Math.Clamp(run.Opacity, 0, 1)))
                        .Append(";font-weight:").Append(run.Bold ? "bold" : "normal").Append(";font-style:")
                        .Append(run.Italic ? "italic" : "normal").Append(";text-decoration:")
                        .Append(run.Underline ? run.Strike ? "underline line-through" : "underline" : run.Strike ? "line-through" : "none")
                        .Append("\">").Append(Enc(text)).Append("</span>");
                    if (link is not null) _body.Append("</a>");
                }
                var stops = tabbed && paragraph.TabStops is { Count: > 0 } source
                    ? source.Where(stop => stop is not null && double.IsFinite(stop.Position) && stop.Position is >= 0 and <= 5376).OrderBy(stop => stop.Position).Take(32).ToArray() : [];
                if (stops.Length == 0)
                {
                    foreach (var run in paragraph.Runs) Run(run, run.Text);
                    _body.Append("</div>");
                    continue;
                }
                // The paragraph as lines (a break ends one) of segments (a tab ends one) of run pieces.
                var lines = new List<List<List<(DrawingTextRun Run, string Text)>>> { new() { new() } };
                foreach (var run in paragraph.Runs)
                {
                    var from = 0;
                    for (var at = 0; at <= run.Text.Length; at++)
                    {
                        if (at < run.Text.Length && run.Text[at] is not ('\t' or '\n')) continue;
                        // A break stays with the line it ends.
                        var take = at < run.Text.Length && run.Text[at] == '\n' ? at + 1 : at;
                        if (take > from) lines[^1][^1].Add((run, run.Text[from..take]));
                        if (at == run.Text.Length) break;
                        if (run.Text[at] == '\t') lines[^1].Add(new()); else lines.Add(new() { new() });
                        from = at + 1;
                    }
                }
                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var segments = lines[lineIndex];
                    // After a hanging bullet the text starts at the margin; a bullet that does not hang has a width only a browser knows.
                    var lineStart = lineIndex > 0 || marked && hanging > 0 ? textStart : firstLineStart;
                    var reachable = stops.Where(stop => stop.Position > lineStart + .01).Take(segments.Count - 1).ToArray();
                    for (var index = reachable.Length - 1; index >= 0; index--)
                        _body.Append("<span class=\"source-caption-tab\" style=\"display:inline-block;min-width:").Append(Px(reachable[index].Position - lineStart))
                            .Append("px;text-indent:0;text-align:start;white-space:pre\">");
                    for (var index = 0; index < segments.Count; index++)
                    {
                        var pieces = segments[index];
                        if (index > 0 && index <= reachable.Length) _body.Append("</span>");
                        // A tab no stop is left for stays a tab character.
                        else if (index > 0 && pieces.Count > 0) pieces[0] = (pieces[0].Run, "\t" + pieces[0].Text);
                        else if (index > 0) pieces.Add((segments[index - 1].LastOrDefault().Run ?? paragraph.Runs[0], "\t"));
                        var alignment = index > 0 && index <= reachable.Length ? reachable[index - 1].Alignment : "l";
                        // What a right, centre or decimal stop holds back takes no room: the pen stays at the stop.
                        var shifted = alignment switch { "r" => pieces.Count, "ctr" => pieces.Count, "dec" => 0, _ => -1 };
                        if (alignment == "dec")
                        {
                            // The part before the decimal point ends at the stop; a number without one ends there as a whole.
                            var point = pieces.FindIndex(piece => piece.Text.Contains('.'));
                            if (point >= 0 && pieces[point].Text.IndexOf('.') is var dot)
                            {
                                var (run, text) = pieces[point];
                                pieces[point] = (run, text[..dot]);
                                pieces.Insert(point + 1, (run, text[dot..]));
                            }
                            shifted = point < 0 ? pieces.Count : point + 1;
                        }
                        // A break stays outside what is shifted.
                        if (shifted > 0 && pieces[shifted - 1].Text.EndsWith('\n'))
                        {
                            var (run, text) = pieces[shifted - 1];
                            pieces[shifted - 1] = (run, text[..^1]);
                            pieces.Insert(shifted, (run, "\n"));
                        }
                        if (shifted > 0)
                            _body.Append("<span style=\"display:inline-block;width:0;text-indent:0;white-space:pre\"><span style=\"display:inline-block;transform:translateX(")
                                .Append(alignment == "ctr" ? rtl ? "50%" : "-50%" : rtl ? "100%" : "-100%").Append(")\">");
                        for (var piece = 0; piece < pieces.Count; piece++)
                        {
                            if (piece == shifted && shifted > 0) _body.Append("</span></span>");
                            if (pieces[piece].Text.Length > 0) Run(pieces[piece].Run, pieces[piece].Text);
                        }
                        if (shifted > 0 && shifted >= pieces.Count) _body.Append("</span></span>");
                    }
                }
                _body.Append("</div>");
            }
        }
    }
}

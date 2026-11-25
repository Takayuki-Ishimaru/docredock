using System.Globalization;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

public static partial class ReadableHtmlWriter
{
    private sealed partial class HtmlDocumentWriter
    {
        private int _sceneBackgroundNumber;
        private readonly Dictionary<string, string?> _sharedSceneImages = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sceneCellStyles = new(StringComparer.Ordinal);

        /// <summary>
        /// Paints the scene's background (a slide's) behind its objects: the fill, then the picture, stretched over the
        /// frame or repeated as its tile says: at its pixel size (a picture pixel is taken as one CSS pixel) times sx
        /// and sy, anchored by algn and moved by tx and ty. A stretched picture shows the part its a:srcRect keeps in
        /// the rectangle its a:fillRect insets from the frame, still as the page's one shared picture. Tiles that are
        /// mirrored (flip) or cropped are drawn as the picture fill of a shape is, as an SVG pattern over the frame:
        /// CSS repeats neither a mirrored nor a cropped picture, so this slide holds the picture's bytes itself.
        /// </summary>
        private void SceneBackground(SourceDrawingScene scene)
        {
            if (scene.Background is { Kind: not "none" } paint)
            {
                var id = "source-scene-background-" + (++_sceneBackgroundNumber).ToString(CultureInfo.InvariantCulture);
                var frame = new WorkbookDrawingObject(scene.Id + ":background", "shape", scene.Width, scene.Height, WorkbookDrawingTransform.Identity);
                _body.Append("<svg class=\"source-scene-background\" xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Px(scene.Width)).Append("\" height=\"")
                    .Append(Px(scene.Height)).Append("\" aria-hidden=\"true\"><defs>");
                var fill = SourcePaint(paint, "none", id, frame);
                _body.Append("</defs><rect width=\"").Append(Px(scene.Width)).Append("\" height=\"").Append(Px(scene.Height)).Append("\" fill=\"").Append(fill)
                    .Append("\" fill-opacity=\"").Append(Px(Math.Clamp(paint.Opacity, 0, 1))).Append("\"/></svg>");
            }
            static DrawingInsets? Insets(DrawingInsets? insets) => insets is { } given && new[] { given.Left, given.Top, given.Right, given.Bottom }.All(double.IsFinite) &&
                given is not { Left: 0, Top: 0, Right: 0, Bottom: 0 } && 1 - given.Left - given.Right > 1e-6 && 1 - given.Top - given.Bottom > 1e-6 ? given : null;
            var crop = Insets(scene.BackgroundImageCrop);
            if (scene.BackgroundImageReference is { } tiled && scene.BackgroundImageTile is { } mirrored && (mirrored.Flip is "x" or "y" or "xy" || crop is not null) &&
                TryFindAsset(tiled, out _, out _))
            {
                var id = "source-scene-background-" + (++_sceneBackgroundNumber).ToString(CultureInfo.InvariantCulture);
                var frame = new WorkbookDrawingObject(scene.Id + ":background", "shape", scene.Width, scene.Height, WorkbookDrawingTransform.Identity);
                _body.Append("<svg class=\"source-scene-background\" xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Px(scene.Width)).Append("\" height=\"")
                    .Append(Px(scene.Height)).Append("\" aria-hidden=\"true\"><defs>");
                var fill = SourcePicture(new DrawingPaint { Kind = "picture", ImageReference = tiled, Crop = crop, Tile = mirrored }, id, frame);
                _body.Append("</defs><rect width=\"").Append(Px(scene.Width)).Append("\" height=\"").Append(Px(scene.Height)).Append("\" fill=\"").Append(fill).Append("\"/></svg>");
            }
            else if (scene.BackgroundImageReference is { } reference && SharedSceneImage(reference) is { } image)
            {
                if (scene.BackgroundImageTile is null && (crop is not null || Insets(scene.BackgroundImageStretch) is not null))
                {
                    var kept = crop ?? new DrawingInsets(); var placed = Insets(scene.BackgroundImageStretch) ?? new DrawingInsets();
                    // Where the picture lies in the frame, and the size and place of the whole picture so that its kept part fills that rectangle.
                    var left = placed.Left * scene.Width; var top = placed.Top * scene.Height;
                    var width = (1 - placed.Left - placed.Right) * scene.Width; var height = (1 - placed.Top - placed.Bottom) * scene.Height;
                    var pictureWidth = width / (1 - kept.Left - kept.Right); var pictureHeight = height / (1 - kept.Top - kept.Bottom);
                    // The box is the part of that rectangle inside the frame: a picture that reaches outside the slide is cut at its edge.
                    var boxLeft = Math.Max(0, left); var boxTop = Math.Max(0, top);
                    var boxWidth = Math.Min(scene.Width, left + width) - boxLeft; var boxHeight = Math.Min(scene.Height, top + height) - boxTop;
                    if (boxWidth <= 0 || boxHeight <= 0 || !new[] { pictureWidth, pictureHeight, left, top }.All(value => double.IsFinite(value) && Math.Abs(value) <= 1e7)) return;
                    _body.Append("<div class=\"source-scene-background source-shared-image ").Append(image).Append("\" style=\"left:").Append(Px(boxLeft)).Append("px;top:")
                        .Append(Px(boxTop)).Append("px;width:").Append(Px(boxWidth)).Append("px;height:").Append(Px(boxHeight)).Append("px;background-size:")
                        .Append(Px(pictureWidth)).Append("px ").Append(Px(pictureHeight)).Append("px;background-position:")
                        .Append(Px(left - kept.Left * pictureWidth - boxLeft)).Append("px ").Append(Px(top - kept.Top * pictureHeight - boxTop)).Append("px\"></div>");
                    return;
                }
                _body.Append("<div class=\"source-scene-background source-shared-image ").Append(image).Append('"');
                if (scene.BackgroundImageTile is { } tile && TryFindAsset(reference, out _, out var asset) &&
                    PixelSize(asset.Content.Span) is var (pixelWidth, pixelHeight) &&
                    pixelWidth * Math.Abs(tile.ScaleX) is >= .01 and <= 1e6 and var tileWidth && pixelHeight * Math.Abs(tile.ScaleY) is >= .01 and <= 1e6 and var tileHeight &&
                    double.IsFinite(tile.OffsetX) && double.IsFinite(tile.OffsetY) && Math.Abs(tile.OffsetX) <= 1e6 && Math.Abs(tile.OffsetY) <= 1e6)
                {
                    // A percentage places that point of the tile on the same point of the frame, as algn does.
                    var alignX = tile.Alignment switch { "t" or "ctr" or "b" => 50, "tr" or "r" or "br" => 100, _ => 0 };
                    var alignY = tile.Alignment switch { "l" or "ctr" or "r" => 50, "bl" or "b" or "br" => 100, _ => 0 };
                    static string Offset(double value) => (value < 0 ? "- " : "+ ") + Px(Math.Abs(value)) + "px";
                    _body.Append(" style=\"background-size:").Append(Px(tileWidth)).Append("px ").Append(Px(tileHeight)).Append("px;background-repeat:repeat;background-position:calc(")
                        .Append(alignX).Append("% ").Append(Offset(tile.OffsetX)).Append(") calc(").Append(alignY).Append("% ").Append(Offset(tile.OffsetY)).Append(")\"");
                }
                _body.Append("></div>");
            }
        }

        /// <summary>The pixel size of a picture from its header (PNG: the IHDR chunk follows the signature), or null when it is not read.</summary>
        private static (int Width, int Height)? PixelSize(ReadOnlySpan<byte> bytes)
        {
            var size = bytes.Length >= 24 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G'
                ? (Width: System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[16..]), Height: System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[20..]))
                : SourcePictureCropWindow.TryReadPixelSize(bytes, out var width, out var height) ? (Width: width, Height: height) : (Width: 0, Height: 0);
            return size.Width is > 0 and <= 1_000_000 && size.Height is > 0 and <= 1_000_000 ? size : null;
        }

        /// <summary>
        /// A picture the scene repeats from another part (a slide master's logo on every slide): drawn as a CSS
        /// background whose bytes the page holds once. Nothing is drawn when the picture is not among the assets.
        /// </summary>
        private void SharedScenePicture(WorkbookDrawingObject item)
        {
            if (item.ImageReference is not { } reference || SharedSceneImage(reference) is not { } image) return;
            var width = Math.Max(.001, 1 - item.CropLeft - item.CropRight); var height = Math.Max(.001, 1 - item.CropTop - item.CropBottom);
            _body.Append("<div class=\"source-drawing\" data-drawing-id=\"").Append(Enc(item.Id)).Append("\" data-drawing-kind=\"picture\" data-source-part=\"")
                .Append(Enc(item.Source?.PartUri ?? "")).Append("\" style=\"width:").Append(Px(Math.Max(1, item.Width))).Append("px;height:")
                .Append(Px(Math.Max(1, item.Height))).Append("px;transform:matrix(").Append(Matrix(item.Transform))
                .Append(")\"><div class=\"source-picture-clip\"><div class=\"source-shared-image ").Append(image).Append("\" role=\"img\" aria-label=\"")
                .Append(Enc(item.Text)).Append("\" style=\"position:absolute;width:").Append(Px(100 / width)).Append("%;height:").Append(Px(100 / height))
                .Append("%;left:").Append(Px(-100 * item.CropLeft / width)).Append("%;top:").Append(Px(-100 * item.CropTop / height)).Append("%\"></div></div></div>");
        }

        /// <summary>
        /// A table placed in a source scene keeps its source cell appearance in every reading theme, like the shapes
        /// around it: its cell styles are repeated, once each, as rules that outrank the reader's own cell edges and
        /// the light and dark themes' cell colors.
        /// </summary>
        private void SceneTableStyles(ReadableBlock block)
        {
            if (block is not ReadableTable table) return;
            foreach (var id in table.Rows.SelectMany(row => row.Cells).Select(cell => cell.StyleId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!_sceneCellStyles.Add(id) || !_styleClasses.TryGetValue(id, out var name) ||
                    _document.Styles?.GetValueOrDefault(id)?.Properties is not { } properties) continue;
                var declarations = string.Concat(properties.Where(p => SafeStyle(p.Key, p.Value)).Select(p => p.Key + ":" + p.Value + "!important;"));
                if (declarations.Length == 0) continue;
                const string cell = " .source-native-table tr>.";
                _pageStyles.Append(".direct-document").Append(cell).Append(name)
                    .Append(",.direct-document>#docredock-theme-light:checked~.reader-shell").Append(cell).Append(name)
                    .Append(",.direct-document>#docredock-theme-dark:checked~.reader-shell").Append(cell).Append(name)
                    .Append('{').Append(declarations).Append("}\n");
            }
            // The runs that are formatted differently from their cell, and the paragraphs that are aligned or indented on their own.
            foreach (var id in table.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Content.Runs).Select(run => run.StyleId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!id.StartsWith("scene-table-run-", StringComparison.Ordinal) && !id.StartsWith("scene-table-par-", StringComparison.Ordinal) || !_sceneCellStyles.Add(id) || !_styleClasses.TryGetValue(id, out var name) ||
                    _document.Styles?.GetValueOrDefault(id)?.Properties is not { } properties) continue;
                var declarations = string.Concat(properties.Where(p => SafeStyle(p.Key, p.Value)).Select(p => p.Key + ":" + p.Value + "!important;"));
                if (declarations.Length > 0) _pageStyles.Append(".direct-document .source-native-table .").Append(name).Append('{').Append(declarations).Append("}\n");
            }
        }

        /// <summary>The CSS class that paints the asset as a background, or null when it cannot be embedded (reported once).</summary>
        private string? SharedSceneImage(string reference)
        {
            if (_sharedSceneImages.TryGetValue(reference, out var known)) return known;
            string? name = null;
            if (TryFindAsset(reference, out var key, out var asset))
            {
                var type = (asset.MediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
                if (type == "image/jpg") type = "image/jpeg";
                var length = asset.Content.Length;
                if (Array.IndexOf(EmbeddableTypes, type) < 0 || !SignatureMatches(type, asset.Content.Span))
                    _diagnostics.Add(new ReadableHtmlDiagnostic(ImageNotEmbeddable,
                        $"Image '{Truncate(reference)}' ({(type.Length == 0 ? "unknown type" : type)}) is not an embeddable image type or its content does not match."));
                else if (length > _options.MaxImageBytes || _embeddedBytes + length > _options.MaxTotalImageBytes)
                    _diagnostics.Add(new ReadableHtmlDiagnostic(ImageEmbedSkipped,
                        $"Image '{Truncate(reference)}' ({length} bytes) exceeds the per-image limit of {_options.MaxImageBytes} bytes or the total image limit of {_options.MaxTotalImageBytes} bytes."));
                else
                {
                    if (!_dataUris.TryGetValue(key, out var uri))
                        _dataUris[key] = uri = "data:" + type + ";base64," + Convert.ToBase64String(asset.Content.Span);
                    _embeddedCount++; _embeddedBytes += length;
                    name = "source-shared-image-" + _sharedSceneImages.Count.ToString(CultureInfo.InvariantCulture);
                    _pageStyles.Append(".direct-document .").Append(name).Append("{background-image:url(\"").Append(uri).Append("\")}\n");
                }
            }
            return _sharedSceneImages[reference] = name;
        }
    }
}

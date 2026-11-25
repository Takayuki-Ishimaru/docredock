using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

public sealed record DrawingGeometryResult(IReadOnlyList<WorkbookDrawingPath> Paths,
    Geometry? TextBounds, IReadOnlyList<string> Warnings);

/// <summary>Bounded evaluator for ECMA-376 guide formulas and source/preset shape paths.</summary>
public static class DrawingMlGeometryReader
{
    private const int MaxGuides = 8192, MaxCommands = 65536;
    private const double FullCircle = 21600000, MaxValue = 1e12, MaxGuideValue = 1e150;
    private static readonly Lazy<XmlDocument> Presets = new(() =>
    {
        using var stream = typeof(DrawingMlGeometryReader).Assembly.GetManifestResourceStream(
            "DocRedock.Formats.OpenXml.Drawing.presetShapeDefinitions.xml")!;
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        document.Load(reader);
        // The standard attachment distributed by POI contains eight redundant trailing
        // zero operands in this family. Normalize only those pinned definitions, never
        // relax the grammar of a document's own custom guides.
        foreach (var shape in document.DocumentElement!.ChildNodes.OfType<XmlElement>()
                     .Where(e => e.LocalName is "circularArrow" or "leftCircularArrow" or "leftRightCircularArrow"))
            foreach (var guide in shape.SelectNodes(".//*[local-name()='gd']")!.OfType<XmlElement>())
            {
                var formula = guide.GetAttribute("fmla");
                if (guide.GetAttribute("name") is "xB" or "yB" or "xJ" or "yJ" &&
                    formula.StartsWith("+- ", StringComparison.Ordinal) && formula.EndsWith(" 0", StringComparison.Ordinal) &&
                    formula.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5)
                    guide.SetAttribute("fmla", formula[..^2]);
            }
        return document;
    });
    public static IReadOnlyList<string> PresetNames => Presets.Value.DocumentElement!.ChildNodes.OfType<XmlElement>().Select(e => e.LocalName).ToArray();

    public static DrawingGeometryResult Read(XmlElement geometry, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        try
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0 || width > 1e8 || height > 1e8)
                throw new GeometryException("Invalid source dimensions.");
            var definition = geometry.LocalName == "prstGeom"
                ? Presets.Value.DocumentElement!.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == geometry.GetAttribute("prst"))
                : geometry;
            if (definition is null) throw new GeometryException("Unknown preset: " + geometry.GetAttribute("prst"));
            var guides = Children(Child(definition, "avLst")).ToArray();
            var overrides = geometry.LocalName == "prstGeom" ? Children(Child(geometry, "avLst")).ToDictionary(e => e.GetAttribute("name"), StringComparer.Ordinal) : new Dictionary<string, XmlElement>();
            if (guides.Length + overrides.Count + Children(Child(definition, "gdLst")).Count() > MaxGuides)
                throw new GeometryException("Guide count exceeds the supported bound.");
            Dictionary<string, double> Context(double w, double h)
            {
                var context = BuiltIns(w, h);
                void SetGuide(XmlElement guide)
                {
                    var name = guide.GetAttribute("name");
                    if (name.Length is 0 or > 128) throw new GeometryException("Invalid guide name.");
                    context[name] = Formula(guide.GetAttribute("fmla"), context);
                }
                foreach (var guide in guides) SetGuide(overrides.GetValueOrDefault(guide.GetAttribute("name")) ?? guide);
                foreach (var guide in overrides.Values.Where(g => !context.ContainsKey(g.GetAttribute("name")))) SetGuide(guide);
                foreach (var guide in Children(Child(definition, "gdLst"))) SetGuide(guide);
                return context;
            }
            // The text rectangle is in the shape's EMU coordinate system, independent
            // of each path's optional w/h coordinate space (ISO 29500 CT_GeomRect).
            // A flat preset (cx or cy 0) is still drawn, as a line: its guides divide by the
            // extent, so they are evaluated one EMU wide and the path collapses on that axis.
            var flat = geometry.LocalName == "prstGeom" ? 1d : 0d;
            var emuWidth = Math.Max(flat, width * 9525); var emuHeight = Math.Max(flat, height * 9525);
            var shapeValues = Context(emuWidth, emuHeight);
            double ShapeValue(string name) => Resolve(name, shapeValues);
            Geometry? bounds = null;
            if (Child(definition, "rect") is { } textRect)
            {
                var l = ShapeValue(textRect.GetAttribute("l")) / 9525; var t = ShapeValue(textRect.GetAttribute("t")) / 9525;
                var r = ShapeValue(textRect.GetAttribute("r")) / 9525; var b = ShapeValue(textRect.GetAttribute("b")) / 9525;
                bounds = new Geometry("drawing-local", l, t, Math.Max(0, r - l), Math.Max(0, b - t));
            }
            var paths = new List<WorkbookDrawingPath>();
            var sourcePaths = Children(Child(definition, "pathLst")).ToArray();
            if (sourcePaths.Length > 256) throw new GeometryException("Path count exceeds the supported bound.");
            var count = 0;
            foreach (var path in sourcePaths)
            {
                double Extent(string attr, double fallback) => path.HasAttribute(attr) && ShapeValue(path.GetAttribute(attr)) > 0 ? ShapeValue(path.GetAttribute(attr)) : fallback;
                var pw = Extent("w", emuWidth); var ph = Extent("h", emuHeight);
                var values = Context(pw, ph);
                double Value(string name) => Resolve(name, values);
                var sx = pw > 0 ? width / pw : 1; var sy = ph > 0 ? height / ph : 1;
                var commands = new List<WorkbookDrawingCommand>();
                (double X, double Y) current = (0, 0), first = (0, 0);
                void Add(string kind, params double[] coordinates)
                {
                    if (++count > MaxCommands || coordinates.Any(v => !double.IsFinite(v) || Math.Abs(v) > MaxValue))
                        throw new GeometryException("Path complexity or coordinate exceeds the supported bound.");
                    commands.Add(new(kind, coordinates));
                }
                foreach (var command in Children(path))
                {
                    var points = Children(command).Where(e => e.LocalName == "pt")
                        .Select(e => (X: Value(e.GetAttribute("x")), Y: Value(e.GetAttribute("y")))).ToArray();
                    switch (command.LocalName)
                    {
                        case "moveTo" when points.Length == 1:
                            first = current = points[0]; Add("M", current.X * sx, current.Y * sy); break;
                        case "lnTo" when points.Length == 1:
                            current = points[0]; Add("L", current.X * sx, current.Y * sy); break;
                        case "quadBezTo" when points.Length == 2:
                            current = points[^1]; Add("Q", points[0].X * sx, points[0].Y * sy, current.X * sx, current.Y * sy); break;
                        case "cubicBezTo" when points.Length == 3:
                            current = points[^1]; Add("C", points[0].X * sx, points[0].Y * sy, points[1].X * sx, points[1].Y * sy, current.X * sx, current.Y * sy); break;
                        case "arcTo":
                        {
                            var rx = Math.Abs(Value(command.GetAttribute("wR"))); var ry = Math.Abs(Value(command.GetAttribute("hR")));
                            var angle = Value(command.GetAttribute("stAng")); var sweep = Value(command.GetAttribute("swAng"));
                            if (Math.Abs(sweep) > FullCircle * 10) throw new GeometryException("Arc sweep exceeds the supported bound.");
                            if ((rx == 0 && ry == 0) || sweep == 0) break;
                            (double X, double Y) Offset(double a)
                            {
                                var rad = a / 60000 * Math.PI / 180;
                                var cos = Math.Cos(rad); var sin = Math.Sin(rad);
                                if (rx == 0) return (0, ry * sin);
                                if (ry == 0) return (rx * cos, 0);
                                var denominator = Math.Sqrt(ry * ry * cos * cos + rx * rx * sin * sin);
                                return (rx * ry * cos / denominator, rx * ry * sin / denominator);
                            }
                            var start = Offset(angle); var center = (X: current.X - start.X, Y: current.Y - start.Y);
                            if (rx == 0 || ry == 0)
                            {
                                // A zero radius collapses the ellipse to a segment: a straight line to the arc end.
                                var tip = Offset(angle + sweep);
                                current = (center.X + tip.X, center.Y + tip.Y);
                                Add("L", current.X * sx, current.Y * sy);
                                break;
                            }
                            var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (FullCircle / 2)));
                            for (var i = 1; i <= pieces; i++)
                            {
                                var end = Offset(angle + sweep * i / pieces);
                                current = (center.X + end.X, center.Y + end.Y);
                                Add("A", rx * sx, ry * sy, 0, 0, sweep > 0 ? 1 : 0, current.X * sx, current.Y * sy);
                            }
                            break;
                        }
                        case "close": current = first; Add("Z"); break;
                        default: throw new GeometryException("Unsupported path command: " + command.LocalName);
                    }
                }
                if (commands.Count == 0) continue;
                var mode = path.GetAttribute("fill"); if (mode.Length == 0) mode = "norm";
                if (mode is not ("norm" or "none" or "lighten" or "lightenLess" or "darken" or "darkenLess"))
                    throw new GeometryException("Unsupported path fill mode: " + mode);
                paths.Add(new(commands, mode != "none", path.GetAttribute("stroke") is not ("0" or "false")) { FillMode = mode });
            }
            if (paths.Count == 0) throw new GeometryException("No source path commands.");
            return new(paths, bounds, []);
        }
        catch (GeometryException exception) { return new([], null, [exception.Message]); }
        catch (ArgumentException exception) { return new([], null, ["Invalid guide collection: " + exception.Message]); }
    }

    public static bool TryEvaluateFormula(string formula, IReadOnlyDictionary<string, double> values, out double value)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(values);
        try { value = Formula(formula, values); return true; }
        catch (GeometryException) { value = 0; return false; }
    }
    private static double Formula(string formula, IReadOnlyDictionary<string, double> values)
    {
        if (formula.Length > 4096) throw new GeometryException("Guide expression exceeds the supported bound.");
        var tokens = formula.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var arity = tokens.FirstOrDefault() switch { "val" or "abs" or "sqrt" => 1, "at2" or "cos" or "sin" or "tan" or "min" or "max" => 2,
            "*/" or "+-" or "+/" or "?:" or "cat2" or "sat2" or "mod" or "pin" => 3, _ => -1 };
        if (tokens.Length != arity + 1 || arity < 0) throw new GeometryException("Unknown guide operator or arity: " + formula);
        var x = Resolve(tokens[1], values); var y = arity > 1 ? Resolve(tokens[2], values) : 0; var z = arity > 2 ? Resolve(tokens[3], values) : 0;
        double Divide(double a, double b) => b != 0 ? a / b : throw new GeometryException("Zero guide divisor.");
        var result = tokens[0] switch
        {
            "val" => x, "abs" => Math.Abs(x), "sqrt" => x >= 0 ? Math.Sqrt(x) : throw new GeometryException("Negative guide square root."),
            "*/" => Divide(x * y, z), "+-" => x + y - z, "+/" => Divide(x + y, z), "?:" => x > 0 ? y : z,
            "min" => Math.Min(x, y), "max" => Math.Max(x, y), "pin" => y < x ? x : y > z ? z : y,
            "mod" => Math.Sqrt(x * x + y * y + z * z), "at2" => Math.Atan2(y, x) * 180 / Math.PI * 60000,
            "cat2" => x * Math.Cos(Math.Atan2(z, y)), "sat2" => x * Math.Sin(Math.Atan2(z, y)),
            "cos" => x * Math.Cos(y / 60000 * Math.PI / 180), "sin" => x * Math.Sin(y / 60000 * Math.PI / 180),
            "tan" => x * Math.Tan(y / 60000 * Math.PI / 180), _ => throw new GeometryException("Unknown guide formula.")
        };
        if (!double.IsFinite(result) || Math.Abs(result) > MaxGuideValue) throw new GeometryException("Nonfinite or excessive guide value.");
        return result;
    }
    private static double Resolve(string key, IReadOnlyDictionary<string, double> values)
    {
        if (double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal) && double.IsFinite(literal) && Math.Abs(literal) <= MaxValue) return literal;
        if (values.TryGetValue(key, out var value) && double.IsFinite(value) && Math.Abs(value) <= MaxGuideValue) return value;
        throw new GeometryException("Unresolved or invalid guide: " + key);
    }
    private static Dictionary<string, double> BuiltIns(double w, double h)
    {
        var v = new Dictionary<string, double>(StringComparer.Ordinal) { ["w"] = w, ["h"] = h, ["l"] = 0, ["t"] = 0, ["r"] = w, ["b"] = h,
            ["hc"] = w / 2, ["vc"] = h / 2, ["ss"] = Math.Min(w, h), ["ls"] = Math.Max(w, h), ["cd2"] = FullCircle / 2, ["cd4"] = FullCircle / 4,
            ["cd8"] = FullCircle / 8, ["3cd4"] = FullCircle * 3 / 4, ["3cd8"] = FullCircle * 3 / 8, ["5cd8"] = FullCircle * 5 / 8, ["7cd8"] = FullCircle * 7 / 8 };
        foreach (var divisor in new[] { 2, 3, 4, 5, 6, 8, 10, 12, 16, 32 })
        { v["wd" + divisor] = w / divisor; v["hd" + divisor] = h / divisor; v["ssd" + divisor] = Math.Min(w, h) / divisor; }
        return v;
    }
    private static XmlElement? Child(XmlElement parent, string name) => parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name);
    private static IEnumerable<XmlElement> Children(XmlElement? parent) => parent?.ChildNodes.OfType<XmlElement>() ?? [];
    private sealed class GeometryException(string message) : Exception(message);
}

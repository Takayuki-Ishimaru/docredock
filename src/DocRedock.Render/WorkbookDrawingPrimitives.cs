using System.Globalization;
using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Render;

/// <summary>Emits source DrawingML shape geometry as an SVG path in local CSS pixels. No re-layout, no markup beyond path data.</summary>
internal static class WorkbookDrawingPrimitives
{
    private const double MaxDimension = 1e8;
    private static readonly HashSet<string> LinePresets = new(StringComparer.Ordinal)
    {
        "line", "straightConnector1",
        "bentConnector2", "bentConnector3", "bentConnector4", "bentConnector5",
        "curvedConnector2", "curvedConnector3", "curvedConnector4", "curvedConnector5",
    };

    public static string? Path(WorkbookDrawingObject item)
    {
        if (item is null) return null;
        var w = item.Width;
        var h = item.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w < 0 || h < 0 || w > MaxDimension || h > MaxDimension)
            return null;
        if (item.Paths is { Count: > 0 }) return CustomPath(item.Paths);
        return PresetPath(item, w, h);
    }

    /// <summary>
    /// Shortens an open path at its start and end, for the line ends drawn there. An end is shortened only when it is a
    /// straight segment longer than the amount; otherwise it stays and its amount is reported as 0. The path is one
    /// this class wrote: commands and numbers separated by single spaces.
    /// </summary>
    public static string TrimEnds(string path, double start, double end, out double startTrimmed, out double endTrimmed)
    {
        startTrimmed = endTrimmed = 0;
        if (!(start > 0) && !(end > 0)) return path;
        var commands = new List<(string Kind, double[] Values)>();
        var tokens = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length;)
        {
            var kind = tokens[index++];
            var count = kind switch { "M" or "L" => 2, "Q" => 4, "C" => 6, "A" => 7, _ => -1 };
            if (count < 0 || index + count > tokens.Length) return path;
            var values = new double[count];
            for (var value = 0; value < count; value++)
                if (!double.TryParse(tokens[index++], NumberStyles.Float, CultureInfo.InvariantCulture, out values[value]) || !double.IsFinite(values[value])) return path;
            commands.Add((kind, values));
        }
        if (commands.Count < 2 || commands[0].Kind != "M" || commands.Skip(1).Any(command => command.Kind == "M")) return path;
        // Moves the first point towards the second by the amount when the segment between them is longer.
        static bool Shorten(double[] point, int at, double towardX, double towardY, double amount)
        {
            var length = double.Hypot(towardX - point[at], towardY - point[at + 1]);
            if (!(amount > 0) || !(length > amount * 1.05)) return false;
            point[at] += (towardX - point[at]) * amount / length; point[at + 1] += (towardY - point[at + 1]) * amount / length;
            return true;
        }
        if (commands[1].Kind == "L" && Shorten(commands[0].Values, 0, commands[1].Values[0], commands[1].Values[1], start)) startTrimmed = start;
        var last = commands[^1].Values; var before = commands[^2].Values;
        if (commands[^1].Kind == "L" && Shorten(last, 0, before[^2], before[^1], end)) endTrimmed = end;
        if (startTrimmed == 0 && endTrimmed == 0) return path;
        var sb = new StringBuilder();
        foreach (var (kind, values) in commands)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(kind);
            foreach (var value in values) sb.Append(' ').Append(N(value));
        }
        return sb.ToString();
    }

    private static string? CustomPath(IReadOnlyList<WorkbookDrawingPath> paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths)
        {
            if (path?.Commands is not { Count: > 0 }) return null;
            foreach (var command in path.Commands)
            {
                if (command?.Values is not { } values) return null;
                var expected = command.Kind switch
                {
                    "M" or "L" => 2,
                    "Q" => 4,
                    "C" => 6,
                    "A" => 7, // SVG arc: rx ry rotation large-arc-flag sweep-flag x y
                    "Z" => 0,
                    _ => -1,
                };
                if (expected < 0 || values.Count != expected) return null;
                foreach (var value in values)
                    if (!double.IsFinite(value)) return null;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(command.Kind);
                foreach (var value in values) sb.Append(' ').Append(N(value));
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? PresetPath(WorkbookDrawingObject item, double w, double h)
    {
        var a = item.Adjustments;
        var isLine = item.Preset is not null && LinePresets.Contains(item.Preset);
        if (isLine ? w == 0 && h == 0 : w == 0 || h == 0) return null;
        switch (item.Preset)
        {
            case "rect":
            case "flowChartProcess":
                return Poly((0, 0), (w, 0), (w, h), (0, h));
            case "roundRect":
            {
                var r = Math.Min(Math.Min(w, h) / 2, Adj(a, "adj", 16667) / 100000 * Math.Min(w, h));
                return r <= 0 ? Poly((0, 0), (w, 0), (w, h), (0, h)) : RoundedRect(w, h, r);
            }
            case "ellipse":
                return $"M {N(0)} {N(h / 2)} A {N(w / 2)} {N(h / 2)} 0 1 1 {N(w)} {N(h / 2)} A {N(w / 2)} {N(h / 2)} 0 1 1 {N(0)} {N(h / 2)} Z";
            case "triangle":
                return Poly((w / 2, 0), (w, h), (0, h));
            case "rtTriangle":
                return Poly((0, 0), (0, h), (w, h));
            case "diamond":
            case "flowChartDecision":
                return Poly((w / 2, 0), (w, h / 2), (w / 2, h), (0, h / 2));
            case "parallelogram":
            {
                var x = Math.Min(w, Adj(a, "adj", 25000) / 100000 * Math.Min(w,h));
                return Poly((x, 0), (w, 0), (w - x, h), (0, h));
            }
            case "trapezoid":
            {
                var x = Math.Min(w / 2, Adj(a, "adj", 25000) / 100000 * Math.Min(w,h));
                return Poly((x, 0), (w - x, 0), (w, h), (0, h));
            }
            case "hexagon":
            {
                var x = Math.Min(w / 2, Adj(a, "adj", 25000) / 100000 * Math.Min(w,h));
                return Poly((x, 0), (w - x, 0), (w, h / 2), (w - x, h), (x, h), (0, h / 2));
            }
            case "octagon":
            {
                var x = Math.Min(Math.Min(w, h) / 2, Adj(a, "adj", 29289) / 100000 * Math.Min(w, h));
                return Poly((x, 0), (w - x, 0), (w, x), (w, h - x), (w - x, h), (x, h), (0, h - x), (0, x));
            }
            case "chevron":
            {
                var x = Math.Min(w, Adj(a, "adj", 50000) / 100000 * Math.Min(w,h));
                return Poly((0, 0), (w - x, 0), (w, h / 2), (w - x, h), (0, h), (x, h / 2));
            }
            case "pentagon":
            {
                var sw=w/2*1.05146;var sh=h/2*1.10557;
                var dx1=sw*Math.Cos(Math.PI/10);var dx2=sw*Math.Cos(17*Math.PI/10);
                var y1=sh-sh*Math.Sin(Math.PI/10);var y2=sh-sh*Math.Sin(17*Math.PI/10);
                return Poly((w/2-dx1,y1),(w/2,0),(w/2+dx1,y1),(w/2+dx2,y2),(w/2-dx2,y2));
            }
            case "flowChartTerminator":
            {
                var r=w*3475/21600;
                return $"M {N(r)} 0 L {N(w-r)} 0 A {N(r)} {N(h/2)} 0 0 1 {N(w-r)} {N(h)} L {N(r)} {N(h)} A {N(r)} {N(h/2)} 0 0 1 {N(r)} 0 Z";
            }
            case "flowChartInputOutput":
            {
                var x = w/5;
                return Poly((x, 0), (w, 0), (w - x, h), (0, h));
            }
            case "rightArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * h; // shaft thickness
                var t = Math.Min(w, Adj(a, "adj2", 50000) / 100000 * Math.Min(w, h)); // head length
                return Poly((0, (h - s) / 2), (w - t, (h - s) / 2), (w - t, 0), (w, h / 2), (w - t, h), (w - t, (h + s) / 2), (0, (h + s) / 2));
            }
            case "leftArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * h;
                var t = Math.Min(w, Adj(a, "adj2", 50000) / 100000 * Math.Min(w, h));
                return Poly((t, 0), (t, (h - s) / 2), (w, (h - s) / 2), (w, (h + s) / 2), (t, (h + s) / 2), (t, h), (0, h / 2));
            }
            case "upArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * w; // shaft width
                var t = Math.Min(h, Adj(a, "adj2", 50000) / 100000 * Math.Min(w, h)); // head length
                return Poly((w / 2, 0), (w, t), ((w + s) / 2, t), ((w + s) / 2, h), ((w - s) / 2, h), ((w - s) / 2, t), (0, t));
            }
            case "downArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * w;
                var t = Math.Min(h, Adj(a, "adj2", 50000) / 100000 * Math.Min(w, h));
                return Poly((w / 2, h), (0, h - t), ((w - s) / 2, h - t), ((w - s) / 2, 0), ((w + s) / 2, 0), ((w + s) / 2, h - t), (w, h - t));
            }
            case "leftRightArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * h;
                var t = Math.Min(w / 2, Adj(a, "adj2", 50000) / 100000 * Math.Min(w,h));
                return Poly((0,h/2),(t,0),(t,(h-s)/2),(w-t,(h-s)/2),(w-t,0),(w,h/2),(w-t,h),(w-t,(h+s)/2),(t,(h+s)/2),(t,h));
            }
            case "upDownArrow":
            {
                var s = Adj(a, "adj1", 50000) / 100000 * w;
                var t = Math.Min(h / 2, Adj(a, "adj2", 50000) / 100000 * Math.Min(w,h));
                return Poly((w / 2, 0), (w, t), ((w + s) / 2, t), ((w + s) / 2, h - t), (w, h - t), (w / 2, h), (0, h - t), ((w - s) / 2, h - t), ((w - s) / 2, t), (0, t));
            }
            case "line" or "straightConnector1":
                return Open((0, 0), (w, h));
            case "bentConnector2":
                return Open((0, 0), (w, 0), (w, h));
            case "bentConnector3":
            case "bentConnector4":
            case "bentConnector5":
            case "curvedConnector2":
            case "curvedConnector3":
            case "curvedConnector4":
            case "curvedConnector5":
                return Connector(item,w,h);
        }
        return null; // Unknown preset: never fall back to a rectangle.
    }

    private static double Adj(IReadOnlyDictionary<string, double> adjustments, string key, double fallback)
    {
        if (adjustments is not null && adjustments.TryGetValue(key, out var value) && double.IsFinite(value))
            return Math.Clamp(value, 0, 100000); // DrawingML guide values are 1/100000ths.
        return fallback;
    }

    // ECMA-376 preset guides: connectors may deliberately bend outside their local box.
    private static string Connector(WorkbookDrawingObject item,double w,double h)
    {
        double Guide(string name)=>item.Adjustments?.TryGetValue(name,out var value)==true&&double.IsFinite(value)?Math.Clamp(value,-2147483647,2147483647)/100000:.5;
        var x=w*Guide("adj1");var y=h*Guide("adj2");var z=w*Guide("adj3");
        if(item.Preset=="bentConnector3")return Open((0,0),(x,0),(x,h),(w,h));
        if(item.Preset=="bentConnector4")return Open((0,0),(x,0),(x,y),(w,y),(w,h));
        if(item.Preset=="bentConnector5")return Open((0,0),(x,0),(x,y),(z,y),(z,h),(w,h));
        if(item.Preset=="curvedConnector2")return $"M 0 0 C {N(w/2)} 0 {N(w)} {N(h/2)} {N(w)} {N(h)}";
        if(item.Preset=="curvedConnector3")return $"M 0 0 C {N(x/2)} 0 {N(x)} {N(h/4)} {N(x)} {N(h/2)} C {N(x)} {N(3*h/4)} {N((w+x)/2)} {N(h)} {N(w)} {N(h)}";
        if(item.Preset=="curvedConnector4")
        {
            var x3=(w+x)/2;
            return $"M 0 0 C {N(x/2)} 0 {N(x)} {N(y/4)} {N(x)} {N(y/2)} C {N(x)} {N(3*y/4)} {N((x+x3)/2)} {N(y)} {N(x3)} {N(y)} C {N((x3+w)/2)} {N(y)} {N(w)} {N((h+y)/2)} {N(w)} {N(h)}";
        }
        var mid=(x+z)/2;var ym=(h+y)/2;
        return $"M 0 0 C {N(x/2)} 0 {N(x)} {N(y/4)} {N(x)} {N(y/2)} C {N(x)} {N(3*y/4)} {N((x+mid)/2)} {N(y)} {N(mid)} {N(y)} C {N((z+mid)/2)} {N(y)} {N(z)} {N((ym+y)/2)} {N(z)} {N(ym)} C {N(z)} {N((ym+h)/2)} {N((z+w)/2)} {N(h)} {N(w)} {N(h)}";
    }

    private static string Poly(params (double X, double Y)[] points)
        => Build(points, close: true);

    private static string Open(params (double X, double Y)[] points)
        => Build(points, close: false);

    private static string Build((double X, double Y)[] points, bool close)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < points.Length; i++)
            sb.Append(i == 0 ? "M " : " L ").Append(N(points[i].X)).Append(' ').Append(N(points[i].Y));
        if (close) sb.Append(" Z");
        return sb.ToString();
    }

    private static string RoundedRect(double w, double h, double r)
    {
        var arc = $" A {N(r)} {N(r)} 0 0 1 ";
        return $"M {N(r)} {N(0)} L {N(w - r)} {N(0)}{arc}{N(w)} {N(r)} L {N(w)} {N(h - r)}{arc}{N(w - r)} {N(h)}" +
               $" L {N(r)} {N(h)}{arc}{N(0)} {N(h - r)} L {N(0)} {N(r)}{arc}{N(r)} {N(0)} Z";
    }

    private static string N(double value)
        => (value == 0 ? 0d : value).ToString("0.####", CultureInfo.InvariantCulture);
}

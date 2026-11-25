using System.Globalization;
using System.Xml;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Drawing;

/// <summary>Reads DrawingML a:bodyPr text-layout attributes into a bounded core record.</summary>
public static class DrawingTextLayoutReader
{
    public const string DefaultDirection = "horz";

    private static readonly HashSet<string> KnownDirections = new(StringComparer.Ordinal)
    {
        "horz", "vert", "vert270", "eaVert", "wordArtVert", "wordArtVertRtl", "mongolianVert",
    };

    public static DrawingTextLayout Read(XmlElement? body, ICollection<string>? warnings = null)
    {
        var direction = DefaultDirection;
        double rotationDegrees = 0;
        var upright = false;
        var rightToLeftColumns = false;

        if (body is not null)
        {
            var vert = body.GetAttribute("vert");
            if (!string.IsNullOrEmpty(vert))
            {
                if (KnownDirections.Contains(vert))
                {
                    direction = vert;
                }
                else
                {
                    warnings?.Add($"Unknown text direction '{vert}'; defaulting to '{DefaultDirection}'.");
                }
            }

            var rot = body.GetAttribute("rot");
            if (!string.IsNullOrEmpty(rot))
            {
                if (double.TryParse(rot, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var raw)
                    && double.IsFinite(raw))
                {
                    rotationDegrees = NormalizeDegrees(raw / 60000.0);
                }
                else
                {
                    warnings?.Add($"Invalid text rotation '{rot}'; defaulting to 0.");
                }
            }

            upright = ReadBool(body.GetAttribute("upright"), "upright", warnings);
            rightToLeftColumns = ReadBool(body.GetAttribute("rtlCol"), "rtlCol", warnings);
        }

        return new DrawingTextLayout
        {
            Direction = direction,
            RotationDegrees = rotationDegrees,
            Upright = upright,
            RightToLeftColumns = rightToLeftColumns,
        };
    }

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static bool ReadBool(string? value, string name, ICollection<string>? warnings)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value is "1" or "true")
        {
            return true;
        }

        if (value is "0" or "false")
        {
            return false;
        }

        warnings?.Add($"Invalid boolean '{value}' for '{name}'; defaulting to false.");
        return false;
    }
}

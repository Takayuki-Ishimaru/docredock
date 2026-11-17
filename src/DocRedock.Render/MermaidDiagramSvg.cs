using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DocRedock.Tests")]

namespace DocRedock.Render;

/// <summary>
/// Static, script-free SVG renderer for the small Mermaid subset DocRedock's readable projection emits:
/// <c>flowchart LR|TD</c> (terminator, process, decision and data nodes; four link kinds with optional labels) and
/// <c>sequenceDiagram</c> (participants, solid/dotted messages, notes). Anything else returns <c>null</c> so the caller
/// can fall back to showing the Mermaid source.
/// </summary>
internal static partial class MermaidDiagramSvg
{
    private const int MaxNodes = 200;
    private const int MaxEdges = 400;
    private const int MaxDummies = 4000;
    private const int MaxSequenceItems = 400;
    private const double NodeFont = 14;
    private const double NodeLine = 18;
    private const double LabelFont = 12;
    private const double LabelLine = 15;
    private const double Margin = 24;
    private const string FontFamily = "'Hiragino Sans','Yu Gothic UI','Meiryo','Noto Sans CJK JP','Noto Sans JP',-apple-system,'Segoe UI',sans-serif";
    private const string BoxFill = "#f4f6fa";
    private const string BoxStroke = "#5b6577";
    private const string BoxText = "#1f2430";
    private const string NoteFill = "#f3efd9";

    private static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Rx = RegexOptions.CultureInvariant;
    private static readonly Regex HeadRx = new(@"^flowchart\s+(LR|TD|TB)$", Rx, RxTimeout);
    private static readonly Regex EntityRx = new(@"&(amp|quot|lt|gt|#124|#91|#93);|#59;", Rx, RxTimeout);
    private static readonly Regex BrRx = new(@"<br\s*/?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RxTimeout);
    private static readonly Regex IdRx = new(@"\G[\p{L}\p{N}_]+", Rx, RxTimeout);
    private static readonly Regex LinkRx = new(
        @"\G\s*(?:(?<op>-{2,}>|-{3,}|={2,}>|={3,}|-\.+->|-\.+-)(?:\s*\|(?<l>[^|]*)\|)?|--\s+(?<t1>.+?)\s+(?<op1>-{2,}>|-{3,})|-\.\s+(?<t2>.+?)\s+(?<op2>\.+->|\.+-)|==\s+(?<t3>.+?)\s+(?<op3>={2,}>|={3,}))",
        Rx, RxTimeout);
    private static readonly Regex SubgraphRx = new(@"^subgraph\s+(.+)$", Rx, RxTimeout);
    private static readonly Regex SubgraphIdRx = new(@"^([\p{L}\p{N}_]+)\s*\[(.*)\]$", Rx, RxTimeout);
    private static readonly Regex DirectionRx = new(@"^direction\s+(TB|TD|LR|RL|BT)$", Rx, RxTimeout);
    private static readonly Regex TitleCommentRx = new(@"^%%\s*title\s*:\s*(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RxTimeout);
    private static readonly Regex SeqParticipantRx = new(@"^(participant|actor)\s+([\p{L}\p{N}_]+)(?:\s+as\s+(.*))?$", Rx, RxTimeout);
    private static readonly Regex SeqMessageRx = new(@"^([\p{L}\p{N}_]+)\s*(-->>|->>|--x|-x|--\)|-\)|-->|->)\s*([+-])?\s*([\p{L}\p{N}_]+)\s*:\s*(.*)$", Rx, RxTimeout);
    private static readonly Regex SeqNoteRx = new(@"^note\s+(?:over\s+([\p{L}\p{N}_]+)(?:\s*,\s*([\p{L}\p{N}_]+))?|(left|right)\s+of\s+([\p{L}\p{N}_]+))\s*:\s*(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RxTimeout);
    private static readonly Regex SeqActivateRx = new(@"^(activate|deactivate)\s+([\p{L}\p{N}_]+)$", Rx, RxTimeout);
    private static readonly Regex SeqTitleRx = new(@"^title(?:\s*:\s*|\s+)(.+)$", Rx, RxTimeout);
    private static readonly Regex SeqFrameOpenRx = new(@"^(alt|opt|loop|par|critical|break)(?:\s+(.*))?$", Rx, RxTimeout);
    private static readonly Regex SeqFrameBranchRx = new(@"^(else|and|option)(?:\s+(.*))?$", Rx, RxTimeout);
    private static readonly Regex SeqAutonumberRx = new(@"^autonumber(?:\s+.*)?$", Rx, RxTimeout);

    public static string? Render(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return null;
            var id = "mmd" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).Substring(0, 8).ToLowerInvariant();
            if (lines[0] == "sequenceDiagram") return RenderSequence(lines, id);
            var head = HeadRx.Match(lines[0]);
            return head.Success ? RenderFlowchart(lines, head.Groups[1].Value == "LR", id) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private static string Decode(string value) => EntityRx.Replace(value, m => m.Value == "#59;" ? ";" : m.Groups[1].Value switch
    {
        "amp" => "&",
        "quot" => "\"",
        "lt" => "<",
        "gt" => ">",
        "#124" => "|",
        "#91" => "[",
        _ => "]",
    });

    private static string Esc(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default:
                    if (c < 0x20 && c != '\t') sb.Append(' '); else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string N(double v)
    {
        var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;
        return r.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static double RuneEm(Rune r)
    {
        var v = r.Value;
        if (v >= 0x2E80 && v <= 0xD7AF || v >= 0xF900 && v <= 0xFAFF || v >= 0xFE30 && v <= 0xFE4F || v >= 0xFF00 && v <= 0xFF60 ||
            v >= 0xFFE0 && v <= 0xFFE6 || v >= 0x1100 && v <= 0x11FF || v >= 0x20000 && v <= 0x3FFFF) return 1.0;
        return 0.6;
    }

    private static double TextWidth(string text, double fontSize)
    {
        var em = 0.0;
        foreach (var r in text.EnumerateRunes()) em += RuneEm(r);
        return em * fontSize;
    }

    private static List<string> Wrap(string text, double maxEm, double fontSize)
    {
        var limit = maxEm * fontSize;
        var result = new List<string>();
        var cur = new StringBuilder();
        var curW = 0.0;
        var lastSpace = -1;
        foreach (var r in text.EnumerateRunes())
        {
            var w = RuneEm(r) * fontSize;
            if (curW + w > limit && cur.Length > 0)
            {
                if (r.Value != ' ' && lastSpace > 0)
                {
                    var s = cur.ToString();
                    result.Add(s[..lastSpace].TrimEnd());
                    cur.Clear().Append(s[(lastSpace + 1)..]);
                    curW = TextWidth(cur.ToString(), fontSize);
                }
                else
                {
                    result.Add(cur.ToString().TrimEnd());
                    cur.Clear();
                    curW = 0;
                }
                lastSpace = -1;
                if (r.Value == ' ') continue;
            }
            if (r.Value == ' ') lastSpace = cur.Length;
            cur.Append(r.ToString());
            curW += w;
        }
        if (cur.Length > 0 || result.Count == 0) result.Add(cur.ToString().TrimEnd());
        return result;
    }

    private sealed class Bounds
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;
        public void Add(double x, double y)
        {
            MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
        }
        public void AddRect(double x, double y, double w, double h) { Add(x, y); Add(x + w, y + h); }
    }

    private static string Wrapper(string kind, string label, string body, Bounds b, string id, string extraDefs = "")
    {
        if (b.MinX > b.MaxX) { b.Add(0, 0); b.Add(10, 10); }
        var x = Math.Floor(b.MinX - 16);
        var y = Math.Floor(b.MinY - 16);
        var w = Math.Ceiling(b.MaxX + 16 - x);
        var h = Math.Ceiling(b.MaxY + 16 - y);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"").Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(w)).Append(' ').Append(N(h))
          .Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h)).Append("\" role=\"img\" aria-label=\"").Append(Esc(label))
          // No inline sizing: the page's stylesheet decides (natural size with scrolling on screen, fitted in print).
          .Append("\" font-family=\"").Append(FontFamily).Append("\" font-size=\"").Append(N(NodeFont)).Append("\">");
        sb.Append("<title>").Append(Esc(label)).Append("</title>");
        sb.Append("<defs><marker id=\"").Append(id).Append("-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto\">")
          .Append("<path d=\"M0,0 L10,5 L0,10 z\" fill=\"currentColor\"/></marker>").Append(extraDefs).Append("</defs>");
        sb.Append(body).Append("</svg>");
        _ = kind;
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, IReadOnlyList<string> lines, double cx, double cy, double fontSize, double lineHeight, string fill, string anchor = "middle", bool bold = false)
    {
        sb.Append("<text text-anchor=\"").Append(anchor).Append("\" font-size=\"").Append(N(fontSize)).Append("\" fill=\"").Append(fill).Append(bold ? "\" font-weight=\"bold\">" : "\">");
        var first = cy - (lines.Count - 1) * lineHeight / 2 + fontSize * 0.35;
        for (var i = 0; i < lines.Count; i++)
            sb.Append("<tspan x=\"").Append(N(cx)).Append("\" y=\"").Append(N(first + i * lineHeight)).Append("\">").Append(Esc(lines[i])).Append("</tspan>");
        sb.Append("</text>");
    }

}

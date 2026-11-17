using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Render;

// Sequence-diagram half of the Mermaid renderer: participants and actors, eight arrow kinds, activation bars,
// notes, titles and the alt/opt/loop/par/critical/break frames with their dividers.
internal static partial class MermaidDiagramSvg
{
    private const string TabFill = "#e3e8f2";
    private const string BarFill = "#dfe5f1";

    private enum SArrow { Filled, None, Cross, Open }

    private sealed class SPart
    {
        public string Id = "";
        public string Label = "";
        public List<string> Lines = [];
        public bool Actor;
        public double W;
        public double X;
    }

    private abstract class SItem
    {
        public List<string> Lines = [];
    }

    private sealed class SMsg : SItem
    {
        public int From, To;
        public SArrow Arrow;
        public bool Dotted, Plus, Minus;
    }

    private sealed class SNote : SItem
    {
        public int Kind; // 0 over, 1 left of, 2 right of
        public int First, Last;
    }

    private sealed class SAct : SItem
    {
        public int Part;
        public bool On;
    }

    private sealed class SFrameOpen : SItem
    {
        public string Kind = "";
        public string Text = "";
    }

    private sealed class SBranch : SItem
    {
        public string Keyword = "";
        public string Text = "";
    }

    private sealed class SFrameEnd : SItem
    {
    }

    private sealed class SFrame
    {
        public string Kind = "";
        public string Text = "";
        public double Y1, Y2, X1, X2;
        public double MinX = double.MaxValue, MaxX = double.MinValue;
        public List<(double Y, string Keyword, string Text)> Branches = [];
    }

    private static string? RenderSequence(List<string> lines, string id)
    {
        var parts = new List<SPart>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<SItem>();
        var frameKinds = new Stack<string>();
        string? title = null;

        int Part(string pid, string? label = null, bool? actor = null)
        {
            if (!index.TryGetValue(pid, out var i))
            {
                i = parts.Count;
                parts.Add(new SPart { Id = pid, Label = pid });
                index[pid] = i;
            }
            if (label is not null) parts[i].Label = label;
            if (actor is not null) parts[i].Actor = actor.Value;
            return i;
        }

        foreach (var line in lines.Skip(1))
        {
            Match m;
            if (line.StartsWith("%%", StringComparison.Ordinal) || SeqAutonumberRx.IsMatch(line)) continue;
            if ((m = SeqParticipantRx.Match(line)).Success)
            {
                Part(m.Groups[2].Value, m.Groups[3].Success ? CleanLabel(m.Groups[3].Value) : null, m.Groups[1].Value == "actor");
            }
            else if ((m = SeqTitleRx.Match(line)).Success)
            {
                title = CleanLabel(m.Groups[1].Value);
            }
            else if ((m = SeqMessageRx.Match(line)).Success)
            {
                var arrow = m.Groups[2].Value;
                var msg = new SMsg
                {
                    From = Part(m.Groups[1].Value),
                    To = Part(m.Groups[4].Value),
                    Dotted = arrow.StartsWith("--", StringComparison.Ordinal),
                    Arrow = arrow.EndsWith('x') ? SArrow.Cross : arrow.EndsWith(')') ? SArrow.Open : arrow.EndsWith(">>") ? SArrow.Filled : SArrow.None,
                    Plus = m.Groups[3].Value == "+",
                    Minus = m.Groups[3].Value == "-",
                };
                msg.Lines = WrapLabel(CleanLabel(m.Groups[5].Value), 30, LabelFont);
                items.Add(msg);
            }
            else if ((m = SeqNoteRx.Match(line)).Success)
            {
                var note = new SNote();
                if (m.Groups[1].Success)
                {
                    var a = Part(m.Groups[1].Value);
                    var b = m.Groups[2].Success ? Part(m.Groups[2].Value) : a;
                    note.First = Math.Min(a, b);
                    note.Last = Math.Max(a, b);
                }
                else
                {
                    note.Kind = m.Groups[3].Value.Equals("left", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    note.First = note.Last = Part(m.Groups[4].Value);
                }
                note.Lines = WrapLabel(CleanLabel(m.Groups[5].Value), 26, LabelFont);
                items.Add(note);
            }
            else if ((m = SeqActivateRx.Match(line)).Success)
            {
                items.Add(new SAct { Part = Part(m.Groups[2].Value), On = m.Groups[1].Value == "activate" });
            }
            else if ((m = SeqFrameOpenRx.Match(line)).Success)
            {
                var text = m.Groups[2].Success ? CleanLabel(m.Groups[2].Value) : "";
                items.Add(new SFrameOpen { Kind = m.Groups[1].Value, Text = text });
                frameKinds.Push(m.Groups[1].Value);
            }
            else if (frameKinds.Count > 0 && (m = SeqFrameBranchRx.Match(line)).Success)
            {
                var kw = m.Groups[1].Value;
                var kind = frameKinds.Peek();
                if (!(kind == "alt" && kw == "else" || kind == "par" && kw == "and" || kind == "critical" && kw == "option")) return null;
                items.Add(new SBranch { Keyword = kw, Text = m.Groups[2].Success ? CleanLabel(m.Groups[2].Value) : "" });
            }
            else if (line == "end" && frameKinds.Count > 0)
            {
                frameKinds.Pop();
                items.Add(new SFrameEnd());
            }
            else return null;
            if (items.Count > MaxSequenceItems || parts.Count > MaxNodes) return null;
        }
        if (parts.Count == 0 || frameKinds.Count != 0) return null;

        foreach (var p in parts)
        {
            p.Lines = WrapLabel(p.Label, 22, NodeFont);
            p.W = Math.Max(90, p.Lines.Max(l => TextWidth(l, NodeFont)) + 28 + (p.Actor ? 22 : 0));
        }
        var headH = Math.Max(36, parts.Max(p => p.Lines.Count) * NodeLine + 18);

        // Horizontal spacing: wide enough for names, message labels and notes.
        var gaps = new double[parts.Count];
        for (var i = 1; i < parts.Count; i++) gaps[i] = Math.Max(130, parts[i - 1].W / 2 + parts[i].W / 2 + 30);
        double Dist(int a, int b) { var d = 0.0; for (var k = a + 1; k <= b; k++) d += gaps[k]; return d; }
        double TextW(SItem it) => it.Lines.Max(l => TextWidth(l, LabelFont));
        foreach (var it in items.Where(i => i is SMsg or SNote).OrderBy(i => i switch { SMsg mm => Math.Abs(mm.From - mm.To), SNote nn => nn.Last - nn.First, _ => 0 }))
        {
            var tw = TextW(it);
            if (it is SMsg msg && msg.From != msg.To)
            {
                var lo = Math.Min(msg.From, msg.To);
                var hi = Math.Max(msg.From, msg.To);
                var need = tw + 30;
                var have = Dist(lo, hi);
                if (have < need) gaps[hi] += need - have;
            }
            else if (it is SMsg self)
            {
                if (self.From + 1 < parts.Count) gaps[self.From + 1] = Math.Max(gaps[self.From + 1], 44 + tw + 24);
            }
            else if (it is SNote note)
            {
                if (note.Kind == 0 && note.Last > note.First)
                {
                    var need = tw + 20 - 80;
                    var have = Dist(note.First, note.Last);
                    if (have < need) gaps[note.Last] += need - have;
                }
                else if (note.Kind == 1 && note.First > 0) gaps[note.First] = Math.Max(gaps[note.First], tw + 20 + 40);
                else if (note.Kind == 2 && note.Last + 1 < parts.Count) gaps[note.Last + 1] = Math.Max(gaps[note.Last + 1], tw + 20 + 40);
            }
        }
        var x = 0.0;
        for (var i = 0; i < parts.Count; i++) { x += gaps[i]; parts[i].X = x; }

        (double Left, double Width) NoteBox(SNote n)
        {
            var w = TextW(n) + 20;
            switch (n.Kind)
            {
                case 1:
                    w = Math.Max(w, 60);
                    return (parts[n.First].X - 14 - w, w);
                case 2:
                    w = Math.Max(w, 60);
                    return (parts[n.Last].X + 14, w);
                default:
                    var spanW = parts[n.Last].X - parts[n.First].X + 80;
                    w = Math.Max(Math.Max(w, spanW), 80);
                    return ((parts[n.First].X + parts[n.Last].X) / 2 - w / 2, w);
            }
        }
        var leftMost = parts.Min(p => p.X - p.W / 2);
        foreach (var n in items.OfType<SNote>()) leftMost = Math.Min(leftMost, NoteBox(n).Left);
        var shiftX = Margin + 10 - leftMost;
        foreach (var p in parts) p.X += shiftX;

        var bounds = new Bounds();
        var top = Margin + (title is null ? 0 : 30);
        var headers = new StringBuilder();
        var lifelines = new StringBuilder();
        var frameLines = new StringBuilder();
        var barSvg = new StringBuilder();
        var rows = new StringBuilder();
        var frameText = new StringBuilder();
        var marker = "url(#" + id + "-arrow)";
        var openMarker = "url(#" + id + "-open)";
        var crossMarker = "url(#" + id + "-cross)";

        var open = new List<double>[parts.Count];
        for (var i = 0; i < parts.Count; i++) open[i] = [];
        var bars = new List<(int Part, int Level, double Y1, double Y2)>();
        var frameStack = new Stack<SFrame>();
        var frames = new List<SFrame>();
        var y = top + headH + 26;
        var lastArrowY = double.NaN;
        var prevWasMessage = false;

        double BarSide(int p, int side, int depth) => depth <= 0 ? parts[p].X : side < 0 ? parts[p].X - 5 : parts[p].X + 5 + (depth - 1) * 5;
        void Touch(double a, double b)
        {
            if (frameStack.Count == 0) return;
            var f = frameStack.Peek();
            f.MinX = Math.Min(f.MinX, Math.Min(a, b));
            f.MaxX = Math.Max(f.MaxX, Math.Max(a, b));
        }
        void Close(int p, double endY)
        {
            if (open[p].Count == 0) return;
            var start = open[p][^1];
            open[p].RemoveAt(open[p].Count - 1);
            bars.Add((p, open[p].Count, start, Math.Max(endY, start + 16)));
        }

        foreach (var it in items)
        {
            var wasMessage = prevWasMessage;
            prevWasMessage = false;
            switch (it)
            {
                case SFrameOpen fo:
                {
                    var f = new SFrame { Kind = fo.Kind, Text = fo.Text, Y1 = y };
                    frameStack.Push(f);
                    frames.Add(f);
                    y += 28;
                    break;
                }
                case SBranch br:
                {
                    frameStack.Peek().Branches.Add((y + 2, br.Keyword, br.Text));
                    y += 28;
                    break;
                }
                case SFrameEnd:
                {
                    var f = frameStack.Pop();
                    f.Y2 = y + 2;
                    if (f.MinX > f.MaxX) { f.MinX = parts[0].X; f.MaxX = parts[^1].X; }
                    f.X1 = f.MinX - 12;
                    var minW = TextWidth(f.Kind, LabelFont) + 16 + 8 + (f.Text.Length > 0 ? TextWidth("[" + f.Text + "]", LabelFont) : 0) + 16;
                    foreach (var b in f.Branches) minW = Math.Max(minW, 8 + TextWidth("[" + b.Text + "]", LabelFont) + 16);
                    f.X2 = Math.Max(f.MaxX + 12, f.X1 + minW);
                    Touch(f.X1, f.X2);
                    y += 12;
                    break;
                }
                case SAct act:
                {
                    if (act.On) open[act.Part].Add(wasMessage ? lastArrowY : y);
                    else Close(act.Part, wasMessage ? lastArrowY + 14 : y - 6);
                    break;
                }
                case SMsg msg when msg.From == msg.To:
                {
                    var th = msg.Lines.Count * LabelLine;
                    var h = Math.Max(40, th + 14);
                    if (msg.Plus) open[msg.To].Add(y + 22);
                    var px = BarSide(msg.From, 1, open[msg.From].Count);
                    rows.Append("<path d=\"M").Append(N(px)).Append(',').Append(N(y)).Append(" L").Append(N(px + 36)).Append(',').Append(N(y)).Append(" L").Append(N(px + 36)).Append(',').Append(N(y + 22))
                        .Append(" L").Append(N(px)).Append(',').Append(N(y + 22)).Append("\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"")
                        .Append(msg.Dotted ? " stroke-dasharray=\"6 4\"" : "").Append(MarkerAttr(msg.Arrow, marker, openMarker, crossMarker)).Append("/>");
                    AppendText(rows, msg.Lines, px + 44, y + 11, LabelFont, LabelLine, "currentColor", "start");
                    var right = px + 44 + TextW(msg);
                    bounds.Add(right, y + h);
                    Touch(parts[msg.From].X, right);
                    lastArrowY = y + 22;
                    prevWasMessage = true;
                    if (msg.Minus) Close(msg.From, y + 22);
                    y += h + 8;
                    break;
                }
                case SMsg msg:
                {
                    var th = msg.Lines.Count * LabelLine;
                    var h = Math.Max(38, th + 24);
                    var ly = y + th;
                    var dir = parts[msg.To].X >= parts[msg.From].X ? 1 : -1;
                    if (msg.Plus) open[msg.To].Add(ly);
                    var x1 = BarSide(msg.From, dir, open[msg.From].Count);
                    var x2 = BarSide(msg.To, -dir, open[msg.To].Count);
                    rows.Append("<path d=\"M").Append(N(x1)).Append(',').Append(N(ly)).Append(" L").Append(N(x2)).Append(',').Append(N(ly)).Append("\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"")
                        .Append(msg.Dotted ? " stroke-dasharray=\"6 4\"" : "").Append(MarkerAttr(msg.Arrow, marker, openMarker, crossMarker)).Append("/>");
                    AppendText(rows, msg.Lines, (x1 + x2) / 2, y + th / 2 - 2, LabelFont, LabelLine, "currentColor");
                    bounds.AddRect(Math.Min(x1, x2), y, Math.Abs(x2 - x1), h);
                    Touch(x1, x2);
                    lastArrowY = ly;
                    prevWasMessage = true;
                    if (msg.Minus) Close(msg.From, ly);
                    y += h;
                    break;
                }
                case SNote note:
                {
                    var th = note.Lines.Count * LabelLine;
                    var (left, w) = NoteBox(note);
                    left += shiftX;
                    var h = th + 14;
                    rows.Append("<rect x=\"").Append(N(left)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
                        .Append("\" rx=\"3\" fill=\"").Append(NoteFill).Append("\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1\"/>");
                    AppendText(rows, note.Lines, left + w / 2, y + h / 2, LabelFont, LabelLine, BoxText);
                    bounds.AddRect(left, y, w, h);
                    Touch(left, left + w);
                    y += h + 14;
                    break;
                }
            }
        }
        for (var p = 0; p < parts.Count; p++)
            while (open[p].Count > 0) Close(p, y);
        var bottom = y + 6;

        // Frames: outline, tab with the keyword, guard text and dashed dividers.
        foreach (var f in frames)
        {
            var w = f.X2 - f.X1;
            var h = f.Y2 - f.Y1;
            frameLines.Append("<rect x=\"").Append(N(f.X1)).Append("\" y=\"").Append(N(f.Y1)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
                .Append("\" fill=\"none\" stroke=\"currentColor\" stroke-opacity=\"0.75\" stroke-width=\"1.2\"/>");
            bounds.AddRect(f.X1, f.Y1, w, h);
            var tabW = TextWidth(f.Kind, LabelFont) + 16;
            frameText.Append("<g class=\"frame\"><path d=\"M").Append(N(f.X1)).Append(',').Append(N(f.Y1)).Append(" L").Append(N(f.X1 + tabW)).Append(',').Append(N(f.Y1))
                .Append(" L").Append(N(f.X1 + tabW)).Append(',').Append(N(f.Y1 + 12)).Append(" L").Append(N(f.X1 + tabW - 6)).Append(',').Append(N(f.Y1 + 18))
                .Append(" L").Append(N(f.X1)).Append(',').Append(N(f.Y1 + 18)).Append(" Z\" fill=\"").Append(TabFill).Append("\" stroke=\"currentColor\" stroke-opacity=\"0.75\" stroke-width=\"1.2\"/>");
            AppendText(frameText, [f.Kind], f.X1 + tabW / 2 - 2, f.Y1 + 9, LabelFont, LabelLine, BoxText, "middle", true);
            if (f.Text.Length > 0) AppendText(frameText, ["[" + f.Text + "]"], f.X1 + tabW + 8, f.Y1 + 9, LabelFont, LabelLine, "currentColor", "start");
            foreach (var b in f.Branches)
            {
                frameText.Append("<path d=\"M").Append(N(f.X1)).Append(',').Append(N(b.Y)).Append(" L").Append(N(f.X2)).Append(',').Append(N(b.Y))
                    .Append("\" fill=\"none\" stroke=\"currentColor\" stroke-opacity=\"0.75\" stroke-width=\"1.2\" stroke-dasharray=\"7 4\"/>");
                AppendText(frameText, [b.Text.Length > 0 ? "[" + b.Text + "]" : b.Keyword], f.X1 + 8, b.Y + 14, LabelFont, LabelLine, "currentColor", "start");
            }
            frameText.Append("</g>");
        }

        foreach (var bar in bars)
        {
            barSvg.Append("<rect x=\"").Append(N(parts[bar.Part].X - 5 + bar.Level * 5)).Append("\" y=\"").Append(N(bar.Y1)).Append("\" width=\"10\" height=\"").Append(N(bar.Y2 - bar.Y1))
                .Append("\" fill=\"").Append(BarFill).Append("\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1\"/>");
            bounds.AddRect(parts[bar.Part].X - 5 + bar.Level * 5, bar.Y1, 10, bar.Y2 - bar.Y1);
        }

        foreach (var p in parts)
        {
            lifelines.Append("<line x1=\"").Append(N(p.X)).Append("\" y1=\"").Append(N(top + headH)).Append("\" x2=\"").Append(N(p.X)).Append("\" y2=\"").Append(N(bottom))
                .Append("\" stroke=\"currentColor\" stroke-width=\"1\" stroke-dasharray=\"4 4\" opacity=\"0.55\"/>");
            headers.Append("<g><rect x=\"").Append(N(p.X - p.W / 2)).Append("\" y=\"").Append(N(top)).Append("\" width=\"").Append(N(p.W)).Append("\" height=\"").Append(N(headH))
                .Append("\" rx=\"3\" fill=\"").Append(BoxFill).Append("\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1.5\"/>");
            var tx = p.X;
            if (p.Actor)
            {
                var ix = p.X - p.W / 2 + 16;
                var iy = top + headH / 2;
                headers.Append("<path d=\"M").Append(N(ix)).Append(',').Append(N(iy - 3)).Append(" L").Append(N(ix)).Append(',').Append(N(iy + 5))
                    .Append(" M").Append(N(ix - 6)).Append(',').Append(N(iy)).Append(" L").Append(N(ix + 6)).Append(',').Append(N(iy))
                    .Append(" M").Append(N(ix)).Append(',').Append(N(iy + 5)).Append(" L").Append(N(ix - 5)).Append(',').Append(N(iy + 12))
                    .Append(" M").Append(N(ix)).Append(',').Append(N(iy + 5)).Append(" L").Append(N(ix + 5)).Append(',').Append(N(iy + 12))
                    .Append("\" fill=\"none\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1.4\"/>")
                    .Append("<ellipse cx=\"").Append(N(ix)).Append("\" cy=\"").Append(N(iy - 8)).Append("\" rx=\"4.5\" ry=\"4.5\" fill=\"none\" stroke=\"").Append(BoxStroke).Append("\" stroke-width=\"1.4\"/>");
                tx += 11;
            }
            AppendText(headers, p.Lines, tx, top + headH / 2, NodeFont, NodeLine, BoxText);
            headers.Append("</g>");
            bounds.AddRect(p.X - p.W / 2, top, p.W, headH);
        }
        bounds.Add(parts.Max(p => p.X) + 10, bottom);

        var titleSvg = new StringBuilder();
        if (title is not null)
        {
            var cx = (parts.Min(p => p.X - p.W / 2) + parts.Max(p => p.X + p.W / 2)) / 2;
            AppendText(titleSvg, [title], cx, Margin + 8, 15, 18, "currentColor", "middle", true);
            bounds.Add(cx - TextWidth(title, 15) / 2, Margin - 4);
            bounds.Add(cx + TextWidth(title, 15) / 2, Margin + 18);
        }

        var extra = "<marker id=\"" + id + "-open\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto\">" +
                    "<polyline points=\"0,0 10,5 0,10\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"/></marker>" +
                    "<marker id=\"" + id + "-cross\" viewBox=\"0 0 10 10\" refX=\"5\" refY=\"5\" markerWidth=\"9\" markerHeight=\"9\" orient=\"auto\">" +
                    "<path d=\"M1,1 L9,9 M9,1 L1,9\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"/></marker>";
        var label = (title is null ? "" : title + ". ") + "Sequence diagram: " + parts.Count.ToString(CultureInfo.InvariantCulture) + " participants, " +
                    items.Count(i => i is SMsg or SNote).ToString(CultureInfo.InvariantCulture) + " items";
        return Wrapper("sequence", label, titleSvg.ToString() + headers + lifelines + frameLines + barSvg + rows + frameText, bounds, id, extra);
    }

    private static string MarkerAttr(SArrow arrow, string filled, string open, string cross) => arrow switch
    {
        SArrow.Filled => " marker-end=\"" + filled + "\"",
        SArrow.Open => " marker-end=\"" + open + "\"",
        SArrow.Cross => " marker-end=\"" + cross + "\"",
        _ => "",
    };
}

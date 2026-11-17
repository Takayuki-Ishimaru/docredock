using System.Globalization;
using System.Text.RegularExpressions;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

// Subgraph lanes, extra shapes and link kinds for flowcharts; full sequence diagrams (frames, activations, arrows).
public sealed class MermaidLaneAndSequenceSvgTests
{
    private const string LaneFlow = """
flowchart TD
    %% title: 承認フロー
    %% an ordinary comment
    subgraph L1["申請者"]
        direction TB
        a(["開始"])
        b["申請する"]
    end
    subgraph L2["上長"]
        direction TB
        c{"妥当か?"}
        d[["記録"]]
    end
    a --> b
    b -->|送信| c
    c -->|はい| d
    c -->|いいえ| b
""";

    private const string FullSequence = """
sequenceDiagram
    title 注文
    autonumber
    actor U as ユーザー
    participant S as サーバー
    participant D as DB
    U->>+S: 要求
    S-)D: 非同期
    S--)D: 非同期2
    S-xD: 失敗
    S--xD: 失敗2
    S->D: 線のみ
    S-->D: 点線のみ
    S->>S: 自己呼び出し
    activate D
    alt 成功
        D-->>-S: 結果 #59; 完了
        Note over U,S: 注記
    else 失敗
        opt 再試行
            S->>D: 再度
        end
    end
    par 並列A
        S->>U: 通知
    and 並列B
        S->>D: 保存
    end
    critical 重要
        S->>D: 書込
    option 失敗時
        S->>U: 通知
    end
    break 中断
        S-->>U: 終了
    end
    loop 毎分
        U->>S: ping
    end
    Note left of U: 左の注記
    Note right of D: 右の注記
    S-->>-U: 応答
""";

    private static readonly Regex Forbidden = new(
        @"<\s*(script|foreignObject|iframe|object|embed|image|use|a)\b|\son[a-z]+\s*=|javascript:|(?:href|src)\s*=|url\(\s*[""']?(?!#)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private const string BoxPattern = "<rect x=\"([-0-9.]+)\" y=\"([-0-9.]+)\" width=\"([0-9.]+)\" height=\"([0-9.]+)\"";

    private static string Svg(string source)
    {
        var svg = MermaidDiagramSvg.Render(source);
        Assert.NotNull(svg);
        Assert.DoesNotMatch(Forbidden, svg);
        return svg!;
    }

    private static int Count(string svg, string pattern) => Regex.Matches(svg, pattern).Count;

    private static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    private static (double X, double Y) Center(string svg, string label)
    {
        var m = Regex.Match(svg, "<tspan x=\"([-0-9.]+)\" y=\"([-0-9.]+)\">" + Regex.Escape(label));
        Assert.True(m.Success, "label not found: " + label);
        return (D(m.Groups[1].Value), D(m.Groups[2].Value));
    }

    private static List<(double X, double Y, double W, double H)> Rects(string svg, string prefix) =>
        Regex.Matches(svg, prefix + BoxPattern).Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value))).ToList();

    [Fact]
    public void Subgraph_lanes_become_vertical_bands_in_a_top_down_flowchart()
    {
        var svg = Svg(LaneFlow);
        var lanes = Rects(svg, "<g class=\"lane\">");
        Assert.Equal(2, lanes.Count);
        Assert.True(lanes[0].X + lanes[0].W <= lanes[1].X, "lanes overlap");
        Assert.Equal(lanes[0].Y, lanes[1].Y);
        Assert.Equal(lanes[0].H, lanes[1].H);
        foreach (var (label, lane) in new[] { ("開始", 0), ("申請する", 0), ("妥当か?", 1), ("記録", 1) })
            Assert.InRange(Center(svg, label).X, lanes[lane].X, lanes[lane].X + lanes[lane].W);
        // The layer order is kept across lanes: the rank axis still runs downwards.
        Assert.True(Center(svg, "申請する").Y > Center(svg, "開始").Y);
        Assert.True(Center(svg, "妥当か?").Y > Center(svg, "申請する").Y);
        Assert.Contains("申請者", svg);
        Assert.Contains("上長", svg);
        Assert.Contains("承認フロー", svg);
        Assert.DoesNotContain("ordinary comment", svg);
        // Every node keeps its text inside its own group.
        Assert.Equal(1, Count(svg, "<g><rect[^>]*/><text[^>]*><tspan[^>]*>開始"));
    }

    [Fact]
    public void Subgraph_lanes_become_horizontal_bands_in_a_left_right_flowchart()
    {
        var svg = Svg(LaneFlow.Replace("flowchart TD", "flowchart LR", StringComparison.Ordinal));
        var lanes = Rects(svg, "<g class=\"lane\">");
        Assert.Equal(2, lanes.Count);
        Assert.True(lanes[0].Y + lanes[0].H <= lanes[1].Y, "lanes overlap");
        Assert.Equal(lanes[0].X, lanes[1].X);
        Assert.InRange(Center(svg, "開始").Y, lanes[0].Y, lanes[0].Y + lanes[0].H);
        Assert.InRange(Center(svg, "妥当か?").Y, lanes[1].Y, lanes[1].Y + lanes[1].H);
        Assert.True(Center(svg, "申請する").X > Center(svg, "開始").X);
    }

    [Fact]
    public void Nested_subgraphs_stay_inside_their_parent_and_loose_nodes_stay_outside()
    {
        var svg = Svg("flowchart TD\n    s([Start])\n    subgraph outer[\"Outer\"]\n        subgraph inner[\"Inner\"]\n            x[X]\n            y[Y]\n        end\n        z[Z]\n    end\n    s --> x\n    x --> y\n    y --> z");
        var boxes = Rects(svg, "<g class=\"group\">");
        Assert.Equal(2, boxes.Count);
        var (outer, inner) = (boxes[0], boxes[1]);
        Assert.True(inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H);
        bool Inside((double X, double Y, double W, double H) r, (double X, double Y) p) => p.X >= r.X && p.X <= r.X + r.W && p.Y >= r.Y && p.Y <= r.Y + r.H;
        Assert.False(Inside(outer, Center(svg, "Start")), "loose node inside the container");
        Assert.True(Inside(outer, Center(svg, "Z")));
        Assert.False(Inside(inner, Center(svg, "Z")), "outer member inside the inner box");
        Assert.True(Inside(inner, Center(svg, "X")) && Inside(inner, Center(svg, "Y")));
    }

    [Fact]
    public void Extra_node_shapes_draw_recognisable_svg()
    {
        var svg = Svg("flowchart LR\n    a[[Sub]]\n    b((Circ))\n    c[(Store)]\n    d{{Hex}}\n    e>Flag]\n    f[\\Alt\\]\n    g[/Trap\\]\n    h(Round)\n    a --> b --> c --> d\n    d --> e --> f --> g --> h");
        Assert.Equal(1 + 1, Count(svg, "<ellipse")); // the circle, and the cylinder's top cap
        Assert.Equal(1, Count(svg, "<polygon points=\"([-0-9.]+,[-0-9.]+ ){5}[-0-9.]+,[-0-9.]+\"")); // hexagon
        Assert.Equal(1, Count(svg, "<polygon points=\"([-0-9.]+,[-0-9.]+ ){4}[-0-9.]+,[-0-9.]+\"")); // flag
        Assert.Equal(2, Count(svg, "<polygon points=\"([-0-9.]+,[-0-9.]+ ){3}[-0-9.]+,[-0-9.]+\"")); // parallelogram and trapezoid
        Assert.Contains(" A", svg); // cylinder arc
        foreach (var t in new[] { "Sub", "Circ", "Store", "Hex", "Flag", "Alt", "Trap", "Round" }) Assert.Contains(">" + t + "<", svg);
        Assert.Equal(7, Count(svg, "marker-end="));
    }

    [Fact]
    public void Thick_links_and_text_labels_parse()
    {
        var svg = Svg("flowchart TD\n    a[A]\n    b[B]\n    c[C]\n    a ==> b\n    b -- yes --> c\n    c -. maybe .-> a\n    a === c\n    b --> c --> a");
        Assert.Equal(2, Count(svg, "stroke-width=\"3\""));
        Assert.Contains(">yes<", svg);
        Assert.Contains(">maybe<", svg);
        Assert.Equal(5, Count(svg, "marker-end="));
    }

    [Fact]
    public void Sequence_with_every_arrow_title_actor_and_activation_renders()
    {
        var svg = Svg(FullSequence);
        Assert.Contains("注文", svg);
        Assert.Contains("結果 ; 完了", svg);
        Assert.Equal(2, Count(svg, "marker-end=\"url\\(#mmd[0-9a-f]+-open\\)\""));
        Assert.Equal(2, Count(svg, "marker-end=\"url\\(#mmd[0-9a-f]+-cross\\)\""));
        Assert.Equal(3, Count(svg, "<line "));
        Assert.True(Count(svg, "width=\"10\" height=") >= 2, "activation bars missing");
        Assert.Contains("<ellipse", svg); // the actor's head
        foreach (var t in new[] { "左の注記", "右の注記", "注記" }) Assert.Contains(t, svg);
        Assert.Equal(6, Count(svg, "<g class=\"frame\">")); // alt, opt, par, critical, break, loop
        foreach (var t in new[] { ">alt<", ">opt<", ">par<", ">critical<", ">break<", ">loop<", "[成功]", "[失敗]", "[並列B]", "[失敗時]" }) Assert.Contains(t, svg);
    }

    [Fact]
    public void Sequence_frames_span_their_messages_nest_and_draw_dividers()
    {
        var svg = Svg("sequenceDiagram\n    participant A\n    participant B\n    participant C\n    A->>B: before\n    alt guard one\n        B->>C: inside\n        loop inner\n            C->>C: self\n        end\n    else guard two\n        C-->>B: other\n    end\n    A->>B: after");
        var outlines = Regex.Matches(svg, BoxPattern + " fill=\"none\" stroke=\"currentColor\" stroke-opacity=\"0.75\"")
            .Select(m => (X: D(m.Groups[1].Value), Y: D(m.Groups[2].Value), W: D(m.Groups[3].Value), H: D(m.Groups[4].Value))).ToList();
        Assert.Equal(2, outlines.Count);
        var (alt, loop) = (outlines[0], outlines[1]);
        Assert.True(loop.X >= alt.X && loop.Y >= alt.Y && loop.X + loop.W <= alt.X + alt.W && loop.Y + loop.H <= alt.Y + alt.H, "nested frame escapes its parent");
        Assert.True(Center(svg, "before").Y < alt.Y && Center(svg, "after").Y > alt.Y + alt.H, "outer messages must lie outside the frame");
        Assert.InRange(Center(svg, "inside").Y, alt.Y, alt.Y + alt.H);
        Assert.True(alt.X > Center(svg, "A").X + 20, "the frame must not include participant A");
        Assert.Contains("stroke-dasharray=\"7 4\"", svg);
        Assert.Contains("[guard one]", svg);
        Assert.Contains("[guard two]", svg);
    }

    [Fact]
    public void Unterminated_or_mismatched_sequence_constructs_return_null()
    {
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    alt x\n    A->>B: y"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    par x\n    A->>B: y\n    else z\n    end"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    A->>B: y\n    Note across A: z"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    create participant X\n    A->>X: y"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart TD\n    subgraph s\n    a --> b"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart TD\n    a --> b\n    end"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart TD\n    a --> s\n    style a fill:#f00"));
    }

    [Fact]
    public void Flowchart_without_subgraphs_keeps_its_original_markup()
    {
        // No containers, no extra markers: the plain flowchart output has the same structure as before.
        var svg = Svg("flowchart LR\n    a[A]\n    b[B]\n    a --> b");
        Assert.DoesNotContain("class=\"lane\"", svg);
        Assert.DoesNotContain("-open", svg);
        Assert.DoesNotContain("-cross", svg);
        Assert.Equal(2, Count(svg, "<path d="));
    }

    [Fact]
    public void Html_writer_renders_laned_flowchart_and_framed_sequence_as_svg()
    {
        var md = "# 設計\n\n```mermaid\n" + LaneFlow + "\n```\n\n```mermaid\n" + FullSequence + "\n```\n";
        var html = ReadableHtmlWriter.Write(md).Html;
        Assert.Equal(2, Count(html, "<figure class=\"diagram\"><svg"));
        Assert.Contains("<g class=\"lane\">", html);
        Assert.Contains("<g class=\"frame\">", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}

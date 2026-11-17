using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Render;

namespace DocRedock.Tests.Render;

public sealed class MermaidDiagramSvgTests
{
    private const string RealFlowchart = """
flowchart LR
    v_6([DRAFT 下書き])
    v_7([SUBMITTED 提出済])
    v_8([PENDING 承認待ち])
    v_9([APPROVED 承認済])
    v_10([PAYMENT_PENDING 支払待ち])
    v_11([PAID 支払済])
    v_12([CANCELLED 取消])
    v_13([RETURNED 差戻し])
    v_14([ERROR 連携エラー])
    v_6 --> v_7
    v_7 --> v_8
    v_8 --> v_9
    v_9 --> v_10
    v_10 --> v_11
    v_8 --> v_13
    v_13 --> v_7
    v_14 -->|TR-09 連携失敗 &#91;retry_count &gt;= 5&#93;| v_10
    v_6 -->|TR-05 差戻し &#91;理由1文字以上&#93;| v_12
""";

    private static readonly Regex Forbidden = new(
        @"<\s*(script|foreignObject|iframe|object|embed|image|use|a)\b|\son[a-z]+\s*=|javascript:|(?:href|src)\s*=|url\(\s*[""']?(?!#)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static string Svg(string source)
    {
        var svg = MermaidDiagramSvg.Render(source);
        Assert.NotNull(svg);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"", svg);
        Assert.DoesNotMatch(Forbidden, svg);
        return svg!;
    }

    private static int Count(string svg, string pattern) => Regex.Matches(svg, pattern).Count;

    private static (double X, double Y) Center(string svg, string label)
    {
        var m = Regex.Match(svg, "<tspan x=\"([-0-9.]+)\" y=\"([-0-9.]+)\">" + Regex.Escape(label));
        Assert.True(m.Success, "label not found: " + label);
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Real_flowchart_renders_every_node_once_with_cycle_and_labelled_edges()
    {
        var svg = Svg(RealFlowchart);
        foreach (var label in new[] { "DRAFT", "SUBMITTED", "PENDING", "APPROVED", "PAYMENT_PENDING", "PAID", "CANCELLED", "RETURNED", "ERROR" })
            Assert.Equal(1, Count(svg, "<tspan[^>]*>" + label + " "));
        Assert.Equal(9, Count(svg, "<g><rect "));
        Assert.Equal(9 + 1, Count(svg, "<path d=")); // 9 edges + the arrowhead marker
        Assert.Contains("TR-09", svg);
        Assert.Contains("[retry_count &gt;= 5]", svg);
        Assert.Contains("marker-end=\"url(#mmd", svg);
    }

    [Fact]
    public void Every_node_shape_and_link_kind_parses()
    {
        var svg = Svg("flowchart TD\n    a([Start])\n    b[Work]\n    c{Ok?}\n    d[/Input/]\n    a --> b\n    b -.-> c\n    c --- d\n    d -.- a\n    c -->|yes &amp; &quot;go&quot;| b\n    d -.->|x &#124; y| a");
        Assert.Equal(2, Count(svg, "<polygon"));
        Assert.Equal(2, Count(svg, "<g><rect "));
        Assert.Contains("stroke-dasharray=\"6 4\"", svg);
        Assert.Contains("yes &amp; &quot;go&quot;", svg);
        Assert.Contains("x | y", svg);
        // Two undirected links have no arrowhead; the other four have one.
        Assert.Equal(4, Count(svg, "marker-end="));
        Assert.Equal(6 + 1, Count(svg, "<path d="));
    }

    [Fact]
    public void Unknown_lines_and_headers_return_null()
    {
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n    a[x]\n    classDef foo fill:#fff"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n    a --> b & c"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n    subgraph s\n    a --> b"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n    a --x b"));
        Assert.Null(MermaidDiagramSvg.Render("graph LR\n    a --> b"));
        Assert.Null(MermaidDiagramSvg.Render("pie\n    \"a\": 1"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    loop forever\n    A->>B: x"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    A->>B: x\n    end"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    opt x\n    A->>B: x\n    else y\n    end"));
        Assert.Null(MermaidDiagramSvg.Render("sequenceDiagram\n    rect rgb(0,0,0)\n    A->>B: x\n    end"));
        Assert.Null(MermaidDiagramSvg.Render(""));
        Assert.Null(MermaidDiagramSvg.Render(null));
    }

    [Fact]
    public void Orientation_changes_rank_axis()
    {
        const string body = "    a[First]\n    b[Second]\n    a --> b";
        var lr = Svg("flowchart LR\n" + body);
        var td = Svg("flowchart TD\n" + body);
        var (ax, ay) = Center(lr, "First");
        var (bx, by) = Center(lr, "Second");
        Assert.True(bx > ax + 50);
        Assert.True(Math.Abs(ay - by) < 2);
        (ax, ay) = Center(td, "First");
        (bx, by) = Center(td, "Second");
        Assert.True(by > ay + 40);
        Assert.True(Math.Abs(ax - bx) < 2);
    }

    [Fact]
    public void Self_loops_and_two_node_cycles_render()
    {
        var svg = Svg("flowchart LR\n    a[A]\n    b[B]\n    a --> a\n    a -->|go| b\n    b --> a");
        Assert.Equal(1, Count(svg, "<tspan[^>]*>A</tspan>"));
        Assert.Equal(1, Count(svg, "<tspan[^>]*>B</tspan>"));
        Assert.Equal(3 + 1, Count(svg, "<path d="));
    }

    [Fact]
    public void Labels_are_xml_escaped()
    {
        var svg = Svg("flowchart LR\n    a[&lt;script&gt;alert(1)&lt;/script&gt; &amp; co]\n    b[x]\n    a -->|&lt;b&gt;| b");
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", svg);
        Assert.Contains("&amp;", svg);
        Assert.Contains("&lt;b&gt;", svg);
    }

    [Fact]
    public void Long_labels_wrap_into_tspans()
    {
        var svg = Svg("flowchart LR\n    a[これは非常に長い日本語のラベルであり複数行に折り返されるはずです]");
        Assert.True(Count(svg, "<tspan") >= 2);
    }

    [Fact]
    public void Output_is_deterministic_and_has_title_and_role()
    {
        Assert.Equal(Svg(RealFlowchart), Svg(RealFlowchart));
        var svg = Svg(RealFlowchart);
        Assert.Contains("role=\"img\"", svg);
        Assert.Contains("<title>", svg);
        Assert.DoesNotContain("<script", svg);
    }

    [Fact]
    public void Sequence_diagram_with_notes_and_self_message_renders()
    {
        var svg = Svg("sequenceDiagram\n    participant P1 as 申請者\n    participant P2 as システム\n    participant P3 as 承認者\n    P1->>P2: 申請する\n    P2-->>P1: 受付 &#91;OK&#93;\n    P2->>P2: 検証\n    Note over P1,P3: 全体の注記 &amp; 補足\n    P2->>P3: 依頼");
        foreach (var s in new[] { "申請者", "システム", "承認者", "申請する", "受付 [OK]", "検証", "全体の注記 &amp; 補足", "依頼" })
            Assert.Contains(s, svg);
        Assert.Equal(3, Count(svg, "<line "));
        Assert.Equal(4, Count(svg, "<rect "));
        Assert.Equal(1, Count(svg, "stroke-dasharray=\"6 4\""));
        Assert.Equal(4, Count(svg, "marker-end="));
    }

    [Fact]
    public void Size_limits_return_null()
    {
        var nodes = string.Join("\n", Enumerable.Range(0, 201).Select(i => $"    n{i}[x]"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n" + nodes));
        var many = string.Join("\n", Enumerable.Range(0, 401).Select(_ => "    a --> b"));
        Assert.Null(MermaidDiagramSvg.Render("flowchart LR\n" + many));
        Assert.NotNull(MermaidDiagramSvg.Render("flowchart LR\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    n{i}[x]"))));
    }

    [Fact]
    public void Long_chain_renders_quickly()
    {
        var sb = new StringBuilder("flowchart TD\n");
        for (var i = 0; i < 150; i++) sb.Append($"    n{i}[Step {i}]\n");
        for (var i = 0; i < 149; i++) sb.Append($"    n{i} --> n{i + 1}\n");
        sb.Append("    n149 --> n0\n    n0 --> n75\n");
        var watch = Stopwatch.StartNew();
        var svg = Svg(sb.ToString());
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");
        Assert.Equal(150, Count(svg, "<tspan[^>]*>Step "));
    }

    [Fact]
    public void Html_writer_embeds_the_flowchart_as_inline_svg()
    {
        var md = "# 設計\n\n```mermaid\n" + RealFlowchart + "\n```\n";
        var html = ReadableHtmlWriter.Write(md).Html;
        Assert.Contains("<figure class=\"diagram\"><svg", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}

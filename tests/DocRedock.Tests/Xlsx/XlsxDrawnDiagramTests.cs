using System.IO.Compression;
using System.Text;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Xlsx;

/// <summary>
/// Flowcharts and sequence diagrams drawn as DrawingML shapes on a worksheet: the text of shapes must never
/// disappear, a drawn sequence diagram becomes a Mermaid sequenceDiagram, a small text box on a connector
/// becomes the connector's label, and every node belongs to exactly one swimlane. Worksheets are built here
/// from absolute anchors so the geometry in the assertions is exactly the geometry of the drawing.
/// </summary>
public sealed class XlsxDrawnDiagramTests
{
    [Fact]
    public void Shape_text_that_is_not_a_diagram_is_still_listed_in_reading_order_and_needs_review()
    {
        var drawing = new DrawingBuilder();
        drawing.Box("rect", 4_000_000, 3_000_000, 1_500_000, 500_000, "下の右");
        drawing.Box("rect", 400_000, 3_000_000, 1_500_000, 500_000, "下の左");
        drawing.Box("rect", 4_000_000, 400_000, 1_500_000, 500_000, "上の右");
        drawing.Box("rect", 400_000, 400_000, 1_500_000, 500_000, "上の左");
        var extraction = new XlsxAdapter().Extract(new MemoryStream(drawing.Package("Notes", "見出し")));

        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(node.Content).Text;
        var order = new[] { "上の左", "上の右", "下の左", "下の右" }.Select(text => mermaid.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(index => index), order);
        Assert.Equal("shape_text", node.Extensions!["diagram_type"].GetString());
        Assert.True(ReadableMarkdownSerializer.RequiresSourceReview(node));
        Assert.Contains(extraction.Warnings, warning => warning.Contains("could not be projected", StringComparison.Ordinal));

        var markdown = new ReadableMarkdownSerializer().Serialize(extraction.Graph);
        foreach (var text in new[] { "上の左", "上の右", "下の左", "下の右", "見出し" })
            Assert.Contains(text, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawn_sequence_diagram_becomes_a_mermaid_sequence_with_kinds_activations_and_frames()
    {
        var drawing = new DrawingBuilder();
        // participants in a row, dashed lifelines under them
        var centers = new long[] { 1_500_000, 4_500_000, 7_500_000 };
        var names = new[] { "利用者", "サーバー", "データベース" };
        for (var index = 0; index < centers.Length; index++)
        {
            var id = drawing.Box("rect", centers[index] - 600_000, 800_000, 1_200_000, 500_000, names[index]);
            drawing.Line("straightConnector1", centers[index], 1_300_000, 0, 6_700_000, dash: "dash", start: id);
        }
        drawing.Box("rect", 3_000_000, 200_000, 3_000_000, 350_000, "注文シーケンス", unpainted: true);
        // activation bar on the server
        drawing.Box("rect", 4_420_000, 2_000_000, 160_000, 1_600_000, "");
        // m1 sync, m2 async (open arrowhead), m3 dashed return, m4 self message
        drawing.Line("straightConnector1", 1_500_000, 2_000_000, 3_000_000, 0, tail: "triangle");
        drawing.Box("rect", 1_700_000, 1_650_000, 2_600_000, 300_000, "注文を送信", unpainted: true);
        drawing.Line("straightConnector1", 4_500_000, 2_800_000, 3_000_000, 0, tail: "arrow");
        drawing.Box("rect", 4_700_000, 2_450_000, 2_600_000, 300_000, "保存を依頼", unpainted: true);
        drawing.Line("straightConnector1", 4_500_000, 3_600_000, 3_000_000, 0, flipH: true, dash: "dash", tail: "triangle");
        drawing.Box("rect", 4_700_000, 3_250_000, 2_600_000, 300_000, "保存結果", unpainted: true);
        drawing.Line("bentConnector3", 4_500_000, 4_400_000, 10_000, 300_000, tail: "triangle");
        drawing.Box("rect", 5_000_000, 4_350_000, 1_500_000, 300_000, "在庫を確認", unpainted: true);
        // alt frame with a keyword tab, a guard, an else divider and its guard; one message in each branch
        drawing.Box("rect", 900_000, 5_000_000, 7_600_000, 2_700_000, "", unpainted: true);
        drawing.Box("homePlate", 900_000, 5_000_000, 700_000, 300_000, "alt");
        drawing.Box("rect", 1_700_000, 5_000_000, 1_200_000, 300_000, "[在庫あり]", unpainted: true);
        drawing.Line("straightConnector1", 1_500_000, 5_800_000, 3_000_000, 0, flipH: true, tail: "triangle");
        drawing.Box("rect", 1_700_000, 5_450_000, 2_600_000, 300_000, "引当を通知", unpainted: true);
        drawing.Line("straightConnector1", 900_000, 6_400_000, 7_600_000, 0, dash: "dash");
        drawing.Box("rect", 1_000_000, 6_450_000, 1_200_000, 300_000, "[在庫なし]", unpainted: true);
        drawing.Line("straightConnector1", 1_500_000, 7_100_000, 3_000_000, 0, tail: "triangle");
        drawing.Box("rect", 1_700_000, 6_750_000, 2_600_000, 300_000, "欠品を連絡", unpainted: true);

        var extraction = new XlsxAdapter().Extract(new MemoryStream(drawing.Package("Orders", "注文")));
        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(node.Content).Text;

        Assert.Equal("sequence", node.Extensions!["diagram_type"].GetString());
        string[] expected =
        [
            "sequenceDiagram",
            "participant P1 as 利用者", "participant P2 as サーバー", "participant P3 as データベース",
            "P1->>P2: 注文を送信", "activate P2",
            "P2-)P3: 保存を依頼",
            "P3-->>P2: 保存結果", "deactivate P2",
            "P2->>P2: 在庫を確認",
            "alt 在庫あり", "P2->>P1: 引当を通知",
            "else 在庫なし", "P1->>P2: 欠品を連絡", "end",
        ];
        var position = -1;
        foreach (var line in expected)
        {
            var found = mermaid.IndexOf(line, position + 1, StringComparison.Ordinal);
            Assert.True(found > position, $"'{line}' is missing or out of order in:\n{mermaid}");
            position = found;
        }
        // Everything on the sheet was used by the diagram: no text is left over for the fallback.
        Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        Assert.DoesNotContain(extraction.Warnings, warning => warning.Contains("not projected", StringComparison.Ordinal));
    }

    [Fact]
    public void Vertical_flowchart_connectors_are_not_taken_for_lifelines()
    {
        var drawing = new DrawingBuilder();
        var first = drawing.Box("flowChartProcess", 1_000_000, 500_000, 1_500_000, 500_000, "受付");
        var second = drawing.Box("flowChartProcess", 1_000_000, 2_500_000, 1_500_000, 500_000, "確認");
        var third = drawing.Box("flowChartProcess", 1_000_000, 4_500_000, 1_500_000, 500_000, "完了");
        drawing.Line("straightConnector1", 1_750_000, 1_000_000, 0, 1_500_000, tail: "triangle", start: first, end: second);
        drawing.Line("straightConnector1", 1_750_000, 3_000_000, 0, 1_500_000, tail: "triangle", start: second, end: third);
        var extraction = new XlsxAdapter().Extract(new MemoryStream(drawing.Package("Flow", "x")));

        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(node.Content).Text;
        Assert.StartsWith("flowchart", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceDiagram", mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_text_box_on_a_connector_labels_that_connector_instead_of_becoming_a_node()
    {
        var drawing = new DrawingBuilder();
        var decision = drawing.Box("flowChartDecision", 3_000_000, 500_000, 1_800_000, 800_000, "承認済み?");
        var yes = drawing.Box("flowChartProcess", 3_000_000, 3_500_000, 1_800_000, 600_000, "支払う");
        var no = drawing.Box("flowChartProcess", 6_500_000, 500_000, 1_800_000, 600_000, "差し戻す");
        // down: the label sits on the connector; right: the label sits beside it, well within a line of text
        drawing.Line("straightConnector1", 3_900_000, 1_300_000, 0, 2_200_000, tail: "triangle", start: decision, end: yes);
        drawing.Box("rect", 3_950_000, 2_250_000, 500_000, 280_000, "はい", unpainted: true);
        drawing.Line("straightConnector1", 4_800_000, 900_000, 1_700_000, 0, tail: "triangle", start: decision, end: no);
        drawing.Box("rect", 5_300_000, 560_000, 700_000, 280_000, "いいえ", unpainted: true);
        // a text box far from every connector is not a label and stays visible
        drawing.Box("rect", 9_500_000, 6_000_000, 1_600_000, 400_000, "遠くの注記", unpainted: true);
        var extraction = new XlsxAdapter().Extract(new MemoryStream(drawing.Package("Approval", "x")));

        var diagram = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram && item.Extensions!["diagram_type"].GetString() != "shape_text");
        var mermaid = Assert.IsType<TextNodeContent>(diagram.Content).Text;
        Assert.Contains($"N_S_{decision} -->|はい| N_S_{yes}", mermaid, StringComparison.Ordinal);
        Assert.Contains($"N_S_{decision} -->|いいえ| N_S_{no}", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"はい\"]", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"いいえ\"]", mermaid, StringComparison.Ordinal);
        // The unattached box is not dropped: it is listed (as the far note, or as a node of the diagram).
        var everything = string.Join("\n", extraction.Graph.Nodes.Where(item => item.Kind == NodeKind.Diagram)
            .Select(item => ((TextNodeContent)item.Content!).Text));
        Assert.Contains("遠くの注記", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Swimlanes_name_their_subgraphs_and_each_node_belongs_to_exactly_one_lane()
    {
        var drawing = new DrawingBuilder();
        drawing.Box("rect", 400_000, 100_000, 6_000_000, 400_000, "業務フロー図", unpainted: true);
        // two lane containers with a header band each; the lane border is at x = 4_000_000
        drawing.Box("rect", 500_000, 700_000, 3_500_000, 6_000_000, "");
        drawing.Box("rect", 500_000, 700_000, 3_500_000, 400_000, "営業");
        drawing.Box("rect", 4_000_000, 700_000, 3_500_000, 6_000_000, "");
        drawing.Box("rect", 4_000_000, 700_000, 3_500_000, 400_000, "経理");
        var start = drawing.Box("flowChartTerminator", 1_200_000, 1_500_000, 1_800_000, 500_000, "開始");
        var check = drawing.Box("flowChartProcess", 1_200_000, 3_000_000, 1_800_000, 500_000, "金額を確認");
        // centred exactly on the border between the lanes
        var edge = drawing.Box("flowChartProcess", 3_100_000, 4_500_000, 1_800_000, 500_000, "境界の処理");
        var pay = drawing.Box("flowChartProcess", 4_700_000, 5_500_000, 1_800_000, 500_000, "支払う");
        drawing.Line("straightConnector1", 2_100_000, 2_000_000, 0, 1_000_000, tail: "triangle", start: start, end: check);
        drawing.Line("straightConnector1", 2_100_000, 3_500_000, 0, 1_000_000, tail: "triangle", start: check, end: edge);
        drawing.Line("straightConnector1", 4_000_000, 5_000_000, 0, 500_000, tail: "triangle", start: edge, end: pay);
        var extraction = new XlsxAdapter().Extract(new MemoryStream(drawing.Package("Swim", "x")));

        var diagram = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(diagram.Content).Text;
        Assert.Contains("subgraph L1[\"営業\"]", mermaid, StringComparison.Ordinal);
        Assert.Contains("subgraph L2[\"経理\"]", mermaid, StringComparison.Ordinal);
        // lane headers and the title are no nodes; the title is kept as a comment
        Assert.DoesNotContain("[\"営業\"]\n        N_", mermaid, StringComparison.Ordinal);
        Assert.Equal(0, CountNodeDefinitions(mermaid, "営業") + CountNodeDefinitions(mermaid, "経理") + CountNodeDefinitions(mermaid, "業務フロー図"));
        Assert.Contains("%% title: 業務フロー図", mermaid, StringComparison.Ordinal);
        foreach (var id in new[] { start, check, edge, pay })
            Assert.Equal(1, CountDefinitions(mermaid, $"N_S_{id}"));
        var lanes = SplitLanes(mermaid);
        Assert.Contains($"N_S_{start}", lanes[0], StringComparison.Ordinal);
        Assert.Contains($"N_S_{check}", lanes[0], StringComparison.Ordinal);
        Assert.Contains($"N_S_{pay}", lanes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Benchmark_sequence_fixture_is_projected_with_its_frames_and_message_kinds()
    {
        var extraction = new XlsxAdapter().Extract(File.OpenRead(FindFixture("diagram-sequence-oidc.xlsx")));
        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(node.Content).Text;

        Assert.StartsWith("sequenceDiagram", mermaid, StringComparison.Ordinal);
        Assert.Equal(8, mermaid.Split('\n').Count(line => line.TrimStart().StartsWith("participant ", StringComparison.Ordinal)));
        var messages = mermaid.Split('\n').Select(line => line.Trim()).Where(line => line.Contains(">>", StringComparison.Ordinal) || line.Contains(")P", StringComparison.Ordinal)).ToArray();
        Assert.Equal(37, messages.Length);
        Assert.Equal(2, messages.Count(line => line.Contains("-)", StringComparison.Ordinal)));
        Assert.Equal(16, messages.Count(line => line.Contains("-->>", StringComparison.Ordinal)));
        foreach (var kind in new[] { "alt ", "opt ", "loop " })
            Assert.Contains("\n    " + kind, mermaid, StringComparison.Ordinal);
        Assert.Contains("else スコープ不足", mermaid, StringComparison.Ordinal);
        Assert.Equal(4, mermaid.Split('\n').Count(line => line.Trim() == "end"));
        Assert.DoesNotContain(extraction.Warnings, warning => warning.Contains("not projected", StringComparison.Ordinal));
    }

    [Fact]
    public void Benchmark_flowchart_fixture_labels_branches_and_keeps_every_node_in_one_lane()
    {
        var extraction = new XlsxAdapter().Extract(File.OpenRead(FindFixture("diagram-flowchart-approval.xlsx")));
        var node = Assert.Single(extraction.Graph.Nodes, item => item.Kind == NodeKind.Diagram);
        var mermaid = Assert.IsType<TextNodeContent>(node.Content).Text;

        Assert.Equal(4, mermaid.Split('\n').Count(line => line.TrimStart().StartsWith("subgraph ", StringComparison.Ordinal)));
        // a branch word is an edge label, never a node of its own
        Assert.DoesNotContain("[\"はい\"]", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"いいえ\"]", mermaid, StringComparison.Ordinal);
        Assert.Contains("|はい|", mermaid, StringComparison.Ordinal);
        Assert.Contains("|いいえ|", mermaid, StringComparison.Ordinal);
        Assert.Contains("|再申請|", mermaid, StringComparison.Ordinal);
        var definitions = mermaid.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("N_S_", StringComparison.Ordinal) && (line.Contains("[\"", StringComparison.Ordinal) || line.Contains("{\"", StringComparison.Ordinal) || line.Contains("([\"", StringComparison.Ordinal)))
            .Select(line => line[..line.IndexOfAny(['[', '{', '('])]).ToArray();
        Assert.Equal(definitions.Length, definitions.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("申請者\"]\n        N_S_", mermaid, StringComparison.Ordinal);
    }

    private static int CountDefinitions(string mermaid, string id) =>
        mermaid.Split('\n').Count(line => line.TrimStart().StartsWith(id + "[", StringComparison.Ordinal) ||
                                         line.TrimStart().StartsWith(id + "(", StringComparison.Ordinal) ||
                                         line.TrimStart().StartsWith(id + "{", StringComparison.Ordinal));

    private static int CountNodeDefinitions(string mermaid, string text) =>
        mermaid.Split('\n').Count(line => line.TrimStart().StartsWith("N_", StringComparison.Ordinal) &&
                                         line.Contains("\"" + text + "\"", StringComparison.Ordinal));

    private static string[] SplitLanes(string mermaid)
    {
        var lanes = new List<string>();
        var current = new StringBuilder();
        var inside = false;
        foreach (var line in mermaid.Split('\n'))
        {
            if (line.TrimStart().StartsWith("subgraph ", StringComparison.Ordinal)) { inside = true; current.Clear(); }
            else if (line.Trim() == "end" && inside) { lanes.Add(current.ToString()); inside = false; }
            else if (inside) current.AppendLine(line);
        }
        return lanes.ToArray();
    }

    private static string FindFixture(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Xlsx", fileName);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }
        throw new FileNotFoundException(fileName);
    }

    // Builds drawings of absolutely anchored shapes; ids are the ones the connectors refer to.
    private sealed class DrawingBuilder
    {
        private readonly StringBuilder xml = new();
        private int nextId = 1;

        public int Box(string preset, long x, long y, long width, long height, string text, bool unpainted = false)
        {
            var id = ++nextId;
            var paint = unpainted
                ? "<a:noFill/><a:ln><a:noFill/></a:ln>"
                : "<a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill></a:ln>";
            var body = text.Length == 0
                ? string.Empty
                : $"<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>{System.Security.SecurityElement.Escape(text)}</a:t></a:r></a:p></xdr:txBody>";
            xml.Append($"<xdr:absoluteAnchor><xdr:pos x=\"{x}\" y=\"{y}\"/><xdr:ext cx=\"{width}\" cy=\"{height}\"/>")
                .Append($"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"shape {id}\"/><xdr:cNvSpPr{(unpainted ? " txBox=\"1\"" : string.Empty)}/></xdr:nvSpPr>")
                .Append($"<xdr:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>{paint}</xdr:spPr>")
                .Append(body).Append("</xdr:sp><xdr:clientData/></xdr:absoluteAnchor>");
            return id;
        }

        public int Line(string preset, long x, long y, long width, long height, bool flipH = false, string? dash = null,
            string? tail = null, int? start = null, int? end = null)
        {
            var id = ++nextId;
            var ends = (start is null ? string.Empty : $"<a:stCxn id=\"{start}\" idx=\"2\"/>") + (end is null ? string.Empty : $"<a:endCxn id=\"{end}\" idx=\"0\"/>");
            var line = "<a:ln w=\"15875\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill>" +
                       (dash is null ? string.Empty : $"<a:prstDash val=\"{dash}\"/>") +
                       (tail is null ? string.Empty : $"<a:tailEnd type=\"{tail}\"/>") + "</a:ln>";
            xml.Append($"<xdr:absoluteAnchor><xdr:pos x=\"{x}\" y=\"{y}\"/><xdr:ext cx=\"{width}\" cy=\"{height}\"/>")
                .Append($"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"line {id}\"/><xdr:cNvCxnSpPr>{ends}</xdr:cNvCxnSpPr></xdr:nvCxnSpPr>")
                .Append($"<xdr:spPr><a:xfrm{(flipH ? " flipH=\"1\"" : string.Empty)}><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom>{line}</xdr:spPr>")
                .Append("</xdr:cxnSp><xdr:clientData/></xdr:absoluteAnchor>");
            return id;
        }

        public byte[] Package(string sheetName, string cellText)
        {
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
                ["xl/workbook.xml"] = $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{sheetName}\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
                ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
                ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    $"<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(cellText)}</t></is></c></row></sheetData><drawing r:id=\"rDrawing\" /></worksheet>",
                ["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>",
                ["xl/drawings/drawing1.xml"] = "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" + xml + "</xdr:wsDr>",
            };
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var part in parts)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8);
                    writer.Write(part.Value);
                }
            return output.ToArray();
        }
    }
}

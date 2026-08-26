using System.IO.Compression;
using System.Text;
using DocRedock.Api;
using DocRedock.Formats.OpenXml.Xlsx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Xlsx;

public sealed class XlsxAdapterTests
{
    [Fact]
    public void ExtractsSharedStringFormulaAndClassifiesWithoutExecuting()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePackage()));
        var sheet = Assert.Single(result.Worksheets);
        Assert.Equal("Sheet1", sheet.Name);
        Assert.Equal("Hello", sheet.Cells.Single(x => x.CellReference == "A1").Value);
        Assert.Equal("0", sheet.Cells.Single(x => x.CellReference == "B1").Value);
        var formula = Assert.Single(result.FormulaDiagnostics);
        Assert.Equal("B1", formula.CellReference);
        Assert.Equal(XlsxFormulaSafety.Safe, formula.Safety);
        Assert.Contains(result.Graph.Nodes, node => node.Source?.Locators.Any(x => x.Value == "A1") == true);
    }

    [Fact]
    public void SharedStringPhoneticRunsAreNotAppendedToVisibleCellText()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePackage(withPhoneticRun: true)));

        var value = Assert.Single(result.Worksheets).Cells.Single(cell => cell.CellReference == "A1").Value;
        Assert.Equal("抽出観点", value);
        Assert.Equal("抽出観点", result.SharedStrings["0"]);
        Assert.DoesNotContain("チュウシュツカンテン", value, StringComparison.Ordinal);
    }

    [Fact]
    public void ExposesUsedRangeCellCoordinatesAndMergeMetadataForTableProjection()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePackage()));
        var sheet = Assert.Single(result.Worksheets);

        Assert.Equal("A1:C1", sheet.UsedRange);
        Assert.Equal(1, sheet.RowCount);
        Assert.Equal(3, sheet.ColumnCount);
        var a1 = sheet.Cells.Single(cell => cell.CellReference == "A1");
        Assert.Equal(1, a1.RowIndex);
        Assert.Equal(1, a1.ColumnIndex);
        Assert.False(a1.IsBlank);
        Assert.Equal("s", a1.CellType);
        var node = result.Graph.Nodes.Single(node => node.Source?.Locators.Any(locator => locator.Value == "A1") == true);
        Assert.Equal(1, node.Extensions!["row"].GetInt32());
        Assert.Equal(1, node.Extensions["column"].GetInt32());
        Assert.False(node.Extensions["is_blank"].GetBoolean());
    }

    [Fact]
    public void MergedRangeExtendsSafeUsedRangeWithoutInventingCellNodes()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePackage(withMerge: true)));
        var sheet = Assert.Single(result.Worksheets);

        Assert.Equal("A1:C2", sheet.UsedRange);
        Assert.Equal(["A1:B2"], sheet.MergedRanges);
        Assert.DoesNotContain(sheet.Cells, cell => cell.CellReference == "B2");
    }

    [Fact]
    public void Resolves_display_number_formats_and_header_style_without_changing_raw_cell_values()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateFormattedPackage()));
        var sheet = Assert.Single(result.Worksheets);

        var date = sheet.Cells.Single(cell => cell.CellReference == "A2");
        var amount = sheet.Cells.Single(cell => cell.CellReference == "B2");
        var rate = sheet.Cells.Single(cell => cell.CellReference == "C2");
        Assert.Equal("46235", date.Value);
        Assert.Equal("2026-08-01", date.DisplayValue);
        Assert.Equal("12800", amount.Value);
        Assert.Equal("12,800 円", amount.DisplayValue);
        Assert.Equal("85.00%", rate.DisplayValue);
        Assert.True(sheet.Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.IsBold);
        Assert.True(sheet.Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.HasFill);
        Assert.True(sheet.Cells.Single(cell => cell.CellReference == "A1").DisplayStyle!.HasBorder);

        var node = result.Graph.Nodes.Single(node => node.Source?.Locators.Any(locator => locator.Value == "B2") == true);
        Assert.Equal("12,800 円", node.Extensions!["display_value"].GetString());
    }

    [Fact]
    public void ExtractsWorksheetReferencedByAbsoluteOpcTarget()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePackage(absoluteWorksheetTarget: true)));

        var sheet = Assert.Single(result.Worksheets);
        Assert.Equal("Hello", sheet.Cells.Single(cell => cell.CellReference == "A1").Value);
        Assert.Equal("xl/worksheets/sheet1.xml", sheet.PartUri);
    }

    [Fact]
    public void UnchangedRestoreIsByteIdenticalAndValuePatchKeepsOtherParts()
    {
        var original = CreatePackage();
        var adapter = new XlsxAdapter();
        var empty = adapter.CreatePatchPlan(Array.Empty<XlsxCellEdit>());
        Assert.Equal(original, adapter.Restore(new MemoryStream(original), empty).Bytes);

        var plan = adapter.CreatePatchPlan([new XlsxCellEdit("Sheet1", "A1", "Changed")]);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var before = Entries(original);
        var after = Entries(restored);
        Assert.NotEqual(Convert.ToBase64String(before["xl/worksheets/sheet1.xml"]), Convert.ToBase64String(after["xl/worksheets/sheet1.xml"]));
        Assert.Equal(before["xl/styles.xml"], after["xl/styles.xml"]);
        Assert.Equal(before["custom/unknown.bin"], after["custom/unknown.bin"]);
        Assert.Contains("xl/worksheets/sheet1.xml", plan.DirtyPartGraph.DirtyParts);
        Assert.DoesNotContain("xl/sharedStrings.xml", plan.DirtyPartGraph.DirtyParts);
    }

    [Fact]
    public void NumericCellPatchPreservesNumericCellType()
    {
        var adapter = new XlsxAdapter();
        var plan = adapter.CreatePatchPlan([new XlsxCellEdit("Sheet1", "C1", "54321")]);

        var restored = adapter.Restore(new MemoryStream(CreatePackage()), plan).Bytes;
        var worksheet = Encoding.UTF8.GetString(Entries(restored)["xl/worksheets/sheet1.xml"]);

        Assert.Contains("r=\"C1\" t=\"n\"", worksheet);
        Assert.Contains("<v>54321</v>", worksheet);
        Assert.DoesNotContain("r=\"C1\" t=\"inlineStr\"", worksheet);
        var workbook = Encoding.UTF8.GetString(Entries(restored)["xl/workbook.xml"]);
        Assert.Contains("calcMode=\"auto\"", workbook);
        Assert.Contains("fullCalcOnLoad=\"1\"", workbook);
        Assert.Contains("forceFullCalc=\"1\"", workbook);
        Assert.Contains("xl/workbook.xml", plan.DirtyPartGraph.DirtyParts);
    }

    [Fact]
    public void DangerousFormulaIsRejectedByDefault()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new XlsxAdapter().CreatePatchPlan([new XlsxCellEdit("Sheet1", "A1", Formula: "WEBSERVICE(\"https://example.invalid\")")]));
        Assert.Contains("Dangerous", exception.Message);
    }

    [Fact]
    public void Formula_is_projected_and_graph_edit_becomes_formula_patch_without_evaluation()
    {
        var adapter = new XlsxAdapter();
        var baseline = adapter.Extract(new MemoryStream(CreatePackage())).Graph;
        var markdown = new DocRedockMarkdownSerializer().Serialize(baseline).Markdown;
        Assert.Contains("`=SUM(A1:A1)` → 0", markdown);
        var edited = new MarkdownGraphEditor().Apply(baseline,
            markdown.Replace("=SUM(A1:A1)", "=SUM(A1:A2)", StringComparison.Ordinal));

        var plan = adapter.CreatePatchPlan(baseline, edited.EditedGraph);

        var change = Assert.Single(plan.Edits);
        Assert.Equal("SUM(A1:A2)", change.Formula);
        Assert.Contains("xl/calcChain.xml", plan.DirtyPartGraph.DirtyParts);
    }

    [Fact]
    public void MetadataAddressedTableCompletesXlsxToMarkdownEditToXlsxRoundTrip()
    {
        var original = CreatePackage();
        var adapter = new XlsxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var markdown = new DocRedockMarkdownSerializer().Serialize(extraction.Graph).Markdown;

        Assert.Contains("drmd:sheet-table range=A1:C1 source-columns=A,B,C source-rows=1", markdown);
        Assert.DoesNotContain("| Row | A | B | C |", markdown);
        var edit = new MarkdownGraphEditor().Apply(extraction.Graph, markdown.Replace("Hello", "こんにちは", StringComparison.Ordinal));
        var plan = adapter.CreatePatchPlan(extraction.Graph, edit.EditedGraph);
        var restored = adapter.Restore(new MemoryStream(original), plan).Bytes;
        var reexport = adapter.Extract(new MemoryStream(restored));

        Assert.True(edit.IsValid);
        Assert.Equal("こんにちは", Assert.Single(reexport.Worksheets).Cells.Single(cell => cell.CellReference == "A1").Value);
        Assert.Equal(Entries(original)["custom/unknown.bin"], Entries(restored)["custom/unknown.bin"]);
        Assert.Equal(Entries(original)["xl/styles.xml"], Entries(restored)["xl/styles.xml"]);
        var worksheet = Encoding.UTF8.GetString(Entries(restored)["xl/worksheets/sheet1.xml"]);
        Assert.Contains("r=\"A1\" s=\"3\"", worksheet);
        Assert.Contains("<col min=\"1\" max=\"3\" width=\"18.5\" customWidth=\"1\"", worksheet);
        Assert.Contains("<row r=\"1\" ht=\"24\" customHeight=\"1\"", worksheet);
        Assert.Contains("orientation=\"landscape\"", worksheet);
    }

    [Fact]
    public void Sequence_grid_is_projected_as_protected_mermaid_before_round_trip_table()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="4"><c r="B4" t="inlineStr"><is><t>利用者</t></is></c><c r="H4" t="inlineStr"><is><t>Web画面</t></is></c><c r="N4" t="inlineStr"><is><t>注文API</t></is></c></row>
                <row r="8"><c r="E8" t="inlineStr"><is><t>1. 注文詳細を開く ────────▶</t></is></c></row>
                <row r="11"><c r="K11" t="inlineStr"><is><t>2. 200 OK ◀────────</t></is></c></row>
              </sheetData>
              <mergeCells count="5"><mergeCell ref="B4:F6"/><mergeCell ref="H4:L6"/><mergeCell ref="N4:R6"/><mergeCell ref="E8:I9"/><mergeCell ref="K11:O12"/></mergeCells>
            </worksheet>
            """;
        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("シーケンス図", worksheet)));

        var diagram = Assert.Single(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Diagram);
        var source = Assert.IsType<DocRedock.Core.Documents.TextNodeContent>(diagram.Content).Text;
        Assert.Contains("sequenceDiagram", source);
        Assert.Contains("participant P1 as 利用者", source);
        Assert.Contains("P1->>P2: 1. 注文詳細を開く", source);
        Assert.Contains("P3-->>P2: 2. 200 OK", source);
        Assert.DoesNotContain("autonumber", source, StringComparison.Ordinal);
        Assert.Equal(DocRedock.Core.Documents.NodeEditability.Protected, diagram.Editability);

        var markdown = new DocRedockMarkdownSerializer().Serialize(extraction.Graph).Markdown;
        Assert.Contains("```mermaid\nsequenceDiagram", markdown);
        Assert.True(markdown.IndexOf("```mermaid", StringComparison.Ordinal) < markdown.IndexOf("drmd:sheet-table", StringComparison.Ordinal));
        var roundTrip = new MarkdownGraphEditor().Apply(extraction.Graph, markdown);
        Assert.True(roundTrip.IsValid, string.Join("\n", roundTrip.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Empty(roundTrip.Diff.PatchSet.Operations);
    }

    [Fact]
    public void Flow_grid_is_projected_as_mermaid_subgraphs_nodes_and_edges()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="4"><c r="A4" t="inlineStr"><is><t>利用者</t></is></c><c r="G4" t="inlineStr"><is><t>Web画面</t></is></c><c r="M4" t="inlineStr"><is><t>API / DB</t></is></c></row>
                <row r="6"><c r="B6" t="inlineStr"><is><t>開始&#10;注文詳細を確認</t></is></c></row>
                <row r="9"><c r="C9" t="inlineStr"><is><t>↓</t></is></c></row>
                <row r="11"><c r="B11" t="inlineStr"><is><t>注文を確定</t></is></c><c r="H11" t="inlineStr"><is><t>入力内容を検証</t></is></c></row>
                <row r="12"><c r="F12" t="inlineStr"><is><t>→</t></is></c></row>
              </sheetData>
              <mergeCells count="8"><mergeCell ref="A4:F4"/><mergeCell ref="G4:L4"/><mergeCell ref="M4:R4"/><mergeCell ref="B6:E8"/><mergeCell ref="C9:D10"/><mergeCell ref="B11:E13"/><mergeCell ref="F12:G12"/><mergeCell ref="H11:K13"/></mergeCells>
            </worksheet>
            """;
        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("業務フロー", worksheet)));

        var diagram = Assert.Single(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Diagram);
        var source = Assert.IsType<DocRedock.Core.Documents.TextNodeContent>(diagram.Content).Text;
        Assert.Contains("flowchart TD", source);
        Assert.Contains("subgraph L1[\"利用者\"]", source);
        Assert.Contains("N_B6([\"開始<br/>注文詳細を確認\"])", source);
        Assert.Contains("N_B6 --> N_B11", source);
        Assert.Contains("N_B11 --> N_H11", source);
    }

    [Fact]
    public void State_transition_table_is_reconstructed_as_a_state_diagram()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="2"><c r="A2" t="inlineStr"><is><t>1.1 状態遷移図</t></is></c></row>
                <row r="4"><c r="B4" t="inlineStr"><is><t>下書き&#10;DRAFT</t></is></c></row>
                <row r="4"><c r="H4" t="inlineStr"><is><t>申請済&#10;SUBMITTED</t></is></c></row>
                <row r="10"><c r="A10" t="inlineStr"><is><t>遷移ID</t></is></c><c r="C10" t="inlineStr"><is><t>遷移元</t></is></c><c r="E10" t="inlineStr"><is><t>イベント</t></is></c><c r="G10" t="inlineStr"><is><t>ガード／条件</t></is></c><c r="I10" t="inlineStr"><is><t>遷移先</t></is></c></row>
                <row r="11"><c r="A11" t="inlineStr"><is><t>TR-01</t></is></c><c r="C11" t="inlineStr"><is><t>—</t></is></c><c r="E11" t="inlineStr"><is><t>新規作成</t></is></c><c r="G11" t="inlineStr"><is><t>認証済み</t></is></c><c r="I11" t="inlineStr"><is><t>DRAFT</t></is></c></row>
                <row r="12"><c r="A12" t="inlineStr"><is><t>TR-02</t></is></c><c r="C12" t="inlineStr"><is><t>DRAFT</t></is></c><c r="E12" t="inlineStr"><is><t>提出</t></is></c><c r="G12" t="inlineStr"><is><t>入力OK</t></is></c><c r="I12" t="inlineStr"><is><t>SUBMITTED</t></is></c></row>
              </sheetData>
            </worksheet>
            """;
        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("状態遷移図", worksheet)));

        var diagram = Assert.Single(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Diagram);
        var source = Assert.IsType<DocRedock.Core.Documents.TextNodeContent>(diagram.Content).Text;

        Assert.Contains("stateDiagram-v2", source, StringComparison.Ordinal);
        Assert.Contains("state \"下書き<br/>DRAFT\" as S_DRAFT", source, StringComparison.Ordinal);
        Assert.Contains("[*] --> S_DRAFT: 新規作成<br/>[認証済み]", source, StringComparison.Ordinal);
        Assert.Contains("S_DRAFT --> S_SUBMITTED: 提出<br/>[入力OK]", source, StringComparison.Ordinal);
        Assert.Equal(4, diagram.Extensions!["diagram_min_row"].GetInt32());
    }

    [Fact]
    public void DrawingMl_shapes_are_exposed_with_one_based_worksheet_anchors()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheetData><row r="4"><c r="C4" t="inlineStr"><is><t>Service</t></is></c></row></sheetData>
              <drawing r:id="rDrawing" />
            </worksheet>
            """;
        var drawing = """
            <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <xdr:oneCellAnchor>
                <xdr:from><xdr:col>2</xdr:col><xdr:colOff>100</xdr:colOff><xdr:row>3</xdr:row><xdr:rowOff>200</xdr:rowOff></xdr:from>
                <xdr:ext cx="1000000" cy="500000" />
                <xdr:sp><xdr:nvSpPr><xdr:cNvPr id="7" name="service-shape" /></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="roundRect"><a:avLst /></a:prstGeom></xdr:spPr></xdr:sp>
                <xdr:clientData />
              </xdr:oneCellAnchor>
            </xdr:wsDr>
            """;
        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("Sheet1", worksheet, drawing)));

        var shape = Assert.Single(Assert.Single(extraction.Worksheets).DrawingShapes!);

        Assert.Equal("7", shape.Id);
        Assert.Equal("service-shape", shape.Name);
        Assert.Equal("roundRect", shape.Geometry);
        Assert.Equal(3, shape.Column);
        Assert.Equal(4, shape.Row);
        Assert.Equal(1_000_000, shape.WidthEmu);
        Assert.Equal(500_000, shape.HeightEmu);
    }

    [Fact]
    public void Sequence_projection_preserves_source_numbers_fragments_activations_and_branch_annotations()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheetData>
                <row r="4"><c r="B4" t="inlineStr"><is><t>利用者</t></is></c><c r="H4" t="inlineStr"><is><t>API</t></is></c><c r="N4" t="inlineStr"><is><t>承認者</t></is></c></row>
                <row r="8"><c r="B8" t="inlineStr"><is><t>break</t></is></c><c r="F8" t="inlineStr"><is><t>[validation NG]</t></is></c></row>
                <row r="10"><c r="E10" t="inlineStr"><is><t>4a. 400 Bad Request ───▶</t></is></c></row>
                <row r="15"><c r="B15" t="inlineStr"><is><t>alt</t></is></c></row>
                <row r="16"><c r="F16" t="inlineStr"><is><t>[amount &lt;= 100,000]</t></is></c><c r="N16" t="inlineStr"><is><t>課長承認&#10;TASK-MGR</t></is></c></row>
                <row r="18"><c r="F18" t="inlineStr"><is><t>[amount &gt; 100,000]</t></is></c><c r="N18" t="inlineStr"><is><t>部長承認&#10;TASK-DIR</t></is></c></row>
                <row r="22"><c r="K22" t="inlineStr"><is><t>4b. 承認結果 ◀───</t></is></c></row>
              </sheetData>
              <mergeCells count="3"><mergeCell ref="B4:F5"/><mergeCell ref="H4:L5"/><mergeCell ref="N4:R5"/></mergeCells>
              <drawing r:id="rDrawing" />
            </worksheet>
            """;
        var drawing = """
            <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>7</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="3086100" cy="1200150"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="20" name="break-frame"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="rect"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>14</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="3086100" cy="1333500"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="21" name="alt-frame"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="rect"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>7</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>8</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="152400" cy="571500"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="22" name="activation"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="rect"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="9525" cy="2500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="23" name="lifeline-1"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="line"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>7</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="9525" cy="2500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="24" name="lifeline-2"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="line"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
            </xdr:wsDr>
            """;

        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("処理仕様", worksheet, drawing)));
        var source = Assert.IsType<DocRedock.Core.Documents.TextNodeContent>(
            Assert.Single(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Diagram).Content).Text;

        Assert.DoesNotContain("autonumber", source, StringComparison.Ordinal);
        Assert.Contains("break validation NG", source, StringComparison.Ordinal);
        Assert.Contains("4a. 400 Bad Request", source, StringComparison.Ordinal);
        Assert.Contains("alt amount <= 100,000", source, StringComparison.Ordinal);
        Assert.Contains("else amount > 100,000", source, StringComparison.Ordinal);
        Assert.Contains("課長承認<br/>TASK-MGR", source, StringComparison.Ordinal);
        Assert.Contains("部長承認<br/>TASK-DIR", source, StringComparison.Ordinal);
        Assert.Contains("activate P2", source, StringComparison.Ordinal);
        Assert.Contains("deactivate P2", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_shapes_and_connected_connectors_project_without_sheet_name_or_domain_keywords()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheetData/><drawing r:id="rDrawing" />
            </worksheet>
            """;
        var drawing = """
            <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="900000" cy="500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="1" name="first"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="roundRect"/></xdr:spPr><xdr:txBody><a:p><a:r><a:t>受付</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>10</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="900000" cy="500000"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="2" name="second"/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst="rect"/></xdr:spPr><xdr:txBody><a:p><a:r><a:t>完了</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:oneCellAnchor>
              <xdr:oneCellAnchor><xdr:from><xdr:col>6</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>3</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="600000" cy="10000"/><xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id="3" name="connector"/><xdr:cNvCxnSpPr><a:stCxn id="1" idx="0"/><a:endCxn id="2" idx="0"/></xdr:cNvCxnSpPr></xdr:nvCxnSpPr><xdr:spPr><a:prstGeom prst="line"/></xdr:spPr></xdr:cxnSp><xdr:clientData/></xdr:oneCellAnchor>
            </xdr:wsDr>
            """;

        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("任意名", worksheet, drawing)));
        var shapes = Assert.Single(extraction.Worksheets).DrawingShapes!;
        var connector = Assert.Single(shapes, shape => shape.IsConnector);
        Assert.Equal("1", connector.StartConnectionId);
        Assert.Equal("2", connector.EndConnectionId);
        var source = Assert.IsType<DocRedock.Core.Documents.TextNodeContent>(
            Assert.Single(extraction.Graph.Nodes, node => node.Kind == DocRedock.Core.Documents.NodeKind.Diagram).Content).Text;
        Assert.Contains("受付", source, StringComparison.Ordinal);
        Assert.Contains("完了", source, StringComparison.Ordinal);
        Assert.Contains("N_S_1 --> N_S_2", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouped_drawing_shapes_are_flattened_with_text_and_parent_identity()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheetData/><drawing r:id="rDrawing" />
            </worksheet>
            """;
        var drawing = """
            <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <xdr:oneCellAnchor><xdr:from><xdr:col>2</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>3</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="2000000" cy="1000000"/>
                <xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id="10" name="group"/></xdr:nvGrpSpPr><xdr:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="2000000" cy="1000000"/><a:chOff x="0" y="0"/><a:chExt cx="2000000" cy="1000000"/></a:xfrm></xdr:grpSpPr>
                  <xdr:sp><xdr:nvSpPr><xdr:cNvPr id="11" name="child"/></xdr:nvSpPr><xdr:spPr><a:xfrm><a:off x="100000" y="200000"/><a:ext cx="800000" cy="400000"/></a:xfrm><a:prstGeom prst="rect"/></xdr:spPr><xdr:txBody><a:p><a:r><a:t>グループ内</a:t></a:r></a:p></xdr:txBody></xdr:sp>
                </xdr:grpSp><xdr:clientData/></xdr:oneCellAnchor>
              </xdr:wsDr>
            """;

        var extraction = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("図形", worksheet, drawing)));
        var child = Assert.Single(Assert.Single(extraction.Worksheets).DrawingShapes!);
        Assert.Equal("11", child.Id);
        Assert.Equal("10", child.ParentGroupId);
        Assert.Equal("グループ内", child.Text);
        Assert.Equal(800_000, child.WidthEmu);
        Assert.Equal(400_000, child.HeightEmu);
    }

    [Fact]
    public void DrawingMl_pictures_are_extracted_from_all_anchor_forms_and_external_links_are_skipped()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreatePicturePackage()));
        var sheet = Assert.Single(result.Worksheets);
        Assert.NotNull(sheet.Pictures);
        var pictures = sheet.Pictures!;
        Assert.Equal(4, pictures.Count);

        var twoCell = Assert.Single(pictures, picture => picture.Name == "two-cell");
        Assert.Equal(2, twoCell.Column);
        Assert.Equal(12, twoCell.Row);
        Assert.Equal(4, twoCell.ToColumn);
        Assert.Equal(15, twoCell.ToRow);
        Assert.Equal("/xl/media/image1.png", twoCell.TargetPartUri);
        Assert.Equal("description", twoCell.Description);
        Assert.Equal(111, twoCell.WidthEmu);
        Assert.Equal(222, twoCell.HeightEmu);

        var nodes = result.Graph.Partitions.Single().Nodes;
        var images = nodes.Where(node => node.Kind == DocRedock.Core.Documents.NodeKind.Image).ToArray();
        Assert.Equal(4, images.Length);
        Assert.All(images, image => Assert.Equal(DocRedock.Core.Documents.NodeEditability.Protected, image.Editability));
        var twoCellNode = Assert.Single(images, image => image.Extensions!["picture_name"].GetString() == "two-cell");
        Assert.Equal("B12", twoCellNode.Extensions!["address"].GetString());
        Assert.Equal(111, twoCellNode.Extensions["width_emu"].GetInt64());
        Assert.Equal(222, twoCellNode.Extensions["height_emu"].GetInt64());
        Assert.Equal("/xl/media/image1.png", ((DocRedock.Core.Documents.ReferenceNodeContent)twoCellNode.Content).Reference);
        var absolute = Assert.Single(images, image => image.Extensions!["picture_name"].GetString() == "absolute");
        Assert.False(absolute.Extensions!.ContainsKey("row"));
        Assert.False(absolute.Extensions.ContainsKey("column"));
        var grouped = Assert.Single(images, image => image.Extensions!["picture_name"].GetString() == "grouped");
        Assert.Equal("G6", grouped.Extensions!["address"].GetString());
        Assert.Contains(result.Warnings, warning => warning == "Sheet1: linked picture 'external' was skipped (external image).");
        Assert.Contains(result.Warnings, warning => warning.Contains("1 DrawingML shape(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("5 DrawingML shape(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_xlsx_chart_is_extracted_with_anchor_type_and_formula_resolved_series()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage()));
        var chart = Assert.Single(Assert.Single(result.Worksheets).Charts!);
        var node = Assert.Single(result.Graph.Nodes, item => item.Kind == DocRedock.Core.Documents.NodeKind.Chart);

        Assert.Equal("売上推移", chart.Title);
        Assert.Equal("line", chart.Type);
        Assert.Equal(2, chart.Column);
        Assert.Equal(5, chart.Row);
        Assert.Equal(["4月", "5月"], Assert.Single(chart.Series).Categories);
        Assert.Equal(["12", "18"], Assert.Single(chart.Series).Values);
        Assert.Equal("B5", node.Extensions!["address"].GetString());
        Assert.Equal("line", node.Extensions["chart_type"].GetString());
    }

    [Fact]
    public void Native_xlsx_chart_ignores_hidden_unrelated_sheet()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(hiddenUnrelatedSheet: true)));
        var node = Assert.Single(result.Graph.Nodes, item => item.Kind == DocRedock.Core.Documents.NodeKind.Chart);

        Assert.Equal(DocRedock.Core.Documents.ContentLayer.Body, node.Layer);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("marked hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void Xlsx_metadata_rows_are_not_merged_into_adjacent_tables()
    {
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
              <row r="1"><c r="A1" t="inlineStr"><is><t>更新日</t></is></c><c r="B1" t="inlineStr"><is><t>2026-08-27</t></is></c></row>
              <row r="2"><c r="A2" t="inlineStr"><is><t>リスク</t></is></c><c r="B2" t="inlineStr"><is><t>影響</t></is></c><c r="C2" t="inlineStr"><is><t>対策</t></is></c></row>
              <row r="3"><c r="A3" t="inlineStr"><is><t>納期</t></is></c><c r="B3" t="inlineStr"><is><t>中</t></is></c><c r="C3" t="inlineStr"><is><t>監視</t></is></c></row>
              <row r="4"><c r="A4" t="inlineStr"><is><t>状態</t></is></c><c r="B4" t="inlineStr"><is><t>Public Beta</t></is></c></row>
            </sheetData></worksheet>
            """;
        var graph = new XlsxAdapter().Extract(new MemoryStream(CreateDiagramPackage("Sheet1", worksheet))).Graph;
        var markdown = new DocRedockMarkdownSerializer().Serialize(graph).Markdown;

        Assert.Contains("| リスク | 影響 | 対策 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| 更新日 | 2026-08-27 | リスク |", markdown, StringComparison.Ordinal);
        Assert.Contains("状態", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_xlsx_chart_with_unparsed_reference_stays_hidden_when_workbook_has_hidden_sources()
    {
        const string cellSecret = "DOCREDOCK_SECRET_HIDDEN_CHART";
        const string cacheSecret = "DOCREDOCK_SECRET_HIDDEN_CHART_CACHE";
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(hideValueColumn: true, chartCacheOnly: true)));
        var node = Assert.Single(result.Graph.Nodes, item => item.Kind == DocRedock.Core.Documents.NodeKind.Chart);
        Assert.Equal(DocRedock.Core.Documents.ContentLayer.Hidden, node.Layer);
        Assert.Contains(result.Warnings, warning => warning.Contains("kept hidden", StringComparison.Ordinal));
        var visible = new DocRedock.Markdown.ReadableMarkdownSerializer(new DocRedock.Markdown.ReadableMarkdownOptions(ContentPolicy: "visible")).Serialize(result.Graph);
        var sanitized = new DocRedock.Markdown.ReadableMarkdownSerializer(new DocRedock.Markdown.ReadableMarkdownOptions(ContentPolicy: "sanitized")).Serialize(result.Graph);
        var complete = new DocRedock.Markdown.ReadableMarkdownSerializer(new DocRedock.Markdown.ReadableMarkdownOptions(ContentPolicy: "complete")).Serialize(result.Graph);
        Assert.DoesNotContain(cellSecret, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(cacheSecret, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(cellSecret, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(cacheSecret, sanitized, StringComparison.Ordinal);
        Assert.Contains(cellSecret, complete, StringComparison.Ordinal);
        Assert.Contains(cacheSecret, complete, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_xlsx_chart_retains_cached_data_when_unparsed_reference_has_no_hidden_sources()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(chartCacheOnly: true)));
        var node = Assert.Single(result.Graph.Nodes, item => item.Kind == DocRedock.Core.Documents.NodeKind.Chart);

        Assert.Equal(DocRedock.Core.Documents.ContentLayer.Body, node.Layer);
        Assert.Contains(result.Warnings, warning => warning.Contains("cached chart data was retained", StringComparison.Ordinal));
        Assert.Contains("DOCREDOCK_SECRET_HIDDEN_CHART_CACHE",
            new DocRedock.Markdown.ReadableMarkdownSerializer().Serialize(result.Graph), StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_bounds_hidden_column_range_is_ignored_without_iteration()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(oversizedHiddenColumn: true)));

        Assert.Empty(Assert.Single(result.Worksheets).HiddenColumns!);
    }

    [Fact]
    public void Repeated_hidden_column_ranges_have_a_bounded_processing_budget()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(repeatedHiddenColumns: true)));

        Assert.Equal(16_384, Assert.Single(result.Worksheets).HiddenColumns!.Count);
    }

    [Fact]
    public void Out_of_range_cell_reference_is_skipped()
    {
        const string secret = "DOCREDOCK_INVALID_CELL_SECRET";
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(invalidCellReference: true)));

        Assert.DoesNotContain(Assert.Single(result.Worksheets).Cells, cell => cell.Value?.Contains(secret, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Graph.Nodes, node => node.Content is DocRedock.Core.Documents.TextNodeContent text && text.Text.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Out_of_range_chart_cache_point_index_is_ignored_without_aborting_extraction()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(
            chartCacheOnly: true,
            chartPointIndexOverflow: true)));

        Assert.Empty(Assert.Single(Assert.Single(result.Worksheets).Charts!).Series[0].Values);
    }

    [Fact]
    public void Oversized_chart_formula_range_falls_back_to_bounded_cached_values()
    {
        var result = new XlsxAdapter().Extract(new MemoryStream(CreateChartPackage(oversizedChartRange: true)));

        Assert.Equal(["12", "18"], Assert.Single(Assert.Single(result.Worksheets).Charts!).Series[0].Values);
    }

    private static byte[] CreateChartPackage(
        bool hideValueColumn = false,
        bool oversizedHiddenColumn = false,
        bool oversizedChartRange = false,
        bool repeatedHiddenColumns = false,
        bool invalidCellReference = false,
        bool chartCacheOnly = false,
        bool chartPointIndexOverflow = false,
        bool hiddenUnrelatedSheet = false)
    {
        var hiddenColumns = hideValueColumn
            ? "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"
            : oversizedHiddenColumn
                ? "<cols><col min=\"1\" max=\"2147483647\" hidden=\"1\"/></cols>"
                : repeatedHiddenColumns
                    ? $"<cols>{string.Concat(Enumerable.Repeat("<col min=\"1\" max=\"16384\" hidden=\"1\"/>", 128))}</cols>"
                    : string.Empty;
        var secondValue = hideValueColumn ? "DOCREDOCK_SECRET_HIDDEN_CHART" : "18";
        var invalidCell = invalidCellReference
            ? "<c r=\"XFE1\" t=\"inlineStr\"><is><t>DOCREDOCK_INVALID_CELL_SECRET</t></is></c>"
            : string.Empty;
        var valueFormula = chartCacheOnly
            ? "MissingSheet!$B$1:$B$2"
            : oversizedChartRange
                ? "Sheet1!$A$1:$XFD$1048576"
                : "Sheet1!$B$1:$B$2";
        var cachedSecondValue = chartCacheOnly ? "DOCREDOCK_SECRET_HIDDEN_CHART_CACHE" : "18";
        var cacheFirstIndex = chartPointIndexOverflow ? "2147483647" : "0";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" />{(hiddenUnrelatedSheet ? "<sheet name=\"RawData\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\" />" : string.Empty)}</sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" />{(hiddenUnrelatedSheet ? "<Relationship Id=\"rId2\" Type=\"worksheet\" Target=\"worksheets/sheet2.xml\" />" : string.Empty)}</Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{hiddenColumns}<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>4月</t></is></c><c r=\"B1\" t=\"n\"><v>12</v></c>{invalidCell}</row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>5月</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>{secondValue}</t></is></c></row></sheetData><drawing r:id=\"rDrawing\" /></worksheet>",
            ["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"drawing\" Target=\"../drawings/drawing1.xml\" /></Relationships>",
            ["xl/drawings/_rels/drawing1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdChart\" Type=\"chart\" Target=\"../charts/chart1.xml\" /></Relationships>",
            ["xl/drawings/drawing1.xml"] = "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:row>4</xdr:row></xdr:from><xdr:ext cx=\"1\" cy=\"1\"/><xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"7\" name=\"Sales chart\"/></xdr:nvGraphicFramePr><a:graphic><a:graphicData><c:chart r:id=\"rIdChart\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>",
            ["xl/charts/chart1.xml"] = $"<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart><c:title><c:tx><c:rich><a:p><a:r><a:t>売上推移</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:lineChart><c:ser><c:tx><c:v>売上</c:v></c:tx><c:cat><c:strRef><c:f>Sheet1!$A$1:$A$2</c:f></c:strRef></c:cat><c:val><c:numRef><c:f>{valueFormula}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"2\"/><c:pt idx=\"{cacheFirstIndex}\"><c:v>12</c:v></c:pt><c:pt idx=\"1\"><c:v>{cachedSecondValue}</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser></c:lineChart></c:plotArea></c:chart></c:chartSpace>",
        };
        if (hiddenUnrelatedSheet)
            parts["xl/worksheets/sheet2.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>hidden raw data</t></is></c></row></sheetData></worksheet>";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }

    private static byte[] CreatePackage(bool absoluteWorksheetTarget = false, bool withMerge = false, bool withPhoneticRun = false)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"{(absoluteWorksheetTarget ? "/xl/worksheets/sheet1.xml" : "worksheets/sheet1.xml")}\" /></Relationships>",
            ["xl/sharedStrings.xml"] = withPhoneticRun
                ? "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>抽出観点</t><rPh sb=\"0\" eb=\"4\"><t>チュウシュツカンテン</t></rPh><phoneticPr fontId=\"0\" /></si></sst>"
                : "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Hello</t></si></sst>",
            ["xl/styles.xml"] = "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><name val=\"BIZ UDPGothic\" /><sz val=\"11\" /></font></fonts><cellXfs count=\"4\"><xf /><xf /><xf /><xf fontId=\"0\" applyFont=\"1\" /></cellXfs></styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cols><col min=\"1\" max=\"3\" width=\"18.5\" customWidth=\"1\" /></cols><sheetData><row r=\"1\" ht=\"24\" customHeight=\"1\"><c r=\"A1\" s=\"3\" t=\"s\"><v>0</v></c><c r=\"B1\"><f>SUM(A1:A1)</f><v>0</v></c><c r=\"C1\" t=\"n\"><v>12345</v></c></row></sheetData>{(withMerge ? "<mergeCells count=\"1\"><mergeCell ref=\"A1:B2\" /></mergeCells>" : string.Empty)}<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\" /><pageSetup orientation=\"landscape\" /></worksheet>",
            ["custom/unknown.bin"] = "untouched"
        };
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8); writer.Write(part.Value); }
        return output.ToArray();
    }

    private static byte[] CreateFormattedPackage()
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/styles.xml"] = """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="#\,##0 \&quot;円\&quot;" /></numFmts>
                  <fonts count="2"><font><sz val="11" /></font><font><b/><sz val="12" /></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none" /></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0000FF" /></patternFill></fill></fills>
                  <borders count="2"><border/><border><left style="thin"/><right style="thin"/><top style="thin"/><bottom style="thin"/></border></borders>
                  <cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="1" fillId="1" borderId="1"><alignment horizontal="center"/></xf><xf numFmtId="14" fontId="0" fillId="0" borderId="0"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0"/><xf numFmtId="10" fontId="0" fillId="0" borderId="0"/></cellXfs>
                </styleSheet>
                """,
            ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"1\" t=\"inlineStr\"><is><t>日付</t></is></c></row><row r=\"2\"><c r=\"A2\" s=\"2\" t=\"n\"><v>46235</v></c><c r=\"B2\" s=\"3\" t=\"n\"><v>12800</v></c><c r=\"C2\" s=\"4\" t=\"n\"><v>0.85</v></c></row></sheetData></worksheet>",
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

    private static byte[] CreateDiagramPackage(string sheetName, string worksheet, string? drawing = null)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{sheetName}\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>",
            ["xl/worksheets/sheet1.xml"] = worksheet,
        };
        if (drawing is not null)
        {
            parts["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>";
            parts["xl/drawings/drawing1.xml"] = drawing;
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8);
                writer.Write(part.Value);
            }
        return output.ToArray();
    }

    private static byte[] CreatePicturePackage()
    {
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"),
            ["xl/workbook.xml"] = Encoding.UTF8.GetBytes("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>"),
            ["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>"),
            ["xl/worksheets/sheet1.xml"] = Encoding.UTF8.GetBytes("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Cell</t></is></c></row></sheetData><drawing r:id=\"rDrawing\" /></worksheet>"),
            ["xl/worksheets/_rels/sheet1.xml.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rDrawing\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"/xl/drawings/drawing1.xml\" /></Relationships>"),
            ["xl/drawings/_rels/drawing1.xml.rels"] = Encoding.UTF8.GetBytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image1.png\" /><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image2.png\" /><Relationship Id=\"rExternal\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"https://example.invalid/image.png\" TargetMode=\"External\" /></Relationships>"),
            ["xl/drawings/drawing1.xml"] = Encoding.UTF8.GetBytes("""
                <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>11</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>14</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="5" name="two-cell" descr="description" /></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId1" /></xdr:blipFill><xdr:spPr><a:xfrm><a:ext cx="111" cy="222" /></a:xfrm></xdr:spPr></xdr:pic><xdr:clientData /></xdr:twoCellAnchor>
                  <xdr:oneCellAnchor><xdr:from><xdr:col>4</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="333" cy="444" /><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="6" name="one-cell" title="title fallback" /></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId2" /></xdr:blipFill><xdr:spPr /></xdr:pic><xdr:clientData /></xdr:oneCellAnchor>
                  <xdr:absoluteAnchor><xdr:pos x="0" y="0" /><xdr:ext cx="555" cy="666" /><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="7" name="absolute" /></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId1" /></xdr:blipFill><xdr:spPr /></xdr:pic><xdr:clientData /></xdr:absoluteAnchor>
                  <xdr:oneCellAnchor><xdr:from><xdr:col>6</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>5</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="777" cy="888" /><xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id="10" name="group" /></xdr:nvGrpSpPr><xdr:grpSpPr /><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="8" name="grouped" /></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="rId2" /></xdr:blipFill><xdr:spPr /></xdr:pic></xdr:grpSp><xdr:clientData /></xdr:oneCellAnchor>
                  <xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:row>0</xdr:row></xdr:from><xdr:ext cx="1" cy="1" /><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="9" name="external" /></xdr:nvPicPr><xdr:blipFill><a:blip r:link="rExternal" /></xdr:blipFill><xdr:spPr /></xdr:pic><xdr:clientData /></xdr:oneCellAnchor>
                  <xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:row>0</xdr:row></xdr:from><xdr:ext cx="1" cy="1" /><xdr:sp><xdr:nvSpPr><xdr:cNvPr id="20" name="shape" /></xdr:nvSpPr><xdr:spPr /></xdr:sp><xdr:clientData /></xdr:oneCellAnchor>
                </xdr:wsDr>
                """)
        };
        parts["xl/media/image1.png"] = [137, 80, 78, 71, 13, 10, 26, 10];
        parts["xl/media/image2.png"] = [137, 80, 78, 71, 13, 10, 26, 10];
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var entry = zip.CreateEntry(part.Key).Open();
                entry.Write(part.Value);
            }
        return output.ToArray();
    }
    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var zip = new ZipArchive(input); var result = new Dictionary<string, byte[]>(); foreach (var entry in zip.Entries) using (var source = entry.Open()) using (var output = new MemoryStream()) { source.CopyTo(output); result[entry.FullName] = output.ToArray(); }
        return result;
    }
}

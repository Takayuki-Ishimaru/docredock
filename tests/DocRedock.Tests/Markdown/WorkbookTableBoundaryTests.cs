using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

/// <summary>
/// A single blank worksheet column can be a spacer inside one table or the border between two.
/// Each case is checked in both directions: rows that belong together stay in one table row, and
/// independent content never shares a table row or a Markdown block with it.
/// </summary>
public sealed class WorkbookTableBoundaryTests
{
    [Fact]
    public void Numeric_ID_table_stays_one_table_across_a_spacer_column()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "ID", bold: true), C("B1", "Item", bold: true), C("D1", "Quantity", bold: true), C("E1", "Amount", bold: true),
            N("A2", 1), C("B2", "Apple"), N("D2", 2), N("E2", 100),
            N("A3", 2), C("B3", "Pear"), N("D3", 3), N("E3", 200));
        Assert.True(SameTableRow(markdown, "1", "Apple", "2", "100"), markdown);
        Assert.True(SameTableRow(markdown, "2", "Pear", "3", "200"), markdown);
        Assert.Equal(1, serializer.RenderedTables);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Text_label_table_stays_one_table_across_a_spacer_column()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Label", bold: true), C("B1", "Name", bold: true), C("D1", "Quantity", bold: true), C("E1", "Amount", bold: true),
            C("A2", "Apple"), C("B2", "Fruit A"), N("D2", 2), N("E2", 100),
            C("A3", "Pear"), C("B3", "Fruit B"), N("D3", 3), N("E3", 200));
        Assert.True(SameTableRow(markdown, "Apple", "Fruit A", "2", "100"), markdown);
        Assert.True(SameTableRow(markdown, "Pear", "Fruit B", "3", "200"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Single_column_row_labels_stay_with_the_numeric_columns_they_label()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("C1", "Q1", bold: true), C("D1", "Q2", bold: true),
            C("A2", "Revenue"), N("C2", 100), N("D2", 120),
            C("A3", "Cost"), N("C3", 80), N("D3", 90));
        Assert.True(SameTableRow(markdown, "Revenue", "100", "120"), markdown);
        Assert.True(SameTableRow(markdown, "Cost", "80", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_beside_a_labelled_table_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Total", bold: true),
            C("A2", "Revenue"), N("B2", 100), N("D2", 130),
            C("A3", "Cost"), N("B3", 80), N("D3", 95));
        Assert.True(SameTableRow(markdown, "Revenue", "100", "130"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData("1,234円", "980円")]
    [InlineData("28.8%", "31.0％")]
    [InlineData("—", "○")]
    [InlineData("2026-08-01", "2026/9/1")]
    [InlineData("(120)", "▲40")]
    public void Values_written_as_text_count_as_values(string first, string second)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Value", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", first),
            C("A3", "Cost"), N("B3", 80), C("D3", second));
        Assert.True(SameTableRow(markdown, "Revenue", "100", first), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_remarks_column_after_a_spacer_annotates_its_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "備考", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "前年比で増加"),
            C("A3", "Cost"), N("B3", 80), C("D3", "横ばい"));
        Assert.True(SameTableRow(markdown, "Revenue", "100", "前年比で増加"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Independent_one_column_list_is_separated_kept_as_a_list_and_marked_for_comparison()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"));
        AssertIndependent(markdown, ["Revenue", "Cost"], ["CANDIDATE_ONE", "CANDIDATE_TWO"]);
        Assert.True(SameTableRow(markdown, "Revenue", "100"), markdown);
        Assert.Contains("| Candidates |\n| --- |\n| CANDIDATE\\_ONE |\n| CANDIDATE\\_TWO |", markdown, StringComparison.Ordinal);
        var review = Assert.Single(serializer.Report.TableBoundaryReviews);
        Assert.Equal(("Data", "C1:C3", "A1:B3", "D1:D3"), (review.SheetName, review.GapRange, review.LeftRange, review.RightRange));
        var diagnostic = Assert.Single(serializer.Diagnostics, item => item.Code == "XlsxTableBoundaryAmbiguous");
        Assert.Equal(MarkdownDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("A1:B3", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("D1:D3", diagnostic.Message, StringComparison.Ordinal);
        // The note sits right before the separated list, where a reader of the Markdown sees it.
        Assert.True(markdown.IndexOf("<!-- inferred: C列の空白", StringComparison.Ordinal) is var note and > 0 &&
            note < markdown.IndexOf("| Candidates |", StringComparison.Ordinal) &&
            note > markdown.IndexOf("| Cost | 80 |", StringComparison.Ordinal), markdown);
    }

    [Fact]
    public void The_boundary_review_names_the_sheet_as_it_is_on_its_tab()
    {
        var serializer = new ReadableMarkdownSerializer();
        var nodes = new[]
        {
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"),
        };
        var markdown = serializer.Serialize(new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "names", DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-Q_1 #<b>", 0, nodes)]));
        Assert.Equal("Q_1 #<b>", Assert.Single(serializer.Report.TableBoundaryReviews).SheetName);
        Assert.Contains("Sheet 'Q_1 #<b>'", Assert.Single(serializer.Diagnostics, item => item.Code == "XlsxTableBoundaryAmbiguous").Message, StringComparison.Ordinal);
        // The section heading keeps its readable form; only the review names the tab exactly.
        Assert.Contains("## Q 1 \\#&lt;b&gt;", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Independent_two_column_table_is_separated_without_a_document_information_heading()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true), C("E1", "Value", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"), N("E2", 1),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"), N("E3", 2));
        AssertIndependent(markdown, ["Revenue", "Cost"], ["CANDIDATE_ONE", "CANDIDATE_TWO"]);
        Assert.True(SameTableRow(markdown, "CANDIDATE\\_ONE", "1"), markdown);
        Assert.DoesNotContain("文書情報", markdown, StringComparison.Ordinal);
        Assert.Single(serializer.Report.TableBoundaryReviews);
        Assert.Equal(1, serializer.Report.SideBySideRegions);
    }

    [Fact]
    public void A_list_whose_header_sits_above_the_table_is_independent_without_review()
    {
        var (markdown, serializer) = Serialize(
            C("D1", "Candidates", bold: true), C("E1", "Value", bold: true),
            C("A2", "Item", bold: true), C("B2", "Q1", bold: true), C("D2", "CANDIDATE_ONE"), N("E2", 1),
            C("A3", "Revenue"), N("B3", 100), C("D3", "CANDIDATE_TWO"), N("E3", 2),
            C("A4", "Cost"), N("B4", 80),
            C("A5", "Total"), N("B5", 180));
        AssertIndependent(markdown, ["Revenue", "Cost", "Total"], ["CANDIDATE_ONE", "CANDIDATE_TWO"]);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_shorter_list_beside_a_table_is_independent_without_review()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Code", bold: true), C("E1", "Name", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "K01"), C("E2", "Alpha"),
            C("A3", "Cost"), N("B3", 80), C("D3", "K02"), C("E3", "Beta"),
            C("A4", "Tax"), N("B4", 20),
            C("A5", "Total"), N("B5", 200));
        AssertIndependent(markdown, ["Revenue", "Cost", "Tax", "Total"], ["Alpha", "Beta"]);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void An_Excel_table_range_separates_a_list_beside_it_without_review()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true, table: "Sales"), C("B1", "Q1", bold: true, table: "Sales"), C("D1", "Candidates", bold: true),
            C("A2", "Revenue", table: "Sales"), N("B2", 100, table: "Sales"), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost", table: "Sales"), N("B3", 80, table: "Sales"), C("D3", "CANDIDATE_TWO"));
        AssertIndependent(markdown, ["Revenue", "Cost"], ["CANDIDATE_ONE", "CANDIDATE_TWO"]);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Two_declared_tables_never_merge_even_when_one_holds_only_values()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true, table: "Sales"), C("B1", "Q1", bold: true, table: "Sales"),
            C("D1", "Month", bold: true, table: "Targets"), C("E1", "Target", bold: true, table: "Targets"),
            C("A2", "Revenue", table: "Sales"), N("B2", 100, table: "Sales"), N("D2", 4, table: "Targets"), N("E2", 120, table: "Targets"),
            C("A3", "Cost", table: "Sales"), N("B3", 80, table: "Sales"), N("D3", 5, table: "Targets"), N("E3", 90, table: "Targets"));
        AssertIndependent(markdown, ["Revenue", "Cost"], ["Month", "Target"]);
        Assert.True(SameTableRow(markdown, "4", "120"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void One_declared_table_keeps_its_columns_together()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true, table: "Sales"), C("B1", "Q1", bold: true, table: "Sales"), C("D1", "Owner", bold: true, table: "Sales"),
            C("A2", "Revenue", table: "Sales"), N("B2", 100, table: "Sales"), C("D2", "Sales team", table: "Sales"),
            C("A3", "Cost", table: "Sales"), N("B3", 80, table: "Sales"), C("D3", "Finance", table: "Sales"));
        Assert.True(SameTableRow(markdown, "Revenue", "100", "Sales team"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_row_of_group_titles_does_not_join_the_lists_beneath_it()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Category tags", bold: true, toColumn: 5), C("G1", "Content tags", bold: true, toColumn: 8),
            C("A2", "Residents", bold: true, toColumn: 2), C("D2", "Business", bold: true, toColumn: 5), C("G2", "Content", bold: true, toColumn: 8),
            C("A3", "T001"), C("B3", "Childcare"), C("D3", "T101"), C("E3", "Licensing"), C("G3", "T201"), C("H3", "Forms"),
            C("A4", "T002"), C("B4", "Schools"), C("D4", "T102"), C("E4", "Taxes"), C("G4", "T202"), C("H4", "Events"),
            C("A5", "T003"), C("B5", "Housing"),
            C("A6", "T004"), C("B6", "Transport"));
        AssertIndependent(markdown, ["Childcare", "Schools", "Housing", "Transport"], ["Licensing", "Taxes"]);
        AssertIndependent(markdown, ["Licensing", "Taxes"], ["Forms", "Events"]);
        Assert.True(SameTableRow(markdown, "T101", "Licensing"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Lists_under_their_own_titles_are_separated_without_review()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Plan", bold: true, toColumn: 2), C("D1", "Categories", bold: true, toColumn: 5),
            C("A2", "Year", bold: true), N("B2", 2026), C("D2", "Category", bold: true), C("E2", "Theme", bold: true),
            C("A3", "Currency", bold: true), C("B3", "JPY"), C("D3", "Product"), C("E3", "Recurring revenue"),
            C("A4", "Margin", bold: true), C("B4", "42%"), C("D4", "Marketing"), C("E4", "Acquisition"));
        AssertIndependent(markdown, ["JPY", "42%"], ["Product", "Marketing"]);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_header_cell_never_becomes_the_workbook_title(bool withTitle)
    {
        var cells = new List<DocumentNode>
        {
            C("A2", "ID", bold: true), C("B2", "Item", bold: true), N("A3", 1), C("B3", "Apple"), N("A4", 2), C("B4", "Pear")
        };
        if (withTitle) cells.Add(C("A1", "Fruit inventory", bold: true, toColumn: 2));
        var markdown = Serialize(cells.Select(cell => withTitle ? cell : Move(cell, -1)).ToArray()).Markdown;
        Assert.StartsWith(withTitle ? "# Fruit inventory\n" : "# ドキュメント\n", markdown, StringComparison.Ordinal);
        Assert.True(SameTableRow(markdown, "1", "Apple"), markdown);
    }

    [Fact]
    public void A_title_followed_by_a_distant_note_still_titles_the_workbook()
    {
        var markdown = Serialize(C("A1", "Expense system design"), C("H1", "Updated"), C("I1", "2026-08-23"),
            C("A3", "No", bold: true), C("B3", "Topic", bold: true), N("A4", 1), C("B4", "Scope"), N("A5", 2), C("B5", "Terms")).Markdown;
        Assert.StartsWith("# Expense system design\n", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_caption_above_a_table_is_not_its_first_column_header(bool bold)
    {
        var markdown = Serialize(C("A1", "Annual report", bold: true), C("A3", "Sales by quarter (thousands)", bold: bold),
            C("A4", "Item", bold: true), C("B4", "Q1", bold: true), C("C4", "Q2", bold: true),
            C("A5", "Revenue"), N("B5", 100), N("C5", 120), C("A6", "Cost"), N("B6", 80), N("C6", 90)).Markdown;
        Assert.Contains("\n\nSales by quarter (thousands)\n\n| Item | Q1 | Q2 |\n| --- | --- | --- |\n| Revenue | 100 | 120 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Sales by quarter", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_information_heading_marks_label_value_blocks_but_not_tables()
    {
        var information = Serialize(
            C("A1", "文書番号", bold: true), C("B1", "EXPS-DES-001"), C("C1", "版", bold: true), C("D1", "1.2"),
            C("A2", "作成日", bold: true), C("B2", "2026-08-23"), C("C2", "状態", bold: true), C("D2", "レビュー中"),
            C("A3", "作成者", bold: true), C("B3", "設計担当"), C("C3", "確認者", bold: true), C("D3", "基盤担当")).Markdown;
        Assert.Contains("### 文書情報", information, StringComparison.Ordinal);
        var table = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("A3", "Cost"), N("B3", 80)).Markdown;
        Assert.DoesNotContain("文書情報", table, StringComparison.Ordinal);
        Assert.True(SameTableRow(table, "Revenue", "100"), table);
    }

    [Fact]
    public void Recording_the_workbook_layout_never_changes_the_Markdown()
    {
        var nodes = new[]
        {
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"),
        };
        var plain = Serialize(nodes).Markdown;
        var recording = new ReadableMarkdownSerializer { RecordWorkbookLayout = true };
        var recorded = recording.Serialize(Graph(nodes));
        Assert.Equal(plain, recorded);
        var sheet = Assert.Single(recording.WorkbookLayout!.Sheets);
        Assert.Equal("## Data", recorded[sheet.Start..sheet.ContentStart].Trim());
        var tables = sheet.Segments.Where(segment => segment.Table is not null).ToArray();
        Assert.Equal(2, tables.Length);
        var revenue = tables[0].Table!;
        Assert.Equal("| Item | Q1 |\n| --- | --- |\n", recorded[tables[0].Start..revenue.HeaderEnd]);
        Assert.Equal(["| Revenue | 100 |\n", "| Cost | 80 |\n"], revenue.Rows.Select(row => recorded[row.Start..row.End]));
        Assert.Equal([2, 3], revenue.Rows.Select(row => row.Row));
        Assert.Equal((1, 2), (revenue.MinColumn, revenue.MaxColumn));
        Assert.Equal(["cell-A1", "cell-B1"], revenue.HeaderNodeIds);
        Assert.Equal(["cell-A2", "cell-B2"], revenue.Rows[0].NodeIds);
        Assert.Equal(nodes.Select(node => node.Id).Order(StringComparer.Ordinal),
            sheet.Segments.SelectMany(segment => segment.NodeIds).Distinct().Order(StringComparer.Ordinal));
        Assert.Null(new ReadableMarkdownSerializer().WorkbookLayout);
    }

    // Review follow-ups: each case is a layout an independent review traced through the rules.

    [Fact]
    public void Row_labels_stay_with_numbers_followed_by_a_remarks_column()
    {
        var (single, singleSerializer) = Serialize(
            C("A1", "項目", bold: true), C("C1", "4月", bold: true), C("D1", "5月", bold: true), C("E1", "備考", bold: true),
            C("A2", "売上"), N("C2", 100), N("D2", 120), C("E2", "前年比増"),
            C("A3", "費用"), N("C3", 80), N("D3", 90), C("E3", "横ばい"));
        Assert.True(SameTableRow(single, "売上", "100", "120", "前年比増"), single);
        AssertNoBoundaryReview(singleSerializer);
        var (bilingual, bilingualSerializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "Item", bold: true), C("D1", "2023", bold: true), C("E1", "2024", bold: true), C("F1", "備考", bold: true),
            C("A2", "売上"), C("B2", "Sales"), N("D2", 100), N("E2", 120), C("F2", "増加"),
            C("A3", "費用"), C("B3", "Cost"), N("D3", 80), N("E3", 90), C("F3", "横ばい"));
        Assert.True(SameTableRow(bilingual, "売上", "Sales", "100", "120", "増加"), bilingual);
        AssertNoBoundaryReview(bilingualSerializer);
    }

    [Fact]
    public void A_long_numeric_table_beside_a_short_list_is_not_joined_to_it()
    {
        var nodes = new List<DocumentNode> { C("A1", "Month", bold: true), C("B1", "Sales", bold: true), C("D1", "Owner", bold: true), C("E1", "Phone", bold: true) };
        for (var month = 1; month <= 12; month++) { nodes.Add(N($"A{month + 1}", month)); nodes.Add(N($"B{month + 1}", month * 100)); }
        nodes.AddRange([C("D2", "Sato"), C("E2", "03-0000-0001"), C("D3", "Kato"), C("E3", "03-0000-0002")]);
        var (markdown, serializer) = Serialize(nodes.ToArray());
        AssertIndependent(markdown, ["Sato", "Kato"], ["100 ", "200 "]);
        Assert.False(SameTableRow(markdown, "1", "100", "Sato"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_list_above_a_numeric_block_in_the_same_columns_is_not_taken_for_values()
    {
        var (markdown, _) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"),
            C("A4", "Tax"), N("B4", 20), C("D4", "CANDIDATE_THREE"),
            C("D6", "Year", bold: true), C("E6", "Target", bold: true), N("D7", 2025), N("E7", 120), N("D8", 2026), N("E8", 130));
        Assert.False(SameTableRow(markdown, "Revenue", "100", "CANDIDATE\\_ONE"), markdown);
        AssertIndependent(markdown, ["Revenue", "Cost", "Tax"], ["CANDIDATE_ONE", "CANDIDATE_TWO", "CANDIDATE_THREE"]);
    }

    [Theory]
    [InlineData("—")]
    [InlineData("-")]
    public void A_status_list_ending_in_a_dash_is_not_taken_for_values(string dash)
    {
        var (markdown, _) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Status", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "Open"),
            C("A3", "Cost"), N("B3", 80), C("D3", dash));
        Assert.False(SameTableRow(markdown, "Revenue", "100", "Open"), markdown);
    }

    [Theory]
    [InlineData("未定")]
    [InlineData("2.5人月")]
    [InlineData("1.5倍")]
    public void An_occasional_text_value_does_not_split_a_values_column_from_its_rows(string text)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "ID", bold: true), C("B1", "Item", bold: true), C("D1", "Quantity", bold: true), C("E1", "Amount", bold: true),
            N("A2", 1), C("B2", "Apple"), N("D2", 2), C("E2", text),
            N("A3", 2), C("B3", "Pear"), N("D3", 3), N("E3", 200),
            N("A4", 3), C("B4", "Plum"), N("D4", 4), N("E4", 300));
        Assert.True(SameTableRow(markdown, "1", "Apple", "2", text), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_list_item_beside_a_blank_table_cell_stays_in_the_list()
    {
        var (markdown, _) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "Red"),
            C("A3", "Pear"), C("D3", "Green"),
            C("A4", "Cost"), N("B4", 80), C("D4", "Blue"));
        Assert.Contains("| Candidates |\n| --- |\n| Red |\n| Green |\n| Blue |", markdown, StringComparison.Ordinal);
        AssertIndependent(markdown, ["Revenue", "Pear", "Cost"], ["Red", "Green", "Blue"]);
    }

    [Fact]
    public void Grid_paper_tables_made_only_of_merged_cells_stay_separate()
    {
        var (markdown, _) = Serialize(
            C("B1", "氏名", bold: true, toColumn: 4), C("E1", "部署", bold: true, toColumn: 8),
            C("K1", "品目", bold: true, toColumn: 14), C("O1", "数量", bold: true, toColumn: 18),
            C("B2", "山田", toColumn: 4), C("E2", "営業", toColumn: 8), C("K2", "りんご", toColumn: 14), C("O2", "5", toColumn: 18),
            C("B3", "佐藤", toColumn: 4), C("E3", "経理", toColumn: 8), C("K3", "みかん", toColumn: 14), C("O3", "7", toColumn: 18));
        AssertIndependent(markdown, ["山田", "佐藤", "営業"], ["りんご", "みかん"]);
        Assert.True(SameTableRow(markdown, "山田", "営業"), markdown);
        Assert.True(SameTableRow(markdown, "りんご", "5"), markdown);
    }

    [Fact]
    public void A_title_beside_a_document_number_still_titles_the_workbook()
    {
        var markdown = Serialize(C("A1", "見積書"), C("C1", "No.123"),
            C("A3", "品目", bold: true), C("B3", "金額", bold: true), C("A4", "設計"), N("B4", 100), C("A5", "試験"), N("B5", 50)).Markdown;
        Assert.StartsWith("# 見積書\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Boundary_review_ranges_cover_the_tables_not_the_whole_section()
    {
        var nodes = new List<DocumentNode>
        {
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Candidates", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "CANDIDATE_ONE"),
            C("A3", "Cost"), N("B3", 80), C("D3", "CANDIDATE_TWO"),
        };
        for (var row = 10; row <= 40; row += 10) nodes.Add(C($"A{row}", $"Note {row}"));
        var (_, serializer) = Serialize(nodes.ToArray());
        var review = Assert.Single(serializer.Report.TableBoundaryReviews);
        Assert.Equal(("C1:C3", "A1:B3", "D1:D3"), (review.GapRange, review.LeftRange, review.RightRange));
    }

    // Adjacent tables: no blank column between them, only their own titles and headers.

    // The "前提・分類" sheet of the business-plan workbook: three tables, each under its own merged
    // title in row 3. A:B and D:E are separated by a blank column; D:E and F:G touch.
    private static DocumentNode[] PlanAssumptionsSheet(string? categoryTable = null) =>
    [
        C("A1", "前提・分類 | FY2026 事業計画", bold: true, fill: true, toColumn: 7),
        C("A3", "計画前提", bold: true, fill: true, border: true, toColumn: 2), C("D3", "分類マスタ", bold: true, fill: true, border: true, toColumn: 5),
        C("F3", "読み方", bold: true, fill: true, border: true, toColumn: 7),
        C("A4", "計画年度", bold: true, fill: true, border: true), C("B4", "2026", numeric: true, fill: true, border: true), C("D4", "カテゴリ", bold: true, fill: true, table: categoryTable), C("E4", "重点テーマ", bold: true, fill: true, table: categoryTable),
        C("F4", "入力セル", bold: true, fill: true, border: true), C("G4", "淡い黄: 編集可能な前提・実績", border: true),
        C("A5", "基準通貨", bold: true, fill: true, border: true), C("B5", "JPY", fill: true, border: true), C("D5", "プロダクト", border: true, table: categoryTable), C("E5", "継続収益", border: true, table: categoryTable),
        C("F5", "計算セル", bold: true, fill: true, border: true), C("G5", "淡い青: 数式で導出", border: true),
        C("A6", "粗利率目標", bold: true, fill: true, border: true), C("B6", "42.0%", numeric: true, fill: true, border: true), C("D6", "マーケティング", border: true, table: categoryTable), C("E6", "獲得効率", border: true, table: categoryTable),
        C("F6", "ステータス", bold: true, fill: true, border: true), C("G6", "条件付き書式で進捗と注意点を表示", border: true),
        C("A7", "投資上限", bold: true, fill: true, border: true), C("B7", "¥50,000,000", numeric: true, fill: true, border: true), C("D7", "オペレーション", border: true, table: categoryTable), C("E7", "品質・自動化", border: true, table: categoryTable),
        C("F7", "出典", bold: true, fill: true, border: true), C("G7", "https://example.com/plan", border: true),
        C("A8", "更新日", bold: true, fill: true, border: true), C("B8", "2026-08-26", numeric: true, fill: true, border: true), C("D8", "人材", border: true, table: categoryTable), C("E8", "組織能力", border: true, table: categoryTable),
    ];

    // In the workbook the first sheet supplies the document title, so the banner of this sheet stays
    // in its rows; it must not draw the rows of the tables below into itself.
    private static string SerializeWithTitle(DocumentNode[] nodes, out ReadableMarkdownSerializer serializer)
    {
        serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(Title: "FY2026 事業計画"));
        return serializer.Serialize(Graph(nodes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CategoryMaster")]
    public void Each_title_of_a_row_of_merged_titles_stays_with_its_own_table(string? categoryTable)
    {
        var markdown = SerializeWithTitle(PlanAssumptionsSheet(categoryTable), out _);
        AssertIndependent(markdown, ["計画前提", "計画年度", "JPY"], ["分類マスタ", "カテゴリ", "プロダクト"]);
        AssertIndependent(markdown, ["計画前提", "計画年度", "JPY"], ["読み方", "入力セル", "淡い黄"]);
        // Each title comes right before its own rows.
        Assert.True(markdown.IndexOf("計画前提", StringComparison.Ordinal) < markdown.IndexOf("計画年度", StringComparison.Ordinal), markdown);
        Assert.True(markdown.IndexOf("分類マスタ", StringComparison.Ordinal) < markdown.IndexOf("カテゴリ", StringComparison.Ordinal), markdown);
        Assert.True(markdown.IndexOf("読み方", StringComparison.Ordinal) < markdown.IndexOf("入力セル", StringComparison.Ordinal), markdown);
        Assert.True(markdown.IndexOf("計画年度", StringComparison.Ordinal) < markdown.IndexOf("分類マスタ", StringComparison.Ordinal), markdown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CategoryMaster")]
    public void Adjacent_tables_under_their_own_titles_are_separated(string? categoryTable)
    {
        var markdown = SerializeWithTitle(PlanAssumptionsSheet(categoryTable), out var serializer);
        AssertIndependent(markdown, ["分類マスタ", "カテゴリ", "プロダクト", "人材"], ["読み方", "入力セル", "計算セル", "淡い黄"]);
        Assert.True(SameTableRow(markdown, "プロダクト", "継続収益"), markdown);
        Assert.True(markdown.Contains("計算セル", StringComparison.Ordinal) && markdown.Contains("淡い青: 数式で導出", StringComparison.Ordinal), markdown);
        Assert.True(markdown.IndexOf("読み方", StringComparison.Ordinal) > markdown.IndexOf("人材", StringComparison.Ordinal), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Group_titles_over_the_values_of_one_table_keep_it_together()
    {
        var (markdown, serializer) = Serialize(
            C("B1", "2024年", bold: true, fill: true, toColumn: 3), C("D1", "2025年", bold: true, fill: true, toColumn: 5),
            C("A2", "項目", bold: true, fill: true), C("B2", "上期", bold: true), C("C2", "下期", bold: true), C("D2", "上期", bold: true), C("E2", "下期", bold: true),
            C("A3", "売上"), N("B3", 10), N("C3", 20), N("D3", 30), N("E3", 40),
            C("A4", "費用"), N("B4", 5), N("C4", 6), N("D4", 7), N("E4", 8));
        Assert.True(SameTableRow(markdown, "売上", "10", "20", "30", "40"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Group_titles_over_text_columns_of_the_same_rows_keep_them_together(bool shorterContacts)
    {
        var nodes = new List<DocumentNode>
        {
            C("A1", "担当者", bold: true, fill: true, toColumn: 2), C("C1", "連絡先", bold: true, fill: true, toColumn: 4),
            C("A2", "氏名", bold: true, fill: true), C("B2", "部署", bold: true, fill: true), C("C2", "電話", bold: true, fill: true), C("D2", "メール", bold: true, fill: true),
            C("A3", "山田"), C("B3", "営業"), C("C3", "03-1111-2222"), C("D3", "yamada@example.com"),
            C("A4", "佐藤"), C("B4", "経理"), C("C4", "03-3333-4444"), C("D4", "sato@example.com"),
            C("A5", "鈴木"), C("B5", "総務"),
        };
        if (!shorterContacts) nodes.AddRange([C("C5", "03-5555-6666"), C("D5", "suzuki@example.com")]);
        var (markdown, serializer) = Serialize(nodes.ToArray());
        Assert.True(SameTableRow(markdown, "山田", "営業", "03-1111-2222", "yamada@example.com"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    // The dashboard of the business-plan workbook: two tables side by side, each under its own merged
    // title, the right one shorter, below KPI tiles that span the blank column between them.
    private static DocumentNode[] DashboardSheet()
    {
        var nodes = new List<DocumentNode>
        {
            C("A1", "FY2026 事業計画ダッシュボード", bold: true, fill: true, toColumn: 12),
            C("A4", "総予算", bold: true, fill: true, border: true, toColumn: 3), C("D4", "実績", bold: true, fill: true, border: true, toColumn: 6),
            C("G4", "予算消化率", bold: true, fill: true, border: true, toColumn: 9), C("J4", "遅延案件", bold: true, fill: true, border: true, toColumn: 12),
            C("A5", "¥44,900,000", numeric: true, bold: true, border: true, toColumn: 3), C("D5", "¥13,530,000", numeric: true, bold: true, border: true, toColumn: 6),
            C("G5", "30%", numeric: true, bold: true, border: true, toColumn: 9), C("J5", "2", numeric: true, bold: true, border: true, toColumn: 12),
            C("A10", "優先アクション（差異が大きい案件）", bold: true, fill: true, border: true, toColumn: 6),
            C("H10", "カテゴリ別 予算・実績", bold: true, fill: true, border: true, toColumn: 12),
        };
        foreach (var (column, header) in new[] { ("A", "案件ID"), ("B", "案件名"), ("C", "責任者"), ("D", "ステータス"), ("E", "差異"), ("F", "進捗率"), ("H", "カテゴリ"), ("I", "予算"), ("J", "実績") })
            nodes.Add(C(column + "11", header, bold: true, fill: true));
        var projects = new[] { ("PJ-001", "サブスク基盤", "佐藤", "進行中", "¥85,714", "35%"), ("PJ-002", "展示会リード獲得", "田中", "完了", "-¥27,083", "96%"),
            ("PJ-003", "CS 自動応答", "鈴木", "遅延", "-¥50,000", "40%"), ("PJ-004", "採用ブランディング", "高橋", "進行中", "¥66,667", "33%"),
            ("PJ-005", "価格体系見直し", "伊藤", "進行中", "¥100,000", "25%") };
        for (var index = 0; index < projects.Length; index++)
        {
            var (id, name, owner, status, difference, progress) = projects[index];
            var row = index + 12;
            nodes.AddRange([C($"A{row}", id, border: true, formula: $"'案件一覧'!A{row - 7}"), C($"B{row}", name, border: true, formula: $"'案件一覧'!B{row - 7}"),
                C($"C{row}", owner, border: true, formula: $"'案件一覧'!E{row - 7}"), C($"D{row}", status, border: true, formula: $"'案件一覧'!F{row - 7}"),
                C($"E{row}", difference, numeric: true, border: true, formula: $"'案件一覧'!K{row - 7}"), C($"F{row}", progress, numeric: true, border: true, formula: $"'案件一覧'!I{row - 7}")]);
        }
        var categories = new[] { ("プロダクト", "¥18,100,000", "¥5,030,000"), ("マーケティング", "¥10,600,000", "¥3,520,000"),
            ("オペレーション", "¥11,500,000", "¥4,100,000"), ("人材", "¥4,700,000", "¥880,000") };
        for (var index = 0; index < categories.Length; index++)
        {
            var (category, budget, actual) = categories[index];
            var row = index + 12;
            nodes.AddRange([C($"H{row}", category, border: true, formula: $"'月次分析'!H{row - 7}"), C($"I{row}", budget, numeric: true, border: true, formula: $"'月次分析'!I{row - 7}"),
                C($"J{row}", actual, numeric: true, border: true, formula: $"'月次分析'!J{row - 7}")]);
        }
        return nodes.ToArray();
    }

    [Fact]
    public void Two_titled_tables_side_by_side_are_not_joined_under_one_header()
    {
        var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(Title: "FY2026 事業計画"));
        var markdown = serializer.Serialize(Graph(DashboardSheet()));
        AssertIndependent(markdown, ["優先アクション", "PJ-001", "サブスク基盤", "PJ-005"], ["カテゴリ別", "プロダクト", "マーケティング", "人材"]);
        Assert.True(SameTableRow(markdown, "PJ-001", "サブスク基盤", "佐藤", "進行中", "¥85,714", "35%"), markdown);
        Assert.True(SameTableRow(markdown, "プロダクト", "¥18,100,000", "¥5,030,000"), markdown);
        Assert.True(markdown.IndexOf("カテゴリ別 予算・実績", StringComparison.Ordinal) < markdown.IndexOf("| カテゴリ |", StringComparison.Ordinal), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Lists_under_one_banner_keep_their_own_rows_and_columns()
    {
        // The lower block of the "参考UMタグ" sheet: a banner over three lists, two with subtitles.
        var nodes = new List<DocumentNode>
        {
            C("A1", "対象者タグ", fill: true, toColumn: 8), C("J1", "コンテンツタグ", fill: true, toColumn: 11),
            C("A2", "住民向け情報", bold: true, toColumn: 2), C("D2", "事業者向け情報", bold: true, toColumn: 5),
        };
        for (var index = 0; index < 6; index++)
            nodes.AddRange([C($"A{index + 3}", $"T0008{6 + index}", border: true), C($"B{index + 3}", $"住民{index}", border: true),
                C($"D{index + 3}", $"T0010{3 + index}", border: true), C($"E{index + 3}", $"業種{index}", border: true)]);
        for (var index = 0; index < 4; index++)
            nodes.AddRange([C($"G{index + 3}", $"T0012{2 + index}", border: true), C($"H{index + 3}", $"その他{index}", border: true),
                C($"J{index + 3}", $"T0007{7 + index}", border: true), C($"K{index + 3}", $"内容{index}", border: true)]);
        var markdown = SerializeWithTitle(nodes.ToArray(), out _);
        AssertIndependent(markdown, ["住民0", "住民5"], ["業種0", "その他0", "内容0"]);
        AssertIndependent(markdown, ["その他0", "その他3"], ["業種0", "内容0"]);
        Assert.Contains("| T00086 | 住民0 |\n| --- | --- |\n| T00087 | 住民1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| T00122 | その他0 |\n| --- | --- |\n| T00123 | その他1 |", markdown, StringComparison.Ordinal);
    }

    // Values-only blocks: the values of the rows beside them, or a table of their own.

    [Fact]
    public void A_values_table_whose_header_sits_beside_the_data_of_another_table_is_its_own_table()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true),
            C("A2", "Apple"), N("B2", 10), C("D2", "Target", bold: true), C("E2", "Actual", bold: true),
            C("A3", "Pear"), N("B3", 20), N("D3", 100), N("E3", 90),
            C("A4", "Plum"), N("B4", 30), N("D4", 120), N("E4", 130),
            C("A5", "Fig"), N("B5", 40));
        AssertIndependent(markdown, ["Apple", "Pear", "Plum", "Fig"], ["Target", "100", "120"]);
        Assert.True(SameTableRow(markdown, "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData("Year", "2024", "2025", "2026")]
    [InlineData("Year", "2026", "2025", "2024")]
    [InlineData("年度", "2024", "2025", "2026")]
    [InlineData("月", "4月", "5月", "6月")]
    [InlineData("日付", "2026-04-01", "2026-04-02", "2026-04-03")]
    [InlineData("日付", "4月1日", "4月8日", "4月15日")]
    [InlineData("Date", "2026年3月30日", "2026年3月31日", "2026年4月1日")]
    public void A_values_table_keyed_by_its_own_periods_is_separated_and_marked_for_comparison(string key, string first, string second, string third)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", key, bold: true), C("E1", "Target", bold: true),
            C("A2", "Apple"), N("B2", 10), C("D2", first, numeric: first.All(char.IsDigit)), N("E2", 100),
            C("A3", "Pear"), N("B3", 20), C("D3", second, numeric: second.All(char.IsDigit)), N("E3", 120),
            C("A4", "Plum"), N("B4", 30), C("D4", third, numeric: third.All(char.IsDigit)), N("E4", 140));
        AssertIndependent(markdown, ["Apple", "Pear", "Plum"], [first, second, third]);
        Assert.True(SameTableRow(markdown, first, "100"), markdown);
        var review = Assert.Single(serializer.Report.TableBoundaryReviews);
        Assert.Equal(("C1:C4", "A1:B4", "D1:E4"), (review.GapRange, review.LeftRange, review.RightRange));
    }

    [Theory]
    [InlineData("Quantity", 2, 3, 4)]
    [InlineData("Year", 2024, 2024, 2026)]
    [InlineData("Year", 2024, 2026, 2025)]
    public void Values_that_only_look_like_a_sequence_stay_with_their_rows(string header, int first, int second, int third)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Owner", bold: true), C("D1", header, bold: true), C("E1", "Amount", bold: true),
            C("A2", "Apple"), C("B2", "Sato"), N("D2", first), N("E2", 100),
            C("A3", "Pear"), C("B3", "Kato"), N("D3", second), N("E3", 120),
            C("A4", "Plum"), C("B4", "Ito"), N("D4", third), N("E4", 140));
        Assert.True(SameTableRow(markdown, "Apple", "Sato", first.ToString(System.Globalization.CultureInfo.InvariantCulture), "100"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_under_the_second_row_of_a_two_row_header_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "商品", bold: true, fill: true), C("B1", "情報", bold: true, fill: true),
            C("A2", "ID", bold: true, fill: true), C("B2", "Item", bold: true, fill: true), C("D2", "Quantity", bold: true, fill: true), C("E2", "Amount", bold: true, fill: true),
            N("A3", 1), C("B3", "Apple"), N("D3", 2), N("E3", 100),
            N("A4", 2), C("B4", "Pear"), N("D4", 3), N("E4", 200));
        Assert.True(SameTableRow(markdown, "1", "Apple", "2", "100"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_list_of_names_computed_by_formulas_is_a_list_not_values()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Owner", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "Sato", formula: "Staff!A2"),
            C("A3", "Cost"), N("B3", 80), C("D3", "Kato", formula: "Staff!A3"),
            C("A4", "Tax"), N("B4", 20));
        AssertIndependent(markdown, ["Revenue", "Cost", "Tax"], ["Sato", "Kato"]);
        AssertNoBoundaryReview(serializer);
        // A formula that computes a number is still a value of the rows beside it.
        var (computed, computedSerializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Total", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "130", numeric: true, formula: "B2*1.3"),
            C("A3", "Cost"), N("B3", 80), C("D3", "104", numeric: true, formula: "B3*1.3"));
        Assert.True(SameTableRow(computed, "Revenue", "100", "130"), computed);
        AssertNoBoundaryReview(computedSerializer);
    }

    // Second review: layouts traced against the first version of these rules.

    [Theory]
    [InlineData("月", "４月", "５月", "６月")]
    [InlineData("年度", "２０２４", "２０２５", "２０２６")]
    [InlineData("日付", "２０２６/４/１", "２０２６/４/２", "２０２６/４/３")]
    [InlineData("日付", "４月１日", "４月２日", "４月３日")]
    public void Full_width_periods_are_read_as_periods(string key, string first, string second, string third)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "金額", bold: true), C("D1", key, bold: true), C("E1", "目標", bold: true),
            C("A2", "売上"), N("B2", 10), C("D2", first), N("E2", 100),
            C("A3", "費用"), N("B3", 20), C("D3", second), N("E3", 120),
            C("A4", "利益"), N("B4", 30), C("D4", third), N("E4", 140));
        AssertIndependent(markdown, ["売上", "費用", "利益"], [first, second, third]);
        Assert.Single(serializer.Report.TableBoundaryReviews);
    }

    [Fact]
    public void Values_under_a_group_title_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "内訳", bold: true), C("D1", "2024年度", bold: true, toColumn: 5),
            C("D2", "上期", bold: true), C("E2", "下期", bold: true),
            C("A3", "売上"), C("B3", "国内"), N("D3", 10), N("E3", 20),
            C("A4", "費用"), C("B4", "人件費"), N("D4", 5), N("E4", 6),
            C("A5", "利益"), C("B5", "営業"), N("D5", 5), N("E5", 14));
        Assert.True(SameTableRow(markdown, "売上", "国内", "10", "20"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_beside_vertically_merged_label_headers_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true, toRow: 2), C("B1", "内訳", bold: true, toRow: 2), C("D1", "売上", bold: true), C("E1", "利益", bold: true),
            C("D2", "（千円）"), C("E2", "（千円）"),
            C("A3", "東日本"), C("B3", "関東"), N("D3", 10), N("E3", 2),
            C("A4", "西日本"), C("B4", "関西"), N("D4", 8), N("E4", 1),
            C("A5", "合計"), C("B5", "全国"), N("D5", 18), N("E5", 3));
        Assert.True(SameTableRow(markdown, "東日本", "関東", "10", "2"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_beside_a_plain_second_header_row_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "商品", bold: true), C("B1", "情報", bold: true),
            C("A2", "ID"), C("B2", "Item"), C("D2", "Quantity"), C("E2", "Amount"),
            N("A3", 1), C("B3", "Apple"), N("D3", 2), N("E3", 100),
            N("A4", 2), C("B4", "Pear"), N("D4", 3), N("E4", 200));
        Assert.True(SameTableRow(markdown, "1", "Apple", "2", "100"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_starting_beside_the_header_of_another_table_are_their_own_table()
    {
        var (markdown, _) = Serialize(
            C("D1", "Target", bold: true), C("E1", "Actual", bold: true),
            C("A2", "Item", bold: true), C("B2", "Q1", bold: true), N("D2", 100), N("E2", 90),
            C("A3", "Apple"), N("B3", 10), N("D3", 120), N("E3", 130),
            C("A4", "Pear"), N("B4", 20), N("D4", 140), N("E4", 150),
            C("A5", "Plum"), N("B5", 30));
        Assert.False(SameTableRow(markdown, "Item", "Q1", "100", "90"), markdown);
        AssertIndependent(markdown, ["Apple", "Pear", "Plum"], ["Target", "120", "140"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_work_breakdown_under_group_titles_stays_one_table(bool numericYears)
    {
        var nodes = new List<DocumentNode>
        {
            C("A1", "基本情報", bold: true, fill: true, toColumn: 3), C("D1", "スケジュール", bold: true, fill: true, toColumn: 8),
            C("A2", "タスク", bold: true, fill: true), C("B2", "担当", bold: true, fill: true), C("C2", "状態", bold: true, fill: true),
            C("D2", "マイルストーン", bold: true, fill: true),
        };
        var months = numericYears ? new[] { "2024", "2025", "2026", "2027" } : ["4月", "5月", "6月", "7月"];
        for (var index = 0; index < months.Length; index++)
            nodes.Add(C($"{(char)('E' + index)}2", months[index], bold: true, fill: true, numeric: numericYears));
        nodes.AddRange([
            C("A3", "要件定義"), C("B3", "佐藤"), C("C3", "完了"), C("D3", "要件確定"), C("E3", "●"),
            C("A4", "設計"), C("B4", "田中"), C("C4", "進行中"), C("D4", "設計レビュー"), C("F4", "●"), C("G4", "●"),
            C("A5", "実装"), C("B5", "鈴木"), C("C5", "未着手"), C("D5", "リリース"), C("H5", "●")]);
        var (markdown, serializer) = Serialize(nodes.ToArray());
        Assert.True(SameTableRow(markdown, "要件定義", "佐藤", "完了", "要件確定", "●"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_remarks_group_beside_a_titled_table_stays_with_its_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "担当者", bold: true, fill: true, toColumn: 2), C("C1", "備考", bold: true, fill: true, toColumn: 4, toRow: 2),
            C("A2", "氏名", bold: true, fill: true), C("B2", "部署", bold: true, fill: true),
            C("A3", "山田"), C("B3", "営業"), C("C3", "4月から在宅勤務"),
            C("A4", "佐藤"), C("B4", "経理"), C("C4", "兼務あり"),
            C("A5", "鈴木"), C("B5", "総務"), C("C5", "新任"));
        Assert.True(SameTableRow(markdown, "山田", "営業", "4月から在宅勤務"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Group_titles_over_unemphasised_sub_headers_keep_one_table()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "担当者", bold: true, fill: true, toColumn: 2), C("C1", "連絡先", bold: true, fill: true, toColumn: 4),
            C("A2", "氏名", bold: true, fill: true), C("B2", "部署", bold: true, fill: true), C("C2", "電話", border: true), C("D2", "メール", border: true),
            C("A3", "山田"), C("B3", "営業"), C("C3", "03-1111-2222", border: true), C("D3", "yamada@example.com", border: true),
            C("A4", "佐藤"), C("B4", "経理"), C("C4", "03-3333-4444", border: true), C("D4", "sato@example.com", border: true));
        Assert.True(SameTableRow(markdown, "山田", "営業", "03-1111-2222", "yamada@example.com"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void An_edge_between_touching_tables_does_not_cut_a_table_lower_on_the_sheet()
    {
        var nodes = PlanAssumptionsSheet().ToList();
        nodes.AddRange([
            C("D20", "部門", bold: true), C("E20", "責任者", bold: true), C("F20", "拠点", bold: true), C("G20", "連絡先", bold: true), C("H20", "備考", bold: true),
            C("D21", "営業部"), C("E21", "山田"), C("F21", "東京"), C("G21", "内線100"), C("H21", "本社"),
            C("D22", "経理部"), C("E22", "佐藤"), C("F22", "大阪"), C("G22", "内線200"), C("H22", "支社")]);
        var markdown = SerializeWithTitle(nodes.ToArray(), out _);
        Assert.True(SameTableRow(markdown, "営業部", "山田", "東京", "内線100", "本社"), markdown);
        AssertIndependent(markdown, ["分類マスタ", "カテゴリ", "プロダクト"], ["読み方", "入力セル", "計算セル"]);
    }

    [Theory]
    [InlineData("年度", "2023", "2024", "2025", "主な施策", "担当", "DX推進", "佐藤")]
    [InlineData("日付", "2026-04-01", "2026-04-02", "2026-04-03", "作業内容", "担当", "環境構築", "佐藤")]
    public void A_period_key_at_the_start_of_a_table_keys_its_rows(string key, string first, string second, string third,
        string textHeader, string ownerHeader, string text, string owner)
    {
        var (markdown, serializer) = Serialize(
            C("A1", key, bold: true), C("B1", "件数", bold: true), C("D1", textHeader, bold: true), C("E1", ownerHeader, bold: true),
            C("A2", first, numeric: first.All(char.IsDigit)), N("B2", 3), C("D2", text), C("E2", owner),
            C("A3", second, numeric: second.All(char.IsDigit)), N("B3", 5), C("D3", "品質改善"), C("E3", "田中"),
            C("A4", third, numeric: third.All(char.IsDigit)), N("B4", 7), C("D4", "教育"), C("E4", "鈴木"));
        Assert.True(SameTableRow(markdown, first, "3", text, owner), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Dates_of_the_rows_of_a_schedule_stay_with_them()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "研修", bold: true), C("B1", "講師", bold: true), C("D1", "日付", bold: true), C("E1", "時間", bold: true),
            C("A2", "新人研修"), C("B2", "佐藤"), C("D2", "2026-04-01"), C("E2", "9:00"),
            C("A3", "安全教育"), C("B3", "田中"), C("D3", "2026-04-02"), C("E3", "10:00"),
            C("A4", "OJT"), C("B4", "鈴木"), C("D4", "2026-04-03"), C("E4", "13:00"));
        Assert.True(SameTableRow(markdown, "新人研修", "佐藤", "2026-04-01", "9:00"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData("2026-04-01", "2026-05-01", "2026-06-01")]
    [InlineData("2026-04-30", "2026-05-31", "2026-06-30")]
    public void Months_stored_as_dates_are_periods(string first, string second, string third)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "月", bold: true), C("E1", "Target", bold: true),
            C("A2", "Apple"), N("B2", 10), C("D2", first, numeric: true), N("E2", 100),
            C("A3", "Pear"), N("B3", 20), C("D3", second, numeric: true), N("E3", 120),
            C("A4", "Plum"), N("B4", 30), C("D4", third, numeric: true), N("E4", 140));
        AssertIndependent(markdown, ["Apple", "Pear", "Plum"], [first, second, third]);
        Assert.Single(serializer.Report.TableBoundaryReviews);
    }

    [Fact]
    public void Showing_formulas_does_not_change_where_tables_are_split()
    {
        var nodes = new[]
        {
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "年度", bold: true), C("E1", "Target", bold: true),
            C("A2", "Apple"), N("B2", 10), N("D2", 2024), N("E2", 100),
            C("A3", "Pear"), N("B3", 20), C("D3", "2025", numeric: true, formula: "D2+1"), N("E3", 120),
            C("A4", "Plum"), N("B4", 30), C("D4", "2026", numeric: true, formula: "D3+1"), N("E4", 140),
        };
        foreach (var showFormulas in new[] { false, true })
        {
            var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ShowFormulas: showFormulas));
            var markdown = serializer.Serialize(Graph(nodes));
            AssertIndependent(markdown, ["Apple", "Pear", "Plum"], ["Target", "2024"]);
            Assert.Single(serializer.Report.TableBoundaryReviews);
        }
    }

    [Fact]
    public void A_status_computed_by_a_formula_is_a_value_of_its_row()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "目標", bold: true), C("D1", "実績", bold: true), C("E1", "判定", bold: true),
            C("A2", "売上"), N("B2", 100), N("D2", 120), C("E2", "達成", formula: "IF(D2>=B2,\"達成\",\"未達\")"),
            C("A3", "費用"), N("B3", 80), N("D3", 90), C("E3", "未達", formula: "IF(D3>=B3,\"達成\",\"未達\")"),
            C("A4", "利益"), N("B4", 20), N("D4", 30), C("E4", "達成", formula: "IF(D4>=B4,\"達成\",\"未達\")"));
        Assert.True(SameTableRow(markdown, "売上", "100", "120", "達成"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    // Third review: the checks must look at the rows beside the values, not the whole section.

    [Fact]
    public void A_table_stacked_below_another_in_the_same_columns_keeps_its_values()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "部署", bold: true), C("B1", "責任者", bold: true),
            C("A2", "営業部"), C("B2", "山田"), C("A3", "経理部"), C("B3", "佐藤"), C("A4", "総務部"), C("B4", "鈴木"),
            C("A10", "項目", bold: true), C("B10", "担当", bold: true), C("D10", "予算", bold: true), C("E10", "実績", bold: true),
            C("A11", "広告"), C("B11", "田中"), N("D11", 100), N("E11", 90),
            C("A12", "研修"), C("B12", "伊藤"), N("D12", 50), N("E12", 60),
            C("A13", "旅費"), C("B13", "高橋"), N("D13", 30), N("E13", 20));
        Assert.True(SameTableRow(markdown, "広告", "田中", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Numbers_elsewhere_in_the_section_do_not_decide_the_header_of_a_table(bool totalRow)
    {
        var nodes = new List<DocumentNode>
        {
            C("A1", "項目", bold: true), C("B1", "担当", bold: true), C("D1", "予算", bold: true), C("E1", "実績", bold: true),
            C("A2", "広告"), C("B2", "田中"), N("D2", 100), N("E2", 90),
            C("A3", "研修"), C("B3", "伊藤"), N("D3", 50), N("E3", 60),
            C("A4", "旅費"), C("B4", "高橋"), N("D4", 30), N("E4", 20),
        };
        if (totalRow) nodes.AddRange([C("A5", "合計"), C("B5", "3件"), N("D5", 180), N("E5", 170)]);
        else nodes.AddRange([C("A20", "月", bold: true), C("B20", "件数", bold: true), C("A21", "4月"), N("B21", 3), C("A22", "5月"), N("B22", 5), C("A23", "6月"), N("B23", 7)]);
        var (markdown, serializer) = Serialize(nodes.ToArray());
        Assert.True(SameTableRow(markdown, "広告", "田中", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Numbers_lower_on_the_sheet_do_not_turn_a_period_key_into_a_second_table()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "年度", bold: true), C("B1", "売上", bold: true), C("D1", "施策", bold: true), C("E1", "担当", bold: true),
            N("A2", 2023), N("B2", 100), C("D2", "DX推進"), C("E2", "佐藤"),
            N("A3", 2024), N("B3", 120), C("D3", "品質改善"), C("E3", "田中"),
            N("A4", 2025), N("B4", 140), C("D4", "教育"), C("E4", "鈴木"),
            C("D20", "区分", bold: true), C("E20", "件数", bold: true), C("D21", "A"), N("E21", 1), C("D22", "B"), N("E22", 2));
        Assert.True(SameTableRow(markdown, "2023", "100", "DX推進", "佐藤"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void An_edge_between_touching_tables_does_not_change_decisions_lower_on_the_sheet()
    {
        var nodes = PlanAssumptionsSheet().ToList();
        nodes.AddRange([
            C("D20", "ID", bold: true), C("E20", "品目", bold: true), C("F20", "区分", bold: true), C("G20", "担当", bold: true), C("I20", "数量", bold: true), C("J20", "金額", bold: true),
            N("D21", 1), C("E21", "用紙"), C("F21", "消耗品"), C("G21", "山田"), N("I21", 10), N("J21", 5000),
            N("D22", 2), C("E22", "トナー"), C("F22", "消耗品"), C("G22", "佐藤"), N("I22", 2), N("J22", 12000)]);
        var markdown = SerializeWithTitle(nodes.ToArray(), out var serializer);
        Assert.True(SameTableRow(markdown, "1", "用紙", "消耗品", "山田", "10", "5000"), markdown);
        Assert.Empty(serializer.Report.TableBoundaryReviews);
    }

    [Fact]
    public void Three_touching_tables_under_their_own_titles_are_separated()
    {
        var (markdown, _) = Serialize(
            C("A1", "分類マスタ", bold: true, fill: true, toColumn: 2), C("C1", "読み方", bold: true, fill: true, toColumn: 4), C("E1", "担当一覧", bold: true, fill: true, toColumn: 6),
            C("A2", "カテゴリ", bold: true, fill: true), C("B2", "重点テーマ", bold: true, fill: true), C("C2", "入力セル", bold: true, fill: true), C("D2", "黄色: 編集可"),
            C("E2", "氏名", bold: true, fill: true), C("F2", "部署", bold: true, fill: true),
            C("A3", "プロダクト"), C("B3", "継続収益"), C("C3", "計算セル", bold: true, fill: true), C("D3", "青: 数式"), C("E3", "山田"), C("F3", "営業"),
            C("A4", "マーケティング"), C("B4", "獲得効率"), C("C4", "出典", bold: true, fill: true), C("D4", "URL"), C("E4", "佐藤"), C("F4", "経理"),
            C("A5", "人材"), C("B5", "組織能力"), C("E5", "鈴木"), C("F5", "総務"));
        AssertIndependent(markdown, ["プロダクト", "マーケティング"], ["計算セル", "出典"]);
        AssertIndependent(markdown, ["計算セル", "出典"], ["山田", "佐藤"]);
    }

    [Fact]
    public void Emphasised_values_beside_the_rows_of_a_titled_table_stay_with_them()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "社員", bold: true, fill: true, toColumn: 2), C("C1", "評価", bold: true, fill: true, toColumn: 4),
            C("A2", "氏名", bold: true, fill: true), C("B2", "部署", bold: true, fill: true),
            C("A3", "山田"), C("B3", "営業"), C("C3", "S評価", bold: true), C("D3", "目標を大きく上回った"),
            C("A4", "佐藤"), C("B4", "経理"), C("C4", "A評価", bold: true), C("D4", "目標を達成"),
            C("A5", "鈴木"), C("B5", "総務"), C("C5", "B評価", bold: true), C("D5", "一部未達"));
        Assert.True(SameTableRow(markdown, "山田", "営業", "S評価", "目標を大きく上回った"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData("VLOOKUP(A2,Staff!A:B,2,FALSE)")]
    [InlineData("IFERROR(INDEX(Staff!B:B,MATCH(A2,Staff!A:A,0)),\"\")")]
    [InlineData("IF(A2=\"\",\"\",XLOOKUP(A2,Staff!A:A,Staff!B:B))")]
    public void Names_looked_up_by_a_formula_are_labels(string lookup)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Owner", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "Sato", formula: lookup),
            C("A3", "Cost"), N("B3", 80), C("D3", "Kato", formula: lookup.Replace("A2", "A3", StringComparison.Ordinal)));
        AssertIndependent(markdown, ["Revenue", "Cost"], ["Sato", "Kato"]);
        Assert.Single(serializer.Report.TableBoundaryReviews);
    }

    [Fact]
    public void Copies_of_a_filled_down_status_formula_are_values_like_the_formula()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "目標", bold: true), C("D1", "実績", bold: true), C("E1", "判定", bold: true),
            C("A2", "売上"), N("B2", 100), N("D2", 120), C("E2", "達成", formula: "IF(D2>=B2,\"達成\",\"未達\")"),
            C("A3", "費用"), N("B3", 80), N("D3", 90), C("E3", "未達", sharedFormula: true),
            C("A4", "利益"), N("B4", 20), N("D4", 30), C("E4", "達成", sharedFormula: true));
        Assert.True(SameTableRow(markdown, "費用", "80", "90", "未達"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void An_open_ended_date_does_not_stop_the_conversion()
    {
        var (markdown, _) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "日付", bold: true), C("E1", "Target", bold: true),
            C("A2", "Apple"), N("B2", 10), C("D2", "2026-03-31", numeric: true), N("E2", 100),
            C("A3", "Pear"), N("B3", 20), C("D3", "2026-04-30", numeric: true), N("E3", 120),
            C("A4", "Plum"), N("B4", 30), C("D4", "9999-12-31", numeric: true), N("E4", 140));
        Assert.Contains("9999-12-31", markdown, StringComparison.Ordinal);
    }

    // Fourth review: being out of step needs values as evidence, not a guessed header row.

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Row_labels_under_an_empty_corner_stay_with_their_values(bool boldLabels, bool twoLabelColumns)
    {
        var labelEnd = twoLabelColumns ? "B" : "A";
        var values = twoLabelColumns ? ("D", "E") : ("C", "D");
        var nodes = new List<DocumentNode> { C($"{values.Item1}1", "予算", bold: true), C($"{values.Item2}1", "実績", bold: true) };
        var labels = new[] { "売上", "費用", "利益", "人件費" };
        for (var index = 0; index < labels.Length; index++)
        {
            var row = index + 2;
            nodes.Add(C($"A{row}", labels[index], bold: boldLabels));
            if (twoLabelColumns) nodes.Add(C($"{labelEnd}{row}", "内訳" + index));
            nodes.AddRange([N($"{values.Item1}{row}", 100 + index), N($"{values.Item2}{row}", 90 + index)]);
        }
        var markdown = SerializeWithTitle(nodes.ToArray(), out var serializer);
        Assert.True(SameTableRow(markdown, "売上", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Each_copy_of_a_formula_is_judged_by_what_it_shows()
    {
        // The written formula shows its fixed word, the copies show names they looked up.
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Owner", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "なし", formula: "IF(Staff!A2=\"\",\"なし\",Staff!B2)"),
            C("A3", "Cost"), N("B3", 80), C("D3", "Kato Jiro", sharedFormula: true),
            C("A4", "Tax"), N("B4", 20), C("D4", "Ito Hanako", sharedFormula: true));
        AssertIndependent(markdown, ["Revenue", "Cost", "Tax"], ["Kato Jiro", "Ito Hanako"]);
        Assert.Single(serializer.Report.TableBoundaryReviews);
    }

    [Fact]
    public void Values_starting_beside_the_bold_header_of_a_text_table_are_their_own_table()
    {
        var (markdown, _) = Serialize(
            C("D1", "Target", bold: true), C("E1", "Actual", bold: true),
            C("A2", "Code", bold: true), C("B2", "Name", bold: true), N("D2", 100), N("E2", 90),
            C("A3", "K01"), C("B3", "Alpha"), N("D3", 120), N("E3", 130),
            C("A4", "K02"), C("B4", "Beta"), N("D4", 140), N("E4", 150),
            C("A5", "K03"), C("B5", "Gamma"));
        Assert.False(SameTableRow(markdown, "Code", "Name", "100", "90"), markdown);
        AssertIndependent(markdown, ["Alpha", "Beta", "Gamma"], ["Target", "120", "140"]);
    }

    [Theory]
    [InlineData("bold")]
    [InlineData("fill")]
    public void Statement_rows_under_an_empty_corner_stay_with_their_values(string emphasis)
    {
        var markdown = SerializeWithTitle([
            C("C1", "前期", bold: true), C("D1", "当期", bold: true),
            C("A2", "売上高", bold: emphasis == "bold", fill: emphasis == "fill"), N("C2", 1000), N("D2", 1200),
            C("A3", "売上原価"), N("C3", 600), N("D3", 700),
            C("A4", "売上総利益", bold: emphasis == "bold", fill: emphasis == "fill"), N("C4", 400), N("D4", 500)], out var serializer);
        Assert.True(SameTableRow(markdown, "売上高", "1000", "1200"), markdown);
        Assert.True(SameTableRow(markdown, "売上原価", "600", "700"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_year_in_the_header_of_the_labelled_side_is_not_a_value()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "2025", bold: true, numeric: true), C("D1", "増減", bold: true), C("E1", "増減率", bold: true),
            C("A2", "売上"), N("B2", 100), N("D2", 10), C("E2", "11%"),
            C("A3", "費用"), N("B3", 80), N("D3", -5), C("E3", "-6%"),
            C("A4", "利益"), N("B4", 20), N("D4", 15), C("E4", "300%"),
            C("A5", "人員"), N("B5", 12), N("D5", 1));
        Assert.True(SameTableRow(markdown, "売上", "100", "10", "11%"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_date_caption_in_the_corner_does_not_split_the_table()
    {
        var markdown = SerializeWithTitle([
            C("A1", "2026/04/01現在"), C("C1", "予算", bold: true), C("D1", "実績", bold: true),
            C("A2", "営業"), N("C2", 100), N("D2", 90),
            C("A3", "経理"), N("C3", 50), N("D3", 60),
            C("A4", "総務"), N("C4", 30), N("D4", 20),
            C("A5", "※速報値")], out var serializer);
        Assert.True(SameTableRow(markdown, "営業", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_category_row_without_a_count_keeps_its_values_in_the_table()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "項目", bold: true), C("B1", "件数", bold: true), C("D1", "予算", bold: true), C("E1", "実績", bold: true),
            C("A2", "東日本"), N("D2", 300), N("E2", 280),
            C("A3", "東京"), N("B3", 5), N("D3", 200), N("E3", 190),
            C("A4", "仙台"), N("B4", 3), N("D4", 100), N("E4", 90));
        Assert.True(SameTableRow(markdown, "東日本", "300", "280"), markdown);
        Assert.True(SameTableRow(markdown, "東京", "5", "200", "190"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_table_one_blank_row_below_another_keeps_its_values()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "部署", bold: true), C("B1", "責任者", bold: true),
            C("A2", "営業部"), C("B2", "山田"), C("A3", "経理部"), C("B3", "佐藤"),
            C("A5", "項目", bold: true), C("B5", "担当", bold: true), C("D5", "予算", bold: true), C("E5", "実績", bold: true),
            C("A6", "広告"), C("B6", "田中"), N("D6", 100), N("E6", 90),
            C("A7", "研修"), C("B7", "伊藤"), N("D7", 50), N("E7", 60),
            C("A8", "旅費"), C("B8", "高橋"), N("D8", 30), N("E8", 20));
        Assert.True(SameTableRow(markdown, "広告", "田中", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void A_caption_above_a_label_column_does_not_split_its_table()
    {
        var markdown = SerializeWithTitle([
            C("A1", "単位：千円"),
            C("A2", "部門", bold: true), C("C2", "予算", bold: true), C("D2", "実績", bold: true),
            C("A3", "営業"), N("C3", 100), N("D3", 90),
            C("A4", "経理"), N("C4", 50), N("D4", 60),
            C("A5", "総務"), N("C5", 30), N("D5", 20)], out var serializer);
        Assert.True(SameTableRow(markdown, "営業", "100", "90"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Fact]
    public void Values_beside_a_plain_second_header_row_of_text_labels_stay_with_their_rows()
    {
        var (markdown, serializer) = Serialize(
            C("A1", "商品", bold: true), C("B1", "情報", bold: true),
            C("A2", "名称"), C("B2", "分類"), C("D2", "数量"), C("E2", "金額"),
            C("A3", "りんご"), C("B3", "果物"), N("D3", 2), N("E3", 300),
            C("A4", "なす"), C("B4", "野菜"), N("D4", 3), N("E4", 200));
        Assert.True(SameTableRow(markdown, "りんご", "果物", "2", "300"), markdown);
        AssertNoBoundaryReview(serializer);
    }

    [Theory]
    [InlineData("Staff!B2&\" \"&Staff!C2")]
    [InlineData("IF(Staff!A2=\"\",\"\",Staff!B2)")]
    public void Names_built_by_a_filled_down_formula_are_labels(string formula)
    {
        var (markdown, serializer) = Serialize(
            C("A1", "Item", bold: true), C("B1", "Q1", bold: true), C("D1", "Owner", bold: true),
            C("A2", "Revenue"), N("B2", 100), C("D2", "Sato Taro", formula: formula),
            C("A3", "Cost"), N("B3", 80), C("D3", "Kato Jiro", sharedFormula: true),
            C("A4", "Tax"), N("B4", 20), C("D4", "Ito Hanako", sharedFormula: true));
        AssertIndependent(markdown, ["Revenue", "Cost", "Tax"], ["Sato Taro", "Kato Jiro", "Ito Hanako"]);
        Assert.Single(serializer.Report.TableBoundaryReviews);
    }

    private static void AssertNoBoundaryReview(ReadableMarkdownSerializer serializer)
    {
        Assert.Empty(serializer.Report.TableBoundaryReviews);
        Assert.DoesNotContain(serializer.Diagnostics, item => item.Code == "XlsxTableBoundaryAmbiguous");
    }

    // Neither a table row nor any blank-line-separated block holds content from both groups.
    private static void AssertIndependent(string markdown, string[] left, string[] right)
    {
        var text = markdown.Replace("\\", "", StringComparison.Ordinal);
        foreach (var line in text.Split('\n'))
            Assert.False(left.Any(line.Contains) && right.Any(line.Contains), line);
        foreach (var block in text.Split("\n\n"))
            Assert.False(left.Any(block.Contains) && right.Any(block.Contains), block);
        Assert.All(left.Concat(right), value => Assert.Contains(value, text, StringComparison.Ordinal));
    }

    private static bool SameTableRow(string markdown, params string[] values) =>
        markdown.Split('\n').Where(line => line.StartsWith('|')).Select(line =>
            line.Trim('|').Split(" | ").Select(cell => cell.Trim()).ToArray())
            .Any(cells => values.All(value => cells.Contains(value)));

    private static (string Markdown, ReadableMarkdownSerializer Serializer) Serialize(params DocumentNode[] nodes)
    {
        var serializer = new ReadableMarkdownSerializer();
        return (serializer.Serialize(Graph(nodes)), serializer);
    }

    private static DocumentGraph Graph(IReadOnlyList<DocumentNode> nodes) =>
        new(DocumentGraph.CurrentSchemaVersion, "boundaries", DocumentFormatKind.Xlsx, [new DocumentPartition("sheet-Data", 0, nodes)]);

    private static DocumentNode N(string address, int value, string? table = null) =>
        C(address, value.ToString(System.Globalization.CultureInfo.InvariantCulture), numeric: true, table: table);

    private static DocumentNode C(string address, string value, bool bold = false, bool numeric = false, string? table = null, int toColumn = 0,
        bool fill = false, bool border = false, string? formula = null, int toRow = 0, bool sharedFormula = false)
    {
        var match = Regex.Match(address, @"^([A-Z]+)(\d+)$");
        var column = match.Groups[1].Value.Aggregate(0, (sum, letter) => sum * 26 + letter - 'A' + 1);
        var row = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["row"] = JsonSerializer.SerializeToElement(row),
            ["column"] = JsonSerializer.SerializeToElement(column),
            ["is_bold"] = JsonSerializer.SerializeToElement(bold),
            ["is_numeric"] = JsonSerializer.SerializeToElement(numeric),
            ["has_fill"] = JsonSerializer.SerializeToElement(fill),
            ["has_border"] = JsonSerializer.SerializeToElement(border),
        };
        if (table is not null) extensions["excel_table"] = JsonSerializer.SerializeToElement(table);
        if (toColumn > 0) extensions["merged_to_column"] = JsonSerializer.SerializeToElement(toColumn);
        if (toRow > 0) extensions["merged_to_row"] = JsonSerializer.SerializeToElement(toRow);
        // Excel stores a formula filled down as one written formula and copies that only point to it
        // (<f t="shared" si="0"/>), which are read with an empty formula.
        if (formula is not null || sharedFormula)
        {
            extensions["is_formula"] = JsonSerializer.SerializeToElement(true);
            extensions["formula"] = JsonSerializer.SerializeToElement(formula ?? string.Empty);
            extensions["cell_type"] = JsonSerializer.SerializeToElement(numeric ? "n" : "str");
        }
        return new DocumentNode("cell-" + address, NodeKind.Cell, null, row * 1000 + column, ContentLayer.Body, new TextNodeContent(value),
            new SourceAnchor("xlsx", "/xl/worksheets/sheet1.xml", [new AnchorLocator("cell_address", address)]), Extensions: extensions);
    }

    private static DocumentNode Move(DocumentNode node, int rows)
    {
        var extensions = node.Extensions!.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var row = extensions["row"].GetInt32() + rows;
        extensions["row"] = JsonSerializer.SerializeToElement(row);
        return node with { Order = row * 1000 + extensions["column"].GetInt32(), Extensions = extensions };
    }
}

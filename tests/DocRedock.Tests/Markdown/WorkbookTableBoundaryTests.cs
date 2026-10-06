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

    private static DocumentNode C(string address, string value, bool bold = false, bool numeric = false, string? table = null, int toColumn = 0)
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
        };
        if (table is not null) extensions["excel_table"] = JsonSerializer.SerializeToElement(table);
        if (toColumn > 0) extensions["merged_to_column"] = JsonSerializer.SerializeToElement(toColumn);
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

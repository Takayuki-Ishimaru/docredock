using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Markdown;
using DocRedock.Render;
using DocRedock.Tests.Xlsx;

namespace DocRedock.Tests.Render;

public sealed class WorkbookInternalLinkTests
{
    [Fact]
    public void Native_worksheet_location_is_a_clickable_unique_screen_cell_anchor()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Go to target</t></is></c></row>
            <row r="3"><c r="B3" t="inlineStr"><is><t>Target value</t></is></c></row></sheetData>
            <hyperlinks><hyperlink ref="A1" location="'Sheet, O''Brien'!B3"/></hyperlinks>
            """).Graph;
        var targetId = WorkbookCellLinks.Id("Sheet, O'Brien", 3, 2);
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;
        Assert.Contains("href=\"#" + targetId + "\"", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "\\bid=\"" + targetId + "\""));
        Assert.Contains("Target value", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_merge_continuation_link_resolves_to_the_visible_origin()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Open merged value</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>Merged target</t></is></c></row></sheetData>
            <mergeCells><mergeCell ref="A2:B2"/></mergeCells>
            <hyperlinks><hyperlink ref="A1" location="B2"/></hyperlinks>
            """).Graph;
        var result = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document);
        var targetId = WorkbookCellLinks.Id("Sheet, O'Brien", 2, 1);
        Assert.Contains("href=\"#" + targetId + "\"", result.Html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(result.Html, "\\bid=\"" + targetId + "\""));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "HtmlWorkbookLinkUnavailable");
    }

    [Fact]
    public void A_destination_outside_the_selected_grid_is_reported()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Outside target</t></is></c></row></sheetData>
            <hyperlinks><hyperlink ref="A1" location="XFD1048576"/></hyperlinks>
            """).Graph;
        var result = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document);
        Assert.Contains(result.Diagnostics, d => d.Code == "HtmlWorkbookLinkUnavailable");
        Assert.DoesNotContain("href=\"#docredock-cell-", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_excluded_sheet_link_does_not_get_a_fictitious_target()
    {
        var graph = WorkbookPresentationTests.Extract("""
            <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Missing destination</t></is></c></row></sheetData>
            <hyperlinks><hyperlink ref="A1" location="'NOT_INCLUDED'!B3"/></hyperlinks>
            """).Graph;
        var result = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document);
        Assert.Contains(result.Diagnostics, item => item.Code == "HtmlWorkbookLinkUnavailable");
        Assert.DoesNotContain("href=\"#docredock-cell-", result.Html, StringComparison.Ordinal);
        Assert.Contains("Missing destination", result.Html, StringComparison.Ordinal);
    }
}

using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

public sealed class WorkbookCellLinksTests
{
    [Theory]
    [InlineData("#'O''Brien ! 設計'!$B$12", "Unused", "O'Brien ! 設計", 12, 2)]
    [InlineData("#Data!A1:C8", "Other", "Data", 1, 1)]
    [InlineData("#$XFD$1048576", "現在", "現在", 1048576, 16384)]
    [InlineData("#Data!b2", "Other", "Data", 2, 2)]
    public void Supported_locations_keep_the_exact_sheet_and_bounded_cell(string location, string current, string sheet, int row, int column)
    {
        Assert.True(WorkbookCellLinks.TryParse(location, current, out var target));
        Assert.Equal(new WorkbookCellTarget(sheet, row, column), target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("#Data!XFE1")]
    [InlineData("#Data!A1048577")]
    [InlineData("#Data!A0")]
    [InlineData("#Data!A01")]
    [InlineData("#Data!A1:Z0")]
    [InlineData("#Data!C8:A1")]
    [InlineData("#'broken!A1")]
    [InlineData("#[External.xlsx]Data!A1")]
    [InlineData("#javascript:alert(1)")]
    [InlineData("#NameOfRange")]
    [InlineData("#Data!A1\n")]
    public void Unsupported_locations_are_not_fabricated_as_cell_links(string? location)
    {
        Assert.False(WorkbookCellLinks.TryParse(location, "Current", out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Cell_ids_are_deterministic_and_do_not_contain_source_markup()
    {
        var first = WorkbookCellLinks.Id("A <tag> & quote\"", 2, 3);
        Assert.Equal(first, WorkbookCellLinks.Id("A <tag> & quote\"", 2, 3));
        Assert.Matches("^docredock-cell-[a-f0-9]{16}-2-3$", first);
        Assert.NotEqual(first, WorkbookCellLinks.Id("Other", 2, 3));
    }
}

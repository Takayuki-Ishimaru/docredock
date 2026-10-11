using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using DocRedock.Gui;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

public sealed class ReviewInlineNestingTests
{
    [AvaloniaFact]
    public void Preview_preserves_nested_styles_inside_strikethrough()
    {
        var preview = ReviewMarkdownPreview.Build("~~本日の**会議** *italic* `code` <u>下線</u>は中止~~");
        var block = Assert.Single(preview.GetLogicalDescendants().OfType<SelectableTextBlock>());
        var runs = block.Inlines!.OfType<Run>().ToArray();
        Assert.Equal("本日の会議 italic code 下線は中止", string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(FontWeight.SemiBold, Assert.Single(runs, run => run.Text == "会議").FontWeight);
        Assert.Equal(FontStyle.Italic, Assert.Single(runs, run => run.Text == "italic").FontStyle);
        Assert.Equal("Menlo", Assert.Single(runs, run => run.Text == "code").FontFamily!.Name);
        Assert.Contains(Assert.Single(runs, run => run.Text == "下線").TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Underline);
        Assert.All(runs, run => Assert.Contains(run.TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Strikethrough));
    }
}

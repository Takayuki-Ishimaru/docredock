using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DocRedock.Core.Documents;
using DocRedock.Render;

namespace DocRedock.Gui;

/// <summary>Shows a page's readable Markdown the way a reader sees it, for side-by-side comparison
/// with the source page image: headings, paragraphs, real table grids, quotes, lists, and each
/// Mermaid flowchart as its list of connections. It reads the same block and inline syntax the
/// render command reads (<see cref="MarkdownAstParser"/>, <see cref="MarkdownInlineParser"/>), so
/// escapes and character references resolve exactly as they do there. It is a preview, not an
/// editor: the Markdown source stays one toggle away.</summary>
public static class ReviewMarkdownPreview
{
    private static readonly Regex MermaidNode = new(@"^\s*(?<id>[A-Za-z0-9_]+)(?<shape>\S.*?)\s*;?\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex MermaidEdge = new(
        @"^\s*(?<from>[A-Za-z0-9_]+)\s*(?<arrow><-->|<-\.->|<==>|-->|---|-\.->|-\.-|==>|===)\s*(?:\|(?<label>[^|]*)\|\s*)?(?<to>[A-Za-z0-9_]+)\s*;?\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>The page Markdown as a control tree, one child per block.</summary>
    public static Control Build(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4, 2, 12, 12) };
        MarkdownDocument document;
        try { document = MarkdownAstParser.Parse(markdown); }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            panel.Children.Add(Text(markdown));
            return panel;
        }
        foreach (var block in document.Blocks)
            if (BuildBlock(block) is { } control) panel.Children.Add(control);
        return panel;
    }

    /// <summary>A Mermaid flowchart as one line per connection ("START → END（ラベル）") followed by
    /// its unconnected nodes, or null when the source is not a flowchart this preview can read -
    /// the caller then shows the source itself rather than a partial picture.</summary>
    public static IReadOnlyList<string>? DescribeFlowchart(string mermaid)
    {
        ArgumentNullException.ThrowIfNull(mermaid);
        var lines = mermaid.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0 || !Regex.IsMatch(lines[0], @"^(?:flowchart|graph)\b", RegexOptions.CultureInvariant)) return null;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var edges = new List<(string From, string To, string? Label, string Arrow)>();
        foreach (var line in lines.Skip(1))
        {
            var edge = MermaidEdge.Match(line);
            if (edge.Success)
            {
                edges.Add((edge.Groups["from"].Value, edge.Groups["to"].Value,
                    edge.Groups["label"].Success ? LabelText(edge.Groups["label"].Value) : null, edge.Groups["arrow"].Value));
                continue;
            }
            var node = MermaidNode.Match(line);
            // A line this preview cannot read would silently drop a connection: show the source.
            if (!node.Success || NodeLabel(node.Groups["shape"].Value) is not { } label) return null;
            labels[node.Groups["id"].Value] = label;
        }
        if (edges.Count == 0 && labels.Count == 0) return null;
        string Name(string id) => labels.TryGetValue(id, out var label) && label.Length > 0 ? label : id;
        var result = edges.Select(edge =>
        {
            var symbol = edge.Arrow.StartsWith('<') ? "↔" : edge.Arrow.EndsWith('>') ? "→" : "―";
            var dotted = edge.Arrow.Contains('.', StringComparison.Ordinal) ? "（点線）" : string.Empty;
            var label = string.IsNullOrEmpty(edge.Label) ? string.Empty : $"（{edge.Label}）";
            return $"{Name(edge.From)} {symbol} {Name(edge.To)}{label}{dotted}";
        }).ToList();
        var connected = edges.SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet(StringComparer.Ordinal);
        result.AddRange(labels.Keys.Where(id => !connected.Contains(id)).Select(id => "・" + Name(id)));
        return result;
    }

    /// <summary>The label inside the node shapes the readable serializer writes - <c>[x]</c>,
    /// <c>([x])</c>, <c>[/x/]</c>, <c>{x}</c> - read by their outer delimiters. The serializer
    /// escapes square brackets inside labels but not parentheses or slashes, so "[Review (QA)]" is
    /// the label "Review (QA)". Any other shape returns null.</summary>
    private static string? NodeLabel(string shape)
    {
        if (shape.Length >= 4 && shape.StartsWith("([", StringComparison.Ordinal) && shape.EndsWith("])", StringComparison.Ordinal))
            return LabelText(shape[2..^2]);
        if (shape.Length >= 4 && shape.StartsWith("[/", StringComparison.Ordinal) && shape.EndsWith("/]", StringComparison.Ordinal))
            return LabelText(shape[2..^2]);
        if (shape.Length >= 2 && (shape[0], shape[^1]) is ('[', ']') or ('{', '}'))
            return LabelText(shape[1..^1]);
        return null;
    }

    private static string LabelText(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value.Trim().Trim('"')), @"<br\s*/?>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

    private static Control? BuildBlock(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => Heading(heading),
        MarkdownTable table => Table(table),
        MarkdownList list => List(list),
        MarkdownCodeBlock code => Code(code),
        MarkdownParagraph paragraph => Paragraph(paragraph.Text),
        _ => null,
    };

    private static Control Heading(MarkdownHeading heading)
    {
        var text = Inline(heading.Text);
        text.FontSize = heading.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        text.FontWeight = FontWeight.SemiBold;
        return text;
    }

    private static Control? Paragraph(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && trimmed.EndsWith("-->", StringComparison.Ordinal)) return null;
        if (Regex.IsMatch(trimmed, @"^(?:-{3,}|\*{3,}|_{3,})$", RegexOptions.CultureInvariant))
            return Themed(new Border { Height = 1, Margin = new Thickness(0, 4) }, Border.BackgroundProperty, "HairlineBrush");
        // A raw SVG preview (the GUI's SVG option) is markup, not text to read.
        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return Text("SVGプレビュー（内容は「ソース」で確認できます）", muted: true, small: true);
        var lines = trimmed.Split('\n');
        // A quote may continue on lines without '>' (a lazy continuation, as the serializer writes
        // its "> " + note pair): the whole paragraph is the quote once its first line is.
        if (lines[0].TrimStart().StartsWith('>'))
        {
            var quoted = string.Join("\n", lines.Select(line => line.TrimStart() is var start && start.StartsWith('>') ? start[1..].TrimStart() : start))
                .Trim();
            var border = new Border
            {
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 4, 4, 4),
                Child = Inline(quoted, muted: true),
            };
            return Themed(border, Border.BorderBrushProperty, "BrandLavenderBrush");
        }
        return Inline(trimmed);
    }

    private static Control List(MarkdownList list)
    {
        var panel = new StackPanel { Spacing = 3 };
        for (var index = 0; index < list.Items.Count; index++)
        {
            var level = list.Levels is { } levels && index < levels.Count ? levels[index] : 0;
            // An ordered item keeps the number it was written with ("4." stays 4).
            var number = list.Numbers is { } numbers && index < numbers.Count ? numbers[index] : null;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(level * 18, 0, 0, 0) };
            var marker = Text(number is { } value ? $"{value}." : "•");
            marker.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(marker);
            var item = Inline(list.Items[index]);
            Grid.SetColumn(item, 1);
            row.Children.Add(item);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control Code(MarkdownCodeBlock code)
    {
        if (code.Language.Equals("mermaid", StringComparison.OrdinalIgnoreCase) && DescribeFlowchart(code.Text) is { Count: > 0 } connections)
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(Text("図（Mermaid）の接続", muted: true, small: true));
            foreach (var line in connections) panel.Children.Add(Text(line));
            return Card(panel);
        }
        var body = new SelectableTextBlock
        {
            Text = code.Text,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        return Card(body);
    }

    private static Control Table(MarkdownTable table)
    {
        var columns = Math.Max(table.Headers.Count, table.Rows.Select(row => row.Count).DefaultIfEmpty(0).Max());
        var grid = new Grid();
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var rows = new List<IReadOnlyList<string>> { table.Headers };
        rows.AddRange(table.Rows);
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < columns; column++)
            {
                var value = column < rows[row].Count ? rows[row][column] : string.Empty;
                var text = Inline(value);
                text.MaxWidth = 320;
                if (row == 0) text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Child = text,
                };
                Themed(cell, Border.BorderBrushProperty, "HairlineBrush");
                if (row == 0) Themed(cell, Border.BackgroundProperty, "SurfaceAltBrush");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
        // A wide table scrolls sideways inside the preview instead of squeezing every column.
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private static Border Card(Control child)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = child,
        };
        Themed(border, Border.BorderBrushProperty, "HairlineBrush");
        return Themed(border, Border.BackgroundProperty, "SurfaceAltBrush");
    }

    /// <summary>Inline Markdown as styled runs: bold, italic, strike, code, links (as text), and
    /// line breaks from <c>&lt;br&gt;</c>; an image appears as its alt text.</summary>
    private static SelectableTextBlock Inline(string markdown, bool muted = false)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (muted) Themed(block, TextBlock.ForegroundProperty, "MutedInkBrush");
        IReadOnlyList<TextRun> runs;
        try { runs = MarkdownInlineParser.Runs(markdown); }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            block.Text = markdown;
            return block;
        }
        var inlines = new InlineCollection();
        foreach (var run in runs)
        {
            if (run.Kind == TextRunKind.LineBreak) { inlines.Add(new LineBreak()); continue; }
            var segment = new Run(run.Text);
            if (run.Bold) segment.FontWeight = FontWeight.SemiBold;
            if (run.Italic) segment.FontStyle = FontStyle.Italic;
            if (run.Code) segment.FontFamily = new FontFamily("Menlo, Consolas, monospace");
            var decorations = new TextDecorationCollection();
            if (run.Underline || run.LinkTarget is not null) decorations.AddRange(TextDecorations.Underline);
            if (run.Strike) decorations.AddRange(TextDecorations.Strikethrough);
            if (decorations.Count > 0) segment.TextDecorations = decorations;
            inlines.Add(segment);
        }
        block.Inlines = inlines;
        return block;
    }

    private static SelectableTextBlock Text(string value, bool muted = false, bool small = false)
    {
        var block = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        if (small) block.FontSize = 12;
        if (muted) Themed(block, TextBlock.ForegroundProperty, "MutedInkBrush");
        return block;
    }

    // Brushes follow the light/dark theme variant through the application's resource keys.
    private static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}

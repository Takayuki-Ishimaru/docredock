using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Api.SourceReading;

public sealed partial class SourceReader
{
    private async Task<SourceTextResult> ReadNodeAsync(string input, string path, DocumentFormatKind format, string name,
        string nodeId, int offset, int maxChars, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new SourceReadException("invalid_locator", "--node needs a node id such as n_4L5SQ2FD7FAKPOC3.");
        var node = await FindNodeAsync(path, format, nodeId.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new SourceReadException("unknown_node",
                $"Node '{nodeId}' was not found in this document. Node ids come from the source-index.json of an AI package or from the diagnostics of export --json " +
                "for this same file, converted with the default options (a different --revisions choice, or another version of the file, can change them).");

        var text = NodeText(node.Content);
        var textTruncated = text is not null && text.Length > maxChars;
        if (textTruncated) text = CutWithoutSplittingPair(text!, maxChars);
        var slice = node.RawSlice;
        string content = "";
        int? next = null;
        long total = 0;
        bool? hashMatches = null;
        string? note;
        if (slice is not null)
        {
            using var archive = OpenPackage(path);
            var entry = FindEntry(archive, slice.PartUri);
            byte[] bytes;
            using (var stream = entry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            if (slice.StartOffset < 0 || slice.EndOffset < slice.StartOffset || slice.EndOffset > bytes.Length)
                throw new SourceReadException("slice_out_of_range",
                    $"The node's raw slice ({slice.StartOffset.ToString(CultureInfo.InvariantCulture)}-{slice.EndOffset.ToString(CultureInfo.InvariantCulture)}) lies outside {entry.FullName} ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes); the file is not the one the node id came from.");
            var span = bytes.AsSpan((int)slice.StartOffset, (int)(slice.EndOffset - slice.StartOffset));
            hashMatches = string.Equals(Convert.ToHexString(SHA256.HashData(span)), slice.Sha256, StringComparison.OrdinalIgnoreCase);
            var window = TextWindow.Read(new StringReader(Encoding.UTF8.GetString(span)), offset, maxChars);
            (content, next, total) = (window.Content, window.NextOffset, window.Total);
            note = next is { } more ? $"More of the raw XML follows: read it with --offset {more.ToString(CultureInfo.InvariantCulture)}." : null;
            if (hashMatches == false)
                note = (note is null ? "" : note + " ") + "The slice's sha256 does not match the file: it is not the version that was converted.";
        }
        else
        {
            if (offset > 0)
                throw new SourceReadException("offset_out_of_range", "This node has no raw slice, so there is no text to page; read it without --offset.");
            note = "This node has no raw slice. " + Where(node);
        }
        var anchor = node.Source;
        var info = new SourceNodeInfo(node.Id, Snake(node.Kind.ToString()), Snake(node.Layer.ToString()), text, textTruncated,
            anchor?.Format, anchor?.PartUri, anchor?.Locators.Select(locator => KeyValuePair.Create(locator.Kind, locator.Value)).ToArray() ?? [],
            slice?.PartUri, slice?.StartOffset, slice?.EndOffset, slice?.Sha256, slice is null ? null : Snake(slice.Kind.ToString()), hashMatches);
        return new SourceTextResult
        {
            Input = input, Format = name, Locator = Locator("node", ("node", node.Id)),
            Node = info, Content = content, Offset = offset, NextOffset = next, TotalChars = total, Note = note,
        };
    }

    /// <summary>Where to read around a node that has no raw slice: the page, sheet range or part its anchor names.</summary>
    private static string Where(DocumentNode node)
    {
        var anchor = node.Source;
        if (anchor is null) return "It has no source anchor either; use the overview to find the part to read.";
        if (anchor.PartUri.StartsWith("pdf:page:", StringComparison.Ordinal))
            return $"Read its page with --page {anchor.PartUri["pdf:page:".Length..]}.";
        var cell = anchor.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value;
        if (cell is not null && node.Extensions?.TryGetValue("sheet_name", out var sheet) == true && sheet.ValueKind == System.Text.Json.JsonValueKind.String)
            return $"Read its cell with --sheet \"{sheet.GetString()}\" --range {cell}, or the sheet's XML with --part {anchor.PartUri.TrimStart('/')}.";
        return $"Use --part {anchor.PartUri.TrimStart('/')} to read the part its anchor names.";
    }

    /// <summary>Finds a node by converting the document the way <c>export</c> does when it is given no options, so the ids are the
    /// ones an AI package and the diagnostics of an export carry. If the first conversion does not have the node, the variants
    /// that change which nodes exist are tried: tracked changes shown as the final text (Word), then OCR text.</summary>
    private async Task<DocumentNode?> FindNodeAsync(string path, DocumentFormatKind format, string nodeId, CancellationToken cancellationToken)
    {
        var attempts = new List<(bool Ocr, string? Revisions)> { (false, null) };
        if (format == DocumentFormatKind.Docx) attempts.Add((false, "final"));
        attempts.Add((true, null));
        foreach (var (ocr, revisions) in attempts)
        {
            var graph = await documents.ExtractDefaultGraphAsync(path, ocr, revisions, cancellationToken).ConfigureAwait(false);
            if (graph.FindNode(nodeId) is { } node) return node;
        }
        return null;
    }

    private static string? NodeText(NodeContent content) => content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        TableNodeContent table => string.Join('\n', table.Rows.Select(row => string.Join(" | ", row.Select(cell => cell.Text)))),
        ReferenceNodeContent reference => reference.AltText ?? reference.Reference,
        _ => null,
    };

    private static string CutWithoutSplittingPair(string text, int length) =>
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    private static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();
}

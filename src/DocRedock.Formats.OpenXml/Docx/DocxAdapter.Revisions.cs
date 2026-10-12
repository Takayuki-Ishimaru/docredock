using System.Text.Json;
using System.Xml.Linq;

namespace DocRedock.Formats.OpenXml.Docx;

public sealed partial class DocxAdapter
{
    // One-way final projection only. The source ZIP and round-trip defaults are unchanged.
    private static bool HasDeletedRevisionContent(XDocument document) => document.Descendants().Any(element =>
        IsDeletedRevision(element) && (element.Name == W + "t" || element.Name == W + "delText" ||
            element.Name == W + "drawing" || element.Name == W + "pict"));

    private static void ApplyFinalRevisionView(XDocument document)
    {
        // Equal surviving row widths alone do not prove equal columns: different rows can
        // delete different columns. Retain that uncertainty through the semantic clones,
        // without changing the original ZIP bytes or assigning surviving cells new headers.
        foreach (var table in document.Descendants(W + "tbl"))
        {
            var deletionPatterns = table.Elements(W + "tr").Where(row => !IsDeletedRevision(row)).Select(row =>
            {
                long column = ParseDocxGridBefore(row.Element(W + "trPr"));
                var deleted = new List<(long Start, long End)>();
                foreach (var cell in row.Elements(W + "tc"))
                {
                    var span = ParsePositiveInt((string?)cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")) ?? 1;
                    if (IsDeletedRevision(cell))
                    {
                        // A merged deleted cell and adjacent single-column deleted cells
                        // describe the same removed columns, regardless of segmentation.
                        if (deleted.Count > 0 && deleted[^1].End == column)
                            deleted[^1] = (deleted[^1].Start, column + span);
                        else deleted.Add((column, column + span));
                    }
                    column += span;
                }
                return string.Join(",", deleted.Select(range => range.Start + ":" + range.End));
            }).Distinct(StringComparer.Ordinal).Take(2).Count();
            if (deletionPatterns > 1) table.SetAttributeValue(FinalTableGridMarker, "true");
        }
        foreach (var deleted in document.Descendants().Where(element => DeletedStructure(element) ||
                     element.Name == W + "del" || element.Name == W + "moveFrom" || element.Name == W + "delText").ToArray())
            deleted.Remove();
        foreach (var previous in document.Descendants().Where(element => element.Name.Namespace == W &&
                     PropertyRevisionNames.Contains(element.Name.LocalName)).ToArray()) previous.Remove();
        foreach (var inserted in document.Descendants().Where(element => element.Name == W + "ins" ||
                     element.Name == W + "moveTo").ToArray()) inserted.ReplaceWith(inserted.Nodes().ToArray());
    }

    private static readonly XName FinalTableGridMarker = XNamespace.Get("urn:docredock:projection") + "unresolved-final-table-grid";

    private static readonly HashSet<string> PropertyRevisionNames = new(StringComparer.Ordinal)
    {
        "rPrChange", "pPrChange", "tblPrChange", "tblPrExChange", "trPrChange", "tcPrChange", "tblGridChange", "sectPrChange",
        "cellMerge", "cellIns", "cellDel", "numberingChange",
    };

    private static bool DeletedStructure(XElement element) => element.Name == W + "tr" && element.Element(W + "trPr")?.Element(W + "del") is not null ||
        element.Name == W + "tc" && element.Element(W + "tcPr")?.Element(W + "cellDel") is not null;

    private static bool InsertedStructure(XElement element) => element.Name == W + "tr" && element.Element(W + "trPr")?.Element(W + "ins") is not null ||
        element.Name == W + "tc" && element.Element(W + "tcPr")?.Element(W + "cellIns") is not null;

    private static IEnumerable<XElement> RevisionRows(XElement table, DocxHiddenStyles styles) =>
        table.Elements(W + "tr").Where(row => styles.ShowTrackedRevisions || !DeletedStructure(row));

    private static IEnumerable<XElement> RevisionCells(XElement row, DocxHiddenStyles styles) =>
        row.Elements(W + "tc").Where(cell => styles.ShowTrackedRevisions || !DeletedStructure(cell));

    private static Dictionary<string, JsonElement>? WithRevisionNotes(XElement block, DocxHiddenStyles styles,
        Dictionary<string, JsonElement>? extensions)
    {
        if (!styles.ShowTrackedRevisions) return extensions;
        var result = extensions ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        AddRevisionExtensions(result, block, styles);
        return result.Count == 0 ? null : result;
    }

    private static void AddRevisionExtensions(Dictionary<string, JsonElement> extensions, XElement block, DocxHiddenStyles styles)
    {
        if (!styles.ShowTrackedRevisions) return;
        var changes = new List<object>();
        foreach (var change in block.Descendants().Where(IsRevisionContainer))
        {
            var owner = change.Parent;
            if (owner is null || IsHiddenContentElement(change, styles) || change.Ancestors().Any(element => PropertyRevisionNames.Contains(element.Name.LocalName))) continue;
            // A nested table writes its own notes; the outer cell's paragraphs belong here.
            if (block.Name == W + "tbl" && change.Ancestors(W + "tbl").FirstOrDefault() != block) continue;
            string description;
            if (change.Name == W + "cellMerge")
                description = $"セル結合: {(string?)change.Attribute(W + "vMergeOrig") ?? "なし"} → {(string?)change.Attribute(W + "vMerge") ?? "なし"}";
            else if (change.Name.LocalName is "cellIns" or "cellDel" || change.Name.LocalName is "ins" or "del" &&
                (owner.Name == W + "trPr" || owner.Name == W + "rPr" && owner.Parent?.Name == W + "pPr"))
            {
                var subject = owner.Name == W + "trPr" ? "行" : owner.Name == W + "tcPr" ? "セル" : "段落区切り";
                description = subject + (change.Name.LocalName is "cellIns" or "ins" ? "を追加" : "を削除");
            }
            else if (change.Name == W + "numberingChange")
                description = $"番号: {(string?)change.Attribute(W + "original") ?? "以前の番号"} → 現在の番号";
            else if (PropertyRevisionNames.Contains(change.Name.LocalName))
            {
                var before = RevisionProperties(change.Element(owner.Name));
                var after = RevisionProperties(owner);
                var differences = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)
                    .Where(key => before.GetValueOrDefault(key) != after.GetValueOrDefault(key))
                    .Select(key => $"{key}: {before.GetValueOrDefault(key) ?? "継承"} → {after.GetValueOrDefault(key) ?? "継承"}").ToArray();
                description = differences.Length > 0 ? string.Join("; ", differences) : "書式の変更（現在の値は同一）";
            }
            else continue; // Text insertions/deletions already carry inline marks.
            changes.Add(new { context = RevisionContext(change, block), description,
                author = (string?)change.Attribute(W + "author"), date = (string?)change.Attribute(W + "date") });
        }
        if (changes.Count > 0) extensions["revision_changes"] = JsonSerializer.SerializeToElement(changes);
    }

    private static string RevisionContext(XElement change, XElement block)
    {
        var labels = new List<string>();
        if (change.Ancestors(W + "tr").FirstOrDefault() is { } row) labels.Add("行" + (row.ElementsBeforeSelf(W + "tr").Count() + 1));
        if (change.Ancestors(W + "tc").FirstOrDefault() is { } cell) labels.Add("セル" + (cell.ElementsBeforeSelf(W + "tc").Count() + 1));
        if (change.Ancestors(W + "r").FirstOrDefault() is { } run)
        {
            var text = string.Concat(run.Descendants().Where(element => element.Name == W + "t" || element.Name == W + "delText").Select(element => element.Value));
            labels.Add("「" + (text.Length <= 80 ? text : text[..80] + "…") + "」");
        }
        return labels.Count > 0 ? string.Join("・", labels) : block.Name == W + "tbl" ? "表" : "段落";
    }

    private static Dictionary<string, string> RevisionProperties(XElement? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is null) return result;
        foreach (var property in properties.Elements().Where(element => !PropertyRevisionNames.Contains(element.Name.LocalName) &&
            element.Name.LocalName is not ("ins" or "del" or "moveFrom" or "moveTo")))
        {
            var label = property.Name.LocalName switch
            {
                "b" => "太字", "i" => "斜体", "strike" => "取り消し線", "dstrike" => "二重取り消し線", "u" => "下線",
                "rStyle" => "文字スタイル", "pStyle" => "段落スタイル", "rFonts" => "フォント", "sz" => "文字サイズ（半ポイント）",
                "color" => "文字色", "highlight" => "強調色", "vanish" => "非表示", "jc" => "配置", "ind" => "インデント",
                "spacing" => "間隔", "numPr" => "段落番号", "outlineLvl" => "見出しレベル", "tabs" => "タブ位置",
                "tblW" => "表幅", "tcW" => "セル幅", "gridSpan" => "列の結合数", "vMerge" => "縦の結合", "gridCol" => "列幅",
                "tblBorders" => "表罫線", "tcBorders" => "セル罫線", "tblLayout" => "表レイアウト", "trHeight" => "行高",
                "gridBefore" => "行の前の列数", "gridAfter" => "行の後の列数", "shd" => "背景", "vAlign" => "縦配置",
                "tblHeader" => "見出し行", "cantSplit" => "行の分割禁止", "pgSz" => "用紙サイズ", "pgMar" => "ページ余白",
                "keepNext" => "次の段落と同じページ", "keepLines" => "段落を同じページ", "pageBreakBefore" => "段落前の改ページ",
                _ => property.Name.LocalName,
            };
            var attributes = property.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => attribute.Name.LocalName == "val" ? attribute.Value : attribute.Name.LocalName + "=" + attribute.Value).ToArray();
            var value = attributes.Length == 0 ? "on" : string.Join(", ", attributes);
            if (property.HasElements) value = string.Join(", ", property.Elements().Select(element => element.Name.LocalName + "=" +
                string.Join("/", element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).Select(attribute => attribute.Value))));
            if (result.ContainsKey(label)) result[label] += ", " + value;
            else result[label] = value;
        }
        return result;
    }
}

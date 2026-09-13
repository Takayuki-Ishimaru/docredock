using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Formats.OpenXml.Xlsx;

namespace DocRedock.Tests.RoundTrip;

/// <summary>
/// P3 "実験的な往復編集の複雑ケース拡充": complex, multi-part edits through the full
/// DocumentService.ExportAsync -> edit markdown -> DocumentService.RestoreAsync pipeline, beyond
/// the simple single-run text edits already covered by DocumentServiceTests. Each test pins the
/// actual, current behaviour for one complex case; where the current behaviour is an explicit,
/// diagnosed rejection rather than the aspirational "everything just works", that is noted with a
/// KNOWN GAP comment rather than asserted away.
/// </summary>
public sealed class ComplexEditRoundTripTests
{
    // ---------------------------------------------------------------------------------------
    // 1. DOCX equation (OMML) editing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_editing_the_linearized_equation_text_replaces_omml_with_plain_text_and_reexports()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "equation.docx");
        await WriteEquationDocxAsync(source);
        var markdown = Path.Combine(root, "equation.md");
        var workspace = Path.Combine(root, "equation.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("Given ", projection, StringComparison.Ordinal);
        Assert.Contains("`x^2`", projection, StringComparison.Ordinal);
        Assert.Contains("holds.", projection, StringComparison.Ordinal);
        Assert.Contains("Untouched trailer paragraph.", projection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, projection.Replace("`x^2`", "`y^3`", StringComparison.Ordinal));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.Contains(restored.Diagnostics, item => item.Code == "DocxMathReplaced");
        var patched = ReadDocumentXml(output);
        Assert.DoesNotContain("<m:oMath", patched, StringComparison.Ordinal);
        Assert.Contains("y^3", patched, StringComparison.Ordinal);
        Assert.Contains("Given", patched, StringComparison.Ordinal);
        Assert.Contains("holds.", patched, StringComparison.Ordinal);
        // The unrelated second paragraph never appeared in the diff, so it must survive verbatim.
        Assert.Contains("Untouched trailer paragraph.", patched, StringComparison.Ordinal);

        // The restored package is well-formed: [Content_Types].xml is untouched, and the adapter
        // can extract it again without throwing.
        Assert.Equal(ReadZipEntryBytes(source, "[Content_Types].xml"), ReadZipEntryBytes(output, "[Content_Types].xml"));
        var reextraction = await new DocxAdapter().ExtractAsync(output);
        Assert.Contains(reextraction.Graph.Nodes, node => FlatText(node.Content).Contains("y^3", StringComparison.Ordinal));

        // Re-export from the restored package still shows the edited text (round-trip is stable).
        var reexportMarkdown = Path.Combine(root, "reexported.md");
        var reexported = await service.ExportReadableAsync(new ReadableDocumentExportOptions(output, reexportMarkdown));
        Assert.Contains("y^3", await File.ReadAllTextAsync(reexported.MarkdownPath), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 2. DOCX SDT (content control) internal edits, including one nested in a table cell
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_editing_text_around_an_inline_content_control_nested_in_a_table_cell_keeps_the_control()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "control-in-table.docx");
        await WriteInlineControlInTableDocxAsync(source);
        var markdown = Path.Combine(root, "control-in-table.md");
        var workspace = Path.Combine(root, "control-in-table.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var lines = SplitLines(projection);
        var rowIndex = FindRowIndex(lines, "Pick Choice here");
        // Edit only the text before the control's own run; "Choice" itself is left untouched.
        lines[rowIndex] = ReplaceCellExact(lines[rowIndex], "Pick Choice here", "Select Choice here");
        await File.WriteAllTextAsync(markdown, string.Join('\n', lines));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.DoesNotContain(restored.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
        var patched = ReadDocumentXml(output);
        // The control's own wrapper (alias/tag/id) and inner run survive byte-for-byte.
        Assert.Contains("<w:sdtPr><w:alias w:val=\"Option\" /><w:tag w:val=\"opt\" /><w:id w:val=\"42\" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent>",
            patched, StringComparison.Ordinal);
        // The prefix/suffix text is split across the run-boundary-preserving rewrite (e.g.
        // "Select " may land as "Selec" + "t "), so assert on the logical cell text rather than a
        // literal XML substring.
        var reextraction = await new DocxAdapter().ExtractAsync(output);
        var table = Assert.Single(reextraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var cells = (TableNodeContent)table.Content;
        Assert.Equal("Select Choice here", cells.Rows[0][0].Text);
        Assert.Equal("Other cell", cells.Rows[0][1].Text);
        Assert.Contains(reextraction.Graph.Nodes, node => FlatText(node.Content) == "Tail");
    }

    [Fact]
    public async Task Docx_editing_text_inside_an_inline_content_controls_own_run_keeps_the_control_and_updates_its_text()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "control-in-table.docx");
        await WriteInlineControlInTableDocxAsync(source);
        var markdown = Path.Combine(root, "control-in-table.md");
        var workspace = Path.Combine(root, "control-in-table.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var lines = SplitLines(projection);
        var rowIndex = FindRowIndex(lines, "Pick Choice here");
        // Retype only the word the control itself carries ("Choice" -> "Selection"), leaving the
        // surrounding "Pick "/" here" text exactly as it was. DocxAdapter.PlaceInlineAnchors can
        // rebuild a container anchor's own text (not just find it verbatim) whenever the text
        // immediately before and after the anchor is still present unchanged: it treats whatever
        // now sits between that unchanged prefix/suffix as the control's new inner text, and keeps
        // the w:sdt/w:sdtPr wrapper. (A retype that also touches the surrounding text, so the
        // prefix/suffix no longer match, falls back to unwrapping the control instead - see
        // CanRewriteContainerText/PlaceInlineAnchors and the existing DocxInlineContainerUnwrapped
        // coverage.)
        lines[rowIndex] = ReplaceCellExact(lines[rowIndex], "Pick Choice here", "Pick Selection here");
        await File.WriteAllTextAsync(markdown, string.Join('\n', lines));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.DoesNotContain(restored.Diagnostics, item => item.Code == "DocxInlineContainerUnwrapped");
        var patched = ReadDocumentXml(output);
        Assert.Contains(
            "<w:sdtPr><w:alias w:val=\"Option\" /><w:tag w:val=\"opt\" /><w:id w:val=\"42\" /></w:sdtPr><w:sdtContent><w:r><w:t>Selection</w:t></w:r></w:sdtContent>",
            patched, StringComparison.Ordinal);
        var reextraction = await new DocxAdapter().ExtractAsync(output);
        var table = Assert.Single(reextraction.Graph.Nodes, node => node.Kind == NodeKind.Table);
        var cells = (TableNodeContent)table.Content;
        Assert.Equal("Pick Selection here", cells.Rows[0][0].Text);
        Assert.Equal("Other cell", cells.Rows[0][1].Text);
    }

    // ---------------------------------------------------------------------------------------
    // 3. DOCX nested table edits
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_editing_the_outer_table_cell_through_the_full_pipeline_preserves_the_nested_table()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "nested-table.docx");
        await WriteNestedTableDocxAsync(source);
        var markdown = Path.Combine(root, "nested-table.md");
        var workspace = Path.Combine(root, "nested-table.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("| Outer label | Right |", projection, StringComparison.Ordinal);
        Assert.Contains("Inner label", projection, StringComparison.Ordinal);
        Assert.Contains("Inner value", projection, StringComparison.Ordinal);
        var edited = projection.Replace("| Outer label | Right |", "| Outer label edited | Right |", StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, edited);

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        var patched = ReadDocumentXml(output);
        Assert.Contains("Outer label edited", patched, StringComparison.Ordinal);
        Assert.Contains("Inner label", patched, StringComparison.Ordinal);
        Assert.Contains("Inner value", patched, StringComparison.Ordinal);
        Assert.Contains("Right", patched, StringComparison.Ordinal);
        // Both tables (host + nested) are still there, not collapsed or duplicated. (The restored
        // slice re-declares xmlns:w on the outer <w:tbl> where the source inherited it from
        // <w:document>, so count the opening tag itself rather than an exact "<w:tbl>" substring.)
        Assert.Equal(CountOccurrences(ReadDocumentXml(source), "<w:tbl"), CountOccurrences(patched, "<w:tbl"));

        var reextraction = await new DocxAdapter().ExtractAsync(output);
        var tables = reextraction.Graph.Nodes.Where(node => node.Kind == NodeKind.Table)
            .Select(node => (TableNodeContent)node.Content).OrderBy(content => content.Rows[0][0].Text, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, tables.Length);
        Assert.Contains(tables, table => table.Rows[0][0].Text == "Outer label edited" && table.Rows[0][1].Text == "Right");
        Assert.Contains(tables, table => table.Rows[0][0].Text == "Inner label" && table.Rows[0][1].Text == "Inner value");
    }

    [Fact]
    public async Task Docx_editing_both_the_outer_and_the_nested_table_cell_together_preserves_structure()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "nested-table.docx");
        await WriteNestedTableDocxAsync(source);
        var markdown = Path.Combine(root, "nested-table.md");
        var workspace = Path.Combine(root, "nested-table.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var edited = projection
            .Replace("| Outer label | Right |", "| Outer label edited | Right |", StringComparison.Ordinal)
            .Replace("Inner label", "Inner label edited", StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, edited);

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        Assert.DoesNotContain(restored.Diagnostics, item => item.Code == "ProtectedNodeEdit");
        var reextraction = await new DocxAdapter().ExtractAsync(output);
        var tables = reextraction.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).ToArray();
        Assert.All(tables, node => Assert.Equal(NodeEditability.EditableWithConstraints, node.Editability));
        Assert.All(tables, node => Assert.NotNull(node.RawSlice));
        Assert.Contains(tables, node => ((TableNodeContent)node.Content).Rows[0][0].Text == "Outer label edited");
        Assert.Contains(tables, node => ((TableNodeContent)node.Content).Rows[0][0].Text == "Inner label edited");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var originalXml = XDocument.Parse(ReadDocumentXml(source));
        var restoredXml = XDocument.Parse(ReadDocumentXml(output));
        Assert.Equal(originalXml.Descendants(w + "tbl").Select(t => t.Ancestors(w + "tbl").Count()),
            restoredXml.Descendants(w + "tbl").Select(t => t.Ancestors(w + "tbl").Count()));
        Assert.Equal(originalXml.Descendants(w + "tcPr").Select(p => p.ToString()),
            restoredXml.Descendants(w + "tcPr").Select(p => p.ToString()));
        Assert.Equal("2", restoredXml.Descendants(w + "gridSpan").Single().Attribute(w + "val")!.Value);
        Assert.Contains("<w:p><w:r><w:t>Body</w:t></w:r></w:p>", ReadDocumentXml(output));
        var reexport = await service.ExportReadableAsync(new ReadableDocumentExportOptions(output, Path.Combine(root, "again.md")));
        var readable = await File.ReadAllTextAsync(reexport.MarkdownPath);
        Assert.Contains("Outer label edited", readable);
        Assert.Contains("Inner label edited", readable);
    }

    // ---------------------------------------------------------------------------------------
    // 4. PPTX multi-slide edit
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Pptx_editing_slide_one_and_three_leaves_slide_two_and_notes_byte_identical()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "deck.pptx");
        await WriteThreeSlidePptxAsync(source);
        var markdown = Path.Combine(root, "deck.md");
        var workspace = Path.Combine(root, "deck.drmd");
        var output = Path.Combine(root, "restored.pptx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("### Slide One Title", projection, StringComparison.Ordinal);
        Assert.Contains("### Slide Two Title", projection, StringComparison.Ordinal);
        Assert.Contains("### Slide Three Title", projection, StringComparison.Ordinal);
        Assert.Contains("Tail second bullet", projection, StringComparison.Ordinal);
        var edited = projection
            .Replace("### Slide One Title", "### Slide One Title Updated", StringComparison.Ordinal)
            .Replace("Tail second bullet", "Tail second bullet updated", StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, edited);

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        // Slide 2's own part payload is byte-identical - it was never touched.
        Assert.Equal(ReadZipEntryBytes(source, "ppt/slides/slide2.xml"), ReadZipEntryBytes(output, "ppt/slides/slide2.xml"));
        // Notes are untouched, even on the slide that itself was edited.
        Assert.Equal(ReadZipEntryBytes(source, "ppt/notesSlides/notesSlide1.xml"), ReadZipEntryBytes(output, "ppt/notesSlides/notesSlide1.xml"));
        // Slide order/relationships are unchanged.
        Assert.Equal(ReadZipEntryBytes(source, "ppt/presentation.xml"), ReadZipEntryBytes(output, "ppt/presentation.xml"));
        Assert.Equal(ReadZipEntryBytes(source, "ppt/_rels/presentation.xml.rels"), ReadZipEntryBytes(output, "ppt/_rels/presentation.xml.rels"));

        var slide1 = Encoding.UTF8.GetString(ReadZipEntryBytes(output, "ppt/slides/slide1.xml"));
        Assert.Contains("Slide One Title Updated", slide1, StringComparison.Ordinal);
        var slide3 = Encoding.UTF8.GetString(ReadZipEntryBytes(output, "ppt/slides/slide3.xml"));
        Assert.Contains("Tail first bullet", slide3, StringComparison.Ordinal);
        Assert.Contains("Tail second bullet updated", slide3, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 5. XLSX formula-cell edits
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Xlsx_editing_a_plain_value_cell_preserves_the_formula_cell_and_shared_string()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "formulas.xlsx");
        await WriteFormulaWorkbookAsync(source);
        var markdown = Path.Combine(root, "formulas.md");
        var workspace = Path.Combine(root, "formulas.drmd");
        var output = Path.Combine(root, "restored.xlsx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var lines = SplitLines(projection);
        var rowIndex = FindRowIndex(lines, "Label");
        lines[rowIndex] = ReplaceCellExact(lines[rowIndex], "2", "20");
        await File.WriteAllTextAsync(markdown, string.Join('\n', lines));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        // The unrelated shared-string part is byte-identical - only the worksheet cell changed.
        Assert.Equal(ReadZipEntryBytes(source, "xl/sharedStrings.xml"), ReadZipEntryBytes(output, "xl/sharedStrings.xml"));
        var worksheetXml = Encoding.UTF8.GetString(ReadZipEntryBytes(output, "xl/worksheets/sheet1.xml"));
        Assert.Contains("<f>SUM(B1:B2)</f>", worksheetXml, StringComparison.Ordinal);

        using var extraction = File.OpenRead(output);
        var reextraction = new XlsxAdapter().Extract(extraction);
        var cells = reextraction.Graph.Nodes.Where(node => node.Kind == NodeKind.Cell).ToArray();
        var b1 = Assert.Single(cells, node => Address(node) == "B1");
        Assert.Equal("20", ((TextNodeContent)b1.Content).Text);
        var b3 = Assert.Single(cells, node => Address(node) == "B3");
        Assert.Equal("SUM(B1:B2)", b3.Extensions!["formula"].GetString());
        var a1 = Assert.Single(cells, node => Address(node) == "A1");
        Assert.Equal("Label", ((TextNodeContent)a1.Content).Text);
    }

    [Fact]
    public async Task Xlsx_editing_a_formula_cell_to_a_literal_value_drops_the_formula_without_corrupting_the_workbook()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "formulas.xlsx");
        await WriteFormulaWorkbookAsync(source);
        var markdown = Path.Combine(root, "formulas.md");
        var workspace = Path.Combine(root, "formulas.drmd");
        var output = Path.Combine(root, "restored.xlsx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var lines = SplitLines(projection);
        var rowIndex = FindRowIndex(lines, "SUM(B1:B2)");
        // The DRMD projection of a formula cell is the formula itself ("`=SUM(B1:B2)` → 5"), not
        // its cached value; retyping it as a plain literal (no leading '=') is how a user tells the
        // editor "this is no longer a formula" - the same way typing over a formula cell works in
        // Excel itself. The safe-formula constraint's deliberate behaviour is therefore: the
        // formula is dropped and a new literal value is written, with no rejection.
        lines[rowIndex] = ReplaceCellContaining(lines[rowIndex], "SUM(B1:B2)", "9");
        await File.WriteAllTextAsync(markdown, string.Join('\n', lines));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        var worksheetXml = Encoding.UTF8.GetString(ReadZipEntryBytes(output, "xl/worksheets/sheet1.xml"));
        Assert.DoesNotContain("SUM(B1:B2)", worksheetXml, StringComparison.Ordinal);

        // The workbook is still loadable and B3 now reads as a plain, non-formula value.
        using var extraction = File.OpenRead(output);
        var reextraction = new XlsxAdapter().Extract(extraction);
        var b3 = Assert.Single(reextraction.Graph.Nodes, node => node.Kind == NodeKind.Cell && Address(node) == "B3");
        Assert.Equal("9", ((TextNodeContent)b3.Content).Text);
        Assert.False(b3.Extensions!["is_formula"].GetBoolean());
        Assert.DoesNotContain(b3.Extensions!, item => item.Key == "formula");
    }

    [Fact]
    public async Task Xlsx_editing_a_formula_cell_to_a_literal_value_invalidates_the_stale_calc_chain()
    {
        // A calcChain.xml entry that still points at a cell which no longer holds a formula is
        // exactly the inconsistency that can make Excel show its "we found a problem with some
        // content... do you want us to try to recover" repair prompt, so a formula-removing edit
        // must invalidate the calc chain the same way a formula-adding/changing edit already does
        // (XlsxAdapter.Restore/PatchWorksheet) - and an edit that never touches a formula cell must
        // still leave it byte-identical.
        var root = TempDirectory();
        var source = Path.Combine(root, "formulas.xlsx");
        await WriteFormulaWorkbookAsync(source);
        var calcChainBefore = ReadZipEntryBytes(source, "xl/calcChain.xml");
        Assert.Contains("B3", Encoding.UTF8.GetString(calcChainBefore), StringComparison.Ordinal);

        // An unrelated, non-formula edit leaves the calc chain exactly as it was.
        var unrelatedMarkdown = Path.Combine(root, "unrelated.md");
        var unrelatedWorkspace = Path.Combine(root, "unrelated.drmd");
        var unrelatedOutput = Path.Combine(root, "unrelated.xlsx");
        var service = new DocumentService();
        await service.ExportAsync(new DocumentExportOptions(source, unrelatedWorkspace, unrelatedMarkdown));
        var unrelatedProjection = await File.ReadAllTextAsync(unrelatedMarkdown);
        var unrelatedLines = SplitLines(unrelatedProjection);
        var unrelatedRowIndex = FindRowIndex(unrelatedLines, "Label");
        unrelatedLines[unrelatedRowIndex] = ReplaceCellExact(unrelatedLines[unrelatedRowIndex], "2", "20");
        await File.WriteAllTextAsync(unrelatedMarkdown, string.Join('\n', unrelatedLines));
        var unrelatedRestored = await service.RestoreAsync(new DocumentRestoreOptions(unrelatedWorkspace, unrelatedOutput, unrelatedMarkdown));
        Assert.True(unrelatedRestored.Succeeded);
        Assert.Equal(calcChainBefore, ReadZipEntryBytes(unrelatedOutput, "xl/calcChain.xml"));

        // Dropping the formula in B3 invalidates the calc chain.
        var dropMarkdown = Path.Combine(root, "drop.md");
        var dropWorkspace = Path.Combine(root, "drop.drmd");
        var dropOutput = Path.Combine(root, "drop.xlsx");
        await service.ExportAsync(new DocumentExportOptions(source, dropWorkspace, dropMarkdown));
        var dropProjection = await File.ReadAllTextAsync(dropMarkdown);
        var dropLines = SplitLines(dropProjection);
        var dropRowIndex = FindRowIndex(dropLines, "SUM(B1:B2)");
        dropLines[dropRowIndex] = ReplaceCellContaining(dropLines[dropRowIndex], "SUM(B1:B2)", "9");
        await File.WriteAllTextAsync(dropMarkdown, string.Join('\n', dropLines));
        var dropRestored = await service.RestoreAsync(new DocumentRestoreOptions(dropWorkspace, dropOutput, dropMarkdown));
        Assert.True(dropRestored.Succeeded);

        using (var archive = ZipFile.OpenRead(dropOutput))
        {
            var calcChainEntry = archive.GetEntry("xl/calcChain.xml");
            if (calcChainEntry is not null)
            {
                using var reader = new StreamReader(calcChainEntry.Open(), Encoding.UTF8);
                Assert.DoesNotContain("B3", await reader.ReadToEndAsync(), StringComparison.Ordinal);
            }
            // Whichever of the two consistent outcomes DocRedock chose (dropping calcChain.xml
            // entirely, or - the current mechanism - emptying it while keeping the part), nothing
            // may be left dangling: a Content_Types Override or workbook relationship must not
            // still declare a part the archive no longer has.
            var contentTypesEntry = archive.GetEntry("[Content_Types].xml")!;
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")!;
            using var contentTypesReader = new StreamReader(contentTypesEntry.Open(), Encoding.UTF8);
            using var relsReader = new StreamReader(relsEntry.Open(), Encoding.UTF8);
            var declaresCalcChain = (await contentTypesReader.ReadToEndAsync()).Contains("calcChain", StringComparison.OrdinalIgnoreCase) ||
                (await relsReader.ReadToEndAsync()).Contains("calcChain", StringComparison.OrdinalIgnoreCase);
            Assert.False(declaresCalcChain && calcChainEntry is null,
                "calcChain.xml was removed but something still declares/references it.");
        }

        // The workbook itself is unaffected by the calc-chain handling: B3 still reads as the new,
        // non-formula literal.
        using var extraction = File.OpenRead(dropOutput);
        var reextraction = new XlsxAdapter().Extract(extraction);
        var b3 = Assert.Single(reextraction.Graph.Nodes, node => node.Kind == NodeKind.Cell && Address(node) == "B3");
        Assert.Equal("9", ((TextNodeContent)b3.Content).Text);
    }

    [Fact]
    public async Task Xlsx_editing_a_percent_formatted_cells_raw_value_updates_the_underlying_number()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "formulas.xlsx");
        await WriteFormulaWorkbookAsync(source);
        var markdown = Path.Combine(root, "formulas.md");
        var workspace = Path.Combine(root, "formulas.drmd");
        var output = Path.Combine(root, "restored.xlsx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        var lines = SplitLines(projection);
        var rowIndex = FindRowIndex(lines, "Label");
        // C1 is formatted as a percentage, so the projection shows the raw value backtick-wrapped
        // next to its formatted display text (e.g. "`0.42` → 42.00%"); only the raw, code-span part
        // is what the editor reads back (DecodeSpreadsheetCell), so editing it is how a percentage
        // cell's actual value is changed.
        Assert.Contains("0.42", lines[rowIndex], StringComparison.Ordinal);
        lines[rowIndex] = ReplaceCellContaining(lines[rowIndex], "0.42", "`0.5`");
        await File.WriteAllTextAsync(markdown, string.Join('\n', lines));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        using var extraction = File.OpenRead(output);
        var reextraction = new XlsxAdapter().Extract(extraction);
        var c1 = Assert.Single(reextraction.Graph.Nodes, node => node.Kind == NodeKind.Cell && Address(node) == "C1");
        Assert.Equal("0.5", ((TextNodeContent)c1.Content).Text);
    }

    // ---------------------------------------------------------------------------------------
    // 6. Combined edit + unrelated block untouched (DOCX F1 slice-preservation)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_editing_one_paragraph_leaves_an_unrelated_paragraphs_raw_slice_byte_identical()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "two-paragraphs.docx");
        await WriteTwoParagraphDocxAsync(source);
        var markdown = Path.Combine(root, "two-paragraphs.md");
        var workspace = Path.Combine(root, "two-paragraphs.drmd");
        var output = Path.Combine(root, "restored.docx");
        var service = new DocumentService();

        await service.ExportAsync(new DocumentExportOptions(source, workspace, markdown));
        var projection = await File.ReadAllTextAsync(markdown);
        Assert.Contains("First paragraph.", projection, StringComparison.Ordinal);
        Assert.Contains("Second paragraph, left completely alone.", projection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(markdown, projection.Replace("First paragraph.", "First paragraph, edited.", StringComparison.Ordinal));

        var restored = await service.RestoreAsync(new DocumentRestoreOptions(workspace, output, markdown));

        Assert.True(restored.Succeeded);
        Assert.Equal(FidelityLevel.F1, restored.Fidelity);
        var sourceXml = ReadDocumentXml(source);
        var patchedXml = ReadDocumentXml(output);
        // DOCX F1 is "slice-preserving" (Preservation.F1Target): the exact raw XML for the
        // untouched paragraph is spliced through byte-for-byte, not merely re-serialized to the
        // same text.
        const string unrelatedSlice = "<w:p><w:r><w:t>Second paragraph, left completely alone.</w:t></w:r></w:p>";
        Assert.Contains(unrelatedSlice, sourceXml, StringComparison.Ordinal);
        Assert.Contains(unrelatedSlice, patchedXml, StringComparison.Ordinal);
        Assert.Contains("First paragraph, edited.", patchedXml, StringComparison.Ordinal);
        Assert.DoesNotContain("First paragraph.<", patchedXml, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------

    private static async Task WriteEquationDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><w:body>
            <w:p><w:r><w:t xml:space="preserve">Given </w:t></w:r><m:oMath><m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath><w:r><w:t xml:space="preserve"> holds.</w:t></w:r></w:p>
            <w:p><w:r><w:t>Untouched trailer paragraph.</w:t></w:r></w:p>
            </w:body></w:document>
            """);
    }

    private static async Task WriteInlineControlInTableDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc><w:p><w:r><w:t xml:space="preserve">Pick </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val="Option" /><w:tag w:val="opt" /><w:id w:val="42" /></w:sdtPr><w:sdtContent><w:r><w:t>Choice</w:t></w:r></w:sdtContent></w:sdt><w:r><w:t xml:space="preserve"> here</w:t></w:r></w:p></w:tc>
                <w:tc><w:p><w:r><w:t>Other cell</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            <w:p><w:r><w:t>Tail</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
    }


    [Theory]
    [InlineData("Outer label")]
    [InlineData("Inner label")]
    public async Task Docx_single_table_cell_edit_leaves_all_other_xml_bytes_identical(string originalText)
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "nested.docx");
        await WriteNestedTableDocxAsync(source);
        var adapter = new DocxAdapter();
        var baseline = await adapter.ExtractAsync(source);
        var serializer = new DocRedock.Markdown.DocRedockMarkdownSerializer();
        var markdown = serializer.Serialize(baseline.Graph).Markdown;
        var edit = new MarkdownGraphEditor().Apply(baseline.Graph,
            markdown.Replace(originalText, originalText + " changed", StringComparison.Ordinal));
        Assert.True(edit.IsValid);
        Assert.DoesNotContain(edit.Diagnostics, item => item.Code == "ProtectedNodeEdit");
        var output = Path.Combine(root, "changed.docx");
        var restored = await adapter.RestoreAsync(baseline, edit.EditedGraph, output);
        Assert.True(restored.Succeeded);
        Assert.Equal(RemoveEditedParagraph(ReadDocumentXml(source), originalText),
            RemoveEditedParagraph(ReadDocumentXml(output), originalText + " changed"));
        var reexport = await adapter.ExtractAsync(output);
        Assert.Contains(reexport.Graph.Nodes, node => node.Content is TableNodeContent table &&
            table.Rows.SelectMany(row => row).Any(cell => cell.Text == originalText + " changed"));

        static string RemoveEditedParagraph(string xml, string text)
        {
            var textIndex = xml.IndexOf(text, StringComparison.Ordinal);
            Assert.True(textIndex >= 0);
            var start = xml.LastIndexOf("<w:p", textIndex, StringComparison.Ordinal);
            var end = xml.IndexOf("</w:p>", textIndex, StringComparison.Ordinal) + "</w:p>".Length;
            return xml.Remove(start, end - start);
        }
    }

    [Fact]
    public async Task Docx_outer_table_delete_with_nested_cell_edit_is_rejected_without_output()
    {
        var root = TempDirectory();
        var source = Path.Combine(root, "nested.docx");
        await WriteNestedTableDocxAsync(source);
        var adapter = new DocxAdapter();
        var baseline = await adapter.ExtractAsync(source);
        var tables = baseline.Graph.Nodes.Where(node => node.Kind == NodeKind.Table).OrderBy(node => node.Order).ToArray();
        var edited = new MarkdownGraphEditor().Apply(baseline.Graph,
            new DocRedock.Markdown.DocRedockMarkdownSerializer().Serialize(baseline.Graph).Markdown
                .Replace("Inner label", "Inner changed", StringComparison.Ordinal));
        var output = Path.Combine(root, "rejected.docx");
        var graphWithDelete = edited.EditedGraph with
        {
            Partitions = edited.EditedGraph.Partitions.Select(partition => partition with
            {
                Nodes = partition.Nodes.Where(node => node.Id != tables[0].Id).ToArray()
            }).ToArray()
        };
        var result = await adapter.RestoreAsync(baseline, graphWithDelete, output,
            new DocRedock.Core.Diff.DiffOptions(new HashSet<string> { tables[0].Id }));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "OverlappingEdits");
        Assert.False(File.Exists(output));
    }

    private static async Task WriteNestedTableDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:tbl>
              <w:tr>
                <w:tc>
                  <w:p><w:r><w:t>Outer label</w:t></w:r></w:p>
                  <w:tbl>
                    <w:tr>
                      <w:tc><w:tcPr><w:gridSpan w:val="2"/><w:shd w:fill="EEEEEE"/></w:tcPr><w:p><w:r><w:t>Inner label</w:t></w:r></w:p></w:tc>
                      <w:tc><w:p><w:r><w:t>Inner value</w:t></w:r></w:p></w:tc>
                    </w:tr>
                  </w:tbl>
                  <w:p />
                </w:tc>
                <w:tc><w:p><w:r><w:t>Right</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            <w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr />
            </w:body></w:document>
            """);
    }

    private static async Task WriteTwoParagraphDocxAsync(string path)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
        await WriteEntry(zip, "word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:r><w:t>First paragraph.</w:t></w:r></w:p>
            <w:p><w:r><w:t>Second paragraph, left completely alone.</w:t></w:r></w:p>
            </w:body></w:document>
            """);
    }

    private static async Task WriteThreeSlidePptxAsync(string path)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """,
            ["ppt/presentation.xml"] = """
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId1"/><p:sldId id="257" r:id="rId2"/><p:sldId id="258" r:id="rId3"/></p:sldIdLst>
                </p:presentation>
                """,
            ["ppt/_rels/presentation.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/>
                  <Relationship Id="rId2" Type="slide" Target="slides/slide2.xml"/>
                  <Relationship Id="rId3" Type="slide" Target="slides/slide3.xml"/>
                </Relationships>
                """,
            ["ppt/slides/slide1.xml"] = PptxSlideXml("Slide One Title", ["First bullet", "Second bullet"]),
            ["ppt/slides/slide2.xml"] = PptxSlideXml("Slide Two Title", ["Untouched bullet"]),
            ["ppt/slides/slide3.xml"] = PptxSlideXml("Slide Three Title", ["Tail first bullet", "Tail second bullet"]),
            ["ppt/slides/_rels/slide1.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdNotes" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
                </Relationships>
                """,
            ["ppt/notesSlides/notesSlide1.xml"] = """
                <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                         xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id="3" name="Notes"/>
                  <p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Slide one notes.</a:t></a:r></a:p></p:txBody>
                  </p:sp></p:spTree></p:cSld>
                </p:notes>
                """,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var part in parts) await WriteEntry(zip, part.Key, part.Value);
    }

    private static string PptxSlideXml(string title, string[] bodyLines) => $$"""
        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
               xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <p:cSld><p:spTree>
            <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:txBody><a:bodyPr/><a:p><a:r><a:t>{{title}}</a:t></a:r></a:p></p:txBody></p:sp>
            <p:sp><p:nvSpPr><p:cNvPr id="3" name="Body"/><p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr>
              <p:txBody><a:bodyPr/>{{string.Concat(bodyLines.Select(line => $"<a:p><a:r><a:t>{line}</a:t></a:r></a:p>"))}}</p:txBody></p:sp>
          </p:spTree></p:cSld>
        </p:sld>
        """;

    private static async Task WriteFormulaWorkbookAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
        await WriteEntry(zip, "_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        await WriteEntry(zip, "xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        await WriteEntry(zip, "xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        await WriteEntry(zip, "xl/sharedStrings.xml", """
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Label</t></si></sst>
            """);
        await WriteEntry(zip, "xl/styles.xml", """
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="1"><font><sz val="11"/></font></fonts>
              <cellXfs count="2"><xf numFmtId="0" fontId="0"/><xf numFmtId="10" fontId="0"/></cellXfs>
            </styleSheet>
            """);
        await WriteEntry(zip, "xl/calcChain.xml", """
            <calcChain xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><c r="B3" i="1"/></calcChain>
            """);
        await WriteEntry(zip, "xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>2</v></c><c r="C1" s="1"><v>0.42</v></c></row>
              <row r="2"><c r="B2"><v>3</v></c></row>
              <row r="3"><c r="B3"><f>SUM(B1:B2)</f><v>5</v></c></row>
            </sheetData></worksheet>
            """);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static async Task WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await stream.WriteAsync(text);
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int FindRowIndex(string[] lines, string mustContain)
    {
        var index = Array.FindIndex(lines, line => line.Contains(mustContain, StringComparison.Ordinal));
        Assert.True(index >= 0, $"No line containing '{mustContain}' was found in:\n{string.Join('\n', lines)}");
        return index;
    }

    /// <summary>Replaces the whole line by an exact whole-line match; used for plain DOCX table
    /// rows built from unescaped text (no '|' splitting needed since the row has no ambiguity).</summary>
    private static string ReplaceCellExact(string line, string oldWhole, string newWhole)
    {
        Assert.Contains(oldWhole, line, StringComparison.Ordinal);
        return line.Replace(oldWhole, newWhole, StringComparison.Ordinal);
    }

    /// <summary>Splits a "| a | b | c |" row on '|' and replaces the one cell whose trimmed text
    /// contains <paramref name="mustContain"/>, independent of the exact escaping/decoration
    /// (formula arrow, backtick wrapper, ...) DocRedockMarkdown gave the rest of that cell.</summary>
    private static string ReplaceCellContaining(string row, string mustContain, string newValue)
    {
        var cells = row.Split('|');
        for (var index = 0; index < cells.Length; index++)
        {
            if (!cells[index].Contains(mustContain, StringComparison.Ordinal)) continue;
            cells[index] = " " + newValue + " ";
            return string.Join('|', cells);
        }
        throw new InvalidOperationException($"No cell containing '{mustContain}' was found in row: {row}");
    }

    private static string FlatText(NodeContent content) => content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => string.Concat(rich.Runs.Select(run => run.Text)),
        _ => string.Empty,
    };

    private static string? Address(DocumentNode node) =>
        node.Source?.Locators.FirstOrDefault(locator => locator.Kind == "cell_address")?.Value?.ToString();

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; }
        return count;
    }

    private static byte[] ReadZipEntryBytes(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Entry '{entryName}' not found in '{path}'.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadDocumentXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-complex-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using System.Text;

namespace DocRedock.Tests.Api;

public sealed class BuiltInAdapterCatalogTests
{
    [Fact]
    public async Task Pdf_adapter_projects_vector_visual_graphs_into_page_partitions()
    {
        var path = Path.Combine(Path.GetTempPath(), "docredock-catalog-vector-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length 145 >> stream\nBT 1 0 0 1 0 0 Tm (Start) Tj 100 100 Td (End) Tj ET\n0 0 20 20 re 100 100 20 20 re 0 0 m 100 100 l S\nendstream\n%%EOF"));
            var adapter = BuiltInAdapterCatalog.CreateRegistry().Find(DocumentFormatKind.Pdf)!;

            var extraction = await adapter.ExtractAsync(new AdapterInput(path, Path.GetFileName(path), DocumentFormatKind.Pdf));

            Assert.Contains(extraction.Graph.Nodes, node => node.Kind == NodeKind.Diagram && node.Extensions?.ContainsKey("visual_graph") == true);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Registers_all_builtin_formats_explicitly_with_versioned_capabilities()
    {
        var registry = BuiltInAdapterCatalog.CreateRegistry();

        Assert.Equal(4, registry.ListAdapters().Count);
        foreach (var format in new[] { DocumentFormatKind.Docx, DocumentFormatKind.Xlsx, DocumentFormatKind.Pptx, DocumentFormatKind.Pdf })
        {
            var adapter = registry.Find(format);
            Assert.NotNull(adapter);
            Assert.Equal(1, adapter.Descriptor.InterfaceVersion);
            Assert.True(adapter.Descriptor.IsBuiltIn);
            Assert.Contains("restore.byte_identical", adapter.Descriptor.Capabilities);
        }
    }

    [Fact]
    public void Adapter_warning_helper_promotes_stable_visual_codes_only()
    {
        var visual = AdapterWarningDiagnostics.Create("PptxWarning", "VisualConnectorUnresolved: connector endpoints are ambiguous");
        var ordinary = AdapterWarningDiagnostics.Create("PptxWarning", "Embedded chart was preserved as passthrough");

        Assert.Equal("VisualConnectorUnresolved", visual.Code);
        Assert.Equal("connector endpoints are ambiguous", visual.Message);
        Assert.Equal(DiagnosticSeverity.Warning, visual.Severity);
        Assert.Equal("PptxWarning", ordinary.Code);
        Assert.Equal("Embedded chart was preserved as passthrough", ordinary.Message);

        var repeated = AdapterWarningDiagnostics.Normalize(
        [
            new Diagnostic("Repeated", "same diagnostic", DiagnosticSeverity.Warning, "node-1", "/xl/worksheets/a.xml"),
            new Diagnostic("Repeated", "same  diagnostic", DiagnosticSeverity.Warning, "node-1", "/xl/worksheets/a.xml"),
            new Diagnostic("Repeated", "same diagnostic", DiagnosticSeverity.Warning, "node-2", "/xl/worksheets/a.xml"),
            new Diagnostic("Repeated", "same diagnostic", DiagnosticSeverity.Warning, "node-1", "/xl/worksheets/b.xml"),
        ]);
        var external = AdapterWarningDiagnostics.Create("OfficeWarning",
            "ExternalRelationshipPresent: External relationships are recorded but never fetched.");

        var mixedSeverity = AdapterWarningDiagnostics.Normalize(
        [
            new Diagnostic("Escalated", "same event", DiagnosticSeverity.Information, "node-1", "/xl/worksheets/a.xml"),
            new Diagnostic("Escalated", "same  event", DiagnosticSeverity.Error, "node-1", "/xl/worksheets/a.xml"),
        ]);

        Assert.Equal(3, repeated.Count);
        Assert.Equal("same diagnostic (repeated 2 times).", repeated[0].Message);
        Assert.Equal("node-2", repeated[1].NodeId);
        Assert.Equal("/xl/worksheets/b.xml", repeated[2].PartUri);
        Assert.Equal(2, mixedSeverity.Count);
        Assert.Contains(mixedSeverity, diagnostic => diagnostic.Severity == DiagnosticSeverity.Information);
        Assert.Contains(mixedSeverity, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(DiagnosticSeverity.Information, external.Severity);

        var summaries = AdapterWarningDiagnostics.SummarizeForDisplay(repeated);
        var repeatedSummary = Assert.Single(summaries, summary => summary.Code == "Repeated");
        Assert.Equal(3, repeatedSummary.Count);
        Assert.Equal(DiagnosticSeverity.Warning, repeatedSummary.Severity);
    }
}

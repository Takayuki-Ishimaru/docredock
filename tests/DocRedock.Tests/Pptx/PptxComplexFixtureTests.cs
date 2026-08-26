using System.IO.Compression;
using System.Security.Cryptography;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.OpenXml.Pptx;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// Exercises a multi-slide artifact-tool-generated PPTX through both Markdown
/// projection and DRMD restore, without letting protected native objects become
/// editable text.
/// </summary>
public sealed class PptxComplexFixtureTests
{
    [Fact]
    public async Task Complex_fixture_round_trips_markdown_and_drmd_while_preserving_protected_parts()
    {
        var fixture = FindFixture();
        var original = File.ReadAllBytes(fixture);
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));

        Assert.Equal(4, extraction.Slides.Count);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == NodeKind.Table && node.Editability == NodeEditability.Protected);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == NodeKind.Image && node.Editability == NodeEditability.Protected);
        Assert.Contains(extraction.Graph.Nodes, node => node.Kind == NodeKind.Chart && node.Editability == NodeEditability.Protected);
        Assert.Equal(3, extraction.Graph.Nodes.Count(node => node.Kind == NodeKind.Connector && node.Editability == NodeEditability.Protected));
        Assert.Equal(4, extraction.Graph.Nodes.Count(node => node.Kind == NodeKind.SpeakerNotes));
        Assert.Contains(extraction.Graph.Nodes, node => node.Content is RichTextNodeContent);

        var root = Path.Combine(Path.GetTempPath(), "docredock-pptx-complex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "complex.pptx");
        var markdown = Path.Combine(root, "complex.md");
        var sidecar = Path.Combine(root, "complex.drmd");
        var f0 = Path.Combine(root, "complex-f0.pptx");
        var f1 = Path.Combine(root, "complex-f1.pptx");
        File.WriteAllBytes(source, original);
        var service = new DocumentService();

        var exported = await service.ExportAsync(new DocumentExportOptions(source, sidecar, markdown));
        var projection = await File.ReadAllTextAsync(markdown);

        Assert.Contains("Project Atlas: conversion acceptance", projection, StringComparison.Ordinal);
        Assert.Contains("日本語・English", projection, StringComparison.Ordinal);
        Assert.Contains("native tables, charts, pictures, and connectors", projection, StringComparison.Ordinal);
        Assert.Contains("MD projection keeps semantic titles and bullets.", projection, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sidecar, "graph", "index.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sidecar, "assets"), "img-*.png"));
        Assert.Equal(4, exported.Graph.Partitions.Count);

        var unchanged = await service.RestoreAsync(new DocumentRestoreOptions(sidecar, f0, markdown));
        Assert.Equal(FidelityLevel.F0, unchanged.Fidelity);
        Assert.Equal(Hash(original), Hash(File.ReadAllBytes(f0)));

        await File.WriteAllTextAsync(markdown, projection.Replace(
            "Project Atlas: conversion acceptance",
            "Project Harbor: conversion acceptance",
            StringComparison.Ordinal));
        var changed = await service.RestoreAsync(new DocumentRestoreOptions(sidecar, f1, markdown));
        Assert.True(changed.Succeeded, string.Join(" | ", changed.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var restored = File.ReadAllBytes(f1);
        var before = Entries(original);
        var after = Entries(restored);

        Assert.Equal(FidelityLevel.F1, changed.Fidelity);
        Assert.Contains("Project Harbor: conversion acceptance",
            adapter.Extract(new MemoryStream(restored)).Slides[0].Shapes.Select(shape => shape.Text));
        Assert.Equal(before["ppt/media/image.png"], after["ppt/media/image.png"]);
        // The native chart is on slide 3, while the F1 edit is on slide 1.
        // Whole-slide payload equality proves that its chart relationship and layout stay untouched.
        Assert.Equal(before["ppt/slides/slide3.xml"], after["ppt/slides/slide3.xml"]);
        Assert.Equal(before["ppt/notesSlides/notesSlide1.xml"], after["ppt/notesSlides/notesSlide1.xml"]);
        Assert.Equal(before["ppt/theme/theme1.xml"], after["ppt/theme/theme1.xml"]);
    }

    private static string FindFixture()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx",
                "complex-markdown-roundtrip.original.pptx");
            if (File.Exists(path)) return path;
            current = current.Parent;
        }

        var working = Path.GetFullPath("tests/DocRedock.Tests/Fixtures/Pptx/complex-markdown-roundtrip.original.pptx");
        Assert.True(File.Exists(working), "Generate the PPTX corpus with tests/DocRedock.Tests/Fixtures/Pptx/generate-complex-pptx.mjs.");
        return working;
    }

    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                using var output = new MemoryStream();
                stream.CopyTo(output);
                return output.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

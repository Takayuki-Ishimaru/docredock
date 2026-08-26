using System.IO.Compression;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Pptx;

namespace DocRedock.Tests.Pptx;

/// <summary>
/// Regression coverage for the visually reviewed Project Atlas presentation.
/// It deliberately uses the same real deck as the release artefact, so native
/// tables, connectors, charts, notes, masters, layouts, pictures and footers
/// remain in the normal extraction and F1-preservation path.
/// </summary>
public sealed class PptxRealCorpusTests
{
    [Fact]
    public void Real_office_corpus_extracts_visual_objects_and_keeps_f0()
    {
        var source = FindRealCorpus();
        var original = File.ReadAllBytes(source);
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var nodes = extraction.Graph.Nodes.ToArray();

        Assert.Equal(4, extraction.Slides.Count);
        Assert.Contains(nodes, node => node.Kind == NodeKind.Table && node.Editability == NodeEditability.Protected);
        Assert.Contains(nodes, node => node.Kind == NodeKind.Image && node.Editability == NodeEditability.Protected);
        Assert.Contains(nodes, node => node.Kind == NodeKind.Chart && node.Editability == NodeEditability.Protected);
        Assert.Contains(nodes, node => node.Kind == NodeKind.Connector && node.Editability == NodeEditability.Protected);
        Assert.Equal(4, nodes.Count(node => node.Kind == NodeKind.SpeakerNotes && node.Layer == ContentLayer.Metadata));
        Assert.Equal(4, nodes.Count(node => node.Layer == ContentLayer.Furniture && node.Kind == NodeKind.Shape));
        Assert.All(nodes.Where(node => node.Kind is NodeKind.Chart or NodeKind.Connector), node => Assert.Equal(ContentLayer.Body, node.Layer));
        Assert.All(nodes.Where(node => node.Layer == ContentLayer.Hidden), node => Assert.True(node.Kind == NodeKind.Shape && string.IsNullOrWhiteSpace(Assert.IsType<TextNodeContent>(node.Content).Text)));
        Assert.Equal("ppt/media/image.png", Assert.IsType<ReferenceNodeContent>(nodes.Single(node => node.Kind == NodeKind.Image).Content).Reference);
        Assert.Contains("[Sources]", Assert.IsType<TextNodeContent>(nodes.First(node => node.Kind == NodeKind.SpeakerNotes).Content).Text);
        Assert.All(extraction.Slides, slide => Assert.Contains(slide.Shapes, shape => shape.Role == "title"));

        var restored = adapter.Restore(new MemoryStream(original), adapter.CreatePatchPlan(Array.Empty<PptxShapeTextEdit>()));
        Assert.True(restored.IsByteIdentical);
        Assert.Equal(original, restored.Bytes);
    }

    [Fact]
    public void Real_office_corpus_title_patch_preserves_chart_notes_image_and_template_parts()
    {
        var original = File.ReadAllBytes(FindRealCorpus());
        var adapter = new PptxAdapter();
        var extraction = adapter.Extract(new MemoryStream(original));
        var title = extraction.Slides[0].Shapes.Single(shape => shape.Role == "title");

        var restored = adapter.Restore(new MemoryStream(original),
            adapter.CreatePatchPlan([new PptxShapeTextEdit("slide1", title.ShapeId, "Project Atlas release readiness")])).Bytes;
        var before = Entries(original); var after = Entries(restored);
        Assert.Equal(before["ppt/slides/charts/chart1.xml"], after["ppt/slides/charts/chart1.xml"]);
        Assert.Equal(before["ppt/notesSlides/notesSlide1.xml"], after["ppt/notesSlides/notesSlide1.xml"]);
        Assert.Equal(before["ppt/media/image.png"], after["ppt/media/image.png"]);
        Assert.Equal(before["ppt/slideMasters/slideMaster1.xml"], after["ppt/slideMasters/slideMaster1.xml"]);
        Assert.Equal(before["ppt/slideLayouts/slideLayout1.xml"], after["ppt/slideLayouts/slideLayout1.xml"]);
        Assert.Equal(before["ppt/theme/theme1.xml"], after["ppt/theme/theme1.xml"]);
        Assert.Equal("Project Atlas release readiness",
            adapter.Extract(new MemoryStream(restored)).Slides[0].Shapes.Single(shape => shape.ShapeId == title.ShapeId).Text);
    }

    private static string FindRealCorpus()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var fixture = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pptx", "real-office-roundtrip.original.pptx");
            if (File.Exists(fixture)) return fixture;
            current = current.Parent;
        }

        var working = Path.GetFullPath("tests/DocRedock.Tests/Fixtures/Pptx/real-office-roundtrip.original.pptx");
        Assert.True(File.Exists(working), "The checked-in PPTX corpus is missing: tests/DocRedock.Tests/Fixtures/Pptx/real-office-roundtrip.original.pptx");
        return working;
    }

    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var zip = new ZipArchive(input);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            using (var source = entry.Open())
            using (var output = new MemoryStream())
            {
                source.CopyTo(output);
                result[entry.FullName] = output.ToArray();
            }
        return result;
    }
}

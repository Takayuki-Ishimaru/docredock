using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Api;

public sealed class BuiltInAdapterCatalogTests
{
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
}

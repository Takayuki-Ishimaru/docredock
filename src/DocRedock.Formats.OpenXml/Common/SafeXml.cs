using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DocRedock.Formats.OpenXml.Common;

internal static class SafeXml
{
    public static XDocument LoadDocument(ReadOnlyMemory<byte> xml)
    {
        using var stream = new MemoryStream(xml.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = false
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);
}

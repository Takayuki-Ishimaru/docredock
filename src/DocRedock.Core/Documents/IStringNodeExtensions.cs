using System.Diagnostics.CodeAnalysis;

namespace DocRedock.Core.Documents;

/// <summary>Node extensions that keep their string values as strings: a worksheet cell's properties, read for each of
/// hundreds of thousands of cells. A reader that wants a string gets it without a JSON value being made for it; it is
/// the string the extension's JSON value holds.</summary>
public interface IStringNodeExtensions
{
    /// <summary>The string a key holds; false when the key is absent or holds no string.</summary>
    bool TryGetString(string key, [NotNullWhen(true)] out string? value);
}

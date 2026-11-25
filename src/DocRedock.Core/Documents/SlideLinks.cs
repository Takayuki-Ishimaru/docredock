using System.Globalization;

namespace DocRedock.Core.Documents;

/// <summary>The link target of a jump to another slide of the same presentation: a fragment that names the
/// slide by its position, counting hidden slides. A writer turns it into the anchor of that slide's section
/// when the output has one, and leaves the label plain when it has not.</summary>
public static class SlideLinks
{
    private const string Prefix = "#docredock-slide-";

    /// <summary>The anchor id of slide <paramref name="number"/> (from 1), without the leading '#'.</summary>
    public static string Id(int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
        return string.Concat(Prefix.AsSpan(1), number.ToString(CultureInfo.InvariantCulture));
    }

    public static string Target(int number) => "#" + Id(number);

    public static bool TryParse(string? target, out int number)
    {
        number = 0;
        return target is not null && target.StartsWith(Prefix, StringComparison.Ordinal) && target.Length <= Prefix.Length + 9 &&
            target.AsSpan(Prefix.Length) is { Length: > 0 } digits && digits[0] != '0' && !digits.ContainsAnyExceptInRange('0', '9') &&
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}

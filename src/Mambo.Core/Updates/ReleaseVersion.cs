using System.Globalization;

namespace Mambo.Core.Updates;

internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch, bool Prerelease) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith('v')) text = text[1..];
        var withoutMetadata = text.Split('+')[0];
        var parts = withoutMetadata.Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(static part => part.Length == 0 ||
            (part.Length > 1 && part[0] == '0') || part.Any(static c => c is < '0' or > '9'))) return false;
        if (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))) return false;
        if (!int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        version = new(major, minor, patch, parts.Length == 2);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result == 0) result = other.Prerelease.CompareTo(Prerelease);
        return result;
    }
}

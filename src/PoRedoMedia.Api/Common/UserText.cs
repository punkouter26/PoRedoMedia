namespace PoRedoMedia.Api.Common;

/// <summary>Cleans a line of text a user or a model supplied before it is stored or drawn.</summary>
public static class UserText
{
    /// <summary>Control characters removed, trimmed, cut to <paramref name="maxLength"/>. Null when nothing is left.</summary>
    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (clean.Length > maxLength)
            clean = clean[..maxLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }
}

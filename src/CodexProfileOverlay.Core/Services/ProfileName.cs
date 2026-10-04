namespace CodexProfileOverlay.Core.Services;

public static class ProfileName
{
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (!string.Equals(name, trimmed, StringComparison.Ordinal) || trimmed is "." or ".."
            || trimmed.Length > 255 || trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        string stem = trimmed.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
        {
            return false;
        }

        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !trimmed.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !trimmed.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string RequireValid(string name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("Profile name is invalid.", nameof(name));
        }

        return name;
    }
}

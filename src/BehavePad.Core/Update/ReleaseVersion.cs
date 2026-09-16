namespace BehavePad.Core.Update;

/// <summary>Reads the "v1.2.0" tags BehavePad releases use, and compares them without tripping over unset parts.</summary>
public static class ReleaseVersion
{
    /// <summary>Reads a release tag. Accepts "v1.2.0", "1.2.0" and "1.2.0.4".</summary>
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Trims a version to major.minor.patch. Version counts an unset part as -1, so the 1.2.0 from a release tag
    /// would otherwise read as older than the 1.2.0.0 an assembly reports, and BehavePad would update forever.
    /// </summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    public static bool IsNewerThan(Version candidate, Version current) => Normalize(candidate) > Normalize(current);
}

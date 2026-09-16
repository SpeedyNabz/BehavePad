namespace BehavePad.Core.Update;

public enum UpdateStatus
{
    /// <summary>This copy is current, or the newest release is a draft or pre-release.</summary>
    UpToDate,

    /// <summary>A newer release is published, with a file BehavePad is willing to run.</summary>
    Available,

    /// <summary>A newer release exists, but BehavePad will not install it on its own.</summary>
    Unusable,
}

/// <summary>A release BehavePad is willing to download, with the checksum every byte must match.</summary>
public sealed record UpdateCandidate(
    Version Version,
    string TagName,
    Uri DownloadUrl,
    long Size,
    string Sha256,
    Uri? ReleaseUrl,
    string? Notes)
{
    public string VersionText => Version.ToString(3);

    public string FileName => $"BehavePad-{VersionText}.exe";
}

public sealed record UpdateDecision(UpdateStatus Status, UpdateCandidate? Candidate, string? Reason, Uri? ReleaseUrl = null);

/// <summary>
/// Decides whether a GitHub release should replace this copy of BehavePad. It is deliberately strict, because an
/// update is an executable BehavePad runs: anything unexpected sends the user to the release page instead.
/// </summary>
public static class UpdateCheck
{
    public const string Owner = "SpeedyNabz";
    public const string Repository = "BehavePad";

    /// <summary>The single-file build attached to every release.</summary>
    public const string AssetName = "BehavePad.exe";

    /// <summary>Well past the size of a self-contained build, so a wrong or hostile asset is refused before download.</summary>
    public const long MaxAssetSize = 400L * 1024 * 1024;

    public static Uri ReleasesPage { get; } = new($"https://github.com/{Owner}/{Repository}/releases/latest");

    public static UpdateDecision Evaluate(GitHubRelease? release, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (release is null)
        {
            return new UpdateDecision(UpdateStatus.Unusable, null, "GitHub has no published release.");
        }

        // Drafts and pre-releases are never installed on their own.
        if (release.Draft || release.Prerelease)
        {
            return new UpdateDecision(UpdateStatus.UpToDate, null, null, release.HtmlUrl);
        }

        if (!ReleaseVersion.TryParse(release.TagName, out var version))
        {
            return Unusable($"BehavePad could not read the release tag \"{release.TagName}\".");
        }

        if (!ReleaseVersion.IsNewerThan(version, current))
        {
            return new UpdateDecision(UpdateStatus.UpToDate, null, null, release.HtmlUrl);
        }

        if (release.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase)) is not { } asset)
        {
            return Unusable($"Release {release.TagName} has no {AssetName} to install.");
        }

        if (asset.DownloadUrl is not { } url || !IsTrusted(url))
        {
            return Unusable($"Release {release.TagName} is not hosted on GitHub, so BehavePad left it alone.");
        }

        if (asset.Size <= 0 || asset.Size > MaxAssetSize)
        {
            return Unusable($"The {AssetName} in release {release.TagName} is an unexpected size.");
        }

        if (!TryReadSha256(asset.Digest, out var sha256))
        {
            return Unusable($"Release {release.TagName} has no checksum, so BehavePad will not install it on its own.");
        }

        var notes = release.Body?.Trim();
        var candidate = new UpdateCandidate(
            version,
            release.TagName,
            url,
            asset.Size,
            sha256,
            release.HtmlUrl,
            string.IsNullOrEmpty(notes) ? null : notes);
        return new UpdateDecision(UpdateStatus.Available, candidate, null, release.HtmlUrl);

        UpdateDecision Unusable(string reason) => new(UpdateStatus.Unusable, null, reason, release.HtmlUrl);
    }

    /// <summary>Reads GitHub's "sha256:1a2b..." asset digest.</summary>
    public static bool TryReadSha256(string? digest, out string sha256)
    {
        const string prefix = "sha256:";
        sha256 = "";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = digest[prefix.Length..].Trim();
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        sha256 = value.ToLowerInvariant();
        return true;
    }

    /// <summary>Only GitHub's own hosts, and only over TLS.</summary>
    public static bool IsTrusted(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == Uri.UriSchemeHttps
               && (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                   || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }
}

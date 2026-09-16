using System.Text.Json.Serialization;

namespace BehavePad.Core.Update;

/// <summary>One file attached to a GitHub release.</summary>
public sealed record ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public Uri? DownloadUrl { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>GitHub's own checksum, like "sha256:1a2b...". BehavePad refuses to install an asset without one.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

/// <summary>A release as the GitHub API reports it.</summary>
public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("html_url")]
    public Uri? HtmlUrl { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

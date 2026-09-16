using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BehavePad.Core.Storage;

namespace BehavePad.Core.Update;

/// <summary>Reads the newest published release from the GitHub API.</summary>
public sealed class GitHubReleaseClient(HttpClient http, string owner = UpdateCheck.Owner, string repository = UpdateCheck.Repository)
{
    /// <summary>The releases/latest endpoint, which skips drafts and pre-releases on GitHub's side.</summary>
    public Uri LatestReleaseUrl { get; } = new($"https://api.github.com/repos/{owner}/{repository}/releases/latest");

    /// <summary>Returns the latest release, or null when the repository has never published one.</summary>
    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GitHubRelease>(json, BehavePadJson.Options);
    }
}

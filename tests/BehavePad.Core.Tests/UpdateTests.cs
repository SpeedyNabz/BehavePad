using System.Net;
using System.Security.Cryptography;
using BehavePad.Core.Update;

namespace BehavePad.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly byte[] BuildBytes = Enumerable.Range(0, 150_000).Select(i => (byte)(i * 17 % 253)).ToArray();
    private static readonly Version Current = new(1, 2, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "BehavePadTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData("v1.3.0", 1, 3, 0)]
    [InlineData("1.3.0", 1, 3, 0)]
    [InlineData("V2.0.1", 2, 0, 1)]
    [InlineData("v1.3.0.4", 1, 3, 0)]
    [InlineData(" v1.3.0 ", 1, 3, 0)]
    public void Release_tags_are_read_with_or_without_the_v(string tag, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(tag, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("v")]
    public void Tags_that_are_not_versions_are_refused(string? tag)
    {
        Assert.False(ReleaseVersion.TryParse(tag, out _));
    }

    [Fact]
    public void A_tag_equal_to_the_running_build_is_not_newer_despite_the_unset_fourth_part()
    {
        // An assembly reports 1.2.0.0 while the tag says 1.2.0, and Version counts an unset part as -1.
        Assert.True(ReleaseVersion.TryParse("v1.2.0", out var tag));
        Assert.False(ReleaseVersion.IsNewerThan(tag, new Version(1, 2, 0, 0)));
        Assert.True(ReleaseVersion.IsNewerThan(new Version(1, 2, 1), new Version(1, 2, 0, 0)));
    }

    [Fact]
    public void A_newer_release_with_a_checksum_is_offered()
    {
        var decision = UpdateCheck.Evaluate(Release("v1.3.0", Asset()), Current);

        Assert.Equal(UpdateStatus.Available, decision.Status);
        Assert.NotNull(decision.Candidate);
        Assert.Equal("1.3.0", decision.Candidate!.VersionText);
        Assert.Equal("BehavePad-1.3.0.exe", decision.Candidate.FileName);
        Assert.Equal(Sha256(BuildBytes), decision.Candidate.Sha256);
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("v1.1.9")]
    public void The_same_or_an_older_release_leaves_this_build_alone(string tag)
    {
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate(Release(tag, Asset()), Current).Status);
    }

    [Fact]
    public void Drafts_and_pre_releases_are_never_installed()
    {
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate(Release("v1.3.0", Asset(), draft: true), Current).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate(Release("v1.3.0", Asset(), prerelease: true), Current).Status);
    }

    [Fact]
    public void A_release_without_a_checksum_is_left_for_the_user_to_install()
    {
        var decision = UpdateCheck.Evaluate(Release("v1.3.0", Asset(digest: null)), Current);

        Assert.Equal(UpdateStatus.Unusable, decision.Status);
        Assert.Null(decision.Candidate);
        Assert.Contains("checksum", decision.Reason);
    }

    [Theory]
    [InlineData("md5:0123456789abcdef")]
    [InlineData("sha256:nothexadecimal")]
    [InlineData("sha256:abc")]
    public void Only_a_full_sha256_digest_counts(string digest)
    {
        Assert.False(UpdateCheck.TryReadSha256(digest, out _));
        Assert.Equal(UpdateStatus.Unusable, UpdateCheck.Evaluate(Release("v1.3.0", Asset(digest: digest)), Current).Status);
    }

    [Fact]
    public void A_release_hosted_somewhere_other_than_github_is_refused()
    {
        var asset = Asset() with { DownloadUrl = new Uri("https://example.invalid/BehavePad.exe") };

        Assert.Equal(UpdateStatus.Unusable, UpdateCheck.Evaluate(Release("v1.3.0", asset), Current).Status);
        Assert.False(UpdateCheck.IsTrusted(new Uri("http://github.com/x")));
        Assert.True(UpdateCheck.IsTrusted(new Uri("https://github.com/x")));
        Assert.True(UpdateCheck.IsTrusted(new Uri("https://objects.githubusercontent.com/x")));
    }

    [Fact]
    public void A_release_without_the_behavepad_build_is_refused()
    {
        var decision = UpdateCheck.Evaluate(Release("v1.3.0", Asset() with { Name = "notes.txt" }), Current);

        Assert.Equal(UpdateStatus.Unusable, decision.Status);
        Assert.Contains("BehavePad.exe", decision.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(UpdateCheck.MaxAssetSize + 1)]
    public void An_asset_of_an_unexpected_size_is_refused(long size)
    {
        var decision = UpdateCheck.Evaluate(Release("v1.3.0", Asset() with { Size = size }), Current);

        Assert.Equal(UpdateStatus.Unusable, decision.Status);
    }

    [Fact]
    public async Task A_build_that_matches_its_checksum_is_kept()
    {
        var downloader = new UpdateDownloader(new HttpClient(new FakeServer(BuildBytes)), _folder);
        long reported = 0;

        var path = await downloader.DownloadAsync(Candidate(), new SynchronousProgress(bytes => reported = bytes));

        Assert.Equal(Path.Combine(_folder, "BehavePad-1.3.0.exe"), path);
        Assert.True(UpdateDownloader.Matches(path, Candidate()));
        Assert.Equal(BuildBytes.Length, reported);
    }

    [Fact]
    public async Task A_build_that_does_not_match_its_checksum_is_deleted()
    {
        var downloader = new UpdateDownloader(new HttpClient(new FakeServer(BuildBytes)), _folder);
        var candidate = Candidate() with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(candidate));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task A_download_larger_than_the_release_is_stopped_and_deleted()
    {
        var downloader = new UpdateDownloader(new HttpClient(new FakeServer([.. BuildBytes, 9, 9, 9])), _folder);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Candidate()));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task A_verified_build_is_reused_instead_of_downloaded_again()
    {
        var server = new FakeServer(BuildBytes);
        var downloader = new UpdateDownloader(new HttpClient(server), _folder);

        await downloader.DownloadAsync(Candidate());
        await downloader.DownloadAsync(Candidate());

        Assert.Equal(1, server.Requests);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static UpdateCandidate Candidate() => new(
        new Version(1, 3, 0),
        "v1.3.0",
        new Uri("https://github.com/SpeedyNabz/BehavePad/releases/download/v1.3.0/BehavePad.exe"),
        BuildBytes.Length,
        Sha256(BuildBytes),
        null,
        null);

    private static ReleaseAsset Asset(string? digest = "default") => new()
    {
        Name = UpdateCheck.AssetName,
        DownloadUrl = new Uri("https://github.com/SpeedyNabz/BehavePad/releases/download/v1.3.0/BehavePad.exe"),
        Size = BuildBytes.Length,
        Digest = digest == "default" ? "sha256:" + Sha256(BuildBytes) : digest,
    };

    private static GitHubRelease Release(string tag, ReleaseAsset asset, bool draft = false, bool prerelease = false) => new()
    {
        TagName = tag,
        Draft = draft,
        Prerelease = prerelease,
        HtmlUrl = new Uri($"https://github.com/SpeedyNabz/BehavePad/releases/tag/{tag}"),
        Assets = [asset],
    };

    private sealed class FakeServer(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

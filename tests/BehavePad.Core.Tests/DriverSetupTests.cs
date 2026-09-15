using System.Net;
using System.Security.Cryptography;
using BehavePad.Core.Setup;

namespace BehavePad.Core.Tests;

public sealed class DriverSetupTests : IDisposable
{
    private static readonly byte[] InstallerBytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();

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

    private static DriverPackage TestPackage(string? sha256 = null) => new(
        "test",
        "Test driver",
        "1.0",
        new Uri("https://example.invalid/releases/TestDriver_1.0.exe"),
        InstallerBytes.Length,
        sha256 ?? Convert.ToHexStringLower(SHA256.HashData(InstallerBytes)),
        "/quiet");

    [Fact]
    public void Both_drivers_are_pinned_official_installers_that_never_restart_on_their_own()
    {
        Assert.Equal(2, DriverPackages.All.Count);
        Assert.All(DriverPackages.All, package =>
        {
            Assert.StartsWith("https://github.com/nefarius/", package.Url.AbsoluteUri);
            Assert.Matches("^[0-9a-f]{64}$", package.Sha256);
            Assert.True(package.Size > 1_000_000);
            Assert.Contains("/norestart", package.SilentArguments);
        });
        Assert.Same(DriverPackages.HidHide, DriverPackages.Find("hidhide"));
        Assert.Null(DriverPackages.Find("anything else"));
    }

    [Theory]
    [InlineData(0, InstallerResult.Installed)]
    [InlineData(3010, InstallerResult.RestartRequired)]
    [InlineData(1641, InstallerResult.RestartRequired)]
    [InlineData(1602, InstallerResult.Cancelled)]
    [InlineData(1618, InstallerResult.Busy)]
    [InlineData(1603, InstallerResult.Failed)]
    public void Installer_exit_codes_are_read_as_windows_installer_results(int exitCode, InstallerResult expected)
    {
        Assert.Equal(expected, InstallerExitCodes.Interpret(exitCode));
    }

    [Fact]
    public async Task Downloaded_installer_is_kept_when_it_matches()
    {
        var downloader = new InstallerDownloader(new HttpClient(new FakeServer(InstallerBytes)), _folder);
        long reported = 0;

        var path = await downloader.DownloadAsync(TestPackage(), new SynchronousProgress(bytes => reported = bytes));

        Assert.Equal(Path.Combine(_folder, "TestDriver_1.0.exe"), path);
        Assert.True(TestPackage().IsExactFile(path));
        Assert.Equal(InstallerBytes.Length, reported);
    }

    [Fact]
    public async Task Installer_that_does_not_match_is_deleted()
    {
        var downloader = new InstallerDownloader(new HttpClient(new FakeServer(InstallerBytes)), _folder);
        var package = TestPackage(sha256: new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(package));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_larger_than_the_installer_is_stopped_and_deleted()
    {
        var downloader = new InstallerDownloader(new HttpClient(new FakeServer([.. InstallerBytes, 1, 2, 3])), _folder);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(TestPackage()));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Verified_installer_is_reused_without_downloading_again()
    {
        var server = new FakeServer(InstallerBytes);
        var downloader = new InstallerDownloader(new HttpClient(server), _folder);

        await downloader.DownloadAsync(TestPackage());
        await downloader.DownloadAsync(TestPackage());

        Assert.Equal(1, server.Requests);
    }

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

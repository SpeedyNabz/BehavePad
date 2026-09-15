using System.Security.Cryptography;

namespace BehavePad.Core.Setup;

/// <summary>
/// One official driver installer from Nefarius Software Solutions, pinned to an exact file so BehavePad only ever
/// runs the installer it was tested with.
/// </summary>
public sealed record DriverPackage(string Id, string Name, string Version, Uri Url, long Size, string Sha256, string SilentArguments)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);

    /// <summary>True when the file at <paramref name="path"/> is exactly this installer, byte for byte.</summary>
    public bool IsExactFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != Size)
        {
            return false;
        }

        using var stream = info.OpenRead();
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The drivers BehavePad needs to filter inside games, pinned to the releases listed in the Windows Package Manager catalog.</summary>
public static class DriverPackages
{
    /// <summary>Advanced Installer switches: no setup window, no Windows Installer interface, and never restart on its own.</summary>
    public const string SilentArguments = "/exenoui /qn /norestart";

    /// <summary>The final ViGEmBus release. The project is archived, so this installer will not change.</summary>
    public static DriverPackage ViGEmBus { get; } = new(
        "vigembus",
        "ViGEmBus",
        "1.22.0",
        new Uri("https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"),
        6_278_576,
        "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a",
        SilentArguments);

    /// <summary>HidHide keeps its own updater once installed, so a pinned first install stays current.</summary>
    public static DriverPackage HidHide { get; } = new(
        "hidhide",
        "HidHide",
        "1.5.230",
        new Uri("https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe"),
        8_078_016,
        "f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6",
        SilentArguments);

    public static IReadOnlyList<DriverPackage> All { get; } = [ViGEmBus, HidHide];

    public static DriverPackage? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

public enum InstallerResult
{
    Installed,
    RestartRequired,
    Cancelled,
    Busy,
    Failed,
}

/// <summary>Reads the Windows Installer exit codes that Advanced Installer setups pass through.</summary>
public static class InstallerExitCodes
{
    public static InstallerResult Interpret(int exitCode) => exitCode switch
    {
        0 => InstallerResult.Installed,
        3010 or 1641 => InstallerResult.RestartRequired,
        1602 or 1223 => InstallerResult.Cancelled,
        1618 => InstallerResult.Busy,
        _ => InstallerResult.Failed,
    };
}

/// <summary>Downloads driver installers into a folder, keeping only files that match their pinned size and hash.</summary>
public sealed class InstallerDownloader(HttpClient http, string folder)
{
    private const int BufferSize = 81_920;

    /// <summary>Returns the path of a verified installer, downloading it unless a verified copy is already there.</summary>
    /// <param name="progress">Receives the number of bytes downloaded so far.</param>
    public async Task<string> DownloadAsync(DriverPackage package, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, package.FileName);
        if (package.IsExactFile(path))
        {
            progress?.Report(package.Size);
            return path;
        }

        var partial = path + ".download";
        try
        {
            using (var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                var buffer = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > package.Size)
                    {
                        throw new InvalidDataException($"The {package.Name} download was larger than the expected installer.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(received);
                }
            }

            if (!package.IsExactFile(partial))
            {
                throw new InvalidDataException($"The downloaded {package.Name} installer was not the expected file, so BehavePad deleted it.");
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Security.Cryptography;

namespace BehavePad.Core.Update;

/// <summary>
/// Downloads a release build and keeps it only when every byte matches the checksum GitHub published. This mirrors
/// the driver installer download on purpose: BehavePad never runs a file it has not verified first.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string folder)
{
    private const int BufferSize = 81_920;

    /// <summary>Returns the path of a verified build, downloading it unless a verified copy is already there.</summary>
    /// <param name="progress">Receives the number of bytes downloaded so far.</param>
    public async Task<string> DownloadAsync(UpdateCandidate candidate, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, candidate.FileName);
        if (Matches(path, candidate))
        {
            progress?.Report(candidate.Size);
            return path;
        }

        var partial = path + ".download";
        try
        {
            using (var response = await http.GetAsync(candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
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
                    if (received > candidate.Size)
                    {
                        throw new InvalidDataException("The BehavePad download was larger than the release GitHub described.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(received);
                }
            }

            if (!Matches(partial, candidate))
            {
                throw new InvalidDataException("The downloaded BehavePad build was not the file GitHub described, so BehavePad deleted it.");
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

    /// <summary>True when the file at <paramref name="path"/> is exactly this build, byte for byte.</summary>
    public static bool Matches(string path, UpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != candidate.Size)
        {
            return false;
        }

        try
        {
            using var stream = info.OpenRead();
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), candidate.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

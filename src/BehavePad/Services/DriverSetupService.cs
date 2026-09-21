using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using BehavePad.Core.Setup;
using BehavePad.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BehavePad.Services;

public enum DriverSetupResult
{
    Installed,
    RestartRequired,
    NothingToInstall,
    Cancelled,
    Failed,
}

public sealed record DriverSetupOutcome(DriverSetupResult Result, string Message);

internal sealed record InstallItem(string PackageId, string InstallerPath);

internal sealed record InstallRun(string PackageId, int ExitCode, string? Error);

internal sealed record InstallReport(IReadOnlyList<InstallRun> Runs, string? Error, bool Cancelled = false);

/// <summary>
/// Downloads the official driver installers and runs them silently. Windows asks for permission once, then a small
/// elevated copy of BehavePad installs everything that is missing.
/// </summary>
public sealed partial class DriverSetupService : ObservableObject
{
    public const string InstallArgument = "--install-drivers";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);
    private static readonly Lazy<HttpClient> Http = new(CreateHttpClient);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>Share of the downloads finished, from 0 to 1.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Copies the agent's install progress into the window, which only reports it.</summary>
    public void ApplyRemoteState(Ipc.AgentState state)
    {
        IsBusy = state.DriverBusy;
        Status = state.DriverStatus;
        StatusIsError = state.DriverStatusIsError;
        Progress = state.DriverProgress;
    }

    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "BehavePad", "drivers");

    private static string HelperFolder => Path.Combine(Path.GetTempPath(), "BehavePad");

    private static string CurrentExecutable => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static bool IsElevated => Elevation.IsElevated;

    public async Task<DriverSetupOutcome> InstallAsync(IReadOnlyList<DriverPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (IsBusy)
        {
            return new DriverSetupOutcome(DriverSetupResult.Failed, "Driver setup is already running.");
        }

        if (packages.Count == 0)
        {
            return Show(new DriverSetupOutcome(DriverSetupResult.NothingToInstall, "Both drivers are already installed."));
        }

        IsBusy = true;
        StatusIsError = false;
        Progress = 0;
        try
        {
            var downloader = new InstallerDownloader(Http.Value, DownloadFolder);
            var total = (double)packages.Sum(p => p.Size);
            long finished = 0;
            var items = new List<InstallItem>();
            foreach (var package in packages)
            {
                var before = finished;
                Status = $"Downloading {package.Name}";
                var progress = new Progress<long>(bytes =>
                {
                    Progress = (before + bytes) / total;
                    Status = $"Downloading {package.Name}, {Megabytes(bytes)} of {Megabytes(package.Size)} MB";
                });

                try
                {
                    items.Add(new InstallItem(package.Id, await downloader.DownloadAsync(package, progress)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    var reason = ex switch
                    {
                        HttpRequestException => "Check your internet connection and try again.",
                        TaskCanceledException => "The download timed out.",
                        _ => ex.Message,
                    };
                    return Show(new DriverSetupOutcome(DriverSetupResult.Failed, $"BehavePad couldn't download {package.Name}. {reason}"));
                }

                finished += package.Size;
            }

            Progress = 1;
            var names = Names(packages.Select(p => p.Name));
            Status = IsElevated ? $"Installing {names}. This can take a minute." : $"Waiting for permission to install {names}";
            return Show(Summarize(await RunInstallersAsync(items, names)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows an outcome as the status line and returns it.</summary>
    public DriverSetupOutcome Show(DriverSetupOutcome outcome)
    {
        Status = outcome.Message;
        StatusIsError = outcome.Result is DriverSetupResult.Failed or DriverSetupResult.Cancelled;
        return outcome;
    }

    /// <summary>Entry point for the elevated helper. It only runs elevated, with request and result files in BehavePad's temp folder.</summary>
    public static int RunHelper(string[] args)
    {
        if (!IsElevated || args.Length < 3 || !IsHelperFile(args[1]) || !IsHelperFile(args[2]))
        {
            return 2;
        }

        InstallReport report;
        try
        {
            var items = JsonSerializer.Deserialize<List<InstallItem>>(File.ReadAllText(args[1]), BehavePadJson.Options)
                        ?? throw new InvalidDataException("The driver setup request was empty.");
            report = InstallVerified(items);
        }
        catch (Exception ex)
        {
            report = new InstallReport([], ex.Message);
        }

        File.WriteAllText(args[2], JsonSerializer.Serialize(report, BehavePadJson.Options));
        return report.Error is null ? 0 : 1;
    }

    private async Task<InstallReport> RunInstallersAsync(IReadOnlyList<InstallItem> items, string names)
    {
        if (IsElevated)
        {
            return await Task.Run(() => InstallVerified(items));
        }

        var folder = Directory.CreateDirectory(HelperFolder).FullName;
        var requestPath = Path.Combine(folder, $"install-{Guid.NewGuid():N}.json");
        var resultPath = Path.Combine(folder, $"install-result-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(items, BehavePadJson.Options));

        try
        {
            using var process = Process.Start(new ProcessStartInfo(CurrentExecutable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{InstallArgument} \"{requestPath}\" \"{resultPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return new InstallReport([], "Windows did not start the driver setup helper.");
            }

            Status = $"Installing {names}. This can take a minute.";
            using var timeout = new CancellationTokenSource(InstallTimeout);
            await process.WaitForExitAsync(timeout.Token);

            return File.Exists(resultPath)
                ? JsonSerializer.Deserialize<InstallReport>(await File.ReadAllTextAsync(resultPath), BehavePadJson.Options)
                  ?? new InstallReport([], "The driver setup helper returned nothing.")
                : new InstallReport([], "The driver setup helper finished without a result.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new InstallReport([], null, Cancelled: true);
        }
        catch (OperationCanceledException)
        {
            return new InstallReport([], "Driver setup took longer than 15 minutes.");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>
    /// Copies each installer into a folder only administrators can change, checks the copy is the pinned file, then runs
    /// it silently. Checking the copy means nothing can swap the installer between the download and the install.
    /// </summary>
    private static InstallReport InstallVerified(IReadOnlyList<InstallItem> items)
    {
        var folder = CreateProtectedFolder();
        var runs = new List<InstallRun>();
        try
        {
            foreach (var item in items)
            {
                if (DriverPackages.Find(item.PackageId) is not { } package)
                {
                    runs.Add(new InstallRun(item.PackageId, -1, "BehavePad does not install this driver."));
                    continue;
                }

                try
                {
                    var installer = Path.Combine(folder, package.FileName);
                    File.Copy(item.InstallerPath, installer);
                    if (!package.IsExactFile(installer))
                    {
                        runs.Add(new InstallRun(package.Id, -1, $"The {package.Name} installer changed after it was downloaded, so BehavePad did not run it."));
                        continue;
                    }

                    using var process = Process.Start(new ProcessStartInfo(installer, package.SilentArguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = folder,
                    });

                    runs.Add(
                        process is null ? new InstallRun(package.Id, -1, $"Windows did not start the {package.Name} installer.")
                        : !process.WaitForExit(InstallTimeout) ? new InstallRun(package.Id, -1, $"The {package.Name} installer did not finish in time.")
                        : new InstallRun(package.Id, process.ExitCode, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    runs.Add(new InstallRun(package.Id, -1, $"{package.Name} setup could not run. {ex.Message}"));
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new InstallReport(runs, null);
    }

    private static string CreateProtectedFolder()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BehavePad", "DriverSetup");
        Directory.CreateDirectory(root);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var folder = new DirectoryInfo(Path.Combine(root, Guid.NewGuid().ToString("N")));
        folder.Create(security);
        return folder.FullName;
    }

    /// <summary>The helper only reads and writes its own JSON files in BehavePad's temp folder.</summary>
    private static bool IsHelperFile(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(HelperFolder), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase);
    }

    private static DriverSetupOutcome Summarize(InstallReport report)
    {
        if (report.Cancelled)
        {
            return new DriverSetupOutcome(DriverSetupResult.Cancelled, "Permission was declined, so no drivers were installed.");
        }

        if (report.Error is { } error)
        {
            return new DriverSetupOutcome(DriverSetupResult.Failed, $"Driver setup failed. {error}");
        }

        var installed = new List<string>();
        var problems = new List<string>();
        var restart = false;
        foreach (var run in report.Runs)
        {
            var name = DriverPackages.Find(run.PackageId)?.Name ?? run.PackageId;
            if (run.Error is { } runError)
            {
                problems.Add(runError);
                continue;
            }

            switch (InstallerExitCodes.Interpret(run.ExitCode))
            {
                case InstallerResult.Installed:
                    installed.Add(name);
                    break;
                case InstallerResult.RestartRequired:
                    installed.Add(name);
                    restart = true;
                    break;
                case InstallerResult.Cancelled:
                    problems.Add($"{name} setup was cancelled.");
                    break;
                case InstallerResult.Busy:
                    problems.Add($"{name} couldn't install while another installation is running. Try again when it finishes.");
                    break;
                default:
                    problems.Add($"{name} setup failed with code {run.ExitCode}.");
                    break;
            }
        }

        if (problems.Count > 0)
        {
            var done = installed.Count > 0 ? $"Installed {Names(installed)}. " : "";
            return new DriverSetupOutcome(DriverSetupResult.Failed, done + string.Join(" ", problems));
        }

        return restart
            ? new DriverSetupOutcome(DriverSetupResult.RestartRequired, $"Installed {Names(installed)}. Restart your PC to finish.")
            : new DriverSetupOutcome(DriverSetupResult.Installed, $"Installed {Names(installed)}.");
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static string Megabytes(long bytes) => $"{bytes / 1_048_576.0:0.0}";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BehavePad/{version}");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}

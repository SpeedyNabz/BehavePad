using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using BehavePad.Core.Storage;
using BehavePad.Core.Update;
using BehavePad.Ipc;
using BehavePad.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BehavePad.Services;

public enum UpdateState
{
    Idle,
    Checking,
    Downloading,

    /// <summary>A verified build is waiting to replace this one.</summary>
    Ready,
    Failed,
}

/// <summary>What BehavePad has already downloaded, so a verified build survives a restart instead of downloading twice.</summary>
public sealed record UpdateRecord
{
    public DateTimeOffset LastCheck { get; init; }

    public string? StagedVersion { get; init; }

    public string? StagedPath { get; init; }
}

/// <summary>
/// Keeps BehavePad up to date from its GitHub releases. The downloaded build is its own installer: it waits for this
/// process to exit, copies itself over this file, and starts the result. Nothing runs until its checksum matches the
/// one GitHub published, and an update never interrupts a filter that is on.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>Argv of a staged build running only to replace the copy that started it.</summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Added to the apply arguments when the new build should start itself afterwards.</summary>
    public const string RelaunchAfterArgument = "--relaunch-after";

    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromSeconds(20);
    private static readonly Lazy<HttpClient> Http = new(CreateHttpClient);

    private readonly JsonFileStore<UpdateRecord> _store = new(AppPaths.UpdatePath);
    private readonly SettingsService _settings;
    private readonly Func<bool> _isSafeToApply;
    private UpdateRecord _record;

    /// <summary>Set in the window, null in the agent.</summary>
    private AgentLink? _link;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsReady))]
    private UpdateState _state;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>Share of the download finished, from 0 to 1.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _stagedVersion;

    /// <summary>The window's constructor: the agent checks and installs, this only shows what it reports.</summary>
    public static UpdateService Remote(AgentLink link, SettingsService settings) => new(settings) { _link = link };

    /// <summary>Copies the agent's update progress into the window.</summary>
    public void ApplyRemoteState(AgentState state)
    {
        State = state.UpdateState;
        Status = state.UpdateStatus;
        StatusIsError = state.UpdateStatusIsError;
        Progress = state.UpdateProgress;
        StagedVersion = state.StagedVersion;
    }

    /// <param name="isSafeToApply">Answers whether BehavePad can restart right now. An update waits while the filter is on.</param>
    public UpdateService(SettingsService settings, Func<bool>? isSafeToApply = null)
    {
        _settings = settings;
        _isSafeToApply = isSafeToApply ?? (() => true);
        _record = _store.Load() ?? new UpdateRecord();
    }

    public static Version CurrentVersion =>
        ReleaseVersion.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool IsReady => State == UpdateState.Ready;

    /// <summary>The verified build waiting to replace this one, if any.</summary>
    public string? StagedPath { get; private set; }

    public Uri ReleasePage { get; private set; } = UpdateCheck.ReleasesPage;

    private static string StageFolder => Path.Combine(Path.GetTempPath(), "BehavePad", "update");

    /// <summary>
    /// Looks for a newer release, and downloads it when automatic updates are on. The background check stays quiet
    /// about failures and runs at most once a day, so a PC without internet never nags.
    /// </summary>
    public async Task CheckAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        if (_link is not null)
        {
            _link.Send(AgentCommand.CheckForUpdates);
            return;
        }

        if (IsBusy || State == UpdateState.Ready)
        {
            return;
        }

        if (automatic && (!_settings.Settings.AutomaticUpdates || DateTimeOffset.Now - _record.LastCheck < AutomaticInterval))
        {
            return;
        }

        State = UpdateState.Checking;
        StatusIsError = false;
        Status = "Checking for updates";

        UpdateDecision decision;
        try
        {
            var release = await new GitHubReleaseClient(Http.Value).GetLatestAsync(cancellationToken).ConfigureAwait(true);
            Save(_record with { LastCheck = DateTimeOffset.Now });
            decision = UpdateCheck.Evaluate(release, CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // A background check that cannot reach GitHub is not worth a message.
            Finish(UpdateState.Idle, automatic ? null : $"BehavePad could not check for updates. {Describe(ex)}", isError: !automatic);
            return;
        }

        ReleasePage = decision.ReleaseUrl ?? UpdateCheck.ReleasesPage;

        switch (decision.Status)
        {
            case UpdateStatus.Available when decision.Candidate is { } candidate:
                AvailableVersion = candidate.VersionText;
                if (!_settings.Settings.AutomaticUpdates && automatic)
                {
                    Finish(UpdateState.Idle, $"BehavePad {candidate.VersionText} is available.");
                    return;
                }

                await DownloadAsync(candidate, cancellationToken).ConfigureAwait(true);
                return;

            case UpdateStatus.Unusable:
                AvailableVersion = null;
                Finish(UpdateState.Idle, decision.Reason, isError: !automatic);
                return;

            default:
                AvailableVersion = null;
                Finish(UpdateState.Idle, automatic ? null : $"BehavePad {CurrentVersionText} is the latest version.");
                return;
        }
    }

    /// <summary>
    /// The window's half of Restart and install. The agent turns the filter off, hands over to the new build and exits,
    /// and the new build starts BehavePad again. Returns true once the agent has gone, so the window can close rather
    /// than hold back the one the new build opens, or false when the agent did not go ahead.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        if (_link is not { IsConnected: true } link)
        {
            return false;
        }

        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnectedChanged(object? sender, bool connected)
        {
            if (!connected)
            {
                outcome.TrySetResult(true);
            }
        }

        // The agent stays Ready until it exits, so leaving Ready means it is not installing after all.
        void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State) && State != UpdateState.Ready)
            {
                outcome.TrySetResult(false);
            }
        }

        link.ConnectedChanged += OnConnectedChanged;
        PropertyChanged += OnStateChanged;
        try
        {
            link.Send(AgentCommand.InstallUpdate);
            return await outcome.Task;
        }
        finally
        {
            link.ConnectedChanged -= OnConnectedChanged;
            PropertyChanged -= OnStateChanged;
        }
    }

    /// <summary>
    /// Checks the staged build is still on disk before the agent exits to install it. When something has cleared it
    /// from the temp folder, says so and forgets it, so Check now downloads it again.
    /// </summary>
    public bool BeginInstall()
    {
        if (!IsReady)
        {
            return false;
        }

        if (StagedPath is not { } staged || !File.Exists(staged))
        {
            StagedPath = null;
            StagedVersion = null;
            Save(new UpdateRecord { LastCheck = _record.LastCheck });
            Finish(UpdateState.Failed, "BehavePad could not find the update it downloaded. Choose Check now to download it again.", isError: true);
            return false;
        }

        Status = $"Installing BehavePad {StagedVersion}";
        return true;
    }

    /// <summary>
    /// Installs a staged build over this one. The caller must already have stopped filtering, because the controller
    /// has to be visible to games again before BehavePad goes away. Returns false when there is nothing to install.
    /// </summary>
    public bool TryApply(bool relaunch)
    {
        if (StagedPath is not { } staged || !File.Exists(staged) || Environment.ProcessPath is not { } target)
        {
            return false;
        }

        try
        {
            var arguments = $"{ApplyArgument} \"{target}\" {Environment.ProcessId}" + (relaunch ? $" {RelaunchAfterArgument}" : "");
            using var process = Process.Start(new ProcessStartInfo(staged)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = arguments,
            });

            if (process is null)
            {
                return false;
            }

            // The new build does the swap from here, so nothing may start it a second time.
            StagedPath = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceError($"BehavePad could not start the update: {ex.Message}");
            Finish(UpdateState.Failed, "BehavePad could not start the update. Install it from the release page instead.", isError: true);
            return false;
        }
    }

    /// <summary>True when a verified build is staged and nothing is in the way of restarting.</summary>
    public bool CanApplyNow() => IsReady && StagedPath is not null && _isSafeToApply();

    /// <summary>
    /// Tidies up at startup: removes the previous build kept as a fallback, and picks up a build staged by an
    /// earlier session that has not been installed yet. Returns true when this start is the first run of a build
    /// BehavePad installed itself.
    /// </summary>
    /// <remarks>
    /// There is no version to report updating from: nothing records what was running before, and the staged version
    /// is the one now running, so it only ever says what it already is.
    /// </remarks>
    public bool TakeAppliedUpdate()
    {
        if (Environment.ProcessPath is { } path)
        {
            TryDelete(path + ".old");
        }

        if (_record.StagedVersion is not { } staged)
        {
            return false;
        }

        // The staged build is the one now running, so the replacement landed.
        if (ReleaseVersion.TryParse(staged, out var version) && version == CurrentVersion)
        {
            TryDelete(_record.StagedPath);
            Save(new UpdateRecord { LastCheck = _record.LastCheck });
            return true;
        }

        if (_record.StagedPath is { } stagedPath && File.Exists(stagedPath))
        {
            StagedPath = stagedPath;
            StagedVersion = staged;
            AvailableVersion = staged;
            Status = $"BehavePad {staged} is ready to install.";
            State = UpdateState.Ready;
        }
        else
        {
            Save(new UpdateRecord { LastCheck = _record.LastCheck });
        }

        return false;
    }

    /// <summary>
    /// Entry point for a staged build started only to replace the copy that launched it. It waits for that process
    /// to let go of the file, keeps the old build aside until the new one is in place, then starts it if asked.
    /// </summary>
    public static int RunApplyHelper(string[] args)
    {
        if (args.Length < 3 || Environment.ProcessPath is not { } source)
        {
            return 2;
        }

        var target = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out var processId)
            || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target)
            || string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        WaitForExit(processId);
        var replaced = TryReplace(source, target);

        // Even when the swap failed, so BehavePad carries on with the old build rather than not at all.
        if (args.Contains(RelaunchAfterArgument))
        {
            // The background service first. A window from 1.5.1 or earlier stays open through an update, holding
            // back the new window, and only reconnects to whichever service is running.
            TryStart(target, AgentContract.AgentArgument);
            TryStart(target, SetupViewModel.RelaunchArgument);
        }

        return replaced ? 0 : 1;
    }

    private async Task DownloadAsync(UpdateCandidate candidate, CancellationToken cancellationToken)
    {
        State = UpdateState.Downloading;
        StatusIsError = false;
        Progress = 0;
        Status = $"Downloading BehavePad {candidate.VersionText}";

        try
        {
            var progress = new Progress<long>(bytes =>
            {
                Progress = candidate.Size > 0 ? (double)bytes / candidate.Size : 0;
                Status = $"Downloading BehavePad {candidate.VersionText}, {Megabytes(bytes)} of {Megabytes(candidate.Size)} MB";
            });

            var path = await new UpdateDownloader(Http.Value, StageFolder)
                .DownloadAsync(candidate, progress, cancellationToken)
                .ConfigureAwait(true);

            StagedPath = path;
            StagedVersion = candidate.VersionText;
            Save(_record with { StagedVersion = candidate.VersionText, StagedPath = path });
            Progress = 1;
            Finish(UpdateState.Ready, $"BehavePad {candidate.VersionText} is ready to install.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Finish(UpdateState.Failed, $"BehavePad could not download the update. {Describe(ex)}", isError: true);
        }
    }

    /// <summary>Moves the old build aside before copying the new one in, so a failed update never leaves no BehavePad at all.</summary>
    private static bool TryReplace(string source, string target)
    {
        var backup = target + ".old";
        var deadline = DateTime.UtcNow + ReplaceTimeout;
        while (true)
        {
            // Builds before 1.5.2 start this helper twice on their way out, and the first copy to get here does the work.
            if (IsCopyOf(target, source))
            {
                return true;
            }

            try
            {
                TryDelete(backup);
                File.Move(target, backup, overwrite: true);
                File.Copy(source, target, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(target) && File.Exists(backup))
                {
                    TryMove(backup, target);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Trace.TraceError($"BehavePad could not replace itself: {ex.Message}");
                    return false;
                }

                Thread.Sleep(500);
            }
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit((int)ExitWait.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process is already gone, which is exactly what this was waiting for.
        }
    }

    /// <summary>File.Copy keeps the write time, so a file with the same size and write time is a copy of that build.</summary>
    private static bool IsCopyOf(string path, string source)
    {
        var copy = new FileInfo(path);
        var original = new FileInfo(source);
        return copy.Exists && copy.Length == original.Length && copy.LastWriteTimeUtc == original.LastWriteTimeUtc;
    }

    private static void TryStart(string path, string arguments)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceError($"BehavePad could not start the updated build: {ex.Message}");
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Check your internet connection and try again.",
        TaskCanceledException => "The download timed out.",
        _ => ex.Message,
    };

    private static string Megabytes(long bytes) => $"{bytes / 1_048_576.0:0.0}";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BehavePad/{CurrentVersionText}");
        return client;
    }

    private void Finish(UpdateState state, string? status, bool isError = false)
    {
        Status = status;
        StatusIsError = isError && status is not null;
        State = state;
    }

    private void Save(UpdateRecord record)
    {
        _record = record;
        try
        {
            _store.Save(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"BehavePad could not save its update state: {ex.Message}");
        }
    }
}

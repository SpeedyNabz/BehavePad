using BehavePad.Core.Analysis;
using BehavePad.Core.Engine;
using BehavePad.Core.Filtering;
using BehavePad.Core.Storage;

namespace BehavePad.Services;

public sealed record AppSettings
{
    /// <summary>Hide the real controller from games while the filter runs, so games only see the clean one.</summary>
    public bool HidePhysicalController { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    /// <summary>On by default, so a controller that has already been tested is protected from the moment BehavePad opens.</summary>
    public bool StartFilterOnLaunch { get; init; } = true;

    public bool LaunchAtSignIn { get; init; }

    public bool UseDemoController { get; init; }

    public int PreferredSlot { get; init; } = InputPump.AutoSlot;

    /// <summary>Check BehavePad's GitHub releases once a day and install a newer build on the next exit.</summary>
    public bool AutomaticUpdates { get; init; } = true;

    /// <summary>
    /// Let BehavePad restart the controller when it hides it, so a game that already had it open has to let go.
    /// Off by default: on some controllers the restart brings the device back without the half that games and
    /// BehavePad read, and nothing can use it again until it is unplugged and plugged back in.
    /// </summary>
    public bool RestartControllerWhenHiding { get; init; }
}

/// <summary>Loads and saves settings, the active filter profile, and the most recent test report.</summary>
public sealed class SettingsService
{
    private readonly JsonFileStore<AppSettings> _settingsStore = new(AppPaths.SettingsPath);
    private readonly JsonFileStore<FilterProfile> _profileStore = new(AppPaths.ProfilePath);
    private readonly JsonFileStore<DriftReport> _reportStore = new(AppPaths.ReportPath);

    public SettingsService()
    {
        Settings = _settingsStore.Load() ?? new AppSettings();
        Profile = _profileStore.Load()?.Sanitized();
        LastReport = _reportStore.Load();
    }

    public event EventHandler? Changed;

    public AppSettings Settings { get; private set; }

    public FilterProfile? Profile { get; private set; }

    public DriftReport? LastReport { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        TrySave(() => _settingsStore.Save(Settings));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveTest(DriftReport report, FilterProfile profile)
    {
        LastReport = report;
        Profile = profile.Sanitized();
        TrySave(() => _reportStore.Save(report));
        TrySave(() => _profileStore.Save(Profile));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile(FilterProfile profile)
    {
        Profile = profile.Sanitized();
        TrySave(() => _profileStore.Save(Profile));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetAll()
    {
        Profile = null;
        LastReport = null;
        TrySave(_profileStore.Delete);
        TrySave(_reportStore.Delete);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"BehavePad could not save settings: {ex.Message}");
        }
    }
}

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

    public bool StartFilterOnLaunch { get; init; }

    public bool LaunchAtSignIn { get; init; }

    public bool UseDemoController { get; init; }

    public int PreferredSlot { get; init; } = InputPump.AutoSlot;
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

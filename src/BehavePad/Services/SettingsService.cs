using System.Text.Json.Serialization;
using BehavePad.Core.Analysis;
using BehavePad.Core.Engine;
using BehavePad.Core.Filtering;
using BehavePad.Core.Storage;
using BehavePad.Ipc;

namespace BehavePad.Services;

public sealed record AppSettings
{
    /// <summary>Hide the real controller from games while the filter runs, so games only see the clean one.</summary>
    public bool HidePhysicalController { get; init; } = true;

    /// <summary>
    /// No longer a choice. The background agent keeps filtering whether or not a window is open, so this
    /// is only still here to be read and rewritten without losing an older settings file.
    /// </summary>
    public bool MinimizeToTray { get; init; } = true;

    /// <summary>
    /// Keeps the filter on whenever a tested controller is connected, rather than only trying once at launch.
    /// On by default, so a controller that has already been tested is protected without anyone asking.
    /// </summary>
    public bool AutoFilterWhenConnected { get; init; } = true;

    /// <summary>The old name for <see cref="AutoFilterWhenConnected"/>, still read so existing settings carry over.</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StartFilterOnLaunch { get; init; }

    public bool LaunchAtSignIn { get; init; }

    public bool UseDemoController { get; init; }

    public int PreferredSlot { get; init; } = InputPump.AutoSlot;

    /// <summary>Check BehavePad's GitHub releases once a day and install a newer build on the next exit.</summary>
    public bool AutomaticUpdates { get; init; } = true;
}

/// <summary>Loads and saves settings, the active filter profile, and the most recent test report.</summary>
public sealed class SettingsService
{
    private readonly JsonFileStore<AppSettings> _settingsStore = new(AppPaths.SettingsPath);
    private readonly JsonFileStore<FilterProfile> _profileStore = new(AppPaths.ProfilePath);
    private readonly JsonFileStore<DriftReport> _reportStore = new(AppPaths.ReportPath);

    /// <summary>Set in the window, null in the agent.</summary>
    private readonly AgentLink? _link;

    /// <summary>The agent's constructor: reads and writes the files under %AppData%\BehavePad.</summary>
    public SettingsService()
    {
        Settings = Migrate(_settingsStore.Load() ?? new AppSettings());
        Profile = _profileStore.Load()?.Sanitized();
        LastReport = _reportStore.Load();
    }

    /// <summary>The window's constructor: the agent owns the files, so nothing here is read from disk.</summary>
    private SettingsService(AgentLink link)
    {
        _link = link;
        Settings = new AppSettings();
    }

    public static SettingsService Remote(AgentLink link) => new(link);

    /// <summary>Copies the agent's stored settings, filter and last test into the window.</summary>
    public void ApplyRemoteState(AgentState state)
    {
        Settings = state.Settings;
        Profile = state.Profile;
        LastReport = state.Report;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public AppSettings Settings { get; private set; }

    public FilterProfile? Profile { get; private set; }

    public DriftReport? LastReport { get; private set; }

    /// <summary>Carries a pre-1.4 settings file over to the current names.</summary>
    private static AppSettings Migrate(AppSettings settings) =>
        settings.StartFilterOnLaunch is { } legacy
            ? settings with { AutoFilterWhenConnected = legacy, StartFilterOnLaunch = null }
            : settings;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        if (_link is not null)
        {
            // Show the change at once, then let the agent save it and report back.
            _link.Send(AgentCommand.UpdateSettings, Settings);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        TrySave(() => _settingsStore.Save(Settings));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveTest(DriftReport report, FilterProfile profile)
    {
        LastReport = report;
        Profile = profile.Sanitized();
        if (_link is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        TrySave(() => _reportStore.Save(report));
        TrySave(() => _profileStore.Save(Profile));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile(FilterProfile profile)
    {
        Profile = profile.Sanitized();
        if (_link is not null)
        {
            _link.Send(AgentCommand.SaveProfile, Profile);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        TrySave(() => _profileStore.Save(Profile));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetAll()
    {
        Profile = null;
        LastReport = null;
        if (_link is not null)
        {
            _link.Send(AgentCommand.ResetProfile);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

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

using System.ComponentModel;
using BehavePad.Core.Setup;
using BehavePad.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.Setup;

/// <summary>Which face the installer window is showing.</summary>
public enum InstallStage
{
    Choices,
    Working,
    Done,
    Failed,
}

/// <summary>
/// Drives the installer window. It reuses BehavePad's own driver installer, so the downloads are checked
/// against the same known hashes here as they are inside the app.
/// </summary>
public sealed partial class InstallerViewModel : ObservableObject
{
    private readonly DriverSetupService _drivers = new();
    private readonly bool _uninstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(ShowChoices), nameof(ShowProgress), nameof(Finished), nameof(ShowCancel),
        nameof(PrimaryText), nameof(PrimaryGlyph), nameof(CancelText), nameof(Headline), nameof(Subtext), nameof(CanPrimary))]
    private InstallStage _stage = InstallStage.Choices;

    [ObservableProperty]
    private bool _desktopShortcut = true;

    [ObservableProperty]
    private bool _startMenuShortcut = true;

    [ObservableProperty]
    private bool _startAtSignIn = true;

    [ObservableProperty]
    private bool _installDrivers = true;

    /// <summary>Kept for the uninstaller, where removing the test and filter should be a deliberate choice.</summary>
    [ObservableProperty]
    private bool _keepData = true;

    [ObservableProperty]
    private string _stepText = "";

    [ObservableProperty]
    private string? _detailText;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    public InstallerViewModel(bool uninstalling)
    {
        _uninstalling = uninstalling;
        _drivers.PropertyChanged += OnDriverProgress;
    }

    public event EventHandler? CloseRequested;

    public string Location => AppInstaller.DefaultLocation;

    public bool ShowChoices => Stage == InstallStage.Choices;

    public bool ShowProgress => Stage == InstallStage.Working;

    public bool Finished => Stage is InstallStage.Done or InstallStage.Failed;

    public bool ShowCancel => Stage != InstallStage.Working;

    public bool CanPrimary => Stage != InstallStage.Working;

    public string Headline => Stage switch
    {
        InstallStage.Done => _uninstalling ? "BehavePad is removed" : "BehavePad is ready",
        InstallStage.Failed => "That did not go to plan",
        _ => _uninstalling ? "Remove BehavePad?" : "Install BehavePad",
    };

    public string Subtext => Stage switch
    {
        InstallStage.Done when _uninstalling => "Your controller is visible to games again. The drivers were left alone, because other apps may use them.",
        InstallStage.Done => "The background service is running, so a tested controller is filtered from the moment it connects.",
        InstallStage.Failed => "Nothing was left half done. You can close this and try again.",
        _ when _uninstalling => "This removes BehavePad and its shortcuts. ViGEmBus and HidHide stay, because other apps may use them.",
        _ => $"Version {AppInstaller.CurrentVersionText}. It installs just for you, so Windows only asks for permission when the drivers go in.",
    };

    public string PrimaryText => Stage switch
    {
        InstallStage.Done when _uninstalling => "Close",
        InstallStage.Done => "Open BehavePad",
        InstallStage.Failed => "Close",
        _ when _uninstalling => "Remove BehavePad",
        _ => "Install",
    };

    public string? PrimaryGlyph => Stage switch
    {
        InstallStage.Done when !_uninstalling => "",
        InstallStage.Choices when _uninstalling => "",
        InstallStage.Choices => "",
        _ => null,
    };

    public string CancelText => Finished ? "Close" : "Cancel";

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (Stage)
        {
            case InstallStage.Choices when _uninstalling:
                await RunUninstallAsync();
                break;
            case InstallStage.Choices:
                await RunInstallAsync();
                break;
            case InstallStage.Done when !_uninstalling:
                AppInstaller.OpenInstalledWindow();
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task RunInstallAsync()
    {
        Stage = InstallStage.Working;
        IsIndeterminate = true;
        DetailText = null;

        try
        {
            var choices = new InstallChoices(DesktopShortcut, StartMenuShortcut, StartAtSignIn, InstallDrivers);
            await AppInstaller.InstallAsync(choices, new Progress<string>(text => StepText = text));

            if (InstallDrivers)
            {
                StepText = "Installing the drivers";
                DetailText = "Windows will ask for permission once.";
                var outcome = await InstallDriversAsync();
                DetailText = outcome;
            }

            StepText = "Starting the background service";
            IsIndeterminate = true;
            AppInstaller.StartInstalledAgent();
            await Task.Delay(1200);

            StepText = StartMenuShortcut || DesktopShortcut
                ? "Installed. Your shortcuts are ready."
                : "Installed.";
            DetailText = DetailText is { Length: > 0 } detail
                ? detail
                : "Run the drift test once, and BehavePad turns the filter on for that controller by itself from then on.";
            Stage = InstallStage.Done;
        }
        catch (Exception ex)
        {
            StepText = "BehavePad could not finish installing";
            DetailText = ex.Message;
            Stage = InstallStage.Failed;
        }
    }

    private async Task<string> InstallDriversAsync()
    {
        IsIndeterminate = false;
        var packages = new List<DriverPackage>();
        if (!DriverStatus.CheckVigem().Ready)
        {
            packages.Add(DriverPackages.ViGEmBus);
        }

        if (!DriverStatus.CheckHidHide().Installed)
        {
            packages.Add(DriverPackages.HidHide);
        }

        var outcome = await _drivers.InstallAsync(packages);
        IsIndeterminate = true;
        return outcome.Result switch
        {
            DriverSetupResult.Installed => "The drivers are in.",
            DriverSetupResult.NothingToInstall => "The drivers were already there.",
            DriverSetupResult.RestartRequired => "Restart your PC to finish setting up the drivers.",
            _ => $"{outcome.Message} You can install them later from the Setup page.",
        };
    }

    private async Task RunUninstallAsync()
    {
        Stage = InstallStage.Working;
        IsIndeterminate = true;

        try
        {
            await AppInstaller.UninstallAsync(KeepData, new Progress<string>(text => StepText = text));
            StepText = "BehavePad is removed";
            DetailText = KeepData
                ? "Your settings and last test are still in %AppData%\\BehavePad, in case you come back."
                : null;
            Stage = InstallStage.Done;
        }
        catch (Exception ex)
        {
            StepText = "BehavePad could not finish removing itself";
            DetailText = ex.Message;
            Stage = InstallStage.Failed;
        }
    }

    private void OnDriverProgress(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DriverSetupService.Progress):
                Progress = _drivers.Progress;
                break;
            case nameof(DriverSetupService.Status) when _drivers.Status is { Length: > 0 } status:
                DetailText = status;
                break;
        }
    }
}

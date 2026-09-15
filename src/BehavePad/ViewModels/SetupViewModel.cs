using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using BehavePad.Core.Input;
using BehavePad.Core.Storage;
using BehavePad.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.ViewModels;

public sealed partial class SetupViewModel : ObservableObject, IPageViewModel
{
    public const string RelaunchArgument = "--relaunch";

    private readonly ShellViewModel _shell;
    private bool _loading;

    [ObservableProperty]
    private bool _hidePhysicalController;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startFilterOnLaunch;

    [ObservableProperty]
    private bool _launchAtSignIn;

    [ObservableProperty]
    private bool _useDemoController;

    [ObservableProperty]
    private int _preferredSlot;

    [ObservableProperty]
    private bool _isRefreshing;

    public SetupViewModel(ShellViewModel shell)
    {
        _shell = shell;
        LoadSettings();
        shell.Filter.PropertyChanged += (_, _) => RaiseDriverState();
        shell.Filter.DriverSetup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriverSetupService.IsBusy))
            {
                RaiseDriverState();
            }
        };
    }

    public DriverInfo Vigem => _shell.Filter.Vigem;

    public DriverInfo HidHide => _shell.Filter.HidHideDriver;

    public DriverSetupService DriverSetup => _shell.Filter.DriverSetup;

    /// <summary>True when a driver is missing. A driver that is installed but waiting for a restart has nothing left to install.</summary>
    public bool CanInstallDrivers => !Vigem.Ready || !HidHide.Installed;

    public string InstallButtonText =>
        !Vigem.Ready && !HidHide.Installed ? "Install drivers"
        : !Vigem.Ready ? "Install ViGEmBus"
        : "Install HidHide";

    public string VigemStatus => DescribeDriver(Vigem);

    public string HidHideStatus => DescribeDriver(HidHide);

    public bool HasPendingRestore => _shell.Filter.PhysicalHidden || _shell.Filter.HidHide.HasPendingRestore;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool XInputMissing => !_shell.Controller.HasXInput;

    public string VersionText => $"BehavePad {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)}";

    public string DataFolder => AppPaths.DataDirectory;

    public void OnNavigatedTo()
    {
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        LoadSettings();
        _ = RefreshAsync();
    }

    public void OnNavigatedFrom()
    {
    }

    partial void OnHidePhysicalControllerChanged(bool value) => Save(s => s with { HidePhysicalController = value });

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s with { MinimizeToTray = value });

    partial void OnStartFilterOnLaunchChanged(bool value) => Save(s => s with { StartFilterOnLaunch = value });

    partial void OnLaunchAtSignInChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        StartupRegistration.Apply(value);
        Save(s => s with { LaunchAtSignIn = value });
    }

    partial void OnUseDemoControllerChanged(bool value) => _ = SwitchControllerAsync(value);

    private async Task SwitchControllerAsync(bool useDemo)
    {
        if (_loading)
        {
            return;
        }

        // The filter tracks which controller slot is real, so restart it cleanly around the switch.
        if (_shell.Filter.IsOn)
        {
            await _shell.Filter.StopAsync();
        }

        _shell.Controller.UseDemo(useDemo);
        Save(s => s with { UseDemoController = useDemo });
    }

    partial void OnPreferredSlotChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        if (!_shell.Filter.IsOn)
        {
            _shell.Controller.Pump.PreferredSlot = value;
        }

        Save(s => s with { PreferredSlot = value });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await _shell.Filter.RefreshDriversAsync();
        }
        finally
        {
            IsRefreshing = false;
            RaiseDriverState();
        }
    }

    [RelayCommand]
    private async Task InstallDriversAsync()
    {
        await _shell.Filter.InstallDriversAsync();
        RaiseDriverState();
    }

    [RelayCommand]
    private static void OpenVigemDownload() => OpenExternal(DriverStatus.VigemDownloadUrl);

    [RelayCommand]
    private static void OpenHidHideDownload() => OpenExternal(DriverStatus.HidHideDownloadUrl);

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        OpenExternal(AppPaths.DataDirectory);
    }

    [RelayCommand]
    private async Task RestoreVisibilityAsync()
    {
        if (_shell.Filter.IsOn)
        {
            await _shell.Filter.StopAsync();
        }

        await _shell.Filter.RestoreVisibilityAsync();
        RaiseDriverState();
    }

    [RelayCommand]
    private async Task ResetProfileAsync()
    {
        if (_shell.Filter.IsOn)
        {
            await _shell.Filter.StopAsync();
        }

        _shell.Settings.ResetAll();
        _shell.Filter.ApplyProfile(null);
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (Environment.ProcessPath is not { } path)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path, RelaunchArgument) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined the permission prompt. Keep running as is.
        }
    }

    private static string DescribeDriver(DriverInfo driver) =>
        driver.Problem is { } problem ? problem
        : driver.Installed ? driver.Version is { } version ? $"Installed, version {version}" : "Installed"
        : "Not installed";

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Could not open {target}: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var settings = _shell.Settings.Settings;
            HidePhysicalController = settings.HidePhysicalController;
            MinimizeToTray = settings.MinimizeToTray;
            StartFilterOnLaunch = settings.StartFilterOnLaunch;
            LaunchAtSignIn = settings.LaunchAtSignIn;
            UseDemoController = _shell.Controller.IsDemo;
            PreferredSlot = settings.PreferredSlot;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
        {
            _shell.Settings.Update(change);
        }
    }

    private void RaiseDriverState()
    {
        OnPropertyChanged(nameof(Vigem));
        OnPropertyChanged(nameof(HidHide));
        OnPropertyChanged(nameof(VigemStatus));
        OnPropertyChanged(nameof(HidHideStatus));
        OnPropertyChanged(nameof(HasPendingRestore));
        OnPropertyChanged(nameof(CanInstallDrivers));
        OnPropertyChanged(nameof(InstallButtonText));
    }
}

using System.ComponentModel;
using BehavePad.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.ViewModels;

public enum AppPage
{
    Overview,
    Test,
    Live,
    Setup,
}

public interface IPageViewModel
{
    void OnNavigatedTo();

    void OnNavigatedFrom();
}

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    private AppPage _currentPage;

    public ShellViewModel(ControllerService controller, FilterService filter, SettingsService settings, UpdateService update)
    {
        Controller = controller;
        Filter = filter;
        Settings = settings;
        Update = update;

        Overview = new OverviewViewModel(this);
        Test = new TestViewModel(this);
        Live = new LiveViewModel(this);
        Setup = new SetupViewModel(this);

        Filter.PropertyChanged += OnFilterChanged;
        Controller.PropertyChanged += OnControllerChanged;
        Test.PropertyChanged += OnTestChanged;
        Overview.OnNavigatedTo();
    }

    public ControllerService Controller { get; }

    public FilterService Filter { get; }

    public SettingsService Settings { get; }

    public UpdateService Update { get; }

    public OverviewViewModel Overview { get; }

    public TestViewModel Test { get; }

    public LiveViewModel Live { get; }

    public SetupViewModel Setup { get; }

    public IPageViewModel CurrentViewModel => PageFor(CurrentPage);

    /// <summary>False while the drift test is measuring, so a stray click cannot throw the run away.</summary>
    public bool CanNavigate => !Test.IsRunning;

    public string FilterStatusText => Filter.State switch
    {
        FilterState.On => "Filter on",
        FilterState.Starting => "Turning on",
        FilterState.Stopping => "Turning off",
        _ => Filter.IsArmed && !Controller.IsConnected ? "Waiting for controller" : "Filter off",
    };

    /// <summary>The line under the switch, which explains an armed filter that has nothing to filter yet.</summary>
    public string FilterCaptionText =>
        Filter.State == FilterState.Off && Filter.IsArmed && !Controller.IsConnected
            ? "Turns on by itself"
            : "for games";

    partial void OnCurrentPageChanged(AppPage oldValue, AppPage newValue)
    {
        PageFor(oldValue).OnNavigatedFrom();
        PageFor(newValue).OnNavigatedTo();
    }

    [RelayCommand]
    private void GoToOverview() => CurrentPage = AppPage.Overview;

    [RelayCommand]
    private void GoToTest() => CurrentPage = AppPage.Test;

    [RelayCommand]
    private void GoToLive() => CurrentPage = AppPage.Live;

    [RelayCommand]
    private void GoToSetup() => CurrentPage = AppPage.Setup;

    [RelayCommand]
    private async Task ToggleFilterAsync()
    {
        if (Filter.IsOn)
        {
            await Filter.StopAsync();
        }
        else if (!Filter.IsBusy)
        {
            await Filter.StartAsync();
        }
    }

    [RelayCommand]
    private void DismissMessage() => Filter.Message = null;

    private IPageViewModel PageFor(AppPage page) => page switch
    {
        AppPage.Test => Test,
        AppPage.Live => Live,
        AppPage.Setup => Setup,
        _ => Overview,
    };

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterService.State) or nameof(FilterService.IsArmed))
        {
            RaiseFilterStatus();
        }
    }

    private void OnTestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(CanNavigate));
        }
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControllerService.IsConnected))
        {
            RaiseFilterStatus();
        }
    }

    private void RaiseFilterStatus()
    {
        OnPropertyChanged(nameof(FilterStatusText));
        OnPropertyChanged(nameof(FilterCaptionText));
    }
}

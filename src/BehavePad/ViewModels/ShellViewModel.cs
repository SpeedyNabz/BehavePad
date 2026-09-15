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

    public ShellViewModel(ControllerService controller, FilterService filter, SettingsService settings)
    {
        Controller = controller;
        Filter = filter;
        Settings = settings;

        Overview = new OverviewViewModel(this);
        Test = new TestViewModel(this);
        Live = new LiveViewModel(this);
        Setup = new SetupViewModel(this);

        Filter.PropertyChanged += OnFilterChanged;
        Overview.OnNavigatedTo();
    }

    public ControllerService Controller { get; }

    public FilterService Filter { get; }

    public SettingsService Settings { get; }

    public OverviewViewModel Overview { get; }

    public TestViewModel Test { get; }

    public LiveViewModel Live { get; }

    public SetupViewModel Setup { get; }

    public IPageViewModel CurrentViewModel => PageFor(CurrentPage);

    public string FilterStatusText => Filter.State switch
    {
        FilterState.On => "Filter on",
        FilterState.Starting => "Turning on",
        FilterState.Stopping => "Turning off",
        _ => "Filter off",
    };

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
        if (e.PropertyName == nameof(FilterService.State))
        {
            OnPropertyChanged(nameof(FilterStatusText));
        }
    }
}

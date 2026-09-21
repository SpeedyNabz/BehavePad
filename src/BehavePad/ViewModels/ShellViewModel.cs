using System.ComponentModel;
using BehavePad.Core.Engine;
using BehavePad.Ipc;
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

    public ShellViewModel(ControllerService controller, FilterService filter, SettingsService settings, UpdateService update, AgentLink? link = null)
    {
        Controller = controller;
        Filter = filter;
        Settings = settings;
        Update = update;
        Link = link;
        if (link is not null)
        {
            link.EventReceived += OnAgentEvent;
            link.ConnectedChanged += (_, connected) => AgentConnected = connected;
        }

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

    /// <summary>The pipe to the background agent, or null in the agent's own in-process window.</summary>
    public AgentLink? Link { get; }

    /// <summary>Raised when the agent asks the window to come to the front.</summary>
    public event EventHandler? ActivateRequested;

    [ObservableProperty]
    private bool _agentConnected = true;

    /// <summary>Whether the agent runs with the rights to hide a controller without Windows asking.</summary>
    public bool IsAgentElevated { get; private set; } = Elevation.IsElevated;

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

    /// <summary>Everything the agent sends lands here, on the UI thread.</summary>
    private void OnAgentEvent(object? sender, IpcEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case nameof(AgentEvent.State) when envelope.Read<AgentState>() is { } state:
                IsAgentElevated = state.IsElevated;
                Controller.ApplyRemoteStatus(state.Connected, state.Slot, state.IsDemo, state.ControllerStatusText, state.BatteryText, state.HasXInput);
                Filter.ApplyRemoteState(state);
                Update.ApplyRemoteState(state);
                Settings.ApplyRemoteState(state);
                RaiseFilterStatus();
                break;
            case nameof(AgentEvent.Frame) when envelope.Read<PumpFrame>() is { } frame:
                Controller.ApplyRemoteFrame(frame);
                break;
            case nameof(AgentEvent.Test) when envelope.Read<TestSnapshot>() is { } snapshot:
                Test.ApplySnapshot(snapshot);
                break;
            case nameof(AgentEvent.Activate):
                ActivateRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void RaiseFilterStatus()
    {
        OnPropertyChanged(nameof(FilterStatusText));
        OnPropertyChanged(nameof(FilterCaptionText));
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace BehavePad.Ipc;

/// <summary>
/// The window's end of the pipe. Connects to the agent, raises what it sends on the UI thread, and
/// forwards commands back. If the agent goes away the link reconnects on its own, so closing and
/// reopening the agent never leaves a dead window.
/// </summary>
public sealed class AgentLink : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public AgentLink(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread for everything the agent sends.</summary>
    public event EventHandler<IpcEnvelope>? EventReceived;

    /// <summary>Raised on the UI thread when the link comes up or goes down.</summary>
    public event EventHandler<bool>? ConnectedChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Connects once, so startup can tell straight away whether an agent is already running.</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", AgentContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, _stopping.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            pipe.Dispose();
            return false;
        }

        _pipe = pipe;
        _ = ReadLoopAsync(pipe);
        Post(() => ConnectedChanged?.Invoke(this, true));
        Send(AgentCommand.Hello);
        return true;
    }

    public void Send(AgentCommand command, object? payload = null)
    {
        var line = JsonSerializer.Serialize(IpcEnvelope.For(command, payload), AgentContract.Json) + "\n";
        _ = SendAsync(Encoding.UTF8.GetBytes(line));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _pipe?.Dispose();
        _stopping.Dispose();
        _writeGate.Dispose();
    }

    private async Task SendAsync(byte[] bytes)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            return;
        }

        await _writeGate.WaitAsync();
        try
        {
            await pipe.WriteAsync(bytes, _stopping.Token);
            await pipe.FlushAsync(_stopping.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<IpcEnvelope>(line, AgentContract.Json) is { } envelope)
                {
                    Post(() => EventReceived?.Invoke(this, envelope));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
        }

        pipe.Dispose();
        if (ReferenceEquals(_pipe, pipe))
        {
            _pipe = null;
        }

        Post(() => ConnectedChanged?.Invoke(this, false));
        await ReconnectAsync();
    }

    /// <summary>Keeps trying after the agent stops, so restarting it picks the window back up.</summary>
    private async Task ReconnectAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            await Task.Delay(700, _stopping.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (await ConnectAsync(TimeSpan.FromSeconds(1)))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"BehavePad could not reach the agent: {ex.Message}");
            }
        }
    }

    private void Post(Action action)
    {
        if (!_stopping.IsCancellationRequested)
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}

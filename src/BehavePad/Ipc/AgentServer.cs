using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BehavePad.Ipc;

/// <summary>
/// The agent's end of the pipe. Accepts windows as they open, hands their commands to the agent, and
/// pushes state, frames and test progress back. Everything a caller sees is raised on the agent's thread
/// through <see cref="CommandReceived"/>, so the agent never has to think about locking.
/// </summary>
public sealed class AgentServer : IDisposable
{
    private readonly ConcurrentDictionary<ClientConnection, byte> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<Action> _post;

    /// <param name="post">Runs an action on the agent's own thread, so handlers do not need to be thread safe.</param>
    public AgentServer(Action<Action> post)
    {
        _post = post;
    }

    /// <summary>Raised on the agent's thread for every command a window sends.</summary>
    public event EventHandler<IpcEnvelope>? CommandReceived;

    /// <summary>Raised on the agent's thread when the first window attaches or the last one drops.</summary>
    public event EventHandler? ClientsChanged;

    public bool HasClients => !_clients.IsEmpty;

    public void Start() => _ = AcceptLoopAsync();

    public void Broadcast(AgentEvent notification, object? payload = null)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var line = JsonSerializer.Serialize(IpcEnvelope.For(notification, payload), AgentContract.Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        foreach (var client in _clients.Keys)
        {
            client.Send(bytes);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }

        _clients.Clear();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    AgentContract.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_stopping.Token);
                var client = new ClientConnection(pipe);
                _clients[client] = 0;
                Raise(ClientsChanged);
                _ = ReadLoopAsync(client);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Trace.TraceError($"BehavePad agent could not accept a window: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    private async Task ReadLoopAsync(ClientConnection client)
    {
        try
        {
            using var reader = new StreamReader(client.Pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<IpcEnvelope>(line, AgentContract.Json) is { } envelope)
                {
                    _post(() => CommandReceived?.Invoke(this, envelope));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            Raise(ClientsChanged);
        }
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is not null)
        {
            _post(() => handler(this, EventArgs.Empty));
        }
    }

    /// <summary>One attached window. Writes are serialised so two broadcasts cannot interleave on the wire.</summary>
    private sealed class ClientConnection(NamedPipeServerStream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private volatile bool _dead;

        public NamedPipeServerStream Pipe { get; } = pipe;

        public void Send(byte[] bytes) => _ = SendAsync(bytes);

        public void Dispose()
        {
            _dead = true;
            try
            {
                Pipe.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private async Task SendAsync(byte[] bytes)
        {
            if (_dead)
            {
                return;
            }

            await _writeGate.WaitAsync();
            try
            {
                if (!_dead && Pipe.IsConnected)
                {
                    await Pipe.WriteAsync(bytes);
                    await Pipe.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _dead = true;
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Google.Protobuf;
using HuntReplay.Protocol;
using Microsoft.Win32.SafeHandles;

namespace HuntReplay.App.Services;

internal sealed class HighlightsPipeServer(RecorderService recorder, StudioLog log) : IAsyncDisposable
{
    public const string PipeName = "NvMessageBusBroadcast";
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly ConcurrentDictionary<int, (uint Pid, string Game)> games = new();
    private Task? listener;
    private int nextClient;
    private volatile bool listening;
    public bool IsListening => listening;
    public string? Failure { get; private set; }
    public event Action? StateChanged;
    public IReadOnlyCollection<(uint Pid, string Game)> Games => games.Values.ToArray();

    public void Start()
    {
        if (listener is not null) return;
        try
        {
            // FirstPipeInstance detects NVIDIA/Python/another user's server instead of hijacking it.
            var first = NewPipe(first: true);
            listening = true;
            listener = Task.Run(() => ListenAsync(first));
            log.Write(@"IPC слушает \\.\pipe\NvMessageBusBroadcast");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (SelfInstaller.IsNvidiaActive())
            {
                Failure = "Канал занят активной службой NVIDIA (NvContainer). Разрешите её временную приостановку при запуске или отключите Highlights в оверлее NVIDIA.";
            }
            else
            {
                Failure = "Канал занят другой программой (NVIDIA App / старый сервер). Перехватчик отключён, чтобы не нарушать работу системы. " + ex.Message;
            }
            log.Write(Failure, "ERROR");
        }
    }

    private static NamedPipeServerStream NewPipe(bool first) => new(PipeName, PipeDirection.InOut, 16,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
        (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 65536, 65536);

    private async Task ListenAsync(NamedPipeServerStream initial)
    {
        var next = initial;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await next.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                var connected = next;
                // Keep a listening instance alive for the entire lifetime of the server.
                next = NewPipe(first: false);
                if (clients.Count >= 14) { connected.Dispose(); log.Write("IPC: превышен лимит подключений.", "WARN"); continue; }
                int id = Interlocked.Increment(ref nextClient);
                var task = HandleAsync(connected, id);
                clients[id] = task;
                _ = RemoveWhenDoneAsync(task, id);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failure = ex.Message;
            log.Write("IPC сервер остановлен: " + ex.Message, "ERROR");
        }
        finally { next.Dispose(); listening = false; StateChanged?.Invoke(); }
    }

    private async Task RemoveWhenDoneAsync(Task task, int id)
    {
        try { await task.ConfigureAwait(false); }
        finally { clients.TryRemove(id, out _); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, int id)
    {
        using (pipe)
        {
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var session = new HighlightsSession(pid, () => recorder.NowUs,
                    eventId => recorder.Settings.Replay.IsEnabled(eventId), recorder.TryHighlight, message => log.Write(message));
                session.GameChanged += game =>
                {
                    if (game is null) games.TryRemove(id, out _); else games[id] = (pid, game);
                    StateChanged?.Invoke();
                };
                while (!stop.IsCancellationRequested && await BusFraming.ReadAsync(pipe, stop.Token).ConfigureAwait(false) is { } frame)
                    foreach (var reply in session.Process(frame))
                        await BusFraming.WriteAsync(pipe, reply, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException or InvalidDataException or Win32Exception or InvalidOperationException)
            { log.Write($"IPC клиент {id}: {ex.Message}", "WARN"); }
            finally
            {
                games.TryRemove(id, out _);
                StateChanged?.Invoke();
                log.Write($"IPC клиент {id} отключён; ожидающие клипы не отменены.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (listener is not null) await listener.ConfigureAwait(false);
        await Task.WhenAll(clients.Values.ToArray()).ConfigureAwait(false);
        stop.Dispose();
    }
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

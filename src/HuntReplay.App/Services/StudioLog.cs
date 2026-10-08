using System.Threading.Channels;

namespace HuntReplay.App.Services;

internal sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string Timestamp => Time.ToString("HH:mm:ss");
    public string Line => $"[{Timestamp}] [{Level}] {Message}";
}

internal sealed class StudioLog : IAsyncDisposable
{
    private readonly Channel<LogEntry> queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(512)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task writer;
    public string DirectoryPath { get; }
    public event Action<LogEntry>? Entry;

    public StudioLog(string root)
    {
        DirectoryPath = Path.Combine(root, "logs");
        Directory.CreateDirectory(DirectoryPath);
        writer = Task.Run(WriteLoopAsync);
    }
    public void Write(string message, string level = "INFO")
    {
        var clean = new string(message.Where(c => !char.IsControl(c) || c == '\t').Take(2048).ToArray());
        var entry = new LogEntry(DateTimeOffset.Now, level, clean);
        queue.Writer.TryWrite(entry);
        try { Entry?.Invoke(entry); } catch { /* Logging cannot break a native callback. */ }
    }
    private async Task WriteLoopAsync()
    {
        var path = Path.Combine(DirectoryPath, "studio.log");
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    for (int i = 3; i >= 1; --i)
                    {
                        var from = i == 1 ? path : path + "." + (i - 1);
                        if (File.Exists(from)) File.Move(from, path + "." + i, true);
                    }
                }
                await File.AppendAllTextAsync(path, entry.Line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer.ConfigureAwait(false); }
}

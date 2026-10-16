using System.Threading.Channels;
using HuntReplay.App.Interop;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;
using HuntReplay.Core.Media;
using HuntReplay.Core.Replay;
using HuntReplay.Core.Storage;

namespace HuntReplay.App.Services;

internal sealed record RecorderStatus(bool Running, BufferStatus Buffer, NativeStats Native, string Encoder, int Pending, int Exports);

internal sealed class RecorderService : IAsyncDisposable
{
    private readonly object state = new();
    private readonly object exportGate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly StudioLog log;
    private readonly string libraryRoot;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<ExportJob> exports = Channel.CreateBounded<ExportJob>(new BoundedChannelOptions(2)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task timer;
    private readonly Task exporter;
    private NativeCaptureEngine? engine;
    private PacketRingBuffer? buffer;
    private HighlightPlanner? planner;
    private readonly Dictionary<Guid, Guid> pins = new();
    private bool stopping;
    private const int MaximumExportSnapshots = 2;
    private int exportCount;
    private TaskCompletionSource exportsIdle = CompletedSource();
    private Guid? protectedClip;
    private StudioSettings settings;
    public StudioSettings Settings { get { lock (state) return settings; } }
    public event Action<LibraryClip>? ClipSaved;
    public event Action? Changed;
    private sealed record ExportJob(NativeCaptureEngine Engine, ReplaySnapshot Snapshot, ClipPlan Plan, StudioSettings Settings);

    public RecorderService(StudioSettings settings, StudioLog log, string libraryRoot)
    {
        this.settings = settings.Clone(); this.log = log; this.libraryRoot = libraryRoot;
        timer = Task.Run(TimerLoopAsync);
        exporter = Task.Run(ExportLoopAsync);
    }

    public RecorderStatus Status
    {
        get
        {
            lock (state)
            {
                var native = engine?.Stats ?? default;
                return new(!stopping && native.Running != 0, buffer?.Status ?? default, native,
                    engine?.Description ?? Text.Get("Recorder.EncoderNotRunning"), planner?.PendingCount ?? 0, Volatile.Read(ref exportCount));
            }
        }
    }
    /// <summary>
    /// Applies a UI preference (language, theme) that needs no capture restart. The live settings object is replaced,
    /// never mutated, so readers on other threads keep a consistent snapshot. Returns a copy to persist.
    /// </summary>
    public StudioSettings UpdatePreferences(Action<SystemSettings> change)
    {
        lock (state)
        {
            var next = settings.Clone(); change(next.System); next.Validate();
            settings = next;
            return next.Clone();
        }
    }
    public long NowUs { get { lock (state) return engine?.Stats.ElapsedUs ?? 0; } }
    public void ProtectPlayingClip(Guid? id) { lock (state) protectedClip = id; }

    public async Task StartAsync(IntPtr window, IReadOnlyList<DeviceOption> devices)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (state) if (engine is not null) throw new InvalidOperationException(Text.Get("Recorder.StopFirst"));
            var value = Settings.Clone(); value.Validate();
            if (value.Video.Source == CaptureSource.HuntWindow && window == IntPtr.Zero)
                throw new InvalidOperationException(Text.Get("Recorder.GameWindowMissing"));
            var bytesPerSecond = value.Video.BitrateMbps * 1_000_000L / 8 + 48_000;
            var reserveSeconds = value.Replay.BufferSeconds + value.Replay.MaxSequenceSeconds + value.Replay.CoalesceGapSeconds + 32;
            var physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long limit = Math.Min(bytesPerSecond * reserveSeconds + 32L * 1024 * 1024, Math.Max(64L * 1024 * 1024, physical / 4));
            var ram = new PacketRingBuffer(value.Replay.BufferSeconds, limit);
            var capture = new NativeCaptureEngine();
            capture.Packet += ram.Add;
            capture.Log += (level, message) => log.Write(message, level == 2 ? "ERROR" : level == 1 ? "WARN" : "MEDIA");
            try
            {
                await Task.Run(() => capture.Start(value, value.Video.Source == CaptureSource.HuntWindow ? window : IntPtr.Zero, devices)).ConfigureAwait(false);
                lock (state)
                {
                    engine = capture; buffer = ram; planner = new(value.Replay); pins.Clear(); stopping = false;
                }
            }
            catch { capture.Dispose(); throw; }
            log.Write(Text.Format("Log.BufferStarted", value.Replay.BufferSeconds, limit / 1024 / 1024));
            Changed?.Invoke();
        }
        finally { lifecycle.Release(); }
    }

    public bool TryHighlight(HighlightEvent evt)
    {
        lock (state)
        {
            if (engine is null || buffer is null || planner is null || stopping || engine.Stats.Running == 0 || buffer.Status.Seconds < 0.1) return false;
            try
            {
                var plan = planner.Register(evt);
                if (plan is null) return true;
                if (pins.TryGetValue(plan.Id, out var pin)) buffer.UpdatePin(pin, plan.StartUs);
                else pins[plan.Id] = buffer.Pin(plan.StartUs);
                log.Write(Text.Format("Log.HighlightPlanned", evt.HighlightId, plan.EventTag, plan.EndUs / 1_000_000d), "EVENT");
                Changed?.Invoke();
                return true;
            }
            catch (InvalidOperationException ex) { log.Write(ex.Message, "WARN"); return false; }
        }
    }

    public async Task SaveManualAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        Guid? manualPin = null;
        try
        {
            long end;
            lock (state)
            {
                if (engine is null || buffer is null || stopping || engine.Stats.Running == 0)
                    throw new InvalidOperationException(Text.Get("Recorder.StartFirst"));
                end = buffer.Status.LatestUs;
                manualPin = buffer.Pin(Math.Max(0, end - settings.Replay.BufferSeconds * 1_000_000L));
            }
            // Allow the 120ms WASAPI jitter buffer and AAC frame to catch up to this exact request time.
            await Task.Delay(250).ConfigureAwait(false);
            lock (state)
            {
                var plan = new ClipPlan
                {
                    SessionId = "manual", GroupId = "manual", FirstEventUs = end, LastEventUs = end,
                    StartUs = Math.Max(0, end - settings.Replay.BufferSeconds * 1_000_000L), EndUs = end
                };
                plan.Events.Add("MANUAL");
                if (!QueuePlan(plan)) throw new InvalidOperationException(Text.Get("Recorder.NothingToSave"));
            }
        }
        finally
        {
            lock (state) if (manualPin is { } pin) buffer?.Unpin(pin);
            lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        NativeCaptureEngine? capture;
        lock (state) { capture = engine; stopping = true; }
        if (capture is null) return;
        await Task.Run(capture.Stop).ConfigureAwait(false);
        // Drain in bounded waves, retaining the remaining plans and pins. A slow disk or
        // several simultaneously ready matches must not discard already accepted events.
        while (true)
        {
            int remaining;
            lock (state) { FlushReady(force: true); remaining = planner?.PendingCount ?? 0; }
            Task idle;
            lock (exportGate) idle = exportsIdle.Task;
            await idle.ConfigureAwait(false); // Codec headers stay alive through the last mux.
            if (remaining == 0) break;
        }
        lock (state)
        {
            capture.Dispose(); engine = null; buffer = null; planner = null; pins.Clear(); stopping = false;
        }
        log.Write(Text.Get("Log.BufferStopped"));
        Changed?.Invoke();
    }

    public async Task ApplySettingsAsync(StudioSettings value)
    {
        value.Validate();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            lock (state) settings = value.Clone();
        }
        finally { lifecycle.Release(); }
    }

    private async Task TimerLoopAsync()
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await tick.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                lock (state) if (engine is not null && !stopping) FlushReady(force: false);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Write(Text.Format("Log.SchedulerError", ex.Message), "ERROR"); }
    }

    private void FlushReady(bool force)
    {
        if (planner is null || engine is null || buffer is null) return;
        while (Volatile.Read(ref exportCount) < MaximumExportSnapshots)
        {
            var ready = planner.TakeReady(engine.Stats.ElapsedUs - 250_000, force, maximum: 1);
            if (ready.Count == 0) break;
            var plan = ready[0];
            QueuePlan(plan); // Producers are serialized by state; only the consumer can reduce exportCount.
            if (pins.Remove(plan.Id, out var pin)) buffer.Unpin(pin);
        }
    }

    private bool QueuePlan(ClipPlan plan)
    {
        if (engine is null || buffer is null) return false;
        if (Volatile.Read(ref exportCount) >= MaximumExportSnapshots)
        {
            log.Write(Text.Get("Log.QueueFullManual"), "WARN");
            return false;
        }
        var snapshot = buffer.Snapshot(plan.StartUs, plan.EndUs);
        if (snapshot is null) { log.Write(Text.Format("Log.NoKeyframe", plan.EventTag), "WARN"); return false; }
        lock (exportGate)
        {
            if (exportCount == 0) exportsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            exportCount++;
            if (exports.Writer.TryWrite(new(engine, snapshot, plan, settings.Clone()))) return true;
            exportCount--;
            if (exportCount == 0) exportsIdle.TrySetResult();
        }
        snapshot.Dispose(); // Not queued: its payload chunks may be recycled right away.
        log.Write(Text.Get("Log.QueueFull"), "WARN");
        return false;
    }

    private async Task ExportLoopAsync()
    {
        await foreach (var job in exports.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            string? temp = null;
            try
            {
                var library = new ClipLibrary(job.Settings.Storage.Directory, libraryRoot);
                var now = DateTimeOffset.Now;
                var final = library.NewPath(job.Settings.Storage.FileNameTemplate, job.Plan.EventTag, now);
                ClipLibrary.RequireDiskSpace(final, job.Snapshot.Bytes);
                temp = final + ".partial.mp4";
                // Exclusive reservation prevents accidentally overwriting a concurrently created file.
                using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                job.Engine.Export(job.Snapshot, temp);
                job.Snapshot.Dispose(); // The MP4 holds the data now; free the buffer chunks before slower file work.
                File.Move(temp, final); temp = null;
                var clip = library.Register(final, new ClipMetadata
                {
                    FileName = Path.GetFileName(final), EventTag = job.Plan.EventTag, Events = job.Plan.Events.ToArray(),
                    GroupId = job.Plan.GroupId, CreatedAt = now, DurationSeconds = job.Snapshot.DurationSeconds,
                    Encoder = job.Engine.Description, TruncatedStart = job.Snapshot.TruncatedStart,
                    TruncatedEnd = job.Snapshot.TruncatedEnd
                });
                try { NativeCaptureEngine.SaveThumbnail(final, clip.ThumbnailPath); }
                catch (Exception ex) { log.Write(Text.Format("Log.ThumbnailFailed", ex.Message), "WARN"); }
                log.Write(Text.Format("Log.ClipSaved", clip.Metadata.FileName, clip.Metadata.DurationSeconds), "SAVED");
                if (clip.Metadata.TruncatedStart || clip.Metadata.TruncatedEnd)
                    log.Write(Text.Get("Log.ClipTruncated"), "WARN");
                var protectedIds = new HashSet<Guid> { clip.Metadata.Id };
                lock (state) if (protectedClip is { } id) protectedIds.Add(id);
                try
                {
                    var removed = library.EnforceLimit(job.Settings.Storage, protectedIds);
                    if (removed > 0) log.Write(Text.Format("Log.AutoCleanup", removed));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log.Write(Text.Format("Log.AutoCleanupFailed", ex.Message), "WARN"); }
                ClipSaved?.Invoke(clip);
            }
            catch (Exception ex) { log.Write(Text.Format("Log.ExportFailed", ex.Message), "ERROR"); }
            finally
            {
                job.Snapshot.Dispose(); // Idempotent; covers a failed export.
                if (temp is not null)
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                lock (exportGate)
                {
                    exportCount--;
                    if (exportCount == 0) exportsIdle.TrySetResult();
                }
                Changed?.Invoke();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source;
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await timer.ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        exports.Writer.TryComplete(); await exporter.ConfigureAwait(false);
        stop.Dispose(); lifecycle.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using HuntReplay.App.Interop;
using HuntReplay.App.Services;
using HuntReplay.App.Views;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Media;
using HuntReplay.Core.Storage;
using Microsoft.Win32;

namespace HuntReplay.App.ViewModels;

internal sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly SettingsStore store;
    private readonly RecorderService recorder;
    private readonly HighlightsPipeServer pipe;
    private readonly StudioLog log;
    private readonly bool portable;
    private readonly string libraryRoot;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private Task? watcher;
    private HotkeyService? hotkey;
    private bool userPaused, manuallyStarted;
    private IntPtr lastGameWindow;
    private int retryCount;
    private long nextRetry;
    private StudioSettings settingsEdit;
    private string page = "Dashboard", message = "Все данные остаются на вашем компьютере.", diagnostics = "Запустите проверку, чтобы получить локальный отчёт о готовности системы.";
    private RecorderStatus status = new(false, default, default, "", 0, 0);
    private bool busy, setupReady;
    private string search = "";
    private ClipViewModel? selectedClip;
    public IReadOnlyList<DeviceOption> Devices { get; private set; } = [];
    public DeviceSelectionViewModel MonitorSelection { get; }
    public DeviceSelectionViewModel OutputSelection { get; }
    public DeviceSelectionViewModel MicrophoneSelection { get; }
    public ObservableCollection<LogEntry> Events { get; } = [];
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ICollectionView FilteredClips { get; }
    public IEnumerable<ClipViewModel> RecentClips => Clips.Take(3);
    public bool HasClips => Clips.Count > 0;
    public int ClipCount => Clips.Count;
    public string Page { get => page; set { if (Set(ref page, value)) Notify(nameof(PageTitle)); } }
    public string PageTitle => Page switch { "Gallery" => "Библиотека хайлайтов", "Settings" => "Настройки записи", "Diagnostics" => "Готовность системы", _ => "Обзор" };
    public string Message { get => message; private set => Set(ref message, value); }
    public string Diagnostics { get => diagnostics; private set => Set(ref diagnostics, value); }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public StudioSettings SettingsEdit
    {
        get => settingsEdit;
        private set { if (Set(ref settingsEdit, value)) RefreshDeviceSelections(); }
    }
    public string Search { get => search; set { if (Set(ref search, value)) FilteredClips.Refresh(); } }
    public ClipViewModel? SelectedClip
    {
        get => selectedClip;
        set { if (Set(ref selectedClip, value)) { recorder.ProtectPlayingClip(value?.Clip.Metadata.Id); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsBufferActive => status.Running && status.Buffer.Seconds > 0;
    public bool IsGameConnected => pipe.Games.Count > 0;
    public string GameStatus => IsGameConnected ? string.Join(", ", pipe.Games.Select(g => g.Game).Distinct()) + " подключён" :
        lastGameWindow != IntPtr.Zero ? "Окно игры найдено · ожидание SDK" : "Ожидание игры…";
    public string BufferStatus => status.Running ? (IsBufferActive ? "Буфер записывает" : "Прогрев буфера…") : "Буфер остановлен";
    public string BufferTime => TimeSpan.FromSeconds(Math.Max(0, status.Buffer.Seconds)).ToString(@"mm\:ss");
    public string BufferTarget => $"/ {TimeSpan.FromSeconds(recorder.Settings.Replay.BufferSeconds):mm\\:ss}";
    public double BufferFill => Math.Clamp(status.Buffer.Seconds / recorder.Settings.Replay.BufferSeconds * 100, 0, 100);
    public string MemoryText => $"{status.Buffer.Bytes / 1024d / 1024:F1} МБ RAM";
    public string GpuStatus => status.Running ? status.Encoder :
        (Devices.FirstOrDefault(d => d.Kind == 3)?.Name ?? "GPU не определён") + " · ожидание";
    public string EncodingModeLabel => recorder.Settings.Video.Encoder == VideoEncoder.X264 ? "CPU x264 · ВЫБРАН ЯВНО" : "GPU ENCODING";
    public string VideoStatus => status.Running ? $"{status.Native.Width} × {status.Native.Height} · {recorder.Settings.Video.Fps} FPS · {recorder.Settings.Video.Codec}" :
        "Windows Graphics Capture · Direct3D 11";
    public string CaptureDetail => status.Buffer.MemoryPressure ? "Лимит RAM: начало клипа может быть укорочено" :
        status.Native.SourceStale != 0 ? "Источник не обновлялся >2 с (статичный экран / свёрнутое окно)" :
        status.Running ? $"Сжатых кадров: {status.Native.Frames:N0} · пропущено: {status.Native.DroppedFrames:N0}" : "";
    public string PendingText => status.Pending > 0 ? $"{status.Pending} эпизод(а) · ожидается постзапись" :
        status.Exports > 0 ? $"Сохранение MP4 · {status.Exports}" : "Готов к следующему событию";
    public string ToggleLabel => status.Running ? "Остановить буфер" : "Запустить буфер";
    public string ManualLabel => $"Сохранить последние {recorder.Settings.Replay.BufferSeconds} с";
    public string HotkeyLabel => "F" + (recorder.Settings.System.HotkeyVirtualKey - 0x6F);
    public string SetupLabel => setupReady ? "HIGHLIGHTS ГОТОВ" : "НУЖНА УСТАНОВКА";
    public string PipeLabel => pipe.IsListening ? "IPC • ONLINE" : SelfInstaller.IsNvidiaActive() ? "IPC • NVIDIA АКТИВЕН" : "IPC • OFFLINE";
    public string LibrarySummary => $"{Clips.Count} клип(ов) · {Clips.Sum(c => c.Clip.Metadata.Bytes) / 1_000_000_000d:F2} ГБ";
    public bool CloseToTray => recorder.Settings.System.CloseToTray;
    public ICommand ToggleBufferCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand OpenInPlayerCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand SelectRecentCommand { get; }
    public event Action? ClosePlayer;

    public IReadOnlyList<Choice<CaptureSource>> Sources { get; } = [new(CaptureSource.HuntWindow, "Окно Hunt / подключённой игры"), new(CaptureSource.Monitor, "Выбранный монитор (весь экран)")];
    public IReadOnlyList<Choice<VideoResolution>> Resolutions { get; } = [new(VideoResolution.Native, "Авто · исходное разрешение"), new(VideoResolution.FullHd, "1920 × 1080"), new(VideoResolution.Qhd, "2560 × 1440"), new(VideoResolution.Uhd, "3840 × 2160")];
    public IReadOnlyList<Choice<VideoEncoder>> Encoders { get; } = [new(VideoEncoder.Auto, "Авто · GPU захватываемого монитора"), new(VideoEncoder.Amf, "AMD AMF"), new(VideoEncoder.Nvenc, "NVIDIA NVENC"), new(VideoEncoder.Qsv, "Intel QSV"), new(VideoEncoder.X264, "CPU x264 · только H.264")];
    public IReadOnlyList<Choice<VideoCodec>> Codecs { get; } = [new(VideoCodec.H264, "H.264 / AVC · совместимость"), new(VideoCodec.Hevc, "H.265 / HEVC · требуется поддержка GPU"), new(VideoCodec.Av1, "AV1 · требуется поддержка GPU")];
    public IReadOnlyList<int> FrameRates { get; } = [60, 120, 30];
    public IReadOnlyList<int> GopLengths { get; } = [1, 2];
    public IReadOnlyList<Choice<AudioMode>> AudioModes { get; } = [new(AudioMode.Mix, "Один стереотрек · игра + микрофон"), new(AudioMode.SeparateTracks, "Две дорожки · игра / микрофон")];
    public IReadOnlyList<Choice<int>> DiskLimits { get; } = [new(15, "15 ГБ"), new(30, "30 ГБ"), new(60, "60 ГБ"), new(0, "Без ограничений")];
    public IReadOnlyList<Choice<int>> Hotkeys { get; } = Enumerable.Range(0x70, 12).Select(k => new Choice<int>(k, "F" + (k - 0x6F))).ToArray();

    public MainViewModel(SettingsStore store, RecorderService recorder, HighlightsPipeServer pipe, StudioLog log, bool portable, string libraryRoot)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.store = store; this.recorder = recorder; this.pipe = pipe; this.log = log; this.portable = portable; this.libraryRoot = libraryRoot;
        settingsEdit = recorder.Settings.Clone();
        MonitorSelection = new(0, "Основной монитор · автоматически",
            () => SettingsEdit.Video.MonitorDevice, value => SettingsEdit.Video.MonitorDevice = value);
        OutputSelection = new(1, "Устройство вывода Windows по умолчанию",
            () => SettingsEdit.Audio.OutputDeviceId, value => SettingsEdit.Audio.OutputDeviceId = value);
        MicrophoneSelection = new(2, "Микрофон Windows по умолчанию",
            () => SettingsEdit.Audio.MicrophoneDeviceId, value => SettingsEdit.Audio.MicrophoneDeviceId = value);
        FilteredClips = CollectionViewSource.GetDefaultView(Clips);
        FilteredClips.Filter = value => value is ClipViewModel c &&
            (string.IsNullOrWhiteSpace(Search) || c.FileName.Contains(Search, StringComparison.OrdinalIgnoreCase) || c.EventLabel.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ToggleBufferCommand = new AsyncCommand(ToggleAsync, Error, () => !Busy);
        SaveCommand = new AsyncCommand(recorder.SaveManualAsync, Error, () => IsBufferActive && !Busy);
        ApplySettingsCommand = new AsyncCommand(ApplyAsync, Error, () => !Busy);
        BrowseCommand = new RelayCommand(_ => Safe(Browse));
        OpenFolderCommand = new RelayCommand(_ => Safe(() => DesktopIntegration.OpenFolder(recorder.Settings.Storage.Directory)));
        RefreshCommand = new AsyncCommand(RefreshClipsAsync, Error);
        DeleteCommand = new RelayCommand(_ => Safe(DeleteClip), _ => SelectedClip is not null);
        RenameCommand = new RelayCommand(_ => Safe(RenameClip), _ => SelectedClip is not null);
        RevealCommand = new RelayCommand(_ => Safe(() => DesktopIntegration.RevealFile(SelectedClip!.Clip.FullPath)), _ => SelectedClip is not null);
        OpenInPlayerCommand = new RelayCommand(_ => Safe(() => DesktopIntegration.OpenWithDefaultPlayer(SelectedClip!.Clip.FullPath)), _ => SelectedClip is not null);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync, Error, () => !Busy);
        SaveReportCommand = new RelayCommand(_ => Safe(SaveReport));
        InstallCommand = new AsyncCommand(InstallAsync, Error, () => !Busy);
        UninstallCommand = new AsyncCommand(UninstallAsync, Error, () => !Busy);
        OpenLogsCommand = new RelayCommand(_ => Safe(() => DesktopIntegration.OpenFolder(log.DirectoryPath)));
        SelectRecentCommand = new RelayCommand(value => { if (value is ClipViewModel clip) { Page = "Gallery"; SelectedClip = clip; } });
        log.Entry += OnLog;
        recorder.Changed += OnChanged;
        pipe.StateChanged += OnChanged;
        recorder.ClipSaved += OnSaved;
    }

    public async Task InitializeAsync(Window window)
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            hotkey = new HotkeyService(window, () => SaveCommand.Execute(null));
            try { hotkey.Register(recorder.Settings.System.HotkeyVirtualKey); } catch (Exception ex) { Error(ex); }
            try { DesktopIntegration.ConfigureStartup(recorder.Settings.System.StartWithWindows, portable); } catch (Exception ex) { Error(ex); }
            await ReloadDevicesAsync();
            try { setupReady = (await Task.Run(SelfInstaller.Check)).Ready; } catch (Exception ex) { Error(ex); }
            await RefreshClipsAsync();
            RefreshStatus();
            if (!stop.IsCancellationRequested) watcher = Task.Run(WatchGameAsync);
        }
        finally { Busy = false; operation.Release(); }
    }

    private async Task ReloadDevicesAsync()
    {
        try { Devices = await Task.Run(NativeCaptureEngine.EnumerateDevices); }
        catch (Exception ex) { Error(ex); Devices = []; }
        RefreshDeviceSelections();
        Notify(nameof(GpuStatus));
    }

    private void RefreshDeviceSelections()
    {
        MonitorSelection.Refresh(Devices);
        OutputSelection.Refresh(Devices);
        MicrophoneSelection.Refresh(Devices);
    }

    private async Task ToggleAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            if (recorder.Status.Running)
            {
                userPaused = true; manuallyStarted = false;
                await recorder.StopAsync();
            }
            else
            {
                userPaused = false; manuallyStarted = true; retryCount = 0;
                await recorder.StopAsync();
                await StartCurrentAsync();
            }
        }
        finally { Busy = false; operation.Release(); RefreshStatus(); }
    }
    private async Task StartCurrentAsync()
    {
        var pid = pipe.Games.FirstOrDefault().Pid;
        var window = GameWindow.Find((int)pid);
        var devices = await Task.Run(NativeCaptureEngine.EnumerateDevices);
        await recorder.StartAsync(window, devices);
    }

    private async Task WatchGameAsync()
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        try
        {
            while (await tick.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                // All automation state and user operations are serialized on the UI dispatcher.
                await dispatcher.InvokeAsync(async () =>
                {
                    if (stop.IsCancellationRequested || Busy || !await operation.WaitAsync(0)) return;
                    try
                    {
                        var window = GameWindow.Find((int)pipe.Games.FirstOrDefault().Pid);
                        if (window != lastGameWindow) { userPaused = false; retryCount = 0; nextRetry = 0; }
                        var previous = lastGameWindow; lastGameWindow = window;
                        var current = recorder.Status;
                        if (!current.Running && current.Native.ElapsedUs > 0)
                        {
                            await recorder.StopAsync();
                            if (window != IntPtr.Zero || recorder.Settings.Video.Source == CaptureSource.Monitor) retryCount++;
                            nextRetry = Environment.TickCount64 + 5000;
                        }
                        if (recorder.Settings.System.AutoBufferWithGame && !manuallyStarted && previous != IntPtr.Zero && window != previous)
                            await recorder.StopAsync();
                        if (window == IntPtr.Zero && previous != IntPtr.Zero && recorder.Settings.Video.Source == CaptureSource.HuntWindow)
                            manuallyStarted = false;
                        bool sourceAvailable = recorder.Settings.Video.Source == CaptureSource.Monitor || window != IntPtr.Zero;
                        bool wanted = manuallyStarted || (recorder.Settings.System.AutoBufferWithGame && window != IntPtr.Zero);
                        if (wanted && sourceAvailable && !userPaused && !recorder.Status.Running && retryCount < 3 && Environment.TickCount64 >= nextRetry)
                        {
                            Busy = true;
                            try { await StartCurrentAsync(); }
                            catch (Exception ex) { retryCount++; nextRetry = Environment.TickCount64 + retryCount * 15000; Error(ex); }
                            finally { Busy = false; }
                        }
                        if (retryCount >= 3 && wanted && !recorder.Status.Running)
                            Message = "Автоповтор остановлен после трёх ошибок. Проверьте диагностику и нажмите «Запустить буфер».";
                        RefreshStatus();
                    }
                    catch (Exception ex) { Error(ex); }
                    finally { operation.Release(); }
                }).Task.Unwrap().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task ApplyAsync()
    {
        if (Application.Current.MainWindow is { } root && HasFormErrors(root, new HashSet<DependencyObject>()))
            throw new InvalidDataException("Исправьте поля с некорректными числами перед сохранением настроек.");
        var updated = SettingsEdit.Clone(); updated.Validate();
        await operation.WaitAsync(); Busy = true;
        var old = recorder.Settings.Clone();
        var wasRunning = recorder.Status.Running;
        bool committed = false;
        try
        {
            hotkey?.Register(updated.System.HotkeyVirtualKey);
            DesktopIntegration.ConfigureStartup(updated.System.StartWithWindows, portable);
            store.Save(updated);
            await recorder.ApplySettingsAsync(updated);
            committed = true;
            SettingsEdit = updated.Clone();
            await ReloadDevicesAsync();
            await RefreshClipsAsync();
            Message = "Настройки сохранены в config.json.";
            if (wasRunning) await StartCurrentAsync();
            log.Write(wasRunning
                ? "Настройки применены. Буфер перезапущен с новым форматом."
                : "Настройки применены. Буфер не запущен; ожидание игры или ручного запуска.");
        }
        catch
        {
            // If persistence failed, preserve the previous functional shortcut/startup entry.
            if (!committed)
            {
                hotkey?.Register(old.System.HotkeyVirtualKey);
                DesktopIntegration.ConfigureStartup(old.System.StartWithWindows, portable);
            }
            throw;
        }
        finally { Busy = false; operation.Release(); RefreshStatus(); }
    }

    private static bool HasFormErrors(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return false;
        if (System.Windows.Controls.Validation.GetHasError(node)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            if (HasFormErrors(child, visited)) return true;
        if (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); ++i)
                if (HasFormErrors(System.Windows.Media.VisualTreeHelper.GetChild(node, i), visited)) return true;
        return false;
    }

    private ClipLibrary Library() => new(recorder.Settings.Storage.Directory, libraryRoot);

    public async Task RefreshClipsAsync()
    {
        var directory = recorder.Settings.Storage.Directory;
        var selectedId = SelectedClip?.Clip.Metadata.Id;
        var result = await Task.Run(() =>
        {
            var library = new ClipLibrary(directory, libraryRoot);
            try
            {
                // Moves 0.1.2 sidecars out of the clips folder and tidies records of deleted MP4 files.
                var report = library.Maintain();
                if (report.Adopted + report.Relocated + report.Pruned > 0)
                    log.Write($"Библиотека клипов: перенесено из папки клипов {report.Adopted}, перепривязано после переименования папки {report.Relocated}, удалено служебных записей {report.Pruned}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log.Write("Библиотека клипов: обслуживание пропущено: " + ex.Message, "WARN"); }
            return library.List().Take(500).Select(c => new ClipViewModel(c)).ToArray();
        });
        Clips.Clear(); foreach (var clip in result) Clips.Add(clip);
        SelectedClip = Clips.FirstOrDefault(c => c.Clip.Metadata.Id == selectedId);
        Notify(nameof(RecentClips)); Notify(nameof(HasClips)); Notify(nameof(ClipCount)); Notify(nameof(LibrarySummary));
    }
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Папка для хайлайтов", InitialDirectory = SettingsEdit.Storage.Directory };
        if (dialog.ShowDialog() == true) { SettingsEdit.Storage.Directory = dialog.FolderName; SettingsEdit = SettingsEdit.Clone(); }
    }
    private void DeleteClip()
    {
        if (SelectedClip is not { } clip || MessageBox.Show("Удалить клип без возможности восстановления?\n\n" + clip.FileName,
            "Удаление хайлайта", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ClosePlayer?.Invoke();
        Library().Delete(clip.Clip);
        Clips.Remove(clip); SelectedClip = null;
        Notify(nameof(RecentClips)); Notify(nameof(HasClips)); Notify(nameof(ClipCount)); Notify(nameof(LibrarySummary));
    }
    private void RenameClip()
    {
        if (SelectedClip is not { } clip) return;
        var dialog = new InputDialog("Переименовать клип", clip.FileName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        ClosePlayer?.Invoke();
        var renamed = Library().Rename(clip.Clip, dialog.Value);
        var index = Clips.IndexOf(clip); var model = new ClipViewModel(renamed);
        Clips[index] = model; SelectedClip = model; Notify(nameof(RecentClips));
    }
    private async Task DiagnoseAsync()
    {
        await operation.WaitAsync(); Busy = true; Page = "Diagnostics";
        try { Diagnostics = "Проверяется реальный энкодер. Это может занять несколько секунд…";
            Diagnostics = await Task.Run(() => DiagnosticsService.Run(recorder.Settings.Clone(), recorder, pipe)); }
        finally { Busy = false; operation.Release(); }
    }
    private void SaveReport()
    {
        var dialog = new SaveFileDialog { FileName = "HuntReplay-diagnostics.txt", Filter = "Текстовый отчёт|*.txt" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, Diagnostics);
    }
    private async Task InstallAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            var status = await Task.Run(SelfInstaller.Check);
            var wizard = new SetupWizard(status) { Owner = Application.Current.MainWindow };
            wizard.ShowDialog();
            setupReady = (await Task.Run(SelfInstaller.Check)).Ready; Notify(nameof(SetupLabel));
        }
        finally { Busy = false; operation.Release(); }
    }
    private async Task UninstallAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            if (MessageBox.Show("Восстановить значения реестра до установки Studio? Автоматические Highlights перестанут поступать. Клипы останутся.",
                "Восстановление NVIDIA", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var code = await SelfInstaller.RunElevatedAsync(uninstall: true);
            if (code != 0) throw new IOException("Восстановление не выполнено. См. сообщение elevated-установщика.");
            setupReady = false; Notify(nameof(SetupLabel));
        }
        finally { Busy = false; operation.Release(); }
    }
    private void OnLog(LogEntry entry) => OnUi(() =>
    {
        Events.Insert(0, entry);
        while (Events.Count > 250) Events.RemoveAt(Events.Count - 1);
        if (entry.Level == "ERROR") Message = entry.Message;
    });
    private void OnChanged() => OnUi(RefreshStatus);
    private void OnSaved(LibraryClip clip) => OnUi(async () =>
    {
        try { await RefreshClipsAsync(); DesktopIntegration.NotifySaved(clip, recorder.Settings, log); Message = "Сохранён: " + clip.Metadata.FileName; }
        catch (Exception ex) { Error(ex); }
    });
    private void OnUi(Action action)
    {
        if (!dispatcher.HasShutdownStarted && !stop.IsCancellationRequested) dispatcher.BeginInvoke(action);
    }
    private void RefreshStatus()
    {
        status = recorder.Status;
        foreach (var property in new[] { nameof(IsBufferActive), nameof(IsGameConnected), nameof(GameStatus), nameof(BufferStatus), nameof(BufferTime),
            nameof(BufferTarget), nameof(BufferFill), nameof(MemoryText), nameof(GpuStatus), nameof(VideoStatus), nameof(EncodingModeLabel), nameof(CaptureDetail), nameof(PendingText),
            nameof(ToggleLabel), nameof(ManualLabel), nameof(HotkeyLabel), nameof(SetupLabel), nameof(PipeLabel) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }
    public void Error(Exception ex) { Message = ex.Message; log.Write(ex.Message, "ERROR"); }
    // A preview failure is not an export failure: the saved MP4 stays as written, so this is a warning, not an error.
    public void PlaybackFailed(int hresult, string fileName)
    {
        Message = "Клип сохранён: " + fileName + ". Встроенный проигрыватель не открыл его (" + PlaybackDiagnostics.Code(hresult) + ").";
        log.Write(PlaybackDiagnostics.LogLine(hresult, fileName), "WARN");
    }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (watcher is not null) await watcher;
        await operation.WaitAsync(); // Wait for a user-initiated start/settings/probe/UAC workflow too.
        Busy = true;
        hotkey?.Dispose();
        recorder.Changed -= OnChanged; recorder.ClipSaved -= OnSaved; pipe.StateChanged -= OnChanged; log.Entry -= OnLog;
        stop.Dispose(); operation.Dispose();
    }
}

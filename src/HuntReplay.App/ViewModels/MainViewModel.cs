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
    private string page = "Dashboard";
    private string? customMessage, customDiagnostics;
    private RecorderStatus status = new(false, default, default, "", 0, 0);
    private bool busy, setupReady;
    private bool hasUpdate, isCheckingUpdate;
    private string? latestVersion, updateUrl;
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
    public LocalizationService Loc => LocalizationService.Instance;
    public ThemeService ThemeSvc => ThemeService.Instance;
    public string LanguageButtonLabel => Loc.IsRussian ? "RU  /  EN" : "EN  /  RU";
    public string ThemeButtonLabel => ThemeSvc.IsDark ? (Loc.IsRussian ? "🌙 ТЁМНАЯ" : "🌙 DARK") : (Loc.IsRussian ? "☀️ СВЕТЛАЯ" : "☀️ LIGHT");
    public string Page
    {
        get => page;
        set
        {
            if (page == value) return;
            if (page == "Settings" && HasUnsavedSettings)
            {
                _ = HandleUnsavedAndNavigateAsync(value);
                return;
            }
            if (Set(ref page, value)) Notify(nameof(PageTitle));
        }
    }

    public async Task<bool> ConfirmLeaveSettingsAsync()
    {
        if (!HasUnsavedSettings) return true;
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            Loc["Settings.UnsavedChangesPrompt"],
            Loc["Settings.UnsavedChangesTitle"],
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            return await SaveSettingsDirectAsync();
        }
        if (result == MessageBoxResult.No)
        {
            RevertSettings();
            return true;
        }
        return false;
    }

    private async Task HandleUnsavedAndNavigateAsync(string targetPage)
    {
        if (await ConfirmLeaveSettingsAsync())
        {
            if (Set(ref page, targetPage)) Notify(nameof(PageTitle));
        }
        else
        {
            // Re-trigger notification to ensure radio button stays on Settings
            Notify(nameof(Page));
        }
    }

    public string PageTitle => Page switch
    {
        "Gallery" => Loc["PageTitle.Gallery"],
        "Settings" => Loc["PageTitle.Settings"],
        "Diagnostics" => Loc["PageTitle.Diagnostics"],
        _ => Loc["PageTitle.Dashboard"]
    };
    public string Message
    {
        get => customMessage ?? Loc["Footer.DefaultMessage"];
        private set { if (Set(ref customMessage, value)) Notify(); }
    }
    public string Diagnostics
    {
        get => customDiagnostics ?? Loc["Footer.DiagnosticsDefault"];
        private set { if (Set(ref customDiagnostics, value)) Notify(); }
    }
    public string SelectedEventLabel => SelectedClip?.EventLabel ?? Loc["Gallery.NoClipSelected"];
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
        set { if (Set(ref selectedClip, value)) { recorder.ProtectPlayingClip(value?.Clip.Metadata.Id); Notify(nameof(SelectedEventLabel)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsBufferActive => status.Running && status.Buffer.Seconds > 0;
    public bool IsGameConnected => pipe.Games.Count > 0;
    public string GameStatus => IsGameConnected ? string.Format(Loc["Dash.GameConnected"], string.Join(", ", pipe.Games.Select(g => g.Game).Distinct())) :
        lastGameWindow != IntPtr.Zero ? Loc["Dash.GameWindowFound"] : Loc["Dash.WaitingForGame"];
    public string BufferStatus => status.Running ? (IsBufferActive ? Loc["Dash.BufferWriting"] : Loc["Dash.BufferWarming"]) : Loc["Dash.BufferStopped"];
    public string BufferTime => TimeSpan.FromSeconds(Math.Max(0, status.Buffer.Seconds)).ToString(@"mm\:ss");
    public string BufferTarget => $"/ {TimeSpan.FromSeconds(recorder.Settings.Replay.BufferSeconds):mm\\:ss}";
    public double BufferFill => Math.Clamp(status.Buffer.Seconds / recorder.Settings.Replay.BufferSeconds * 100, 0, 100);
    public string MemoryText => $"{status.Buffer.Bytes / 1024d / 1024:F1} {Loc["Dash.RamSuffix"]}";
    public string GpuStatus => status.Running ? status.Encoder :
        string.Format(Loc["Dash.GpuWaiting"], Devices.FirstOrDefault(d => d.Kind == 3)?.Name ?? Loc["Dash.GpuUnknown"]);
    public string EncodingModeLabel => recorder.Settings.Video.Encoder == VideoEncoder.X264 ? Loc["Dash.CpuExplicit"] : Loc["Dash.GpuEncoding"];
    public string VideoStatus => status.Running ? $"{status.Native.Width} × {status.Native.Height} · {recorder.Settings.Video.Fps} FPS · {recorder.Settings.Video.Codec}" :
        Loc["Dash.WgcD3D"];
    public string CaptureDetail => status.Buffer.MemoryPressure ? Loc["Dash.MemoryPressure"] :
        status.Native.SourceStale != 0 ? Loc["Dash.SourceStale"] :
        status.Running ? string.Format(Loc["Dash.FramesDetail"], status.Native.Frames, status.Native.DroppedFrames) : "";
    public string PendingText => status.Pending > 0 ? string.Format(Loc["Dash.PendingEpisodes"], status.Pending) :
        status.Exports > 0 ? string.Format(Loc["Dash.SavingMp4"], status.Exports) : Loc["Dash.ReadyForNext"];
    public string ToggleLabel => status.Running ? Loc["Dash.StopBuffer"] : Loc["Dash.StartBuffer"];
    public string ManualLabel => string.Format(Loc["Dash.SaveManual"], recorder.Settings.Replay.BufferSeconds);
    public string HotkeyLabel => "F" + (recorder.Settings.System.HotkeyVirtualKey - 0x6F);
    public string SetupLabel => setupReady ? Loc["Header.SetupReady"] : Loc["Header.SetupNeeded"];
    public string PipeLabel => pipe.IsListening ? "IPC • ONLINE" : SelfInstaller.IsNvidiaActive() ? "IPC • NVIDIA " + (Loc.IsEnglish ? "ACTIVE" : "АКТИВЕН") : "IPC • OFFLINE";
    public string LibrarySummary => string.Format(Loc["Gallery.Summary"], Clips.Count, Clips.Sum(c => c.Clip.Metadata.Bytes) / 1_000_000_000d);
    public bool CloseToTray => recorder.Settings.System.CloseToTray;
    public bool HasUpdate => hasUpdate;
    public bool IsCheckingUpdate => isCheckingUpdate;
    public string? LatestVersion => latestVersion;
    public string UpdateButtonLabel => string.IsNullOrWhiteSpace(latestVersion) ? Loc["Header.UpdateAvailable"] : string.Format(Loc["Header.UpdateAvailableVersion"], latestVersion);
    public string UpdateStatusLabel => isCheckingUpdate ? (Loc.IsEnglish ? "Checking for updates…" : "Проверка обновлений…") :
        hasUpdate ? (Loc.IsEnglish ? $"Version {latestVersion} is available" : $"Доступна версия {latestVersion}") : (Loc.IsEnglish ? "Latest version installed" : "Установлена последняя версия");
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
    public ICommand CheckUpdateCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand ToggleLanguageCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public event Action? ClosePlayer;

    private readonly Choice<CaptureSource>[] sources = [
        new(CaptureSource.HuntWindow, ""),
        new(CaptureSource.Monitor, "")
    ];
    private readonly Choice<VideoResolution>[] resolutions = [
        new(VideoResolution.Native, ""),
        new(VideoResolution.FullHd, "1920 × 1080"),
        new(VideoResolution.Qhd, "2560 × 1440"),
        new(VideoResolution.Uhd, "3840 × 2160")
    ];
    private readonly Choice<VideoEncoder>[] encoders = [
        new(VideoEncoder.Auto, ""),
        new(VideoEncoder.Amf, "AMD AMF"),
        new(VideoEncoder.Nvenc, "NVIDIA NVENC"),
        new(VideoEncoder.Qsv, "Intel QSV"),
        new(VideoEncoder.X264, "")
    ];
    private readonly Choice<VideoCodec>[] codecs = [
        new(VideoCodec.H264, ""),
        new(VideoCodec.Hevc, ""),
        new(VideoCodec.Av1, "")
    ];
    private readonly Choice<AudioMode>[] audioModes = [
        new(AudioMode.Mix, ""),
        new(AudioMode.SeparateTracks, "")
    ];
    private readonly Choice<int>[] diskLimits = [
        new(15, ""),
        new(30, ""),
        new(60, ""),
        new(0, "")
    ];

    public IReadOnlyList<Choice<CaptureSource>> Sources => sources;
    public IReadOnlyList<Choice<VideoResolution>> Resolutions => resolutions;
    public IReadOnlyList<Choice<VideoEncoder>> Encoders => encoders;
    public IReadOnlyList<Choice<VideoCodec>> Codecs => codecs;
    public IReadOnlyList<int> FrameRates { get; } = [60, 120, 30];
    public IReadOnlyList<int> GopLengths { get; } = [1, 2];
    public IReadOnlyList<Choice<AudioMode>> AudioModes => audioModes;
    public IReadOnlyList<Choice<int>> DiskLimits => diskLimits;
    public IReadOnlyList<Choice<int>> Hotkeys { get; } = Enumerable.Range(0x70, 12).Select(k => new Choice<int>(k, "F" + (k - 0x6F))).ToArray();

    private void RebuildChoices()
    {
        bool en = Loc.IsEnglish;
        sources[0].Label = en ? "Hunt / Connected Game Window" : "Окно Hunt / подключённой игры";
        sources[1].Label = en ? "Selected Monitor (Entire Screen)" : "Выбранный монитор (весь экран)";

        resolutions[0].Label = en ? "Auto · Native Resolution" : "Авто · исходное разрешение";

        encoders[0].Label = en ? "Auto · GPU of captured monitor" : "Авто · GPU захватываемого монитора";
        encoders[4].Label = en ? "CPU x264 · H.264 only" : "CPU x264 · только H.264";

        codecs[0].Label = en ? "H.264 / AVC · Maximum Compatibility" : "H.264 / AVC · совместимость";
        codecs[1].Label = en ? "H.265 / HEVC · Requires GPU Support" : "H.265 / HEVC · требуется поддержка GPU";
        codecs[2].Label = en ? "AV1 · Requires GPU Support" : "AV1 · требуется поддержка GPU";

        audioModes[0].Label = en ? "Single stereo track · Game + Mic" : "Один стереотрек · игра + микрофон";
        audioModes[1].Label = en ? "Two separate tracks · Game / Mic" : "Две дорожки · игра / микрофон";

        diskLimits[0].Label = en ? "15 GB" : "15 ГБ";
        diskLimits[1].Label = en ? "30 GB" : "30 ГБ";
        diskLimits[2].Label = en ? "60 GB" : "60 ГБ";
        diskLimits[3].Label = en ? "Unlimited" : "Без ограничений";
    }

    public MainViewModel(SettingsStore store, RecorderService recorder, HighlightsPipeServer pipe, StudioLog log, bool portable, string libraryRoot)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.store = store; this.recorder = recorder; this.pipe = pipe; this.log = log; this.portable = portable; this.libraryRoot = libraryRoot;
        Loc.SetLanguage(recorder.Settings.System.Language);
        settingsEdit = recorder.Settings.Clone();
        MonitorSelection = new(0,
            () => Loc["Choice.MonitorDefault"],
            () => Loc.IsEnglish ? "Unavailable · saved monitor is not connected" : "Недоступно · сохранённый монитор не подключён",
            () => SettingsEdit.Video.MonitorDevice, value => SettingsEdit.Video.MonitorDevice = value);
        OutputSelection = new(1,
            () => Loc["Choice.OutputDefault"],
            () => Loc.IsEnglish ? "Unavailable · saved device is not connected" : "Недоступно · сохранённое устройство не подключено",
            () => SettingsEdit.Audio.OutputDeviceId, value => SettingsEdit.Audio.OutputDeviceId = value);
        MicrophoneSelection = new(2,
            () => Loc["Choice.MicDefault"],
            () => Loc.IsEnglish ? "Unavailable · saved device is not connected" : "Недоступно · сохранённое устройство не подключено",
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
        CheckUpdateCommand = new AsyncCommand(CheckUpdateManualAsync, Error, () => !isCheckingUpdate);
        OpenUpdateCommand = new RelayCommand(_ => Safe(OpenUpdatePage));
        ToggleLanguageCommand = new RelayCommand(_ => Safe(ToggleLanguage));
        ThemeSvc.SetTheme(recorder.Settings.System.Theme);
        ToggleThemeCommand = new RelayCommand(_ => Safe(ToggleTheme));
        RebuildChoices();
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
            _ = Task.Run(() => CheckUpdateSilentAsync(stop.Token));
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
                        int pid = (int)pipe.Games.FirstOrDefault().Pid;
                        var window = GameWindow.Find(pid);
                        bool gameRunning = window != IntPtr.Zero || pipe.Games.Count > 0 || GameWindow.HasGameProcess(pid);

                        if (window != lastGameWindow) { userPaused = false; retryCount = 0; nextRetry = 0; }
                        var previous = lastGameWindow; lastGameWindow = window;
                        var current = recorder.Status;
                        if (!current.Running && current.Native.ElapsedUs > 0)
                        {
                            await recorder.StopAsync();
                            if (gameRunning || recorder.Settings.Video.Source == CaptureSource.Monitor) retryCount++;
                            nextRetry = Environment.TickCount64 + 5000;
                        }

                        // When capturing game window directly, stop if the window disappeared or changed.
                        // When capturing monitor, keep running as long as the game process is running!
                        if (recorder.Settings.Video.Source == CaptureSource.HuntWindow)
                        {
                            if (previous != IntPtr.Zero && window == IntPtr.Zero && recorder.Status.Running)
                            {
                                if (recorder.Settings.System.AutoBufferWithGame && !manuallyStarted)
                                {
                                    await recorder.StopAsync();
                                    manuallyStarted = false;
                                }
                            }
                            else if (previous != IntPtr.Zero && window != IntPtr.Zero && previous != window && recorder.Status.Running)
                            {
                                await recorder.StopAsync();
                            }
                        }
                        else if (recorder.Settings.Video.Source == CaptureSource.Monitor)
                        {
                            if (recorder.Settings.System.AutoBufferWithGame && !manuallyStarted)
                            {
                                if (!gameRunning && recorder.Status.Running)
                                {
                                    await recorder.StopAsync();
                                }
                            }
                        }

                        bool sourceAvailable = recorder.Settings.Video.Source == CaptureSource.Monitor || window != IntPtr.Zero;
                        bool wanted = manuallyStarted || (recorder.Settings.System.AutoBufferWithGame && gameRunning);
                        if (wanted && sourceAvailable && !userPaused && !recorder.Status.Running && retryCount < 3 && Environment.TickCount64 >= nextRetry)
                        {
                            Busy = true;
                            try { await StartCurrentAsync(); }
                            catch (Exception ex) { retryCount++; nextRetry = Environment.TickCount64 + retryCount * 15000; Error(ex); }
                            finally { Busy = false; }
                        }
                        if (retryCount >= 3 && wanted && !recorder.Status.Running)
                            Message = Loc.IsEnglish
                                ? "Auto-retry stopped after 3 errors. Check diagnostics and click 'Start Buffer'."
                                : "Автоповтор остановлен после трёх ошибок. Проверьте диагностику и нажмите «Запустить буфер».";
                        RefreshStatus();
                    }
                    catch (Exception ex) { Error(ex); }
                    finally { operation.Release(); }
                }).Task.Unwrap().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public bool HasUnsavedSettings => !SettingsEdit.IsEquivalent(recorder.Settings);

    public void RevertSettings()
    {
        SettingsEdit = recorder.Settings.Clone();
    }

    public async Task<bool> SaveSettingsDirectAsync()
    {
        try
        {
            await ApplyAsync();
            return true;
        }
        catch (Exception ex)
        {
            Error(ex);
            return false;
        }
    }

    private async Task ApplyAsync()
    {
        if (Application.Current.MainWindow is { } root && HasFormErrors(root, new HashSet<DependencyObject>()))
            throw new InvalidDataException(Loc.IsEnglish ? "Please correct invalid numeric fields before saving settings." : "Исправьте поля с некорректными числами перед сохранением настроек.");
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
            Message = Loc.IsEnglish ? "Settings saved to config.json." : "Настройки сохранены в config.json.";
            retryCount = 0; nextRetry = 0; userPaused = false;
            int pid = (int)pipe.Games.FirstOrDefault().Pid;
            bool gameActive = GameWindow.Find(pid) != IntPtr.Zero || pipe.Games.Count > 0 || GameWindow.HasGameProcess(pid);
            bool shouldRun = wasRunning || (updated.System.AutoBufferWithGame && gameActive);
            if (shouldRun)
            {
                await StartCurrentAsync();
            }
            log.Write(shouldRun
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
        var dialog = new OpenFolderDialog { Title = Loc.IsEnglish ? "Clips folder" : "Папка для хайлайтов", InitialDirectory = SettingsEdit.Storage.Directory };
        if (dialog.ShowDialog() == true) { SettingsEdit.Storage.Directory = dialog.FolderName; SettingsEdit = SettingsEdit.Clone(); }
    }
    private void DeleteClip()
    {
        if (SelectedClip is not { } clip || MessageBox.Show(
            (Loc.IsEnglish ? "Permanently delete this clip?\n\n" : "Удалить клип без возможности восстановления?\n\n") + clip.FileName,
            Loc.IsEnglish ? "Delete highlight" : "Удаление хайлайта", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ClosePlayer?.Invoke();
        Library().Delete(clip.Clip);
        Clips.Remove(clip); SelectedClip = null;
        Notify(nameof(RecentClips)); Notify(nameof(HasClips)); Notify(nameof(ClipCount)); Notify(nameof(LibrarySummary));
    }
    private void RenameClip()
    {
        if (SelectedClip is not { } clip) return;
        var dialog = new InputDialog(Loc.IsEnglish ? "Rename clip" : "Переименовать клип", clip.FileName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        ClosePlayer?.Invoke();
        var renamed = Library().Rename(clip.Clip, dialog.Value);
        var index = Clips.IndexOf(clip); var model = new ClipViewModel(renamed);
        Clips[index] = model; SelectedClip = model; Notify(nameof(RecentClips));
    }
    private async Task DiagnoseAsync()
    {
        await operation.WaitAsync(); Busy = true; Page = "Diagnostics";
        try { Diagnostics = Loc.IsEnglish ? "Testing encoder. This may take a few seconds…" : "Проверяется реальный энкодер. Это может занять несколько секунд…";
            Diagnostics = await Task.Run(() => DiagnosticsService.Run(recorder.Settings.Clone(), recorder, pipe)); }
        finally { Busy = false; operation.Release(); }
    }
    private void SaveReport()
    {
        var dialog = new SaveFileDialog { FileName = "HuntReplay-diagnostics.txt", Filter = (Loc.IsEnglish ? "Text report" : "Текстовый отчёт") + "|*.txt" };
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
            if (MessageBox.Show(
                Loc.IsEnglish ? "Restore registry values to pre-Studio state? Automatic Highlights will stop working. Saved clips will remain." : "Восстановить значения реестра до установки Studio? Автоматические Highlights перестанут поступать. Клипы останутся.",
                Loc.IsEnglish ? "Restore NVIDIA" : "Восстановление NVIDIA", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var code = await SelfInstaller.RunElevatedAsync(uninstall: true);
            if (code != 0) throw new IOException(Loc.IsEnglish ? "Restoration failed. See elevated installer message." : "Восстановление не выполнено. См. сообщение elevated-установщика.");
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
        try { await RefreshClipsAsync(); DesktopIntegration.NotifySaved(clip, recorder.Settings, log); Message = (Loc.IsEnglish ? "Saved: " : "Сохранён: ") + clip.Metadata.FileName; }
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
        Message = (Loc.IsEnglish ? "Clip saved: " : "Клип сохранён: ") + fileName + (Loc.IsEnglish ? ". Built-in player could not open it (" : ". Встроенный проигрыватель не открыл его (") + PlaybackDiagnostics.Code(hresult) + ").";
        log.Write(PlaybackDiagnostics.LogLine(hresult, fileName), "WARN");
    }
    private async Task CheckUpdateSilentAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token); // Let the app start up and render before network I/O
            var res = await UpdateCheckerService.CheckForUpdatesAsync(token);
            if (token.IsCancellationRequested) return;
            OnUi(() =>
            {
                hasUpdate = res.HasUpdate;
                latestVersion = res.LatestVersion;
                updateUrl = res.ReleaseUrl;
                Notify(nameof(HasUpdate));
                Notify(nameof(LatestVersion));
                Notify(nameof(UpdateButtonLabel));
                Notify(nameof(UpdateStatusLabel));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("Background update check: " + ex.Message, "DEBUG");
        }
    }

    public async Task CheckUpdateManualAsync()
    {
        isCheckingUpdate = true;
        Notify(nameof(IsCheckingUpdate));
        Notify(nameof(UpdateStatusLabel));
        Message = Loc.IsEnglish ? "Checking for updates on GitHub…" : "Проверка обновлений на GitHub…";
        try
        {
            var res = await Task.Run(() => UpdateCheckerService.CheckForUpdatesAsync(stop.Token));
            hasUpdate = res.HasUpdate;
            latestVersion = res.LatestVersion;
            updateUrl = res.ReleaseUrl;
            Notify(nameof(HasUpdate));
            Notify(nameof(LatestVersion));
            Notify(nameof(UpdateButtonLabel));
            Notify(nameof(UpdateStatusLabel));

            if (res.HasUpdate)
            {
                Message = Loc.IsEnglish ? $"New version {res.LatestVersion} is available! Click header button to view release." : $"Доступна новая версия {res.LatestVersion}! Нажмите кнопку в заголовке для перехода к релизу.";
            }
            else if (!string.IsNullOrEmpty(res.ErrorMessage))
            {
                Message = (Loc.IsEnglish ? "Failed to check updates: " : "Не удалось проверить обновления: ") + res.ErrorMessage;
            }
            else
            {
                Message = Loc.IsEnglish ? $"You have the latest version ({res.CurrentVersion}). No updates." : $"У вас установлена актуальная версия ({res.CurrentVersion}). Обновлений нет.";
            }
        }
        finally
        {
            isCheckingUpdate = false;
            Notify(nameof(IsCheckingUpdate));
            Notify(nameof(UpdateStatusLabel));
        }
    }

    private void OpenUpdatePage()
    {
        var url = !string.IsNullOrWhiteSpace(updateUrl) ? updateUrl : "https://github.com/Mlovo/HuntOpenHighlights/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ToggleLanguage()
    {
        Loc.ToggleLanguage();
        recorder.Settings.System.Language = Loc.IsEnglish ? "en" : "ru";
        store.Save(recorder.Settings);
        RebuildChoices();
        RefreshDeviceSelections();
        customMessage = null;
        customDiagnostics = null;
        Notify(nameof(Message));
        Notify(nameof(Diagnostics));
        Notify(nameof(SelectedEventLabel));
        Notify(nameof(Loc));
        Notify(nameof(LanguageButtonLabel));
        Notify(nameof(PageTitle));
        Notify(nameof(GameStatus));
        Notify(nameof(BufferStatus));
        Notify(nameof(MemoryText));
        Notify(nameof(GpuStatus));
        Notify(nameof(EncodingModeLabel));
        Notify(nameof(VideoStatus));
        Notify(nameof(CaptureDetail));
        Notify(nameof(PendingText));
        Notify(nameof(ToggleLabel));
        Notify(nameof(ManualLabel));
        Notify(nameof(SetupLabel));
        Notify(nameof(PipeLabel));
        Notify(nameof(LibrarySummary));
        Notify(nameof(UpdateButtonLabel));
        Notify(nameof(UpdateStatusLabel));
        Notify(nameof(ThemeButtonLabel));
        FilteredClips.Refresh();
    }

    private void ToggleTheme()
    {
        ThemeSvc.ToggleTheme();
        recorder.Settings.System.Theme = ThemeSvc.ThemeCode;
        store.Save(recorder.Settings);
        Notify(nameof(ThemeButtonLabel));
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

using System.Reflection;
using System.Security.Principal;
using System.Windows;
using HuntReplay.App.Services;
using HuntReplay.App.ViewModels;
using HuntReplay.App.Views;
using HuntReplay.Core.Configuration;
using Microsoft.Toolkit.Uwp.Notifications;
using Forms = System.Windows.Forms;

namespace HuntReplay.App;
public partial class App : Application
{
    static App()
    {
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(
            System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
    }
    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;
    private Forms.NotifyIcon? tray;
    private StudioLog? log;
    private RecorderService? recorder;
    private HighlightsPipeServer? pipe;
    private MainViewModel? model;
    private MainWindow? window;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--install-shim") || e.Args.Contains("--uninstall-shim"))
        {
            try
            {
                using var mutex = new Mutex(false, @"Global\HuntReplayStudio.ShimSetup");
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Другой установщик уже работает.");
                try
                {
                    if (e.Args.Contains("--uninstall-shim")) SelfInstaller.Uninstall();
                    else SelfInstaller.Install(e.Args.Contains("--replace-nvidia"));
                }
                finally { mutex.ReleaseMutex(); }
                Shutdown(0);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Установка HuntReplay Studio", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new PlatformNotSupportedException("Нужны Windows 10 (2004, build 19041) или Windows 11, x64.");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? Environment.UserName;
            var name = @"Local\HuntReplayStudio." + sid;
            instance = new Mutex(true, name, out bool first);
            if (!first)
            {
                try { using var signal = EventWaitHandle.OpenExisting(name + ".Activate"); signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("HuntReplay Studio уже запущена. Проверьте системный трей."); }
                Shutdown(); return;
            }
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
            activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(() => window?.ShowStudio()), null, Timeout.Infinite, false);
            bool portable = e.Args.Contains("--portable");
            var root = portable ? AppContext.BaseDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuntReplayStudio");
            Directory.CreateDirectory(root);
            log = new StudioLog(root);
            DispatcherUnhandledException += (_, args) =>
            {
                log.Write(args.Exception.ToString(), "ERROR");
                MessageBox.Show(args.Exception.Message, "HuntReplay Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
                _ = ExitAsync(1);
            };
            var store = new SettingsStore(Path.Combine(root, "config.json"));
            var settings = store.Load();
            if (store.LoadWarning is { } warning) log.Write(warning, "WARN");
            string? storageWarning = null;
            try { Directory.CreateDirectory(settings.Storage.Directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { storageWarning = "Хранилище недоступно; выберите другую папку в настройках. " + ex.Message; }
            if (!settings.System.FirstRunCompleted)
            {
                var status = await Task.Run(SelfInstaller.Check);
                var wizard = new SetupWizard(status);
                wizard.ShowDialog();
                settings.System.FirstRunCompleted = true;
                store.Save(settings);
            }
            recorder = new RecorderService(settings, log);
            pipe = new HighlightsPipeServer(recorder, log);
            model = new MainViewModel(store, recorder, pipe, log, portable);
            window = new MainWindow { DataContext = model }; MainWindow = window;
            CreateTray();
            try { ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.BeginInvoke(() => { if (model is not null) model.Page = "Gallery"; window?.ShowStudio(); }); }
            catch (Exception ex) { log.Write("Toast activation недоступна: " + ex.Message, "WARN"); }
            bool startInTray = e.Args.Contains("--tray");
            if (startInTray) { window.ShowInTaskbar = false; window.WindowState = WindowState.Minimized; }
            window.Show();
            if (startInTray) window.Hide();
            pipe.Start();
            await model.InitializeAsync(window);
            if (storageWarning is not null) model.Error(new IOException(storageWarning));
            if (store.LoadWarning is { } loadWarning) model.Error(new InvalidDataException(loadWarning));
            log.Write("HuntReplay Studio готова. Ожидание игры; нажатия клавиш не эмулируются.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось запустить HuntReplay Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(1);
        }
    }

    private void CreateTray()
    {
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HuntReplay.Studio.ico")!;
        using var icon = new System.Drawing.Icon(iconStream);
        tray = new Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "HuntReplay Studio · автономные хайлайты", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => window?.ShowStudio());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть HuntReplay Studio", null, (_, _) => Dispatcher.Invoke(() => window?.ShowStudio()));
        menu.Items.Add("Сохранить повтор", null, (_, _) => Dispatcher.Invoke(() => model?.SaveCommand.Execute(null)));
        menu.Items.Add("Запустить / остановить буфер", null, (_, _) => Dispatcher.Invoke(() => model?.ToggleBufferCommand.Execute(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = menu;
    }

    internal async Task ExitAsync(int code = 0)
    {
        if (exiting) return; exiting = true;
        if (window is not null) { window.IsEnabled = false; window.ClosePlayer(); }
        try
        {
            try { if (model is not null) await model.DisposeAsync(); }
            finally
            {
                try { if (pipe is not null) await pipe.DisposeAsync(); }
                finally { if (recorder is not null) await recorder.DisposeAsync(); }
            }
        }
        catch (Exception ex) { log?.Write("Ошибка завершения: " + ex.Message, "ERROR"); code = 1; }
        finally
        {
            if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); }
            activationWait?.Unregister(null); activate?.Dispose(); instance?.Dispose();
            if (log is not null) await log.DisposeAsync();
            if (window is not null) { window.CanClose = true; window.Close(); }
            Shutdown(code);
        }
    }
}

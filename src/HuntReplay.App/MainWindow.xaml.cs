using System.ComponentModel;
using System.Windows;
using HuntReplay.App.ViewModels;

namespace HuntReplay.App;
public partial class MainWindow : Window
{
    internal bool CanClose { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        MinWidth = Math.Min(980, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(680, SystemParameters.WorkArea.Height);
        Width = Math.Min(1380, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(920, SystemParameters.WorkArea.Height - 24);
        SourceInitialized += (_, _) => Interop.WindowAppearance.DarkCaption(this);
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (CanClose) return;
        e.Cancel = true;
        if (DataContext is MainViewModel vm)
        {
            if (!await vm.ConfirmLeaveSettingsAsync()) return;
            if (vm.CloseToTray) { Hide(); return; }
        }
        await ((App)Application.Current).ExitAsync();
    }
    internal void ShowStudio()
    {
        ShowInTaskbar = true; Show(); WindowState = WindowState.Normal; Activate();
    }
    internal void ClosePlayer() => Gallery.Close();
}

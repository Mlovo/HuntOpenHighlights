using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private double taglineHeight;
    // The tagline is the least needed part of the sidebar, so a short window hides it before the support card has to scroll.
    private void OnSidebarScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var spare = SidebarScroll.ViewportHeight - SidebarScroll.ExtentHeight;
        if (Tagline.Visibility == Visibility.Visible && spare < 0)
        {
            taglineHeight = Tagline.ActualHeight + Tagline.Margin.Bottom;
            Tagline.Visibility = Visibility.Collapsed;
        }
        else if (Tagline.Visibility == Visibility.Collapsed && spare >= taglineHeight) Tagline.Visibility = Visibility.Visible;
    }
    internal void ShowStudio()
    {
        ShowInTaskbar = true; Show(); WindowState = WindowState.Normal; Activate();
    }
    internal void ClosePlayer() => Gallery.Close();
}

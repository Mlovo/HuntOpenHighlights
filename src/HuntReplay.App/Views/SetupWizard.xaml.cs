using System.ComponentModel;
using System.Windows;
using HuntReplay.App.Services;

namespace HuntReplay.App.Views;
public partial class SetupWizard : Window
{
    private readonly SetupStatus initial;
    private bool installing;
    internal SetupWizard(SetupStatus status)
    {
        InitializeComponent(); initial = status;
        DataContext = new { Loc = LocalizationService.Instance };
        Title = LocalizationService.Instance["Wizard.Title"];
        SourceInitialized += (_, _) => Interop.WindowAppearance.DarkCaption(this);
        StatusText.Text = status.Detail;
        var loc = LocalizationService.Instance;
        if (status.Conflict)
        {
            ReplaceCheck.Visibility = Visibility.Visible;
            StatusText.Text = loc["Wizard.ConflictFound"];
        }
        if (status.NvidiaActive) StatusText.Text = loc["Wizard.NvidiaActive"];
        if (status.Ready) InstallButton.Content = loc["Wizard.AllSet"];
        Closing += (_, args) => { if (installing) args.Cancel = true; };
    }
    private void LaterClicked(object sender, RoutedEventArgs e) { if (!installing) { DialogResult = false; Close(); } }
    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (initial.Ready) { DialogResult = true; Close(); return; }
        if (initial.Conflict && ReplaceCheck.IsChecked != true) { StatusText.Text = LocalizationService.Instance["Wizard.ConsentRequired"]; return; }
        installing = true; InstallButton.IsEnabled = false;
        try
        {
            StatusText.Text = LocalizationService.Instance["Wizard.WaitingUac"];
            var code = await SelfInstaller.RunElevatedAsync(replaceNvidia: ReplaceCheck.IsChecked == true);
            if (code != 0) throw new IOException(LocalizationService.Instance["Wizard.InstallIncomplete"]);
            var result = await Task.Run(SelfInstaller.Check);
            if (!result.Ready) throw new IOException(result.Detail);
            installing = false; DialogResult = true; Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = LocalizationService.Instance["Wizard.UacCancelled"]; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { installing = false; InstallButton.IsEnabled = true; }
    }
}

using System.ComponentModel;
using System.Windows;
using HuntReplay.App.Services;

namespace HuntReplay.App.Views;
public partial class SetupWizard : Window
{
    private SetupStatus status;
    // A message that replaced the check result (consent needed, UAC cancelled, an error); re-read after a language switch.
    private Func<string>? message;
    private bool installing;
    internal SetupWizard(SetupStatus status)
    {
        InitializeComponent(); this.status = status;
        DataContext = new { Loc = LocalizationService.Instance };
        SourceInitialized += (_, _) => Interop.WindowAppearance.DarkCaption(this);
        ShowStatus();
        Closing += (_, args) => { if (installing) args.Cancel = true; };
    }

    // Text set from code does not follow the Loc bindings, so it is rebuilt here for the current language.
    private void ShowStatus()
    {
        var loc = LocalizationService.Instance;
        Title = loc["Wizard.Title"];
        ReplaceCheck.Visibility = status.Conflict ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message?.Invoke()
            ?? (status.NvidiaActive ? loc["Wizard.NvidiaActive"] : status.Conflict ? loc["Wizard.ConflictFound"] : status.Detail);
        InstallButton.Content = status.Ready ? loc["Wizard.AllSet"] : loc["Wizard.Install"];
    }

    private void ShowMessage(Func<string> text) { message = text; ShowStatus(); }

    private async void LanguageClicked(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        LocalizationService.Instance.ToggleLanguage(); // App saves the chosen language with the first-run flag.
        ShowStatus();
        // The check result was worded in the previous language; checking again is quick and rewords it.
        try { status = await Task.Run(SelfInstaller.Check); } catch { return; }
        if (!installing) ShowStatus();
    }

    private void LaterClicked(object sender, RoutedEventArgs e) { if (!installing) { DialogResult = false; Close(); } }
    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (status.Ready) { DialogResult = true; Close(); return; }
        if (status.Conflict && ReplaceCheck.IsChecked != true) { ShowMessage(() => LocalizationService.Instance["Wizard.ConsentRequired"]); return; }
        installing = true; InstallButton.IsEnabled = false;
        try
        {
            ShowMessage(() => LocalizationService.Instance["Wizard.WaitingUac"]);
            var code = await SelfInstaller.RunElevatedAsync(replaceNvidia: ReplaceCheck.IsChecked == true);
            if (code != 0) throw new IOException(LocalizationService.Instance["Wizard.InstallIncomplete"]);
            var result = await Task.Run(SelfInstaller.Check);
            if (!result.Ready) throw new IOException(result.Detail);
            installing = false; DialogResult = true; Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { ShowMessage(() => LocalizationService.Instance["Wizard.UacCancelled"]); }
        catch (Exception ex) { ShowMessage(() => ex.Message); }
        finally { installing = false; InstallButton.IsEnabled = true; }
    }
}

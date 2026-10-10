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
        bool en = LocalizationService.Instance.IsEnglish;
        if (status.Conflict)
        {
            ReplaceCheck.Visibility = Visibility.Visible;
            StatusText.Text = en
                ? "Existing NVIDIA registration found. Replacing it may disable Highlights in NVIDIA App. Do not run both servers concurrently."
                : "Найдена существующая регистрация NVIDIA. Замена может отключить Highlights в NVIDIA App. Не запускайте два сервера одновременно.";
        }
        if (status.NvidiaActive)
        {
            StatusText.Text = en
                ? "NVIDIA service (NvContainer) detected. HuntReplay will pause it while running and restore it upon exit."
                : "Обнаружена служба NVIDIA (NvContainer). HuntReplay автоматически приостановит её на время работы и возобновит при выходе.";
        }
        if (status.Ready) InstallButton.Content = en ? "All set · continue" : "Всё готово · продолжить";
        Closing += (_, args) => { if (installing) args.Cancel = true; };
    }
    private void LaterClicked(object sender, RoutedEventArgs e) { if (!installing) { DialogResult = false; Close(); } }
    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (initial.Ready) { DialogResult = true; Close(); return; }
        if (initial.Conflict && ReplaceCheck.IsChecked != true) { StatusText.Text = "Для замены существующей регистрации нужно ваше согласие."; return; }
        installing = true; InstallButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Ожидается UAC. Приложение не загружает файлы из интернета…";
            var code = await SelfInstaller.RunElevatedAsync(replaceNvidia: ReplaceCheck.IsChecked == true);
            if (code != 0) throw new IOException("Установка не завершена. Подробности показаны в окне установщика.");
            var result = await Task.Run(SelfInstaller.Check);
            if (!result.Ready) throw new IOException(result.Detail);
            installing = false; DialogResult = true; Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "Запрос UAC отменён. Можно продолжить без автоматических Highlights."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { installing = false; InstallButton.IsEnabled = true; }
    }
}

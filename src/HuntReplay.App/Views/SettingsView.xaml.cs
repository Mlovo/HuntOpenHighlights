using System.Windows;
using System.Windows.Controls;
using HuntReplay.App.ViewModels;

namespace HuntReplay.App.Views;

public partial class SettingsView : UserControl
{
    private TabItem? previousTab;
    private bool suppressingSelection;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => previousTab = SettingsTabControl.SelectedItem as TabItem;
    }

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to TabControl itself, not ComboBoxes or child selectors bubbling up
        if (e.OriginalSource != SettingsTabControl || suppressingSelection) return;

        var targetTab = SettingsTabControl.SelectedItem as TabItem;
        if (targetTab == previousTab || previousTab is null)
        {
            previousTab = targetTab;
            return;
        }

        if (DataContext is MainViewModel vm && vm.HasUnsavedSettings)
        {
            var result = MessageBox.Show(
                Application.Current.MainWindow,
                vm.Loc["Settings.UnsavedChangesPrompt"],
                vm.Loc["Settings.UnsavedChangesTitle"],
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                bool saved = await vm.SaveSettingsDirectAsync();
                if (!saved)
                {
                    RevertTab();
                    return;
                }
            }
            else if (result == MessageBoxResult.No)
            {
                vm.RevertSettings();
            }
            else
            {
                RevertTab();
                return;
            }
        }

        previousTab = targetTab;
    }

    private void RevertTab()
    {
        suppressingSelection = true;
        try
        {
            SettingsTabControl.SelectedItem = previousTab;
        }
        finally
        {
            suppressingSelection = false;
        }
    }
}

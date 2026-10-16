using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to TabControl itself. A capture source or monitor picked in a ComboBox changes which presets fit.
        if (e.OriginalSource != SettingsTabControl)
        {
            if (e.OriginalSource is ComboBox) (DataContext as MainViewModel)?.RefreshResolutionChoices();
            return;
        }
        if (suppressingSelection) return;

        var targetTab = SettingsTabControl.SelectedItem as TabItem;
        if (targetTab == previousTab || previousTab is null)
        {
            previousTab = targetTab;
            return;
        }

        if (DataContext is MainViewModel vm && vm.HasUnsavedSettings)
        {
            // A TabItem selects on mouse-down, so this runs while the click still holds the mouse capture. A modal
            // dialog opened now swallows its first click. Stay on the current tab and ask once the click is over.
            SelectTab(previousTab);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await AskAndSwitchAsync(vm, targetTab)));
            return;
        }

        previousTab = targetTab;
    }

    private async Task AskAndSwitchAsync(MainViewModel vm, TabItem? targetTab)
    {
        Mouse.Capture(null);
        if (!await vm.ConfirmLeaveSettingsAsync()) return; // Cancelled or the save failed: stay on this tab.
        SelectTab(targetTab);
        previousTab = targetTab;
    }

    private void SelectTab(TabItem? tab)
    {
        suppressingSelection = true;
        try
        {
            SettingsTabControl.SelectedItem = tab;
        }
        finally
        {
            suppressingSelection = false;
        }
    }
}

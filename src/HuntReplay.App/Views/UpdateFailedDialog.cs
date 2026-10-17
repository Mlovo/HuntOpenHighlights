using System.Windows;
using System.Windows.Controls;

namespace HuntReplay.App.Views;

internal enum UpdateFailedChoice { Close, Retry, OpenReleases }

/// <summary>Explains why an update failed and offers to retry or to download it from GitHub by hand.</summary>
internal sealed class UpdateFailedDialog : Window
{
    public UpdateFailedChoice Choice { get; private set; }

    public UpdateFailedDialog(string reason)
    {
        var loc = Services.LocalizationService.Instance;
        SourceInitialized += (_, _) => Interop.WindowAppearance.DarkCaption(this);
        Title = loc["Update.FailedTitle"]; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Background");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = loc["Update.FailedTitle"], FontSize = 18, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = loc["Update.FailedHint"], Style = (Style)FindResource("MutedText") });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(Option(loc["Common.Close"], UpdateFailedChoice.Close, isCancel: true));
        buttons.Children.Add(Option(loc["Update.OpenReleases"], UpdateFailedChoice.OpenReleases));
        var retry = Option(loc["Update.Retry"], UpdateFailedChoice.Retry);
        retry.IsDefault = true; retry.Style = (Style)FindResource("PrimaryButton");
        buttons.Children.Add(retry);
        panel.Children.Add(buttons); Content = panel;
    }

    private Button Option(string label, UpdateFailedChoice choice, bool isCancel = false)
    {
        var button = new Button { Content = label, IsCancel = isCancel, Margin = new Thickness(10, 0, 0, 0) };
        button.Click += (_, _) => { Choice = choice; DialogResult = true; };
        return button;
    }
}

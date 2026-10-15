using System.Windows;
using System.Windows.Controls;

namespace HuntReplay.App.Views;
internal sealed class InputDialog : Window
{
    private readonly TextBox input;
    public string Value => input.Text.Trim();
    public InputDialog(string title, string value)
    {
        SourceInitialized += (_, _) => Interop.WindowAppearance.DarkCaption(this);
        Title = title; Width = 520; Height = 220; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Background");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new Thickness(0, 0, 0, 20) });
        input = new TextBox { Text = value }; panel.Children.Add(input);
        var saveLabel = Services.LocalizationService.Instance["Common.Save"];
        var button = new Button { Content = saveLabel, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        button.Click += (_, _) => { DialogResult = true; };
        panel.Children.Add(button); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.Select(0, Path.GetFileNameWithoutExtension(value).Length); };
    }
}

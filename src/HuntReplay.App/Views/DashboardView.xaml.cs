using System.Windows;
using System.Windows.Controls;
using HuntReplay.App.ViewModels;
namespace HuntReplay.App.Views;
public partial class DashboardView : UserControl
{
    private const double RecentCardWidth = 330;

    public DashboardView()
    {
        InitializeComponent();
        // One more recent clip per ~330 px instead of ever-wider thumbnails: 3 at the minimum window, up to 6.
        SizeChanged += (_, e) => { if (DataContext is MainViewModel model) model.RecentCount = Math.Clamp((int)(e.NewSize.Width / RecentCardWidth), 3, 6); };
    }
    private void OpenGallery(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model) model.Page = "Gallery"; }
}

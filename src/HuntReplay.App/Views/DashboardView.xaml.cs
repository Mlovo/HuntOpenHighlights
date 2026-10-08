using System.Windows;
using System.Windows.Controls;
using HuntReplay.App.ViewModels;
namespace HuntReplay.App.Views;
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
    private void OpenGallery(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model) model.Page = "Gallery"; }
}

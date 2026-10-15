using System.ComponentModel;
using System.Windows;

namespace HuntReplay.App.Services;

public enum AppTheme
{
    Dark,
    Light
}

public sealed class ThemeService : INotifyPropertyChanged
{
    public static ThemeService Instance { get; } = new();

    private AppTheme currentTheme = AppTheme.Dark;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppTheme CurrentTheme
    {
        get => currentTheme;
        set
        {
            if (currentTheme == value) return;
            currentTheme = value;
            Apply(currentTheme);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public bool IsDark => currentTheme == AppTheme.Dark;
    public bool IsLight => currentTheme == AppTheme.Light;

    public string ThemeCode => currentTheme == AppTheme.Dark ? "dark" : "light";

    public void ToggleTheme()
    {
        CurrentTheme = currentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
    }

    public void SetTheme(string? code)
    {
        if (string.Equals(code, "light", StringComparison.OrdinalIgnoreCase))
        {
            CurrentTheme = AppTheme.Light;
        }
        else
        {
            CurrentTheme = AppTheme.Dark;
        }
    }

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        void DoApply()
        {
            // Only the palette changes; Controls.xaml stays merged and picks the new brushes up through DynamicResource.
            var palette = new ResourceDictionary { Source = new Uri(
                $"/HuntReplayStudio;component/Themes/Palette.{(theme == AppTheme.Light ? "Light" : "Dark")}.xaml", UriKind.RelativeOrAbsolute) };
            var dicts = app.Resources.MergedDictionaries;
            var index = dicts.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);
            if (index >= 0) dicts[index] = palette; else dicts.Insert(0, palette);
        }

        if (app.Dispatcher.CheckAccess())
        {
            DoApply();
        }
        else
        {
            app.Dispatcher.Invoke(DoApply);
        }
    }
}

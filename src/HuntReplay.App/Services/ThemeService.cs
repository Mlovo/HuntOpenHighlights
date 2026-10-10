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
            var uri = new Uri(theme == AppTheme.Light
                ? "/HuntReplayStudio;component/Themes/Light.xaml"
                : "/HuntReplayStudio;component/Themes/Dark.xaml", UriKind.RelativeOrAbsolute);
            var newDict = new ResourceDictionary { Source = uri };

            var dicts = app.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                var src = dicts[i].Source?.OriginalString;
                if (src != null && (src.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase) || src.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    dicts.RemoveAt(i);
                }
            }
            dicts.Add(newDict);
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

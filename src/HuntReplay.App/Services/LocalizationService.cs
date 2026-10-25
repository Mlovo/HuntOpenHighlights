using System.ComponentModel;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

/// <summary>
/// WPF face of <see cref="Text"/>: XAML binds <c>{Binding Loc[Key]}</c> and code uses <c>Loc["Key"]</c> or
/// <see cref="Format"/>. The strings themselves live in <c>HuntReplay.Core/Localization/{language}.json</c>.
/// A language change raises PropertyChanged for every property, so all bound text refreshes at once.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private LocalizationService()
    {
        Text.LanguageChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRussian => Text.Language == Text.Russian;

    public void ToggleLanguage() => Text.SetLanguage(IsRussian ? Text.English : Text.Russian);

    public void SetLanguage(string? code) => Text.SetLanguage(code);

    public string this[string key] => Text.Get(key);

    public string Format(string key, params object?[] args) => Text.Format(key, args);
}

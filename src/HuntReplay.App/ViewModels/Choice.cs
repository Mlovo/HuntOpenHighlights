using CommunityToolkit.Mvvm.ComponentModel;

namespace HuntReplay.App.ViewModels;

// The label is what a ComboBox shows in both the closed box and the popup, even when a template
// does not forward DisplayMemberPath / ContentTemplate; "Choice { Value = …, Label = … }" must never appear.
internal sealed class Choice<T> : ObservableObject
{
    private string label;
    private bool isAvailable = true;
    public T Value { get; }
    public string Label
    {
        get => label;
        set => SetProperty(ref label, value);
    }
    // An unavailable choice stays in the list (replacing ItemsSource would reset the ComboBox selection) but is hidden.
    public bool IsAvailable
    {
        get => isAvailable;
        set => SetProperty(ref isAvailable, value);
    }

    public Choice(T value, string label)
    {
        Value = value;
        this.label = label;
    }

    public override string ToString() => Label;
}

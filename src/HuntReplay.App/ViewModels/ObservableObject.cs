using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HuntReplay.App.ViewModels;

// WPF-free on purpose: the Linux regression suite links this file together with
// DeviceSelectionViewModel.cs and DeviceOption.cs. Commands live in Commands.cs.
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(property); return true;
    }
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

// The label is what a ComboBox shows in both the closed box and the popup, even when a template
// does not forward DisplayMemberPath / ContentTemplate; "Choice { Value = …, Label = … }" must never appear.
internal sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

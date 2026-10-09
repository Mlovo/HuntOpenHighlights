using HuntReplay.App.Interop;

namespace HuntReplay.App.ViewModels;

/// <summary>
/// An empty device ID means Windows default. A null SelectedValue is a transient
/// Selector reset during ItemsSource changes, NOT a request to erase the saved ID.
/// Keep even disconnected IDs selectable so refresh never silently picks another device.
/// The saved Windows ID stays in settings and in DeviceOption.Id, but it is never shown as text:
/// monitor paths and audio endpoint GUIDs are not user-facing captions.
/// </summary>
internal sealed class DeviceSelectionViewModel : ObservableObject
{
    private readonly int kind;
    private readonly string defaultLabel;
    private readonly Func<string> readId;
    private readonly Action<string> writeId;
    private IReadOnlyList<DeviceOption> options = [];
    private bool refreshing;

    public DeviceSelectionViewModel(int kind, string defaultLabel, Func<string> readId, Action<string> writeId)
    {
        this.kind = kind; this.defaultLabel = defaultLabel;
        this.readId = readId; this.writeId = writeId;
        Refresh([]);
    }

    public IReadOnlyList<DeviceOption> Options { get => options; private set => Set(ref options, value); }

    private string UnavailableCaption => kind == 0
        ? "Недоступно · сохранённый монитор не подключён"
        : "Недоступно · сохранённое устройство не подключено";

    public string? SelectedId
    {
        get => refreshing ? null : readId();
        set
        {
            if (value is null || refreshing || value == readId()) return;
            writeId(value);
            Notify();
        }
    }

    public void Refresh(IEnumerable<DeviceOption> devices)
    {
        string selected = readId();
        var replacement = new List<DeviceOption> { new(kind, "", defaultLabel) };
        replacement.AddRange(devices.Where(d => d.Kind == kind && !string.IsNullOrEmpty(d.Id)));
        if (selected.Length > 0 && replacement.All(d => d.Id != selected))
            replacement.Add(new(kind, selected, UnavailableCaption));
        refreshing = true;
        try
        {
            // Clear only the presentation before replacing items. WPF may read the
            // source back while its internal selection change is still active; if
            // that read returns the old ID, SelectedValue can look restored while
            // SelectedItem remains null. Restore after the replacement has finished.
            Notify(nameof(SelectedId));
            Options = replacement;
        }
        finally
        {
            refreshing = false;
            Notify(nameof(SelectedId));
        }
    }
}

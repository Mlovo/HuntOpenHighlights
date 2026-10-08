using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HuntReplay.App.Interop;
using HuntReplay.App.ViewModels;
using HuntReplay.Core.Configuration;

namespace HuntReplay.WindowsTests;

internal static class Program
{
    private static int passed, failed;

    [STAThread]
    public static int Main()
    {
        // No window, native backend or real device is required, but real WPF binding,
        // Selector coercion and dispatcher behavior must run on Windows / STA.
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (int kind in new[] { 0, 1, 2 })
        {
            Test($"device kind {kind}: default survives repeated refresh", () => WithSelector(kind, "", (s, box, read, _) =>
            {
                for (int i = 0; i < 5; i++) { s.Refresh(Devices(kind)); Pump(); Equal("", read()); Equal("", box.SelectedValue); }
            }));
            Test($"device kind {kind}: explicit ID survives reordered/replaced items", () => WithSelector(kind, "second", (s, box, read, _) =>
            {
                s.Refresh(Devices(kind)); Pump(); Equal("second", box.SelectedValue);
                s.Refresh(Devices(kind).Reverse()); Pump(); Equal("second", read()); Equal("second", box.SelectedValue);
            }));
            Test($"device kind {kind}: unplug preserves ID and inserts unavailable option", () => WithSelector(kind, "second", (s, box, read, _) =>
            {
                s.Refresh(Devices(kind)); Pump();
                s.Refresh([]); Pump(); Equal("second", read()); Equal("second", box.SelectedValue);
                True(((DeviceOption)box.SelectedItem).Name.Contains("Недоступно"));
                s.Refresh(Devices(kind)); Pump(); Equal("second", read()); Equal("second", box.SelectedValue);
                Equal("Second", ((DeviceOption)box.SelectedItem).Name);
            }));
            Test($"device kind {kind}: transient null cannot erase a saved ID", () => WithSelector(kind, "second", (s, box, read, _) =>
            {
                s.Refresh(Devices(kind)); Pump();
                box.SetCurrentValue(ComboBox.SelectedValueProperty, null);
                box.GetBindingExpression(ComboBox.SelectedValueProperty)!.UpdateSource(); Pump();
                Equal("second", read());
                s.Refresh(Devices(kind)); Pump(); Equal("second", box.SelectedValue);
            }));
            Test($"device kind {kind}: selecting default is a real editable empty ID", () => WithSelector(kind, "second", (s, box, read, _) =>
            {
                s.Refresh(Devices(kind)); Pump();
                box.SetCurrentValue(ComboBox.SelectedValueProperty, "");
                box.GetBindingExpression(ComboBox.SelectedValueProperty)!.UpdateSource(); Pump(); Equal("", read());
                box.SetCurrentValue(ComboBox.SelectedValueProperty, "first");
                box.GetBindingExpression(ComboBox.SelectedValueProperty)!.UpdateSource(); Pump(); Equal("first", read());
            }));
            Test($"device kind {kind}: cloned/replaced settings update the visible selection", () => WithSelector(kind, "second", (s, box, read, replace) =>
            {
                s.Refresh(Devices(kind)); Pump(); replace("first"); s.Refresh(Devices(kind)); Pump();
                Equal("first", read()); Equal("first", box.SelectedValue);
            }));
        }
        Application.Current.Shutdown();
        Console.WriteLine($"\n{passed} WPF binding regressions passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static DeviceOption[] Devices(int kind) =>
        [new(kind, "first", "First"), new(kind, "second", "Second"), new((kind + 1) % 3, "wrong-kind", "Exclude me")];

    private static void WithSelector(int kind, string initial, Action<DeviceSelectionViewModel, ComboBox, Func<string>, Action<string>> test)
    {
        var settings = new StudioSettings();
        string Read() => kind switch { 0 => settings.Video.MonitorDevice, 1 => settings.Audio.OutputDeviceId, _ => settings.Audio.MicrophoneDeviceId };
        void Write(string id)
        {
            if (kind == 0) settings.Video.MonitorDevice = id;
            else if (kind == 1) settings.Audio.OutputDeviceId = id;
            else settings.Audio.MicrophoneDeviceId = id;
        }
        Write(initial);
        var selector = new DeviceSelectionViewModel(kind, "Windows default", Read, Write);
        var box = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", IsSynchronizedWithCurrentItem = false };
        box.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(selector.Options)) { Source = selector });
        box.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(selector.SelectedId)) { Source = selector, Mode = BindingMode.TwoWay });
        try
        {
            Pump(); Equal(initial, Read()); Equal(initial, box.SelectedValue);
            test(selector, box, Read, id => { settings = settings.Clone(); Write(id); });
            True(selector.Options.All(d => d.Kind == kind));
            Equal(1, selector.Options.Count(d => d.Id == ""));
        }
        finally { BindingOperations.ClearAllBindings(box); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
    private static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected ?? "<null>"}', got '{actual ?? "<null>"}'");
    }
}

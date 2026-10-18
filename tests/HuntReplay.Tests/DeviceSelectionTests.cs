using HuntReplay.App.Interop;
using HuntReplay.App.ViewModels;

namespace HuntReplay.Tests;

// Production view-model, label and choice code linked from src/HuntReplay.App without any WPF
// dependency. Real ComboBox template and binding behavior is covered by tests/HuntReplay.WindowsTests.
public static partial class Program
{
    private const string MonitorPath = @"\\.\DISPLAY1";
    private const string MissingMonitorPath = @"\\.\DISPLAY9";
    private const string AudioEndpoint = "{0.0.0.00000000}.{5b8e2a10-6f0c-4d3e-9a1b-2c3d4e5f6a7b}";

    private static void DeviceSelectionTests()
    {
        Test("choice labels are human text in both the closed box and the popup", () =>
        {
            Equal("15 ГБ", new Choice<int>(15, "15 ГБ").ToString());
            False(new Choice<int>(0x70, "F1").ToString().Contains("Value ="));
            Equal("Авто · GPU захватываемого монитора", new Choice<string>("auto", "Авто · GPU захватываемого монитора").ToString());
        });
        Test("device caption is what the box shows; the stored Windows ID stays in Id", () =>
        {
            var monitor = new DeviceOption(0, MonitorPath, "Монитор 1 · 1920 × 1080 · основной", IsDefault: true);
            Equal("Монитор 1 · 1920 × 1080 · основной", monitor.ToString());
            Equal(MonitorPath, monitor.Id);
        });
        Test("unavailable monitor keeps the saved path but never shows it as text", () =>
        {
            string saved = MissingMonitorPath;
            var selector = new DeviceSelectionViewModel(0, "Основной монитор · автоматически", () => saved, value => saved = value);
            selector.Refresh([new DeviceOption(0, MonitorPath, "Монитор 1 · 1920 × 1080 · основной", IsDefault: true)]);
            var missing = selector.Options.Single(d => d.Id == MissingMonitorPath);
            True(missing.Name.Contains("Недоступно"));
            False(missing.Name.Contains("DISPLAY9")); False(missing.Name.Contains(@"\\"));
            Equal(MissingMonitorPath, saved);
            selector.Refresh([new DeviceOption(0, MissingMonitorPath, "Монитор 2 · 2560 × 1440")]);
            Equal("Монитор 2 · 2560 × 1440", selector.Options.Single(d => d.Id == MissingMonitorPath).Name);
            Equal(MissingMonitorPath, saved);
        });
        Test("disconnected audio endpoint GUID is never shown in a caption", () =>
        {
            string saved = AudioEndpoint;
            var selector = new DeviceSelectionViewModel(1, "Устройство вывода Windows по умолчанию", () => saved, value => saved = value);
            selector.Refresh([]);
            var missing = selector.Options.Single(d => d.Id == AudioEndpoint);
            True(missing.Name.Contains("Недоступно"));
            False(missing.Name.Contains("{")); False(missing.Name.Contains("5b8e2a10"));
            Equal(AudioEndpoint, saved);
            selector.Refresh([new DeviceOption(1, AudioEndpoint, "Динамики (USB-гарнитура)")]);
            Equal("Динамики (USB-гарнитура)", selector.Options.Single(d => d.Id == AudioEndpoint).Name);
            Equal(AudioEndpoint, saved);
        });
        Test("default device stays an empty stored ID and is never reported unavailable", () =>
        {
            string saved = "";
            var selector = new DeviceSelectionViewModel(2, "Микрофон Windows по умолчанию", () => saved, value => saved = value);
            selector.Refresh([]);
            Equal(1, selector.Options.Count);
            Equal("", selector.Options[0].Id);
            Equal("Микрофон Windows по умолчанию", selector.Options[0].Name);
        });
        Test("default gpu stays an empty stored ID with Auto option and is never reported unavailable", () =>
        {
            string saved = "";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([]);
            Equal(1, selector.Options.Count);
            Equal("", selector.Options[0].Id);
            Equal("Авто", selector.Options[0].Name);
        });
        Test("unavailable gpu keeps the saved ID and shows unavailable without crashing", () =>
        {
            string saved = "1";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de)]);
            var missing = selector.Options.Single(d => d.Id == "1");
            True(missing.Name.Contains("Недоступно"));
            Equal("1", saved);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de),
                new DeviceOption(3, "1", "Intel(R) UHD Graphics 770", Vendor: 0x8086)
            ]);
            Equal("Intel(R) UHD Graphics 770", selector.Options.Single(d => d.Id == "1").Name);
            Equal("1", saved);
        });
        Test("gpu selector enumerates options with Auto at the top", () =>
        {
            string saved = "";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de),
                new DeviceOption(3, "1", "Intel(R) UHD Graphics 770", Vendor: 0x8086)
            ]);
            Equal(3, selector.Options.Count);
            Equal("", selector.Options[0].Id);
            Equal("Авто", selector.Options[0].Name);
            Equal("0", selector.Options[1].Id);
            Equal("NVIDIA GeForce RTX 3080", selector.Options[1].Name);
            Equal("1", selector.Options[2].Id);
            Equal("Intel(R) UHD Graphics 770", selector.Options[2].Name);
        });
        Test("gpu selector with custom unavailable caption provider uses localized caption", () =>
        {
            string saved = "nonexistent_gpu";
            var selector = new DeviceSelectionViewModel(3, () => "Авто", () => "Unavailable · saved GPU is not available", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de)
            ]);
            Equal(3, selector.Options.Count);
            Equal("Unavailable · saved GPU is not available", selector.Options.Single(d => d.Id == "nonexistent_gpu").Name);
            Equal("nonexistent_gpu", saved);
        });
        Test("gpu selector selecting different GPU mutates saved setting", () =>
        {
            string saved = "";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de),
                new DeviceOption(3, "1", "Intel(R) UHD Graphics 770", Vendor: 0x8086)
            ]);
            selector.SelectedId = "1";
            Equal("1", saved);
            selector.SelectedId = "";
            Equal("", saved);
        });
        Test("gpu selector numeric index isolation: saved ID '2' does not match adapter with digit '2' in name", () =>
        {
            string saved = "2";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "Intel(R) UHD Graphics 770", IsDefault: true, Vendor: 0x8086),
                new DeviceOption(3, "1", "NVIDIA GeForce RTX 2070", Vendor: 0x10de)
            ]);
            // Adapter 1 name contains '2', but ID is '1', so ID '2' is unavailable
            var opt = selector.Options.Single(d => d.Id == "2");
            True(opt.Name.Contains("Недоступно"));
            Equal("2", saved);
        });
        Test("gpu selector negative index survives safely as unavailable option", () =>
        {
            string saved = "-1";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de)
            ]);
            Equal(3, selector.Options.Count);
            Equal("-1", selector.Options[2].Id);
            True(selector.Options[2].Name.Contains("Недоступно"));
        });
        Test("gpu selector multiple GPUs with same model description preserve separate indexed IDs", () =>
        {
            string saved = "1";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 4090", IsDefault: true, Vendor: 0x10de),
                new DeviceOption(3, "1", "NVIDIA GeForce RTX 4090", IsDefault: false, Vendor: 0x10de)
            ]);
            Equal(3, selector.Options.Count);
            Equal("0", selector.Options[1].Id);
            Equal("1", selector.Options[2].Id);
            Equal("1", selector.SelectedId);
            Equal("NVIDIA GeForce RTX 4090", selector.Options[2].Name);
        });
        // Native IDs since v0.1.8: the adapter description, unless two adapters share it.
        DeviceOption[] laptop =
        [
            new(3, "Intel(R) Iris(R) Xe Graphics", "Intel(R) Iris(R) Xe Graphics", IsDefault: true, Vendor: 0x8086),
            new(3, "NVIDIA GeForce RTX 3050 Ti Laptop GPU", "NVIDIA GeForce RTX 3050 Ti Laptop GPU", Vendor: 0x10de)
        ];
        Test("saved gpu survives a restart even when the adapter order changes", () =>
        {
            string saved = "";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh(laptop);
            selector.SelectedId = "NVIDIA GeForce RTX 3050 Ti Laptop GPU";
            var restarted = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            restarted.Refresh([laptop[1], laptop[0]]);
            Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", restarted.SelectedId);
            Equal(3, restarted.Options.Count);
            False(restarted.Options.Any(d => d.Name.Contains("Недоступно")));
        });
        Test("gpu index saved by an older build shows the adapter the recorder uses instead of unavailable", () =>
        {
            string saved = "1";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh(laptop);
            Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", selector.SelectedId);
            Equal(3, selector.Options.Count);
            Equal("1", saved);
            Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", DeviceOption.ResolveGpu(saved, laptop)?.Id);
            // Picking the shown adapter again replaces the old index with the stable ID.
            selector.SelectedId = "NVIDIA GeForce RTX 3050 Ti Laptop GPU";
            Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", saved);
        });
        Test("gpu saved by name resolves regardless of case and surrounding spaces", () =>
        {
            Equal("Intel(R) Iris(R) Xe Graphics", DeviceOption.ResolveGpu(" intel(r) iris(r) xe graphics ", laptop)?.Id);
            True(DeviceOption.ResolveGpu("", laptop) is null);
            True(DeviceOption.ResolveGpu("2", laptop) is null);
            True(DeviceOption.ResolveGpu("-1", laptop) is null);
            True(DeviceOption.ResolveGpu("AMD Radeon RX 9060 XT", laptop) is null);
        });
        Test("gpu selector survives transient null selection without clearing setting", () =>
        {
            string saved = "0";
            var selector = new DeviceSelectionViewModel(3, "Авто", () => saved, value => saved = value);
            selector.Refresh([
                new DeviceOption(3, "0", "NVIDIA GeForce RTX 3080", IsDefault: true, Vendor: 0x10de)
            ]);
            selector.SelectedId = null;
            Equal("0", saved);
            Equal("0", selector.SelectedId);
        });
        // Named wait handles exist only on Windows; the core suite also runs on the Linux CI runner.
        if (OperatingSystem.IsWindows()) Test("nvidia keeper ready event handles signaling, reset and disposal safely", () =>
        {
            string eventName = @"Local\HuntReplay.TestReady." + Guid.NewGuid().ToString("N");
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out bool createdNew);
            True(createdNew);
            False(ev.WaitOne(0));
            ev.Set();
            True(ev.WaitOne(0));
            ev.Reset();
            False(ev.WaitOne(0));
        });
    }
}

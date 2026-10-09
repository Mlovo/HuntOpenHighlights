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
    }
}

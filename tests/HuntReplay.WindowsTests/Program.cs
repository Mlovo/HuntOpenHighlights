using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
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
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine("CRITICAL UNHANDLED EXCEPTION: " + e.ExceptionObject);
            };
            // No window, native backend or real device is required, but real WPF binding,
            // Selector coercion and dispatcher behavior must run on Windows / STA.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Console.WriteLine("DISPATCHER UNHANDLED EXCEPTION: " + e.Exception);
                e.Handled = true;
                failed++;
            };
            foreach (int kind in new[] { 0, 1, 2, 3 })
            {
                Test($"device kind {kind}: default survives repeated refresh", () => WithSelector(kind, "", (s, box, read, _) =>
                {
                    for (int i = 0; i < 5; i++) { s.Refresh(Devices(kind)); Pump(); Equal("", read()); Equal("", box.SelectedValue); Equal("", SelectedId(box)); }
                }));
                Test($"device kind {kind}: explicit ID survives reordered/replaced items", () => WithSelector(kind, "second", (s, box, read, _) =>
                {
                    s.Refresh(Devices(kind)); Pump(); Equal("second", box.SelectedValue); Equal("second", SelectedId(box));
                    s.Refresh(Devices(kind).Reverse()); Pump(); Equal("second", read()); Equal("second", box.SelectedValue); Equal("second", SelectedId(box));
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
                    s.Refresh(Devices(kind)); Pump(); Equal("second", box.SelectedValue); Equal("second", SelectedId(box));
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
                    Equal("first", read()); Equal("first", box.SelectedValue); Equal("first", SelectedId(box));
                }));
            }
            ComboBoxPresentationTests();
            GalleryViewTrimmingTests.RunAllTests();
            RangeSliderStressTests.RunAllTests();
            Application.Current.Shutdown();
            Console.WriteLine($"\n{passed} WPF regressions passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("CRITICAL EXCEPTION IN WindowsTests: " + ex);
            return 1;
        }
    }

    // Production ComboBox templates: the theme is parsed from src/HuntReplay.App/Themes/Dark.xaml itself.
    // Its converters live in the App's Views namespace, which this project links from the same source file.
    private static void ComboBoxPresentationTests()
    {
        var choices = new List<Choice<int>> { new(15, "15 ГБ"), new(30, "30 ГБ"), new(0, "Без ограничений") };
        Test("production theme: closed ComboBox shows the Choice label, not a record dump", () => WithThemedWindow((_, host) =>
        {
            var box = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = choices, SelectedValue = 30 };
            host.Children.Add(box);
            Pump(); box.ApplyTemplate(); box.UpdateLayout(); Pump();
            Equal(30, box.SelectedValue);
            True(TextBlocks(box).Any(t => t.Text == "30 ГБ"));
            False(TextBlocks(box).Any(t => t.Text.Contains("Choice", StringComparison.Ordinal) || t.Text.Contains("Value =", StringComparison.Ordinal)));
        }));
        Test("production theme: choice label updates in-place without clearing selected value", () => WithThemedWindow((_, host) =>
        {
            var testChoices = new[] { new Choice<int>(15, "15 ГБ"), new Choice<int>(30, "30 ГБ") };
            var box = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = testChoices, SelectedValue = 30 };
            host.Children.Add(box);
            Pump(); box.ApplyTemplate(); box.UpdateLayout(); Pump();
            Equal(30, box.SelectedValue);
            True(TextBlocks(box).Any(t => t.Text == "30 ГБ"));

            testChoices[1].Label = "30 GB";
            Pump(); box.UpdateLayout(); Pump();
            Equal(30, box.SelectedValue);
            True(TextBlocks(box).Any(t => t.Text == "30 GB"));
        }));
        Test("production theme: opened popup items show each label for its value", () => WithThemedWindow((_, host) =>
        {
            var box = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = choices, SelectedValue = 15 };
            host.Children.Add(box);
            Pump(); box.IsDropDownOpen = true; Pump(); box.UpdateLayout(); Pump();
            for (int i = 0; i < choices.Count; i++)
            {
                var item = box.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem
                    ?? throw new InvalidOperationException("Popup item " + i + " was not generated.");
                item.ApplyTemplate(); item.UpdateLayout(); Pump();
                True(TextBlocks(item).Any(t => t.Text == choices[i].Label));
            }
            Equal(15, box.SelectedValue);
        }));
        Test("production theme: monitor caption is shown while the Windows ID stays the bound value", () => WithThemedWindow((_, host) =>
        {
            var monitors = new[]
            {
                new DeviceOption(0, @"\\.\DISPLAY1", "Монитор 1 · 1920 × 1080 · основной", IsDefault: true),
                new DeviceOption(0, @"\\.\DISPLAY2", "Монитор 2 · 2560 × 1440"),
            };
            var box = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", ItemsSource = monitors, SelectedValue = @"\\.\DISPLAY2" };
            host.Children.Add(box);
            Pump(); box.ApplyTemplate(); box.UpdateLayout(); Pump();
            Equal(@"\\.\DISPLAY2", box.SelectedValue);
            True(TextBlocks(box).Any(t => t.Text == "Монитор 2 · 2560 × 1440"));
            False(TextBlocks(box).Any(t => t.Text.Contains("DISPLAY", StringComparison.Ordinal)));
        }));
        Test("production theme: unavailable saved device shows a caption without the saved ID", () => WithThemedWindow((_, host) =>
        {
            string saved = @"\\.\DISPLAY9";
            var selector = new DeviceSelectionViewModel(0, "Основной монитор · автоматически", () => saved, value => saved = value);
            selector.Refresh([]);
            var box = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", IsSynchronizedWithCurrentItem = false };
            box.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(selector.Options)) { Source = selector });
            box.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(selector.SelectedId)) { Source = selector, Mode = BindingMode.TwoWay });
            host.Children.Add(box);
            try
            {
                Pump(); box.ApplyTemplate(); box.UpdateLayout(); Pump();
                Equal(@"\\.\DISPLAY9", box.SelectedValue);
                Equal(@"\\.\DISPLAY9", saved);
                True(TextBlocks(box).Any(t => t.Text.Contains("Недоступно", StringComparison.Ordinal)));
                False(TextBlocks(box).Any(t => t.Text.Contains("DISPLAY9", StringComparison.Ordinal)));
            }
            finally { BindingOperations.ClearAllBindings(box); }
        }));
        Test("production theme: TabControl content text is light and not black", () => WithThemedWindow((_, host) =>
        {
            var tabControl = new TabControl();
            var tab1 = new TabItem
            {
                Header = "Видео",
                Content = new StackPanel
                {
                    Children = { new TextBlock { Text = "Источник захвата" } }
                }
            };
            var tab2 = new TabItem { Header = "Аудио" };
            tabControl.Items.Add(tab1);
            tabControl.Items.Add(tab2);
            tabControl.SelectedItem = tab1;
            host.Children.Add(tabControl);
            Pump();
            tabControl.ApplyTemplate();
            tabControl.UpdateLayout();
            Pump();

            var innerText = TextBlocks((DependencyObject)tab1.Content).FirstOrDefault(t => t.Text == "Источник захвата");
            True(innerText != null);
            var fg = innerText!.Foreground as SolidColorBrush;
            True(fg != null);
            False(fg!.Color == Colors.Black);
        }));
    }

    private static ResourceDictionary LoadProductionTheme([CallerFilePath] string thisFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "HuntReplay.App", "Themes", "Dark.xaml"));
        var xaml = File.ReadAllText(path).Replace("clr-namespace:HuntReplay.App.Views\"", "clr-namespace:HuntReplay.App.Views;assembly=HuntReplay.WindowsTests\"", StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    internal static void WithThemedWindow(Action<Window, StackPanel> test)
    {
        // The window is shown so popups and their item containers are really generated.
        var host = new StackPanel { Resources = LoadProductionTheme(), Width = 480 };
        var window = new Window { Content = host, Width = 520, Height = 360, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try { Pump(); test(window, host); }
        finally { window.Close(); Pump(); }
    }

    internal static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) yield return text;
            foreach (var nested in TextBlocks(child)) yield return nested;
        }
    }

    private static DeviceOption[] Devices(int kind) =>
        [new(kind, "first", "First"), new(kind, "second", "Second"), new((kind + 1) % 4, "wrong-kind", "Exclude me")];

    private static void WithSelector(int kind, string initial, Action<DeviceSelectionViewModel, ComboBox, Func<string>, Action<string>> test)
    {
        var settings = new StudioSettings();
        string Read() => kind switch { 0 => settings.Video.MonitorDevice, 1 => settings.Audio.OutputDeviceId, 3 => settings.Video.GpuDevice, _ => settings.Audio.MicrophoneDeviceId };
        void Write(string id)
        {
            if (kind == 0) settings.Video.MonitorDevice = id;
            else if (kind == 1) settings.Audio.OutputDeviceId = id;
            else if (kind == 3) settings.Video.GpuDevice = id;
            else settings.Audio.MicrophoneDeviceId = id;
        }
        Write(initial);
        var selector = new DeviceSelectionViewModel(kind, "Windows default", Read, Write);
        var box = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", IsSynchronizedWithCurrentItem = false };
        box.BeginInit();
        box.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(selector.Options)) { Source = selector });
        box.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(selector.SelectedId)) { Source = selector, Mode = BindingMode.TwoWay });
        box.EndInit();
        try
        {
            Pump(); Equal(initial, Read()); Equal(initial, box.SelectedValue); Equal(initial, SelectedId(box));
            test(selector, box, Read, id => { settings = settings.Clone(); Write(id); });
            True(selector.Options.All(d => d.Kind == kind));
            Equal(1, selector.Options.Count(d => d.Id == ""));
        }
        finally { BindingOperations.ClearAllBindings(box); }
    }

    private static string SelectedId(ComboBox box) => (box.SelectedItem as DeviceOption)?.Id
        ?? throw new InvalidOperationException("The displayed SelectedItem is null despite SelectedValue='" + box.SelectedValue + "'.");

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    internal static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex)
        {
            failed++; Console.WriteLine("FAIL " + name + ": " + ex);
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine("::error title=WPF binding regression::" + (name + ": " + ex).Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
        }
    }
    internal static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
    internal static void False(bool value) => True(!value);
    internal static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected ?? "<null>"}', got '{actual ?? "<null>"}'");
    }
}

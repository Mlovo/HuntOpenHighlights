using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HuntReplay.WindowsTests;

public static class RangeSliderStressTests
{
    private static Type? sliderType;
    private static PropertyInfo? propMin;
    private static PropertyInfo? propMax;
    private static PropertyInfo? propStart;
    private static PropertyInfo? propEnd;
    private static PropertyInfo? propMinRange;
    private static PropertyInfo? propPlayhead;
    private static PropertyInfo? propEffectiveMin;
    private static MethodInfo? methodUpdateLayout;
    private static MethodInfo? methodStartDragDelta;
    private static MethodInfo? methodEndDragDelta;
    private static MethodInfo? methodRangeBarMouseDown;
    private static MethodInfo? methodRangeBarMouseMove;
    private static MethodInfo? methodRangeBarMouseUp;

    private static Type GetSliderType()
    {
        if (sliderType != null) return sliderType;

        string current = AppContext.BaseDirectory;
        string? root = null;
        var dir = new DirectoryInfo(current);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HuntReplayStudio.sln")))
            {
                root = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        if (root == null) throw new InvalidOperationException("Could not find repository root containing HuntReplayStudio.sln");

        string[] candidates = [
            Path.Combine(root, "src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(root, "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(root, "src", "HuntReplay.App", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(root, "src", "HuntReplay.App", "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")
        ];

        string? found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            var files = Directory.GetFiles(Path.Combine(root, "src", "HuntReplay.App", "bin"), "HuntReplayStudio.dll", SearchOption.AllDirectories);
            found = files.FirstOrDefault();
        }

        if (found == null) throw new FileNotFoundException("HuntReplayStudio.dll could not be located in any bin output directory.");

        var asm = Assembly.LoadFrom(found);
        sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")
            ?? throw new InvalidOperationException("Type HuntReplay.App.Controls.RangeSlider not found in assembly.");

        propMin = sliderType.GetProperty("Minimum")!;
        propMax = sliderType.GetProperty("Maximum")!;
        propStart = sliderType.GetProperty("RangeStart")!;
        propEnd = sliderType.GetProperty("RangeEnd")!;
        propMinRange = sliderType.GetProperty("MinRange")!;
        propPlayhead = sliderType.GetProperty("Playhead")!;
        propEffectiveMin = sliderType.GetProperty("EffectiveMinRange")!;

        methodUpdateLayout = sliderType.GetMethod("UpdateLayoutPositions", BindingFlags.Public | BindingFlags.Instance);
        methodStartDragDelta = sliderType.GetMethod("OnStartThumbDragDelta", BindingFlags.NonPublic | BindingFlags.Instance);
        methodEndDragDelta = sliderType.GetMethod("OnEndThumbDragDelta", BindingFlags.NonPublic | BindingFlags.Instance);
        methodRangeBarMouseDown = sliderType.GetMethod("OnRangeBarMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
        methodRangeBarMouseMove = sliderType.GetMethod("OnRangeBarMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
        methodRangeBarMouseUp = sliderType.GetMethod("OnRangeBarMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);

        return sliderType;
    }

    private static FrameworkElement CreateSlider()
    {
        var type = GetSliderType();
        return (FrameworkElement)Activator.CreateInstance(type)!;
    }

    public static void RunAllTests()
    {
        Console.WriteLine("\n--- Starting RangeSlider Empirical Stress Test Suite ---");
        TestAssemblyLoadingAndInstantiation();
        TestBoundaryLimits();
        TestMinimalDurationConstraint();
        TestCrossingBounds();
        TestInvalidNumbersAndExtremes();
        TestTranslationDraggingLeft();
        TestTranslationDraggingRight();
        TestMaximumReductionInvariant();
        TestMinimumGreaterThanMaximumCrash();
        TestVisualGeometryAndZeroWidth();
        TestFuzzRandomOperations();
        Console.WriteLine("--- Completed RangeSlider Empirical Stress Test Suite ---\n");
    }

    /// <summary>
    /// EMPIRICAL TEST 1: Production RangeSlider loads from assembly and renders in visual tree.
    /// </summary>
    private static void TestAssemblyLoadingAndInstantiation()
    {
        Program.Test("EMPIRICAL: RangeSlider loads from HuntReplayStudio.dll and mounts in WPF visual tree", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                host.Children.Add(slider);
                Program.Pump();

                Program.Equal(0.0, (double)propMin!.GetValue(slider)!);
                Program.Equal(1.0, (double)propMax!.GetValue(slider)!);
                Program.Equal(0.0, (double)propStart!.GetValue(slider)!);
                Program.Equal(1.0, (double)propEnd!.GetValue(slider)!);
                Program.Equal(0.5, (double)propMinRange!.GetValue(slider)!);
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 2: Boundary limits Start=0, End=Maximum.
    /// </summary>
    private static void TestBoundaryLimits()
    {
        Program.Test("EMPIRICAL: RangeSlider boundary limits Start=0, End=Maximum", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 120.0);
                propStart!.SetValue(slider, 0.0);
                propEnd!.SetValue(slider, 120.0);
                Program.Pump();

                Program.Equal(0.0, (double)propStart.GetValue(slider)!);
                Program.Equal(120.0, (double)propEnd.GetValue(slider)!);

                // Start cannot be pushed below Minimum
                propStart.SetValue(slider, -10.0);
                Program.Pump();
                Program.Equal(0.0, (double)propStart.GetValue(slider)!);

                // End cannot be pushed above Maximum
                propEnd.SetValue(slider, 150.0);
                Program.Pump();
                Program.Equal(120.0, (double)propEnd.GetValue(slider)!);
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 3: Minimal duration constraint stops at 0.5s separation.
    /// </summary>
    private static void TestMinimalDurationConstraint()
    {
        Program.Test("EMPIRICAL: Minimal duration constraint enforces >= 0.5s separation", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 400;
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 100.0);
                propMinRange!.SetValue(slider, 0.5);
                propStart!.SetValue(slider, 10.0);
                propEnd!.SetValue(slider, 20.0);
                Program.Pump();

                // 1. Move Start towards End (attempt 19.8s when End is 20.0s)
                propStart.SetValue(slider, 19.8);
                Program.Pump();
                double actualStart = (double)propStart.GetValue(slider)!;
                Program.Equal(19.5, actualStart);

                // 2. Move End towards Start (attempt 19.6s when Start is 19.5s)
                propEnd.SetValue(slider, 19.6);
                Program.Pump();
                double actualEnd = (double)propEnd.GetValue(slider)!;
                Program.Equal(20.0, actualEnd);

                // 3. Simulate drag delta on StartThumb moving towards End
                // Usable width = 400 - 14 = 386. Range = 100. Delta +50px = +12.95s
                var dragEvent = new DragDeltaEventArgs(100.0, 0.0);
                methodStartDragDelta?.Invoke(slider, [slider, dragEvent]);
                Program.Pump();
                actualStart = (double)propStart.GetValue(slider)!;
                // End is 20.0, so Start must not exceed 19.5
                Program.True(actualStart <= 19.5);
                Program.Equal(19.5, actualStart);
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 4: Crossing bounds (Start past End, End before Start).
    /// </summary>
    private static void TestCrossingBounds()
    {
        Program.Test("EMPIRICAL: Crossing bounds is prevented by coercion and dragging clamps", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 400;
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 60.0);
                propStart!.SetValue(slider, 10.0);
                propEnd!.SetValue(slider, 30.0);
                Program.Pump();

                // Direct attempt to cross: Start = 50.0 (past End=30.0)
                propStart.SetValue(slider, 50.0);
                Program.Pump();
                Program.Equal(29.5, (double)propStart.GetValue(slider)!);

                // Direct attempt to cross: End = 5.0 (before Start=29.5)
                propEnd.SetValue(slider, 5.0);
                Program.Pump();
                Program.Equal(30.0, (double)propEnd.GetValue(slider)!);

                // Dragging End backwards by huge negative delta
                var dragEventBackwards = new DragDeltaEventArgs(-500.0, 0.0);
                methodEndDragDelta?.Invoke(slider, [slider, dragEventBackwards]);
                Program.Pump();
                double endAfterDrag = (double)propEnd.GetValue(slider)!;
                double startAfterDrag = (double)propStart.GetValue(slider)!;
                Program.True(endAfterDrag >= startAfterDrag + 0.5);
                Program.Equal(30.0, endAfterDrag);
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 5: Stress test invalid numbers (NaN, Infinity, negative values, zero duration).
    /// </summary>
    private static void TestInvalidNumbersAndExtremes()
    {
        Program.Test("EMPIRICAL: Invalid numbers (NaN, Infinity, negative, zero duration) handled safely", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 300;
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 100.0);
                propStart!.SetValue(slider, 10.0);
                propEnd!.SetValue(slider, 50.0);
                Program.Pump();

                // NaN RangeStart -> coerced to Minimum
                propStart.SetValue(slider, double.NaN);
                Program.Pump();
                Program.Equal(0.0, (double)propStart.GetValue(slider)!);

                // Positive Infinity RangeStart -> coerced to maxStart (49.5)
                propStart.SetValue(slider, double.PositiveInfinity);
                Program.Pump();
                Program.Equal(0.0, (double)propStart.GetValue(slider)!); // double.IsInfinity returns Minimum

                // NaN RangeEnd -> coerced to Maximum
                propEnd.SetValue(slider, double.NaN);
                Program.Pump();
                Program.Equal(100.0, (double)propEnd.GetValue(slider)!);

                // Zero duration: Maximum == Minimum
                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 0.0);
                Program.Pump();
                double s = (double)propStart.GetValue(slider)!;
                double e = (double)propEnd.GetValue(slider)!;
                Program.Equal(0.0, s);
                Program.Equal(0.0, e);

                // Layout update should not throw DivideByZeroException
                methodUpdateLayout?.Invoke(slider, null);
                Program.Pump();
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 6A: Translation dragging to the LEFT maintains duration window.
    /// </summary>
    private static void TestTranslationDraggingLeft()
    {
        Program.Test("EMPIRICAL: Translation dragging to the LEFT maintains duration window", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 400;
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 100.0);
                propMinRange!.SetValue(slider, 0.5);
                propEnd!.SetValue(slider, 40.0);
                propStart!.SetValue(slider, 30.0); // Duration = 10.0s
                Program.Pump();

                double initialDuration = (double)propEnd.GetValue(slider)! - (double)propStart.GetValue(slider)!;
                Program.Equal(10.0, initialDuration);

                // Drag left: candStart = 15.0, candEnd = 25.0
                double candStart = 15.0;
                double candEnd = 25.0;

                // When moving left, RangeStart is decreased first:
                propStart.SetValue(slider, candStart);
                propEnd.SetValue(slider, candEnd);
                Program.Pump();

                double resultStart = (double)propStart.GetValue(slider)!;
                double resultEnd = (double)propEnd.GetValue(slider)!;
                double resultingDuration = resultEnd - resultStart;

                Program.Equal(15.0, resultStart);
                Program.Equal(25.0, resultEnd);
                Program.Equal(initialDuration, resultingDuration);
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 6B: Translation dragging to the RIGHT maintains duration window.
    /// STRESS TEST: When moving right, RangeStart is assigned before RangeEnd, causing RangeStart to be coerced
    /// against old RangeEnd, which distorts the duration window!
    /// </summary>
    private static void TestTranslationDraggingRight()
    {
        Program.Test("EMPIRICAL STRESS: Translation dragging to the RIGHT maintains duration window", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 400;
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 100.0);
                propMinRange!.SetValue(slider, 0.5);
                propEnd!.SetValue(slider, 20.0);
                propStart!.SetValue(slider, 10.0); // Duration = 10.0s
                Program.Pump();

                double initialDuration = (double)propEnd.GetValue(slider)! - (double)propStart.GetValue(slider)!;
                Program.Equal(10.0, initialDuration);

                // Drag right by +25s: candStart = 35.0, candEnd = 45.0
                double deltaSec = 25.0;
                double candStart = 10.0 + deltaSec;
                double candEnd = 20.0 + deltaSec;

                var methodSetRange = GetSliderType().GetMethod("SetRange", BindingFlags.Public | BindingFlags.Instance);
                if (methodSetRange != null)
                {
                    methodSetRange.Invoke(slider, [candStart, candEnd]);
                }
                else
                {
                    propEnd.SetValue(slider, candEnd);
                    propStart.SetValue(slider, candStart);
                }
                Program.Pump();

                double resultStart = (double)propStart.GetValue(slider)!;
                double resultEnd = (double)propEnd.GetValue(slider)!;
                double resultingDuration = resultEnd - resultStart;

                Console.WriteLine($"\n[DIAGNOSTIC] Translation drag right: Expected Duration=10.0, Actual Duration={resultingDuration} (Start={resultStart}, End={resultEnd})");

                if (Math.Abs(resultingDuration - initialDuration) > 1e-4)
                {
                    throw new InvalidOperationException(
                        $"BUG CONFIRMED: Setting RangeStart before RangeEnd during translation drag corrupts the duration window! " +
                        $"Expected duration={initialDuration}, but got duration={resultingDuration} (RangeStart={resultStart}, RangeEnd={resultEnd})");
                }
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 7: Changing Maximum when RangeStart/RangeEnd exceed new Maximum.
    /// STRESS TEST: When Maximum is reduced below RangeEnd or RangeStart, is the invariant RangeStart <= RangeEnd <= Maximum maintained?
    /// </summary>
    private static void TestMaximumReductionInvariant()
    {
        Program.Test("EMPIRICAL STRESS: Reducing Maximum maintains RangeStart <= RangeEnd <= Maximum invariant", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                host.Children.Add(slider);
                Program.Pump();

                propMin!.SetValue(slider, 0.0);
                propMax!.SetValue(slider, 100.0);
                propEnd!.SetValue(slider, 80.0);
                propStart!.SetValue(slider, 50.0);
                Program.Pump();

                // Now reduce Maximum to 30.0 (less than both RangeStart and RangeEnd)
                propMax.SetValue(slider, 30.0);
                Program.Pump();

                double curStart = (double)propStart.GetValue(slider)!;
                double curEnd = (double)propEnd.GetValue(slider)!;
                double curMax = (double)propMax.GetValue(slider)!;

                Console.WriteLine($"\n[DIAGNOSTIC] Maximum reduced to {curMax}: RangeStart={curStart}, RangeEnd={curEnd}");

                if (curStart > curEnd)
                {
                    throw new InvalidOperationException(
                        $"BUG CONFIRMED: Invariant violated! RangeStart ({curStart}) > RangeEnd ({curEnd}) after reducing Maximum to {curMax}!");
                }

                if (curStart > curMax)
                {
                    throw new InvalidOperationException(
                        $"BUG CONFIRMED: Invariant violated! RangeStart ({curStart}) > Maximum ({curMax})!");
                }

                if (curEnd > curMax)
                {
                    throw new InvalidOperationException(
                        $"BUG CONFIRMED: Invariant violated! RangeEnd ({curEnd}) > Maximum ({curMax})!");
                }
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 8: Minimum > Maximum throws unhandled ArgumentException in CoercePlayhead.
    /// </summary>
    private static void TestMinimumGreaterThanMaximumCrash()
    {
        Program.Test("EMPIRICAL STRESS: Setting Minimum > Maximum does not throw unhandled exception", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                host.Children.Add(slider);
                Program.Pump();

                // Current Maximum is 1.0. Set Minimum to 10.0.
                // In RangeSlider.xaml.cs, CoercePlayhead executes Math.Clamp(val, slider.Minimum, slider.Maximum).
                // When Minimum > Maximum, Math.Clamp throws ArgumentException!
                propMin!.SetValue(slider, 10.0);
                Program.Pump();
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 8: Visual geometry with boundary conditions (width=0, width=1, negative positions).
    /// </summary>
    private static void TestVisualGeometryAndZeroWidth()
    {
        Program.Test("EMPIRICAL: RangeSlider handles zero and extreme widths without layout errors", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                slider.Width = 0;
                host.Children.Add(slider);
                Program.Pump();

                // Force layout update with Width = 0
                methodUpdateLayout?.Invoke(slider, null);
                Program.Pump();

                slider.Width = 10;
                methodUpdateLayout?.Invoke(slider, null);
                Program.Pump();

                slider.Width = 5000;
                methodUpdateLayout?.Invoke(slider, null);
                Program.Pump();
            });
        });
    }

    /// <summary>
    /// EMPIRICAL TEST 9: Fuzzing random operations to stress invariants.
    /// </summary>
    private static void TestFuzzRandomOperations()
    {
        Program.Test("EMPIRICAL: Fuzzing 500 randomized property updates preserves structural invariants", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var slider = CreateSlider();
                host.Children.Add(slider);
                Program.Pump();

                var rng = new Random(42);
                int violations = 0;

                for (int i = 0; i < 500; i++)
                {
                    int op = rng.Next(6);
                    double val = (rng.NextDouble() * 200.0) - 20.0; // [-20.0, 180.0]

                    switch (op)
                    {
                        case 0:
                            propMin!.SetValue(slider, Math.Max(0.0, val));
                            break;
                        case 1:
                            propMax!.SetValue(slider, Math.Max((double)propMin!.GetValue(slider)!, val));
                            break;
                        case 2:
                            propStart!.SetValue(slider, val);
                            break;
                        case 3:
                            propEnd!.SetValue(slider, val);
                            break;
                        case 4:
                            propPlayhead!.SetValue(slider, val);
                            break;
                        case 5:
                            methodUpdateLayout?.Invoke(slider, null);
                            break;
                    }
                    Program.Pump();

                    double s = (double)propStart!.GetValue(slider)!;
                    double e = (double)propEnd!.GetValue(slider)!;
                    double min = (double)propMin!.GetValue(slider)!;
                    double max = (double)propMax!.GetValue(slider)!;

                    double normMin = Math.Min(min, max);
                    double normMax = Math.Max(min, max);

                    // Check invariants
                    if (s < normMin - 1e-4 || s > normMax + 1e-4 || e < normMin - 1e-4 || e > normMax + 1e-4 || s > e + 1e-4)
                    {
                        if (violations == 0)
                        {
                            Console.WriteLine($"\n[FIRST VIOLATION] op={op}, val={val}: min={min}, max={max}, normMin={normMin}, normMax={normMax}, start={s}, end={e}");
                        }
                        violations++;
                    }
                }

                Console.WriteLine($"\n[DIAGNOSTIC] Fuzzing results: {violations} invariant violations detected in 500 random operations.");
                if (violations > 0)
                {
                    throw new InvalidOperationException($"Structural invariants violated {violations} times during fuzzing!");
                }
            });
        });
    }
}

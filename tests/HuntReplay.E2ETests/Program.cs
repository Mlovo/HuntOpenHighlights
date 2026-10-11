using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace HuntReplay.E2ETests;

public static class Program
{
    private static int totalPassed = 0;
    private static int totalFailed = 0;
    private static int t1Passed = 0, t1Failed = 0;
    private static int t2Passed = 0, t2Failed = 0;
    private static int t3Passed = 0, t3Failed = 0;
    private static int t4Passed = 0, t4Failed = 0;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  HuntReplayStudio - OPAQUE-BOX E2E TEST SUITE (TIERS 1-4)");
        Console.WriteLine("================================================================================");

        var stopwatch = Stopwatch.StartNew();

        // Initialize WPF application context for STA dispatching
        if (Application.Current == null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        try
        {
            // Execute Tier 1: Feature Coverage (Isolated Happy Path)
            Tier1_FeatureCoverageTests.RunAll(
                (name, action) => ExecuteTest(1, name, action),
                (name, action) => ExecuteTestAsync(1, name, action).GetAwaiter().GetResult());

            // Execute Tier 2: Boundary & Corner Cases
            Tier2_BoundaryTests.RunAll(
                (name, action) => ExecuteTest(2, name, action),
                (name, action) => ExecuteTestAsync(2, name, action).GetAwaiter().GetResult());

            // Execute Tier 3: Cross-Feature Combinations / Pairwise
            Tier3_CrossFeatureTests.RunAll(
                (name, action) => ExecuteTest(3, name, action),
                (name, action) => ExecuteTestAsync(3, name, action).GetAwaiter().GetResult());

            // Execute Tier 4: Real-World Application Scenarios
            Tier4_RealWorldScenariosTests.RunAll(
                (name, action) => ExecuteTest(4, name, action),
                (name, action) => ExecuteTestAsync(4, name, action).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[CRITICAL ERROR DURING SUITE EXECUTION]: {ex}");
            totalFailed++;
        }
        finally
        {
            if (Application.Current != null)
            {
                Application.Current.Shutdown();
            }
        }

        stopwatch.Stop();

        // Summary report
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("  E2E TEST EXECUTION SUMMARY");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"  Tier 1 (Feature Coverage):        {t1Passed,2} passed, {t1Failed,2} failed");
        Console.WriteLine($"  Tier 2 (Boundary & Corner Cases): {t2Passed,2} passed, {t2Failed,2} failed");
        Console.WriteLine($"  Tier 3 (Cross-Feature Pairwise):  {t3Passed,2} passed, {t3Failed,2} failed");
        Console.WriteLine($"  Tier 4 (Real-World Scenarios):    {t4Passed,2} passed, {t4Failed,2} failed");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"  TOTAL:                            {totalPassed,2} passed, {totalFailed,2} failed in {stopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine("================================================================================");

        if (totalFailed == 0)
        {
            Console.WriteLine("\n>>> ALL E2E REQUIREMENTS VERIFIED SUCCESSFULLY (EXIT CODE 0) <<<\n");
            return 0;
        }
        else
        {
            Console.WriteLine($"\n>>> E2E SUITE FAILED WITH {totalFailed} FAILURES (EXIT CODE 1) <<<\n");
            return 1;
        }
    }

    private static void ExecuteTest(int tier, string name, Action action)
    {
        try
        {
            action();
            RecordPass(tier, name);
        }
        catch (Exception ex)
        {
            RecordFail(tier, name, ex);
        }
    }

    private static async Task ExecuteTestAsync(int tier, string name, Func<Task> action)
    {
        try
        {
            await action();
            RecordPass(tier, name);
        }
        catch (Exception ex)
        {
            RecordFail(tier, name, ex);
        }
    }

    private static void RecordPass(int tier, string name)
    {
        totalPassed++;
        switch (tier)
        {
            case 1: t1Passed++; break;
            case 2: t2Passed++; break;
            case 3: t3Passed++; break;
            case 4: t4Passed++; break;
        }
        Console.WriteLine($"[PASS] {name}");
    }

    private static void RecordFail(int tier, string name, Exception ex)
    {
        totalFailed++;
        switch (tier)
        {
            case 1: t1Failed++; break;
            case 2: t2Failed++; break;
            case 3: t3Failed++; break;
            case 4: t4Failed++; break;
        }
        Console.WriteLine($"[FAIL] {name}\n       Error: {ex.Message}");
    }
}

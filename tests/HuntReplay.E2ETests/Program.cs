using System.Windows;
using HuntReplay.TestShared;

namespace HuntReplay.E2ETests;

/// <summary>
/// The four E2E tiers as MSTest cases, run on the shared STA thread with one WPF Application.
/// Each case name starts with its tier, so a tier can be run alone: dotnet run --project tests/HuntReplay.E2ETests -- --filter "Name~Tier 2"
/// </summary>
[TestClass]
public sealed class E2ESuite
{
    public static IEnumerable<object[]> Cases => StaWorker.Invoke(() => TestRegistry.Rows(RegisterAll));
    public static string CaseName(System.Reflection.MethodInfo method, object[] row) => (string)row[0];

    [TestMethod]
    [DynamicData(nameof(Cases), DynamicDataDisplayName = nameof(CaseName))]
    public void Run(string name) => StaWorker.Run(() => TestRegistry.Run(name));

    private static void RegisterAll()
    {
        // WPF allows one Application per process; the tests use its dispatcher for STA work.
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Register("Tier 1", Tier1_FeatureCoverageTests.RunAll);
        Register("Tier 2", Tier2_BoundaryTests.RunAll);
        Register("Tier 3", Tier3_CrossFeatureTests.RunAll);
        Register("Tier 4", Tier4_RealWorldScenariosTests.RunAll);
    }

    private static void Register(string tier, Action<Action<string, Action>, Action<string, Func<Task>>> runAll) =>
        runAll((name, body) => TestRegistry.Add($"{tier}: {name}", body),
               (name, body) => TestRegistry.Add($"{tier}: {name}", body));
}

using System.IO;
using System.Reflection;

namespace HuntReplay.WindowsTests;

/// <summary>
/// The production HuntReplayStudio.dll built by tools/Build.ps1 (dotnet build src/HuntReplay.App) in the same
/// configuration as this test run. There is deliberately no search through other bin folders and no on-demand
/// build: a stale Debug or x64 output must never stand in for the code under test.
/// </summary>
internal static class AppUnderTest
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif
    private static Assembly? assembly;

    public static Assembly Load()
    {
        if (assembly is not null) return assembly;
        var root = RepositoryRoot();
        var path = Path.Combine(root, "src", "HuntReplay.App", "bin", Configuration,
            "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Build the app first: dotnet build src/HuntReplay.App -c {Configuration}", path);
        return assembly = Assembly.LoadFrom(path);
    }

    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "HuntReplayStudio.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not find repository root containing HuntReplayStudio.sln");
    }
}

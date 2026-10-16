namespace HuntReplay.TestShared;

/// <summary>
/// Bridges the suites' compact <c>Test("name", body)</c> style to MSTest. Registration only records each body;
/// an MSTest <c>[DynamicData]</c> method then runs every case as its own test, so each one is reported, filtered
/// and timed separately (<c>dotnet run --project ... -- --filter "Name~trim"</c>).
/// </summary>
internal static class TestRegistry
{
    private static readonly object Gate = new();
    private static readonly List<string> Order = [];
    private static readonly Dictionary<string, Func<Task>> Bodies = new(StringComparer.Ordinal);
    private static bool registered;

    public static void Add(string name, Action body) => Add(name, () => { body(); return Task.CompletedTask; });

    public static void Add(string name, Func<Task> body)
    {
        lock (Gate)
        {
            if (!Bodies.TryAdd(name, body)) throw new InvalidOperationException("Duplicate test name: " + name);
            Order.Add(name);
        }
    }

    /// <summary>Runs the suite's registration once and returns one data row per test, in registration order.</summary>
    public static IEnumerable<object[]> Rows(Action register)
    {
        lock (Gate)
        {
            if (!registered)
            {
                register();
                registered = true;
            }
            return Order.Select(name => new object[] { name }).ToArray();
        }
    }

    public static Task Run(string name)
    {
        Func<Task> body;
        lock (Gate)
            if (!Bodies.TryGetValue(name, out body!)) throw new InvalidOperationException("Unknown test: " + name);
        return body();
    }
}

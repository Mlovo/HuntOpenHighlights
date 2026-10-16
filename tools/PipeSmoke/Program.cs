namespace HuntReplay.PipeSmoke;

// dotnet run --project tools/PipeSmoke [-- --multi] [-- --control-id]
internal static class Program
{
    public static Task<int> Main(string[] args) =>
        LivePipeSmoke.RunAsync(args.Contains("--multi"), args.Contains("--control-id"));
}

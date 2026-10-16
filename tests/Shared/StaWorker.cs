using System.Collections.Concurrent;
using System.Threading;

namespace HuntReplay.TestShared;

/// <summary>
/// One long-lived STA thread for the WPF suites. WPF allows a single Application per process and ties windows and
/// dispatchers to their thread, so registration, the Application object and every test body run on this thread,
/// one at a time, exactly as the suites' old hand-written Main did.
/// </summary>
internal static class StaWorker
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Thread Thread = StartThread();

    private static Thread StartThread()
    {
        var thread = new Thread(() => { foreach (var item in Work.GetConsumingEnumerable()) item(); })
        {
            IsBackground = true,
            Name = "WPF test STA thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    public static T Invoke<T>(Func<T> body)
    {
        if (Thread.CurrentThread == Thread) return body();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            try { done.SetResult(body()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    public static void Invoke(Action body) => Invoke(() => { body(); return true; });

    /// <summary>Runs an async test body to completion on the STA thread (blocking it, as the old runners did).</summary>
    public static void Run(Func<Task> body) => Invoke(() => body().GetAwaiter().GetResult());
}

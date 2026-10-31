using System.IO;
using PGMS.Desktop;

internal static class RecoveryIoChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var queue = new RecoveryIoQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var first = queue.RunAsync(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); order.Add(1); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = queue.RunAsync(() => order.Add(2));
        check(!second.IsCompleted, "Recovery IO waits for prior operation without blocking dispatcher");
        release.SetResult();
        await Task.WhenAll(first, second);
        check(order.SequenceEqual([1, 2]), "Recovery IO preserves submission order");
        try { await queue.RunAsync(() => throw new IOException("synthetic")); } catch (IOException) { }
        check(await queue.RunAsync(() => 42) == 42, "Recovery IO retries after failed operation");
        int caller = Environment.CurrentManagedThreadId;
        check(await queue.RunAsync(() => Environment.CurrentManagedThreadId) != caller, "Recovery IO executes outside WPF dispatcher");
    }
}

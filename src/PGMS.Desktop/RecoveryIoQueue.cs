namespace PGMS.Desktop;

/// <summary>Orders recovery disk operations without running them on the caller's dispatcher.</summary>
public sealed class RecoveryIoQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;

    public Task<T> RunAsync<T>(Func<T> operation)
    {
        lock (gate)
        {
            Task previous = tail;
            var next = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* A failed operation must not poison later cleanup or retry. */ }
                return operation();
            });
            tail = next;
            return next;
        }
    }

    public Task<T> RunAwaitedAsync<T>(Func<Task<T>> operation)
    {
        lock (gate)
        {
            Task previous = tail;
            var next = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* A failed predecessor does not poison the queue. */ }
                return await operation().ConfigureAwait(false);
            });
            tail = next;
            return next;
        }
    }

    public Task RunAsync(Action operation) => RunAsync(() => { operation(); return true; });
    public T Run<T>(Func<T> operation) => RunAsync(operation).GetAwaiter().GetResult();
    public void Run(Action operation) => RunAsync(operation).GetAwaiter().GetResult();
}
